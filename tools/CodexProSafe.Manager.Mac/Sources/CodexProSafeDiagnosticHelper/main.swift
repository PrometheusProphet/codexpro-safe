import Foundation
import Darwin

private let protocolVersion = "codexpro-diagnostic-v1"
private let requestLimit = 4 * 1024
private let responseLimit = 48 * 1024 * 1024
private let maxEntries = 512
private let maxConfigBytes = 64 * 1024
private let maxDatabaseBytes = 32 * 1024 * 1024
private let configNames = ["config.toml", "config.toml.bak", "config.toml.backup"]
private let databasePattern = try! NSRegularExpression(pattern: #"^(?:logs|state|memories|goals)_[A-Za-z0-9_-]+\.sqlite$"#)

private func fixedRoot() -> String? {
    guard let record = getpwuid(getuid()), let home = record.pointee.pw_dir else { return nil }
    return URL(fileURLWithPath: String(cString: home)).appendingPathComponent(".codex").path
}

private func safeName(_ name: String) -> Bool {
    !name.isEmpty && name != "." && name != ".." && !name.contains("/") && name.utf8.count <= 255
}

private func metadata(_ statValue: stat, name: String) -> [String: Any] {
    let modified = Date(timeIntervalSince1970: TimeInterval(statValue.st_mtimespec.tv_sec))
    let formatter = ISO8601DateFormatter()
    formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
    return [
        "name": name,
        "isDirectory": (statValue.st_mode & S_IFMT) == S_IFDIR,
        "isReparsePoint": (statValue.st_mode & S_IFMT) == S_IFLNK,
        "bytes": statValue.st_size,
        "modifiedUtc": formatter.string(from: modified)
    ]
}

private func entries(rootFD: Int32) throws -> [(String, stat)] {
    guard let directory = fdopendir(dup(rootFD)) else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
    defer { closedir(directory) }
    var result: [(String, stat)] = []
    while let entry = readdir(directory) {
        let name = withUnsafePointer(to: &entry.pointee.d_name) {
            $0.withMemoryRebound(to: CChar.self, capacity: Int(MAXNAMLEN) + 1) { String(cString: $0) }
        }
        guard safeName(name) else { continue }
        var value = stat()
        guard fstatat(rootFD, name, &value, AT_SYMLINK_NOFOLLOW) == 0 else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
        result.append((name, value))
        guard result.count <= maxEntries else { throw NSError(domain: "CodexProSafeDiagnostic", code: 1) }
    }
    return result.sorted { $0.0 < $1.0 }
}

private func readRegular(rootFD: Int32, name: String, maximum: Int) throws -> ([String: Any], Data)? {
    guard safeName(name) else { return nil }
    let descriptor = openat(rootFD, name, O_RDONLY | O_CLOEXEC | O_NOFOLLOW)
    if descriptor < 0 {
        if errno == ENOENT { return nil }
        throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
    }
    defer { close(descriptor) }
    var value = stat()
    guard fstat(descriptor, &value) == 0,
          (value.st_mode & S_IFMT) == S_IFREG,
          value.st_nlink == 1,
          value.st_uid == getuid(),
          value.st_size >= 0,
          value.st_size <= maximum else { throw NSError(domain: "CodexProSafeDiagnostic", code: 2) }
    var data = Data(count: Int(value.st_size))
    let count = data.withUnsafeMutableBytes { buffer in
        pread(descriptor, buffer.baseAddress, buffer.count, 0)
    }
    guard count == data.count else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
    return (metadata(value, name: name), data)
}

private func response(for request: [String: Any], rootOverride: String? = nil) -> [String: Any] {
    func envelope(_ status: String) -> [String: Any] {
        ["protocol": protocolVersion, "helperVersion": protocolVersion, "status": status]
    }
    guard request["protocol"] as? String == protocolVersion,
          let operation = request["operation"] as? String else { return envelope("unavailable") }
    if operation == "handshake" { return envelope("ok") }
    guard let root = rootOverride ?? fixedRoot() else { return envelope("unavailable") }
    let rootFD = open(root, O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW)
    if rootFD < 0 { return errno == ENOENT ? envelope("missing") : envelope("unavailable") }
    defer { close(rootFD) }
    var rootStat = stat()
    guard fstat(rootFD, &rootStat) == 0,
          (rootStat.st_mode & S_IFMT) == S_IFDIR,
          rootStat.st_uid == getuid() else { return envelope("unavailable") }
    do {
        let listing = try entries(rootFD: rootFD)
        if operation == "inventory" {
            var value = envelope("ok")
            value["entries"] = listing.map { metadata($0.1, name: $0.0) }
            return value
        }
        if operation == "configuration" {
            var files: [[String: Any]] = []
            for name in configNames {
                if let (meta, data) = try readRegular(rootFD: rootFD, name: name, maximum: maxConfigBytes) {
                    var item = meta; item["status"] = "present"; item["contentBase64"] = data.base64EncodedString(); files.append(item)
                } else {
                    files.append(["name": name, "status": "absent", "bytes": 0, "modifiedUtc": ""])
                }
            }
            var value = envelope("ok"); value["files"] = files; return value
        }
        if operation == "database", let family = request["familyKind"] as? String,
           ["logs", "state", "memories", "goals"].contains(family) {
            let matches = listing.filter {
                let range = NSRange($0.0.startIndex..., in: $0.0)
                return databasePattern.firstMatch(in: $0.0, range: range) != nil && $0.0.hasPrefix("\(family)_")
            }
            if matches.isEmpty { return envelope("missing") }
            if matches.count != 1 { var value = envelope("ambiguous"); value["matches"] = matches.count; return value }
            let sidecars = listing.map(\.0).compactMap { name -> String? in
                if name == matches[0].0 + "-wal" { return "wal" }
                if name == matches[0].0 + "-shm" { return "shm" }
                return nil
            }
            guard matches[0].1.st_size <= maxDatabaseBytes else {
                var value = envelope("oversized")
                value["database"] = metadata(matches[0].1, name: matches[0].0)
                value["sidecars"] = sidecars
                return value
            }
            guard let (meta, data) = try readRegular(rootFD: rootFD, name: matches[0].0, maximum: maxDatabaseBytes) else { return envelope("missing") }
            var database = meta; database["contentBase64"] = data.base64EncodedString()
            var value = envelope("ok"); value["matches"] = 1; value["database"] = database; value["sidecars"] = sidecars; return value
        }
    } catch {}
    return envelope("unavailable")
}

private func readExact(_ count: Int, allowEOF: Bool) -> Data? {
    var result = Data()
    while result.count < count {
        guard let part = try? FileHandle.standardInput.read(upToCount: count - result.count), !part.isEmpty else {
            return allowEOF && result.isEmpty ? nil : Data()
        }
        result.append(part)
    }
    return result
}

private func serve() -> Never {
    while true {
        guard let header = readExact(4, allowEOF: true) else { exit(0) }
        guard header.count == 4 else { exit(3) }
        let length = header.withUnsafeBytes { $0.loadUnaligned(as: UInt32.self).littleEndian }
        guard length > 0, length <= requestLimit,
              let body = readExact(Int(length), allowEOF: false), body.count == length,
              let request = try? JSONSerialization.jsonObject(with: body) as? [String: Any],
              let encoded = try? JSONSerialization.data(withJSONObject: response(for: request)),
              !encoded.isEmpty, encoded.count <= responseLimit else { exit(3) }
        var outputLength = UInt32(encoded.count).littleEndian
        FileHandle.standardOutput.write(Data(bytes: &outputLength, count: 4))
        FileHandle.standardOutput.write(encoded)
    }
}

if CommandLine.arguments == [CommandLine.arguments[0], "--self-test"] {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    try Data("model = \"safe\"\n".utf8).write(to: root.appendingPathComponent("config.toml"))
    let value = response(for: ["protocol": protocolVersion, "operation": "configuration"], rootOverride: root.path)
    exit(value["status"] as? String == "ok" ? 0 : 1)
}
guard CommandLine.arguments == [CommandLine.arguments[0], "--serve"] else { exit(2) }
serve()
