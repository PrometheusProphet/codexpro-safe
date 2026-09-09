import Foundation

public enum AccessProfile: String, Codable, CaseIterable, Sendable {
    case planning, edit, develop, full

    public var launchArguments: [String] {
        switch self {
        case .planning: return ["--mode", "handoff", "--write", "handoff", "--bash", "off"]
        case .edit: return ["--mode", "agent", "--write", "repository", "--bash", "off"]
        case .develop: return ["--mode", "agent", "--write", "repository", "--bash", "safe"]
        case .full: return ["--mode", "agent", "--write", "repository", "--bash", "full"]
        }
    }
}

public enum TunnelMode: String, Codable, CaseIterable, Sendable { case none }

public struct ManagerSettings: Codable, Equatable, Sendable {
    public var repository: String
    public var workspaceRoot: String
    public var allowedRoot: String
    public var nodePath: String
    public var port: Int
    public var accessProfile: AccessProfile
    public var tunnelMode: TunnelMode
    public var hostname: String
    public var restartOnFailure: Bool
    public var autoStartServices: Bool

    public init(repository: String, workspaceRoot: String, allowedRoot: String, nodePath: String,
                port: Int = 8787, accessProfile: AccessProfile = .planning,
                tunnelMode: TunnelMode = .none, hostname: String = "", restartOnFailure: Bool = false,
                autoStartServices: Bool = false) {
        self.repository = repository; self.workspaceRoot = workspaceRoot; self.allowedRoot = allowedRoot
        self.nodePath = nodePath; self.port = port; self.accessProfile = accessProfile
        self.tunnelMode = tunnelMode; self.hostname = hostname; self.restartOnFailure = restartOnFailure
        self.autoStartServices = autoStartServices
    }

    private enum CodingKeys: String, CodingKey {
        case repository, workspaceRoot, allowedRoot, nodePath, port, accessProfile
        case tunnelMode, hostname, restartOnFailure, autoStartServices
    }

    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        repository = try values.decode(String.self, forKey: .repository)
        workspaceRoot = try values.decode(String.self, forKey: .workspaceRoot)
        allowedRoot = try values.decode(String.self, forKey: .allowedRoot)
        nodePath = try values.decode(String.self, forKey: .nodePath)
        port = try values.decode(Int.self, forKey: .port)
        accessProfile = try values.decode(AccessProfile.self, forKey: .accessProfile)
        tunnelMode = try values.decode(TunnelMode.self, forKey: .tunnelMode)
        hostname = try values.decode(String.self, forKey: .hostname)
        restartOnFailure = try values.decode(Bool.self, forKey: .restartOnFailure)
        autoStartServices = try values.decodeIfPresent(Bool.self, forKey: .autoStartServices) ?? false
    }

    public static func defaults(repository: String, nodePath: String) -> ManagerSettings {
        ManagerSettings(repository: repository, workspaceRoot: repository,
                        allowedRoot: URL(fileURLWithPath: repository).deletingLastPathComponent().path,
                        nodePath: nodePath)
    }

    public func validated(fileManager: FileManager = .default) throws -> ManagerSettings {
        let repository = try canonicalDirectory(self.repository, label: "Repository", fileManager: fileManager)
        let workspace = try canonicalDirectory(workspaceRoot, label: "Workspace root", fileManager: fileManager)
        let allowed = try canonicalDirectory(allowedRoot, label: "Allowed root", fileManager: fileManager)
        let node = URL(fileURLWithPath: NSString(string: nodePath).expandingTildeInPath).standardizedFileURL.path
        guard fileManager.isExecutableFile(atPath: node) else { throw ManagerError.invalid("Node.js is not executable.") }
        guard contains(workspace, within: allowed) else { throw ManagerError.invalid("Workspace root must be inside Allowed root.") }
        guard (1...65535).contains(port) else { throw ManagerError.invalid("Port must be between 1 and 65535.") }
        let launcher = URL(fileURLWithPath: repository).appendingPathComponent("scripts/codexpro.mjs").path
        guard fileManager.isReadableFile(atPath: launcher) else { throw ManagerError.invalid("Repository does not contain scripts/codexpro.mjs.") }
        var copy = self
        copy.repository = repository; copy.workspaceRoot = workspace; copy.allowedRoot = allowed; copy.nodePath = node
        copy.hostname = hostname.trimmingCharacters(in: .whitespacesAndNewlines)
        return copy
    }

    public func launcherArguments() -> [String] {
        var result = [URL(fileURLWithPath: repository).appendingPathComponent("scripts/codexpro.mjs").path,
                      "start", "--root", workspaceRoot, "--allow-root", allowedRoot,
                      "--port", String(port), "--tunnel", tunnelMode.rawValue,
                      "--no-copy-url", "--codex-diagnostic-read", "off"]
        result.append(contentsOf: accessProfile.launchArguments)
        return result
    }

    public var localHealthURL: URL { URL(string: "http://127.0.0.1:\(port)/healthz")! }
}

public enum ManagerError: LocalizedError, Equatable {
    case invalid(String)
    public var errorDescription: String? { if case let .invalid(message) = self { return message }; return nil }
}

public func contains(_ child: String, within parent: String) -> Bool {
    let childParts = URL(fileURLWithPath: child).standardizedFileURL.pathComponents
    let parentParts = URL(fileURLWithPath: parent).standardizedFileURL.pathComponents
    return childParts.count >= parentParts.count && Array(childParts.prefix(parentParts.count)) == parentParts
}

private func canonicalDirectory(_ value: String, label: String, fileManager: FileManager) throws -> String {
    var isDirectory: ObjCBool = false
    let expanded = NSString(string: value).expandingTildeInPath
    guard fileManager.fileExists(atPath: expanded, isDirectory: &isDirectory), isDirectory.boolValue else {
        throw ManagerError.invalid("\(label) does not exist or is not a directory.")
    }
    return URL(fileURLWithPath: expanded).resolvingSymlinksInPath().standardizedFileURL.path
}

public enum LogSanitizer {
    public static func sanitize(_ input: String) -> String {
        var value = input
        let patterns = [#"(?i)(authorization\s*[:=]\s*bearer\s+)[^\s]+"#,
                        #"(?i)((?:api[_-]?key|token|secret)\s*[:=]\s*)[^\s]+"#,
                        #"https?://[^\s/?#]+(?::\d+)?/[^\s]*[?&][^\s]+"#,
                        #"/(?:Users|private|Volumes)/[^\s]+"#]
        for pattern in patterns {
            guard let regex = try? NSRegularExpression(pattern: pattern) else { continue }
            value = regex.stringByReplacingMatches(in: value, range: NSRange(value.startIndex..., in: value), withTemplate: "<redacted>")
        }
        return String(value.prefix(4_096))
    }
}

public actor SettingsFileStore {
    private let url: URL
    public init(url: URL) { self.url = url }
    public func load(defaults: ManagerSettings) -> ManagerSettings {
        Self.loadSynchronously(url: url, defaults: defaults)
    }
    public func save(_ settings: ManagerSettings) throws {
        try Self.saveSynchronously(settings, url: url)
    }

    public static func loadSynchronously(url: URL, defaults: ManagerSettings) -> ManagerSettings {
        guard let data = try? Data(contentsOf: url),
              let value = try? JSONDecoder().decode(ManagerSettings.self, from: data) else { return defaults }
        return value
    }

    public static func saveSynchronously(_ settings: ManagerSettings, url: URL) throws {
        let directory = url.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
        try JSONEncoder().encode(settings).write(to: url, options: [.atomic])
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
}
