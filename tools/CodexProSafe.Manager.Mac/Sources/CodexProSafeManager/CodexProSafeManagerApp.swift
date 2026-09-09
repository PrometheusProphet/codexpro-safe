import SwiftUI
import AppKit
import Security
import ServiceManagement
import Darwin
import ManagerCore

enum ServiceState { case stopped, starting, ready, degraded, stopping }

@MainActor
final class ManagerModel: NSObject, ObservableObject {
    @Published var settings: ManagerSettings
    @Published var state: ServiceState = .stopped
    @Published var status = "Stopped"
    @Published var launchAtLogin = false
    @Published var pendingTakeover: ExternalConnectorPlan?
    @Published var showingTakeoverConfirmation = false
    @Published var diagnosticHelperState = "unavailable"
    private var connectorProcess: Process?
    private var tunnelProcess: Process?
    private var healthTask: Task<Void, Never>?
    private var monitorTask: Task<Void, Never>?
    private var intentionalStop = false
    private var restartRequested = false
    private var suppressNextTunnelRecovery = false
    private var wakeObserver: NSObjectProtocol?
    private var terminationObserver: NSObjectProtocol?
    private let store: SettingsFileStore

    override init() {
        let environment = ProcessInfo.processInfo.environment
        let repository = environment["CODEXPRO_MANAGER_REPOSITORY"] ?? FileManager.default.currentDirectoryPath
        let node = environment["CODEXPRO_MANAGER_NODE"] ??
            ["/opt/homebrew/bin/node", "/usr/local/bin/node", "/usr/bin/node"]
                .first(where: FileManager.default.isExecutableFile(atPath:)) ?? "/usr/local/bin/node"
        let defaults = ManagerSettings.defaults(repository: repository, nodePath: node)
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let settingsURL = environment["CODEXPRO_MANAGER_SETTINGS"].map { URL(fileURLWithPath: $0) }
            ?? base.appendingPathComponent("CodexProSafe Manager/settings.json")
        store = SettingsFileStore(url: settingsURL)
        settings = SettingsFileStore.loadSynchronously(url: settingsURL, defaults: defaults)
        launchAtLogin = SMAppService.mainApp.status == .enabled
        super.init()
        if let executable = Bundle.main.executableURL,
           (try? DiagnosticHelperTrust.verify(appExecutableURL: executable)) != nil {
            diagnosticHelperState = "sealed (off by default)"
        }
        wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didWakeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.recoverAfterWake() }
        }
        terminationObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.stopManagedProcessesForTermination() }
        }
        if (try? settings.validated()) == nil {
            status = "Setup required: choose the CodexPro-Safe repository and workspace in Settings"
        } else if settings.autoStartServices {
            Task { @MainActor [weak self] in
                await Task.yield()
                self?.start()
            }
        }
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
        guard connectorProcess == nil, tunnelProcess == nil else { return }
        do {
            let checked = try settings.validated()
            let token = try KeychainTokenStore.read(account: .httpToken)
            if try ExternalConnectorInspector.loopbackListenerIsPresent(port: checked.port) {
                throw ManagerError.invalid("The configured loopback port is already in use. Use Take Over Existing; mismatched processes will be refused.")
            }
            if checked.tunnelMode == .openAI,
               try ExternalConnectorInspector.loopbackListenerIsPresent(port: checked.tunnelHealthPort) {
                throw ManagerError.invalid("The configured tunnel health port is already in use. No external tunnel was changed.")
            }
            settings = checked
            intentionalStop = false
            restartRequested = false
            state = .starting
            status = "Starting connector"
            var environment = ProcessInfo.processInfo.environment
            if !token.isEmpty { environment["CODEXPRO_HTTP_TOKEN"] = token }
            environment["NO_COLOR"] = "1"
            connectorProcess = try launchManaged(
                executable: checked.nodePath,
                arguments: checked.launcherArguments(),
                directory: checked.repository,
                environment: environment,
                captureOutput: true,
                exit: { [weak self] code in self?.connectorDidExit(code: code) }
            )
            healthTask = Task { await verifyConnectorHealth(checked, token: token) }
        } catch {
            connectorProcess = nil
            state = .degraded
            status = error.localizedDescription
        }
    }

    func stop() {
        guard connectorProcess != nil || tunnelProcess != nil else { state = .stopped; status = "Stopped"; return }
        let running = [tunnelProcess, connectorProcess].compactMap { $0 }.filter(\.isRunning)
        guard running.allSatisfy({ getpgid($0.processIdentifier) == $0.processIdentifier }) else {
            state = .degraded
            status = "Process-group ownership could not be verified; no signal was sent"
            return
        }
        intentionalStop = true
        state = .stopping
        status = "Stopping services"
        healthTask?.cancel()
        healthTask = nil
        monitorTask?.cancel()
        monitorTask = nil
        signalOwned(tunnelProcess, signal: SIGTERM)
        signalOwned(connectorProcess, signal: SIGTERM)
        forceStopAfterGrace([tunnelProcess, connectorProcess].compactMap { $0 })
    }

    func restart() {
        restartRequested = true
        stop()
    }

    func prepareTakeover() {
        guard connectorProcess == nil, tunnelProcess == nil else { return }
        Task {
            do {
                let checked = try settings.validated()
                let token = try KeychainTokenStore.read(account: .httpToken)
                guard await healthIsReady(checked.localHealthURL, token: token) else {
                    throw ManagerError.invalid("No authenticated external connector is ready on the configured port.")
                }
                let plan = try await Task.detached {
                    try ExternalConnectorInspector.inspect(settings: checked)
                }.value
                pendingTakeover = plan
                showingTakeoverConfirmation = true
                status = "Exact external connector verified"
            } catch {
                pendingTakeover = nil
                showingTakeoverConfirmation = false
                state = .degraded
                status = error.localizedDescription
            }
        }
    }

    func confirmTakeover() {
        guard let plan = pendingTakeover else { return }
        pendingTakeover = nil
        showingTakeoverConfirmation = false
        state = .stopping
        status = "Stopping exact verified external connector"
        Task {
            do {
                let checked = try settings.validated()
                let token = try KeychainTokenStore.read(account: .httpToken)
                try await ExternalConnectorInspector.stop(plan: plan, settings: checked)
                guard await waitForHealthToStop(checked.localHealthURL, token: token) else {
                    throw ManagerError.invalid("External endpoint remained available after the verified process exited.")
                }
                state = .stopped
                status = "External connector stopped; starting Manager ownership"
                start()
            } catch {
                state = .degraded
                status = error.localizedDescription
            }
        }
    }

    func cancelTakeover() {
        pendingTakeover = nil
        showingTakeoverConfirmation = false
        status = "Takeover cancelled; external connector was not changed"
    }

    func saveToken(_ token: String) {
        do { try KeychainTokenStore.save(token, account: .httpToken); status = "Connector token saved in Keychain" }
        catch { status = "Keychain error: \(error.localizedDescription)" }
    }

    func saveControlPlaneKey(_ key: String) {
        do { try KeychainTokenStore.save(key, account: .controlPlaneKey); status = "OpenAI runtime key saved in Keychain" }
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

    func recoverAfterWake() {
        guard settings.autoStartServices, state != .starting, state != .stopping else { return }
        Task {
            do {
                let checked = try settings.validated()
                let token = try KeychainTokenStore.read(account: .httpToken)
                if let child = connectorProcess, child.isRunning {
                    if await healthIsReady(checked.localHealthURL, token: token) {
                        if checked.tunnelMode == .openAI {
                            guard let expectedID = TunnelReadiness.expectedTunnelID(profile: checked.tunnelProfile),
                                  await tunnelIsReady(checked, expectedTunnelID: expectedID) else {
                                status = "Secure tunnel unhealthy after wake; restarting services"
                                restart()
                                return
                            }
                            state = .ready
                            status = "Connector and authenticated secure tunnel ready after wake"
                        } else {
                            state = .ready
                            status = token.isEmpty ? "Connector ready after wake" : "Connector ready and authenticated after wake"
                        }
                    } else {
                        status = "Connector unhealthy after wake; restarting"
                        restart()
                    }
                } else {
                    state = .stopped
                    status = "Connector was not running after wake; starting"
                    start()
                }
            } catch {
                state = .degraded
                status = error.localizedDescription
            }
        }
    }

    func quit() {
        stopManagedProcessesForTermination()
        NSApplication.shared.terminate(nil)
    }

    private func stopManagedProcessesForTermination() {
        intentionalStop = true
        healthTask?.cancel()
        monitorTask?.cancel()
        let children = [tunnelProcess, connectorProcess].compactMap { $0 }
        for child in children { signalOwned(child, signal: SIGTERM) }
        for _ in 0..<100 where children.contains(where: \.isRunning) { usleep(10_000) }
        for child in children where child.isRunning {
            signalOwned(child, signal: SIGKILL)
        }
    }

    private func verifyConnectorHealth(_ checked: ManagerSettings, token: String) async {
        for _ in 0..<60 {
            if Task.isCancelled { return }
            if await healthIsReady(checked.localHealthURL, token: token) {
                if checked.tunnelMode == .openAI {
                    await startTunnel(checked)
                } else {
                    state = .ready
                    status = token.isEmpty ? "Connector ready" : "Connector ready and authenticated"
                    beginMonitoring(checked, token: token)
                }
                return
            }
            try? await Task.sleep(for: .milliseconds(250))
        }
        state = .degraded
        status = "Connector health verification timed out"
    }

    private func healthIsReady(_ url: URL, token: String) async -> Bool {
        var request = URLRequest(url: url, timeoutInterval: 1)
        if !token.isEmpty { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpShouldUsePipelining = false
        guard let (_, response) = try? await URLSession(configuration: configuration).data(for: request),
              let http = response as? HTTPURLResponse else { return false }
        return http.statusCode == 200
    }

    private func waitForHealthToStop(_ url: URL, token: String) async -> Bool {
        for _ in 0..<32 {
            if !(await healthIsReady(url, token: token)) { return true }
            try? await Task.sleep(for: .milliseconds(250))
        }
        return false
    }

    private func append(_ text: String) {
        let sanitized = text.split(whereSeparator: \.isNewline).map { LogSanitizer.sanitize(String($0)) }
        if let last = sanitized.last { status = last }
    }

    private func connectorDidExit(code: Int32) {
        healthTask?.cancel()
        healthTask = nil
        connectorProcess = nil
        if tunnelProcess != nil {
            suppressNextTunnelRecovery = true
            signalOwned(tunnelProcess, signal: SIGTERM)
        }
        if intentionalStop { finishStopIfPossible(); return }
        state = .degraded
        status = "Connector exited (\(code))"
        if settings.restartOnFailure {
            restartRequested = true
            intentionalStop = true
            finishStopIfPossible()
        }
    }

    private func tunnelDidExit(code: Int32) {
        tunnelProcess = nil
        if suppressNextTunnelRecovery {
            suppressNextTunnelRecovery = false
            if intentionalStop { finishStopIfPossible() }
            return
        }
        if intentionalStop { finishStopIfPossible(); return }
        state = .degraded
        status = "Secure tunnel exited (\(code)); local connector remains available"
        if settings.restartOnFailure, connectorProcess?.isRunning == true {
            Task { try? await Task.sleep(for: .seconds(2)); await startTunnel(settings) }
        }
    }

    private func finishStopIfPossible() {
        guard connectorProcess == nil, tunnelProcess == nil else { return }
        state = .stopped
        status = "Stopped"
        intentionalStop = false
        if restartRequested {
            restartRequested = false
            Task { try? await Task.sleep(for: .milliseconds(500)); start() }
        }
    }

    private func startTunnel(_ checked: ManagerSettings) async {
        guard tunnelProcess == nil else { return }
        do {
            guard let expectedID = TunnelReadiness.expectedTunnelID(profile: checked.tunnelProfile) else {
                throw ManagerError.invalid("The selected tunnel profile has no valid tunnel_id; local connector remains available.")
            }
            let key = try controlPlaneKey()
            guard !key.isEmpty else {
                throw ManagerError.invalid("Save an OpenAI runtime API key before starting the secure tunnel; local connector remains available.")
            }
            status = "Checking secure tunnel configuration"
            try await runTunnelDoctor(checked, key: key)
            var environment = ProcessInfo.processInfo.environment
            environment["CONTROL_PLANE_API_KEY"] = key
            environment["HEALTH_LISTEN_ADDR"] = "127.0.0.1:\(checked.tunnelHealthPort)"
            environment["ALLOW_REMOTE_UI"] = "false"
            environment["OPEN_WEB_UI"] = "false"
            environment["NO_COLOR"] = "1"
            if !checked.organizationID.isEmpty { environment["CONTROL_PLANE_ORGANIZATION_ID"] = checked.organizationID }
            status = "Starting OpenAI secure tunnel"
            tunnelProcess = try launchManaged(
                executable: checked.tunnelClientPath,
                arguments: checked.tunnelArguments,
                directory: checked.repository,
                environment: environment,
                captureOutput: false,
                exit: { [weak self] code in self?.tunnelDidExit(code: code) }
            )
            for _ in 0..<120 {
                if Task.isCancelled { return }
                if await tunnelIsReady(checked, expectedTunnelID: expectedID) {
                    state = .ready
                    status = "Connector and authenticated OpenAI secure tunnel ready"
                    let token = try KeychainTokenStore.read(account: .httpToken)
                    beginMonitoring(checked, token: token)
                    return
                }
                try? await Task.sleep(for: .milliseconds(250))
            }
            suppressNextTunnelRecovery = true
            signalOwned(tunnelProcess, signal: SIGTERM)
            throw ManagerError.invalid("Secure tunnel authenticated readiness timed out; local connector remains available.")
        } catch {
            state = .degraded
            status = error.localizedDescription
        }
    }

    private func runTunnelDoctor(_ checked: ManagerSettings, key: String) async throws {
        let doctor = Process()
        doctor.executableURL = URL(fileURLWithPath: checked.tunnelClientPath)
        doctor.arguments = ["doctor", "--profile", checked.tunnelProfile, "--explain", "--json"]
        var environment = ProcessInfo.processInfo.environment
        environment["CONTROL_PLANE_API_KEY"] = key
        environment["HEALTH_LISTEN_ADDR"] = "127.0.0.1:\(checked.tunnelHealthPort)"
        environment["NO_COLOR"] = "1"
        if !checked.organizationID.isEmpty { environment["CONTROL_PLANE_ORGANIZATION_ID"] = checked.organizationID }
        doctor.environment = environment
        doctor.standardOutput = FileHandle.nullDevice
        doctor.standardError = FileHandle.nullDevice
        try doctor.run()
        for _ in 0..<120 where doctor.isRunning { try? await Task.sleep(for: .milliseconds(250)) }
        if doctor.isRunning { doctor.terminate(); throw ManagerError.invalid("Secure tunnel doctor timed out.") }
        guard doctor.terminationStatus == 0 else {
            throw ManagerError.invalid("Secure tunnel doctor failed; verify the profile, key permissions, organization, and local MCP target.")
        }
    }

    private func tunnelIsReady(_ checked: ManagerSettings, expectedTunnelID: String) async -> Bool {
        guard await healthIsReady(checked.tunnelHealthURL, token: ""),
              await healthIsReady(checked.tunnelReadyURL, token: "") else { return false }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpShouldUsePipelining = false
        var request = URLRequest(url: checked.tunnelStatusURL, timeoutInterval: 1)
        request.setValue("no-store", forHTTPHeaderField: "Cache-Control")
        guard let (data, response) = try? await URLSession(configuration: configuration).data(for: request),
              let http = response as? HTTPURLResponse, http.statusCode == 200 else { return false }
        return TunnelReadiness.matches(statusData: data, expectedTunnelID: expectedTunnelID)
    }

    private func controlPlaneKey() throws -> String {
        let environment = ProcessInfo.processInfo.environment
        if environment["CI"] == "1", environment["CODEXPRO_MANAGER_SETTINGS"] != nil,
           let testKey = environment["CODEXPRO_MANAGER_TEST_CONTROL_PLANE_API_KEY"] {
            return testKey
        }
        return try KeychainTokenStore.read(account: .controlPlaneKey)
    }

    private func launchManaged(executable: String, arguments: [String], directory: String,
                               environment: [String: String], captureOutput: Bool,
                               exit: @escaping @MainActor (Int32) -> Void) throws -> Process {
        guard let managerExecutable = Bundle.main.executableURL else {
            throw ManagerError.invalid("Manager executable location is unavailable.")
        }
        let launcher = managerExecutable.deletingLastPathComponent().appendingPathComponent("CodexProSafeLauncher")
        guard FileManager.default.isExecutableFile(atPath: launcher.path) else {
            throw ManagerError.invalid("Trusted process launcher is missing beside the Manager.")
        }
        let child = Process()
        child.executableURL = launcher
        child.arguments = [executable] + arguments
        child.environment = environment
        child.currentDirectoryURL = URL(fileURLWithPath: directory)
        if captureOutput {
            let output = Pipe()
            child.standardOutput = output
            child.standardError = output
            output.fileHandleForReading.readabilityHandler = { [weak self] handle in
                let text = String(decoding: handle.availableData, as: UTF8.self)
                Task { @MainActor in self?.append(text) }
            }
        } else {
            child.standardOutput = FileHandle.nullDevice
            child.standardError = FileHandle.nullDevice
        }
        child.terminationHandler = { terminated in
            let code = terminated.terminationStatus
            Task { @MainActor in exit(code) }
        }
        try child.run()
        guard verifyProcessGroup(child.processIdentifier) else {
            child.terminate()
            throw ManagerError.invalid("Could not isolate a managed process group.")
        }
        return child
    }

    private func beginMonitoring(_ checked: ManagerSettings, token: String) {
        monitorTask?.cancel()
        monitorTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(5))
                guard !Task.isCancelled, let self else { return }
                guard await self.healthIsReady(checked.localHealthURL, token: token) else {
                    self.state = .degraded
                    self.status = "Connector lost authenticated health"
                    if checked.restartOnFailure { self.restart() }
                    return
                }
                if checked.tunnelMode == .openAI {
                    guard let expectedID = TunnelReadiness.expectedTunnelID(profile: checked.tunnelProfile),
                          await self.tunnelIsReady(checked, expectedTunnelID: expectedID) else {
                        self.state = .degraded
                        self.status = "Secure tunnel lost authenticated readiness; local connector remains available"
                        if checked.restartOnFailure { self.signalOwned(self.tunnelProcess, signal: SIGTERM) }
                        return
                    }
                }
            }
        }
    }

    private func signalOwned(_ child: Process?, signal: Int32) {
        guard let child, child.isRunning, getpgid(child.processIdentifier) == child.processIdentifier else { return }
        kill(-child.processIdentifier, signal)
    }

    private func forceStopAfterGrace(_ children: [Process]) {
        Task {
            try? await Task.sleep(for: .seconds(2))
            for child in children where child.isRunning { signalOwned(child, signal: SIGKILL) }
        }
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

enum KeychainAccount: String { case httpToken = "http-token", controlPlaneKey = "openai-control-plane-key" }

enum KeychainTokenStore {
    private static let service = "com.prometheusprophet.codexpro-safe-manager"
    static func read(account: KeychainAccount) throws -> String {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: service,
                                    kSecAttrAccount as String: account.rawValue,
                                    kSecReturnData as String: true,
                                    kSecMatchLimit as String: kSecMatchLimitOne]
        var item: CFTypeRef?
        let result = SecItemCopyMatching(query as CFDictionary, &item)
        if result == errSecItemNotFound { return "" }
        guard result == errSecSuccess, let data = item as? Data else { throw ManagerError.invalid("Keychain read failed.") }
        return String(decoding: data, as: UTF8.self)
    }
    static func save(_ token: String, account: KeychainAccount) throws {
        let base: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                   kSecAttrService as String: service,
                                   kSecAttrAccount as String: account.rawValue]
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
            .accessibilityLabel("Service status")
            .accessibilityValue(model.status)
            .accessibilityIdentifier("service-status")
        Divider()
        Button("Start All") { model.start() }.disabled(model.state == .starting || model.state == .ready)
            .accessibilityIdentifier("start-all")
        Button("Restart All") { model.restart() }.disabled(model.state == .stopped)
            .accessibilityIdentifier("restart-all")
        Button("Stop All") { model.stop() }.disabled(model.state == .stopped)
            .accessibilityIdentifier("stop-all")
        Button("Take Over Existing…") { model.prepareTakeover() }
            .disabled(model.state == .starting || model.state == .ready || model.state == .stopping)
            .confirmationDialog(
                "Take control of existing CodexPro-Safe connector?",
                isPresented: $model.showingTakeoverConfirmation,
                titleVisibility: .visible
            ) {
                Button("Stop Verified Process and Restart", role: .destructive) { model.confirmTakeover() }
                Button("Cancel", role: .cancel) { model.cancelTakeover() }
            } message: {
                if let plan = model.pendingTakeover {
                    Text("The listener PID \(plan.listener.pid) and owner PID \(plan.owner.pid) exactly match the saved executable, roots, access profile, arguments, and isolated process group. Identity is checked again before any signal is sent.")
                }
            }
        SettingsLink { Text("Settings…") }
        Divider()
        Button("Quit") { model.quit() }
    }
}

