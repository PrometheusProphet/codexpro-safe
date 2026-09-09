import SwiftUI
import Security
import ServiceManagement
import Darwin
import ManagerCore

enum ServiceState { case stopped, starting, ready, degraded, stopping }

@MainActor
final class ManagerModel: ObservableObject {
    @Published var settings: ManagerSettings
    @Published var state: ServiceState = .stopped
    @Published var status = "Stopped"
    @Published var launchAtLogin = false
    private var process: Process?
    private var healthTask: Task<Void, Never>?
    private var intentionalStop = false
    private let store: SettingsFileStore

    init() {
        let environment = ProcessInfo.processInfo.environment
        let repository = environment["CODEXPRO_MANAGER_REPOSITORY"] ?? FileManager.default.currentDirectoryPath
        let node = environment["CODEXPRO_MANAGER_NODE"] ??
            ["/opt/homebrew/bin/node", "/usr/local/bin/node", "/usr/bin/node"]
                .first(where: FileManager.default.isExecutableFile(atPath:)) ?? "/usr/local/bin/node"
        let defaults = ManagerSettings.defaults(repository: repository, nodePath: node)
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        store = SettingsFileStore(url: base.appendingPathComponent("CodexProSafe Manager/settings.json"))
        settings = defaults
        launchAtLogin = SMAppService.mainApp.status == .enabled
        Task { settings = await store.load(defaults: defaults) }
    }

    func save() {
        Task {
            do {
                let checked = try settings.validated()
                try await store.save(checked)
                settings = checked
                status = "Settings saved"
            } catch { status = error.localizedDescription }
        }
    }

    func start() {
        guard process == nil else { return }
        do {
            let checked = try settings.validated()
            let token = try KeychainTokenStore.read()
            settings = checked
            intentionalStop = false
            state = .starting
            status = "Starting connector"
            let child = Process()
            guard let managerExecutable = Bundle.main.executableURL else {
                throw ManagerError.invalid("Manager executable location is unavailable.")
            }
            let launcher = managerExecutable.deletingLastPathComponent().appendingPathComponent("CodexProSafeLauncher")
            guard FileManager.default.isExecutableFile(atPath: launcher.path) else {
                throw ManagerError.invalid("Trusted process launcher is missing beside the Manager.")
            }
            child.executableURL = launcher
            child.arguments = [checked.nodePath] + checked.launcherArguments()
            var environment = ProcessInfo.processInfo.environment
            if !token.isEmpty { environment["CODEXPRO_HTTP_TOKEN"] = token }
            environment["NO_COLOR"] = "1"
            child.environment = environment
            child.currentDirectoryURL = URL(fileURLWithPath: checked.repository)
            let output = Pipe()
            child.standardOutput = output
            child.standardError = output
            output.fileHandleForReading.readabilityHandler = { [weak self] handle in
                let text = String(decoding: handle.availableData, as: UTF8.self)
                Task { @MainActor in self?.append(text) }
            }
            child.terminationHandler = { [weak self] terminated in
                let code = terminated.terminationStatus
                Task { @MainActor in self?.didExit(code: code) }
            }
            try child.run()
            guard verifyProcessGroup(child.processIdentifier) else {
                child.terminate()
                throw ManagerError.invalid("Could not isolate the connector process group.")
            }
            process = child
            healthTask = Task { await verifyHealth(checked.localHealthURL, token: token) }
        } catch {
            process = nil
            state = .degraded
            status = error.localizedDescription
        }
    }

    func stop() {
        guard let child = process else { state = .stopped; status = "Stopped"; return }
        guard child.isRunning, getpgid(child.processIdentifier) == child.processIdentifier else {
            healthTask?.cancel()
            healthTask = nil
            process = nil
            state = .degraded
            status = "Process-group ownership could not be verified; no signal was sent"
            return
        }
        intentionalStop = true
        state = .stopping
        status = "Stopping services"
        kill(-child.processIdentifier, SIGTERM)
        Task {
            try? await Task.sleep(for: .seconds(2))
            if child.isRunning, getpgid(child.processIdentifier) == child.processIdentifier {
                kill(-child.processIdentifier, SIGKILL)
            }
        }
    }

    func restart() {
        stop()
        Task { try? await Task.sleep(for: .seconds(3)); start() }
    }

    func saveToken(_ token: String) {
        do { try KeychainTokenStore.save(token); status = "Token saved in Keychain" }
        catch { status = "Keychain error: \(error.localizedDescription)" }
    }

    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            launchAtLogin = enabled
        } catch {
            launchAtLogin = SMAppService.mainApp.status == .enabled
            status = "Login item requires an installed app bundle"
        }
    }

    func quit() {
        guard let child = process, child.isRunning,
              getpgid(child.processIdentifier) == child.processIdentifier else {
            NSApplication.shared.terminate(nil)
            return
        }
        intentionalStop = true
        kill(-child.processIdentifier, SIGTERM)
        for _ in 0..<100 where child.isRunning { usleep(10_000) }
        if child.isRunning, getpgid(child.processIdentifier) == child.processIdentifier {
            kill(-child.processIdentifier, SIGKILL)
        }
        NSApplication.shared.terminate(nil)
    }

    private func verifyHealth(_ url: URL, token: String) async {
        for _ in 0..<60 {
            if Task.isCancelled { return }
            var request = URLRequest(url: url, timeoutInterval: 1)
            if !token.isEmpty { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
            if let (_, response) = try? await URLSession.shared.data(for: request),
               let http = response as? HTTPURLResponse, http.statusCode == 200 {
                state = .ready
                status = token.isEmpty ? "Connector ready" : "Connector ready and authenticated"
                return
            }
            try? await Task.sleep(for: .milliseconds(250))
        }
        state = .degraded
        status = "Connector health verification timed out"
    }

    private func append(_ text: String) {
        let sanitized = text.split(whereSeparator: \.isNewline).map { LogSanitizer.sanitize(String($0)) }
        if let last = sanitized.last { status = last }
    }

    private func didExit(code: Int32) {
        healthTask?.cancel()
        healthTask = nil
        process = nil
        if intentionalStop { state = .stopped; status = "Stopped"; return }
        state = .degraded
        status = "Connector exited (\(code))"
        if settings.restartOnFailure { Task { try? await Task.sleep(for: .seconds(2)); start() } }
    }

    private func verifyProcessGroup(_ processIdentifier: Int32) -> Bool {
        for _ in 0..<100 {
            if getpgid(processIdentifier) == processIdentifier { return true }
            if kill(processIdentifier, 0) != 0 { return false }
            usleep(1_000)
        }
        return false
    }
}

