import Foundation
import CryptoKit
import Darwin

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

public enum TunnelMode: String, Codable, CaseIterable, Sendable {
    case none
    case openAI = "openai-secure"
}

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
    public var tunnelClientPath: String
    public var tunnelProfile: String
    public var tunnelHealthPort: Int
    public var organizationID: String

    public init(repository: String, workspaceRoot: String, allowedRoot: String, nodePath: String,
                port: Int = 8787, accessProfile: AccessProfile = .planning,
                tunnelMode: TunnelMode = .none, hostname: String = "", restartOnFailure: Bool = false,
                autoStartServices: Bool = false, tunnelClientPath: String = "",
                tunnelProfile: String = "codexpro-safe-local", tunnelHealthPort: Int = 8080,
                organizationID: String = "") {
        self.repository = repository; self.workspaceRoot = workspaceRoot; self.allowedRoot = allowedRoot
        self.nodePath = nodePath; self.port = port; self.accessProfile = accessProfile
        self.tunnelMode = tunnelMode; self.hostname = hostname; self.restartOnFailure = restartOnFailure
        self.autoStartServices = autoStartServices
        self.tunnelClientPath = tunnelClientPath; self.tunnelProfile = tunnelProfile
        self.tunnelHealthPort = tunnelHealthPort; self.organizationID = organizationID
    }

    private enum CodingKeys: String, CodingKey {
        case repository, workspaceRoot, allowedRoot, nodePath, port, accessProfile
        case tunnelMode, hostname, restartOnFailure, autoStartServices
        case tunnelClientPath, tunnelProfile, tunnelHealthPort, organizationID
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
        tunnelClientPath = try values.decodeIfPresent(String.self, forKey: .tunnelClientPath) ?? ""
        tunnelProfile = try values.decodeIfPresent(String.self, forKey: .tunnelProfile) ?? "codexpro-safe-local"
        tunnelHealthPort = try values.decodeIfPresent(Int.self, forKey: .tunnelHealthPort) ?? 8080
        organizationID = try values.decodeIfPresent(String.self, forKey: .organizationID) ?? ""
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
        copy.tunnelClientPath = NSString(string: tunnelClientPath).expandingTildeInPath
        copy.tunnelProfile = tunnelProfile.trimmingCharacters(in: .whitespacesAndNewlines)
        copy.organizationID = organizationID.trimmingCharacters(in: .whitespacesAndNewlines)
        if copy.tunnelMode == .openAI {
            guard fileManager.isExecutableFile(atPath: copy.tunnelClientPath) else {
                throw ManagerError.invalid("OpenAI tunnel-client is not executable.")
            }
            guard copy.tunnelProfile.range(of: #"^[A-Za-z0-9_.-]{1,100}$"#, options: .regularExpression) != nil else {
                throw ManagerError.invalid("Tunnel profile must use 1–100 letters, numbers, dots, underscores, or hyphens.")
            }
            guard (1...65535).contains(copy.tunnelHealthPort), copy.tunnelHealthPort != copy.port else {
                throw ManagerError.invalid("Tunnel health port must be valid and different from the connector port.")
            }
            if !copy.organizationID.isEmpty,
               copy.organizationID.range(of: #"^org-[A-Za-z0-9_-]+$"#, options: .regularExpression) == nil {
                throw ManagerError.invalid("Organization ID must be empty or start with org-.")
            }
        }
        return copy
    }

    public func launcherArguments() -> [String] {
        var result = [URL(fileURLWithPath: repository).appendingPathComponent("scripts/codexpro.mjs").path,
                      "start", "--root", workspaceRoot, "--allow-root", allowedRoot,
                      "--port", String(port), "--tunnel", TunnelMode.none.rawValue,
                      "--no-copy-url", "--codex-diagnostic-read", "off"]
        result.append(contentsOf: accessProfile.launchArguments)
        return result
    }

    public var localHealthURL: URL { URL(string: "http://127.0.0.1:\(port)/healthz")! }
    public var tunnelHealthURL: URL { URL(string: "http://127.0.0.1:\(tunnelHealthPort)/healthz")! }
    public var tunnelReadyURL: URL { URL(string: "http://127.0.0.1:\(tunnelHealthPort)/readyz")! }
    public var tunnelStatusURL: URL { URL(string: "http://127.0.0.1:\(tunnelHealthPort)/api/status")! }
    public var tunnelArguments: [String] { ["run", "--profile", tunnelProfile] }
}

public struct TunnelStatusDocument: Decodable, Sendable {
    public struct Metadata: Decodable, Sendable { public let ID: String }
    public struct Channel: Decodable, Sendable {
        public let name: String
        public let probe_status: String?
    }
    public let control_plane_tunnel_id: String
    public let tunnel_metadata: Metadata
    public let channels: [Channel]
}

public enum TunnelReadiness {
    public static func matches(statusData: Data, expectedTunnelID: String) -> Bool {
        guard statusData.count <= 524_288,
              let status = try? JSONDecoder().decode(TunnelStatusDocument.self, from: statusData),
              !expectedTunnelID.isEmpty,
              !status.tunnel_metadata.ID.isEmpty,
              status.control_plane_tunnel_id == status.tunnel_metadata.ID,
              expectedTunnelID == status.tunnel_metadata.ID else { return false }
        return status.channels.contains { $0.name == "main" && $0.probe_status?.lowercased() == "ok" }
    }