struct SettingsView: View {
    @EnvironmentObject var model: ManagerModel
    @State private var token = ""
    @State private var controlPlaneKey = ""
    var body: some View {
        Form {
            directoryField("Repository", path: $model.settings.repository) { selected in
                let previousRepository = model.settings.repository
                model.settings.repository = selected
                if model.settings.workspaceRoot == previousRepository { model.settings.workspaceRoot = selected }
                if model.settings.allowedRoot == URL(fileURLWithPath: previousRepository).deletingLastPathComponent().path {
                    model.settings.allowedRoot = URL(fileURLWithPath: selected).deletingLastPathComponent().path
                }
            }
            directoryField("Workspace root", path: $model.settings.workspaceRoot) { model.settings.workspaceRoot = $0 }
            directoryField("Allowed root", path: $model.settings.allowedRoot) { model.settings.allowedRoot = $0 }
            TextField("Node.js", text: $model.settings.nodePath)
            TextField("Port", value: $model.settings.port, format: .number)
            Picker("Access", selection: $model.settings.accessProfile) {
                ForEach(AccessProfile.allCases, id: \.self) { Text($0.rawValue.capitalized) }
            }
            .accessibilityHint("Planning is the safest default; broader profiles are explicit.")
            Picker("Tunnel", selection: $model.settings.tunnelMode) {
                Text("Local only").tag(TunnelMode.none)
                Text("OpenAI Secure MCP Tunnel").tag(TunnelMode.openAI)
            }
            .accessibilityHint("Local only is the default. Secure tunnel is outbound and requires separate credentials.")
            if model.settings.tunnelMode == .openAI {
                fileField("Tunnel client", path: $model.settings.tunnelClientPath)
                TextField("Tunnel profile", text: $model.settings.tunnelProfile)
                TextField("Tunnel health port", value: $model.settings.tunnelHealthPort, format: .number)
                TextField("Organization ID (optional)", text: $model.settings.organizationID)
                SecureField("OpenAI runtime API key", text: $controlPlaneKey)
                Button("Save OpenAI Runtime Key") {
                    model.saveControlPlaneKey(controlPlaneKey)
                    controlPlaneKey = ""
                }
            }
            SecureField("Connector bearer token", text: $token)
            Toggle("Restart after unexpected exit", isOn: $model.settings.restartOnFailure)
            Toggle("Start connector when Manager opens", isOn: $model.settings.autoStartServices)
            Toggle("Launch Manager at login", isOn: Binding(get: { model.launchAtLogin }, set: { model.setLaunchAtLogin($0) }))
            HStack {
                Button("Save Settings") { model.save() }
                Button("Save Connector Token") { model.saveToken(token); token = "" }
            }
            Text("Secure tunneling is outbound-only and opt-in. Readiness requires exact profile identity plus an authenticated healthy main channel.").foregroundStyle(.secondary)
            LabeledContent("Native diagnostic helper", value: model.diagnosticHelperState)
            Text("Codex diagnostics remain off until authenticated Manager-to-connector launch proof is enabled.").foregroundStyle(.secondary)
            Text(model.status).foregroundStyle(.secondary)
                .accessibilityLabel("Settings status")
                .accessibilityValue(model.status)
                .accessibilityIdentifier("settings-status")
        }
        .padding(20)
        .frame(width: 620)
    }