enum KeychainTokenStore {
    private static let service = "com.prometheusprophet.codexpro-safe-manager"
    static func read() throws -> String {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: service,
                                    kSecAttrAccount as String: "http-token",
                                    kSecReturnData as String: true,
                                    kSecMatchLimit as String: kSecMatchLimitOne]
        var item: CFTypeRef?
        let result = SecItemCopyMatching(query as CFDictionary, &item)
        if result == errSecItemNotFound { return "" }
        guard result == errSecSuccess, let data = item as? Data else { throw ManagerError.invalid("Keychain read failed.") }
        return String(decoding: data, as: UTF8.self)
    }
    static func save(_ token: String) throws {
        let base: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                   kSecAttrService as String: service,
                                   kSecAttrAccount as String: "http-token"]
        if token.isEmpty { SecItemDelete(base as CFDictionary); return }
        let data = Data(token.utf8)
        let update = SecItemUpdate(base as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if update == errSecSuccess { return }
        guard update == errSecItemNotFound else { throw ManagerError.invalid("Keychain update failed.") }
        var item = base
        item[kSecValueData as String] = data
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        guard SecItemAdd(item as CFDictionary, nil) == errSecSuccess else { throw ManagerError.invalid("Keychain write failed.") }
    }
}

struct ManagerMenu: View {
    @EnvironmentObject var model: ManagerModel
    var body: some View {
        Text(model.status).font(.headline)
        Divider()
        Button("Start All") { model.start() }.disabled(model.state == .starting || model.state == .ready)
        Button("Restart All") { model.restart() }.disabled(model.state == .stopped)
        Button("Stop All") { model.stop() }.disabled(model.state == .stopped)
        SettingsLink { Text("Settings…") }
        Divider()
        Button("Quit") { model.quit() }
    }
}

struct SettingsView: View {
    @EnvironmentObject var model: ManagerModel
    @State private var token = ""
    var body: some View {
        Form {
            TextField("Repository", text: $model.settings.repository)
            TextField("Workspace root", text: $model.settings.workspaceRoot)
            TextField("Allowed root", text: $model.settings.allowedRoot)
            TextField("Node.js", text: $model.settings.nodePath)
            TextField("Port", value: $model.settings.port, format: .number)
            Picker("Access", selection: $model.settings.accessProfile) {
                ForEach(AccessProfile.allCases, id: \.self) { Text($0.rawValue.capitalized) }
            }
            LabeledContent("Tunnel", value: "Local only (verified)")
            SecureField("Bearer token", text: $token)
            Toggle("Restart after unexpected exit", isOn: $model.settings.restartOnFailure)
            Toggle("Launch at login", isOn: Binding(get: { model.launchAtLogin }, set: { model.setLaunchAtLogin($0) }))
            HStack {
                Button("Save Settings") { model.save() }
                Button("Save Token") { model.saveToken(token); token = "" }
            }
            Text("Codex diagnostics remain off until native trust proof is implemented.").foregroundStyle(.secondary)
            Text(model.status).foregroundStyle(.secondary)
        }
        .padding(20)
        .frame(width: 620)
    }
}

enum LoginItemCommand {
    static func runIfRequested() -> Never? {
        let arguments = Set(CommandLine.arguments.dropFirst())
        do {
            if arguments.contains("--login-item-status") {
                print(statusName(SMAppService.mainApp.status))
                exit(0)
            }
            if arguments.contains("--login-item-register") {
                try SMAppService.mainApp.register()
                print(statusName(SMAppService.mainApp.status))
                exit(0)
            }
            if arguments.contains("--login-item-unregister") {
                try SMAppService.mainApp.unregister()
                print(statusName(SMAppService.mainApp.status))
                exit(0)
            }
        } catch {
            FileHandle.standardError.write(Data("Login-item operation failed: \(error.localizedDescription)\n".utf8))
            exit(1)
        }
        return nil
    }

    private static func statusName(_ status: SMAppService.Status) -> String {
        switch status {
        case .notRegistered: return "notRegistered"
        case .enabled: return "enabled"
        case .requiresApproval: return "requiresApproval"
        case .notFound: return "notFound"
        @unknown default: return "unknown"
        }
    }
}

@main
struct CodexProSafeManagerApp: App {
    @StateObject private var model: ManagerModel

    init() {
        _ = LoginItemCommand.runIfRequested()
        _model = StateObject(wrappedValue: ManagerModel())
    }
    var body: some Scene {
        MenuBarExtra("CodexPro-Safe Manager", systemImage: model.state == .ready ? "checkmark.shield.fill" : "shield") {
            ManagerMenu().environmentObject(model)
        }
        Settings { SettingsView().environmentObject(model) }
    }
}