    public static func expectedTunnelID(profile: String, environment: [String: String] = ProcessInfo.processInfo.environment,
                                        fileManager: FileManager = .default) -> String? {
        guard profile.range(of: #"^[A-Za-z0-9_.-]{1,100}$"#, options: .regularExpression) != nil else { return nil }
        let directory: String
        if let override = environment["TUNNEL_CLIENT_PROFILE_DIR"], !override.isEmpty {
            directory = NSString(string: override).expandingTildeInPath
        } else if let xdg = environment["XDG_CONFIG_HOME"], !xdg.isEmpty {
            directory = URL(fileURLWithPath: NSString(string: xdg).expandingTildeInPath)
                .appendingPathComponent("tunnel-client").path
        } else {
            directory = FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent(".config/tunnel-client").path
        }
        let url = URL(fileURLWithPath: directory).appendingPathComponent("\(profile).yaml")
        guard let attributes = try? fileManager.attributesOfItem(atPath: url.path),
              let size = attributes[.size] as? NSNumber, size.intValue <= 65_536,
              let content = try? String(contentsOf: url, encoding: .utf8),
              let regex = try? NSRegularExpression(pattern: #"(?m)^\s*tunnel_id\s*:\s*[\"']?(tunnel_[A-Za-z0-9]+)[\"']?\s*$"#),
              let match = regex.firstMatch(in: content, range: NSRange(content.startIndex..., in: content)),
              let range = Range(match.range(at: 1), in: content) else { return nil }
        return String(content[range])
    }
}

public struct DiagnosticHelperContract: Equatable, Sendable {
    public let executablePath: String
    public let protocolVersion: String
    public let sha256: String
}

public enum DiagnosticHelperTrust {
    public static let protocolVersion = "codexpro-diagnostic-v1"
    public static let executableName = "CodexProSafeDiagnosticHelper"
    public static let manifestName = "CodexProSafeDiagnosticHelper.json"

    private struct Manifest: Decodable {
        let protocolVersion: String
        let executable: String
        let sha256: String
    }

    public static func verify(appExecutableURL: URL, fileManager: FileManager = .default) throws -> DiagnosticHelperContract {
        let executable = appExecutableURL.resolvingSymlinksInPath().standardizedFileURL
        let macOSDirectory = executable.deletingLastPathComponent()
        let contentsDirectory = macOSDirectory.deletingLastPathComponent()
        let manifestURL = contentsDirectory.appendingPathComponent("Resources").appendingPathComponent(manifestName)
        guard let manifestData = try? Data(contentsOf: manifestURL), manifestData.count <= 4_096,
              let object = try? JSONSerialization.jsonObject(with: manifestData) as? [String: Any],
              Set(object.keys) == Set(["protocolVersion", "executable", "sha256"]),
              let manifest = try? JSONDecoder().decode(Manifest.self, from: manifestData),
              manifest.protocolVersion == protocolVersion,
              manifest.executable == executableName,
              manifest.sha256.range(of: #"^[a-f0-9]{64}$"#, options: .regularExpression) != nil else {
            throw ManagerError.invalid("The native diagnostic helper manifest is missing or invalid.")
        }
        let directoryFD = open(macOSDirectory.path, O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW)
        guard directoryFD >= 0 else { throw ManagerError.invalid("The native diagnostic helper directory could not be opened safely.") }
        defer { close(directoryFD) }
        let helperFD = openat(directoryFD, executableName, O_RDONLY | O_CLOEXEC | O_NOFOLLOW)
        guard helperFD >= 0 else { throw ManagerError.invalid("The native diagnostic helper could not be opened safely.") }
        defer { close(helperFD) }
        var value = stat()
        guard fstat(helperFD, &value) == 0,
              (value.st_mode & S_IFMT) == S_IFREG,
              value.st_nlink == 1,
              value.st_uid == getuid(),
              value.st_size > 0,
              value.st_size <= 64 * 1024 * 1024 else {
            throw ManagerError.invalid("The native diagnostic helper has unsafe object identity.")
        }
        let handle = FileHandle(fileDescriptor: helperFD, closeOnDealloc: false)
        try handle.seek(toOffset: 0)
        var hash = SHA256()
        var total = 0
        while let data = try handle.read(upToCount: 65_536), !data.isEmpty {
            total += data.count
            guard total <= value.st_size else { throw ManagerError.invalid("The native diagnostic helper changed while hashing.") }
            hash.update(data: data)
        }
        guard total == value.st_size else { throw ManagerError.invalid("The native diagnostic helper changed while hashing.") }
        let actual = hash.finalize().map { String(format: "%02x", $0) }.joined()
        guard actual == manifest.sha256 else { throw ManagerError.invalid("The native diagnostic helper fingerprint does not match this app.") }
        let helperPath = macOSDirectory.appendingPathComponent(executableName).path
        guard fileManager.isExecutableFile(atPath: helperPath) else { throw ManagerError.invalid("The native diagnostic helper is not executable.") }
        return DiagnosticHelperContract(executablePath: helperPath, protocolVersion: protocolVersion, sha256: actual)
    }
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
                        #"\b(?:sk|ghp|github_pat)-?[A-Za-z0-9_-]{8,}\b"#,
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