    private func directoryField(_ label: String, path: Binding<String>, onSelect: @escaping (String) -> Void) -> some View {
        HStack {
            TextField(label, text: path)
            Button("Choose…") {
                let panel = NSOpenPanel()
                panel.title = "Choose \(label)"
                panel.canChooseDirectories = true
                panel.canChooseFiles = false
                panel.allowsMultipleSelection = false
                if FileManager.default.fileExists(atPath: path.wrappedValue) {
                    panel.directoryURL = URL(fileURLWithPath: path.wrappedValue)
                }
                if panel.runModal() == .OK, let selected = panel.url {
                    onSelect(selected.resolvingSymlinksInPath().standardizedFileURL.path)
                }
            }
        }
    }

    private func fileField(_ label: String, path: Binding<String>) -> some View {
        HStack {
            TextField(label, text: path)
            Button("Choose…") {
                let panel = NSOpenPanel()
                panel.title = "Choose \(label)"
                panel.canChooseDirectories = false
                panel.canChooseFiles = true
                panel.allowsMultipleSelection = false
                if panel.runModal() == .OK, let selected = panel.url {
                    path.wrappedValue = selected.resolvingSymlinksInPath().standardizedFileURL.path
                }
            }
        }
    }
}

enum ManagerCommand {
    static func runIfRequested() -> Never? {
        let orderedArguments = Array(CommandLine.arguments.dropFirst())
        let arguments = Set(orderedArguments)
        do {
            if orderedArguments.count == 3, orderedArguments[0] == "--initialize-local-settings" {
                let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
                let settingsURL = base.appendingPathComponent("CodexProSafe Manager/settings.json")
                if FileManager.default.fileExists(atPath: settingsURL.path) {
                    print("existing settings preserved")
                    exit(0)
                }
                let settings = try ManagerSettings.defaults(
                    repository: orderedArguments[1],
                    nodePath: orderedArguments[2]
                ).validated()
                try SettingsFileStore.saveSynchronously(settings, url: settingsURL)
                print("planning-profile settings initialized")
                exit(0)
            }
            if arguments.contains("--login-item-status") {
                print(statusName(SMAppService.mainApp.status))
                exit(0)
            }
            if arguments.contains("--diagnostic-helper-status") {
                guard let executable = Bundle.main.executableURL else { print("unavailable"); exit(1) }
                do {
                    _ = try DiagnosticHelperTrust.verify(appExecutableURL: executable)
                    print("sealed")
                    exit(0)
                } catch {
                    print("unavailable")
                    exit(1)
                }
            }
            if arguments.contains("--control-plane-key-status") {
                do {
                    print(try KeychainTokenStore.read(account: .controlPlaneKey).isEmpty ? "absent" : "configured")
                    exit(0)
                } catch {
                    print("unavailable")
                    exit(1)
                }
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
        _ = ManagerCommand.runIfRequested()
        _model = StateObject(wrappedValue: ManagerModel())
    }
    var body: some Scene {
        MenuBarExtra("CodexPro-Safe Manager", systemImage: model.state == .ready ? "checkmark.shield.fill" : "shield") {
            ManagerMenu().environmentObject(model)
        }
        Settings { SettingsView().environmentObject(model) }
    }
}
