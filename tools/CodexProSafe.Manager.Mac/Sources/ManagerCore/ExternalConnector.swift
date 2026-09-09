import Foundation
import Darwin
import ProcessInspectionC

public struct ProcessSnapshot: Equatable, Sendable {
    public let pid: Int32
    public let parentPID: Int32
    public let processGroupID: Int32
    public let startTimeMicroseconds: UInt64
    public let executablePath: String
    public let workingDirectory: String
    public let arguments: [String]
}

public struct ExternalConnectorPlan: Equatable, Sendable {
    public let owner: ProcessSnapshot
    public let listener: ProcessSnapshot
}

public enum ExternalConnectorInspector {
    public static func inspect(settings: ManagerSettings) throws -> ExternalConnectorPlan {
        let checked = try settings.validated()
        let listenerPID = try uniqueListenerPID(port: checked.port)
        let listener = try snapshot(pid: listenerPID)
        let owner = try snapshot(pid: listener.parentPID)
        let groupMembers = try processGroupMembers(processGroupID: owner.pid)
        guard groupMembersMatch(groupMembers, ownerPID: owner.pid, listenerPID: listener.pid),
              matches(owner: owner, listener: listener, settings: checked) else {
            throw ManagerError.invalid("External connector identity did not exactly match the saved configuration.")
        }
        return ExternalConnectorPlan(owner: owner, listener: listener)
    }

    public static func loopbackListenerIsPresent(port: Int) throws -> Bool {
        !(try listenerPIDs(port: port)).isEmpty
    }

    public static func stop(plan: ExternalConnectorPlan, settings: ManagerSettings) async throws {
        let current = try inspect(settings: settings)
        guard current == plan else {
            throw ManagerError.invalid("External connector identity changed before takeover; no signal was sent.")
        }
        guard kill(-plan.owner.pid, SIGTERM) == 0 else {
            throw ManagerError.invalid("Could not signal the verified external connector group.")
        }
        for _ in 0..<40 {
            if !isSameProcess(plan.owner) { return }
            try await Task.sleep(for: .milliseconds(100))
        }
        guard isSameProcess(plan.owner),
              let refreshed = try? inspect(settings: settings), refreshed == plan else {
            throw ManagerError.invalid("External connector identity changed during shutdown; escalation was refused.")
        }
        guard kill(-plan.owner.pid, SIGKILL) == 0 else {
            throw ManagerError.invalid("Could not stop the verified external connector group.")
        }
        for _ in 0..<20 {
            if !isSameProcess(plan.owner) { return }
            try await Task.sleep(for: .milliseconds(100))
        }
        throw ManagerError.invalid("Verified external connector did not exit.")
    }

    public static func matches(owner: ProcessSnapshot, listener: ProcessSnapshot,
                               settings: ManagerSettings) -> Bool {
        guard owner.pid > 1, listener.parentPID == owner.pid,
              owner.processGroupID == owner.pid, listener.processGroupID == owner.pid,
              canonical(owner.executablePath) == canonical(settings.nodePath),
              canonical(listener.executablePath) == canonical(settings.nodePath) else { return false }
        let expectedOwner = settings.launcherArguments()
        guard arguments(owner.arguments, match: expectedOwner, cwd: owner.workingDirectory) else { return false }
        let expectedHTTP = URL(fileURLWithPath: settings.repository).appendingPathComponent("dist/http.js").path
        guard listener.arguments.count == 2,
              canonicalArgumentPath(listener.arguments[1], cwd: listener.workingDirectory) == canonical(expectedHTTP) else { return false }
        return true
    }

    static func groupMembersMatch(_ members: Set<Int32>, ownerPID: Int32, listenerPID: Int32) -> Bool {
        members == Set([ownerPID, listenerPID])
    }

    private static func arguments(_ actual: [String], match expected: [String], cwd: String) -> Bool {
        guard actual.count == expected.count + 1, expected.count > 1 else { return false }
        let actualCommand = Array(actual.dropFirst())
        guard canonicalArgumentPath(actualCommand[0], cwd: cwd) == canonical(expected[0]),
              actualCommand[1] == "start", expected[1] == "start" else { return false }
        return parsedOptions(Array(actualCommand.dropFirst(2))) == parsedOptions(Array(expected.dropFirst(2)))
    }

    private static func parsedOptions(_ arguments: [String]) -> [String: String]? {
        var result: [String: String] = [:]
        var index = 0
        while index < arguments.count {
            let name = arguments[index]
            guard name.hasPrefix("--"), result[name] == nil else { return nil }
            if name == "--no-copy-url" {
                result[name] = "true"
                index += 1
            } else {
                guard index + 1 < arguments.count, !arguments[index + 1].hasPrefix("--") else { return nil }
                result[name] = arguments[index + 1]
                index += 2
            }
        }
        return result
    }

    private static func canonicalArgumentPath(_ value: String, cwd: String) -> String {
        if value.hasPrefix("/") { return canonical(value) }
        return canonical(URL(fileURLWithPath: cwd).appendingPathComponent(value).path)
    }

    private static func canonical(_ value: String) -> String {
        URL(fileURLWithPath: value).resolvingSymlinksInPath().standardizedFileURL.path
    }

    private static func isSameProcess(_ expected: ProcessSnapshot) -> Bool {
        guard let current = try? snapshot(pid: expected.pid) else { return false }
        return current.startTimeMicroseconds == expected.startTimeMicroseconds &&
            current.executablePath == expected.executablePath
    }

    private static func uniqueListenerPID(port: Int) throws -> Int32 {
        let pids = try listenerPIDs(port: port)
        guard pids.count == 1, let pid = pids.first else {
            throw ManagerError.invalid("Expected exactly one loopback listener for the configured port.")
        }
        return pid
    }

    private static func listenerPIDs(port: Int) throws -> Set<Int32> {
        let lsof = "/usr/sbin/lsof"
        guard FileManager.default.isExecutableFile(atPath: lsof) else {
            throw ManagerError.invalid("Trusted system lsof is unavailable.")
        }
        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: lsof)
        process.arguments = ["-nP", "-a", "-iTCP@127.0.0.1:\(port)", "-sTCP:LISTEN", "-t"]
        process.standardOutput = output
        process.standardError = Pipe()
        let completion = DispatchSemaphore(value: 0)
        process.terminationHandler = { _ in completion.signal() }
        try process.run()
        guard completion.wait(timeout: .now() + 3) == .success else {
            process.terminate()
            throw ManagerError.invalid("Timed out while resolving the configured loopback listener.")
        }
        let data = output.fileHandleForReading.readDataToEndOfFile()
        let pids = Set(String(decoding: data, as: UTF8.self).split(whereSeparator: \.isNewline).compactMap { Int32($0) })
        if process.terminationStatus == 1, pids.isEmpty { return [] }
        guard process.terminationStatus == 0 else {
            throw ManagerError.invalid("Trusted listener discovery failed.")
        }
        return pids
    }

    private static func snapshot(pid: Int32) throws -> ProcessSnapshot {
        var info = CPSProcessInfo()
        guard cps_process_info(pid, &info) == 0 else { throw ManagerError.invalid("Process identity is unavailable.") }
        let path = try stringValue { buffer, capacity in cps_process_path(pid, buffer, capacity) }
        let cwd = try stringValue { buffer, capacity in cps_process_cwd(pid, buffer, capacity) }
        var raw = [UInt8](repeating: 0, count: 1_048_576)
        var length = raw.count
        guard cps_process_arguments(pid, &raw, &length) == 0 else {
            throw ManagerError.invalid("Process arguments are unavailable.")
        }
        let arguments = parseArguments(Array(raw.prefix(length)))
        guard !arguments.isEmpty else { throw ManagerError.invalid("Process arguments are empty.") }
        return ProcessSnapshot(pid: pid, parentPID: info.parent_pid, processGroupID: info.process_group_id,
                               startTimeMicroseconds: info.start_time_microseconds,
                               executablePath: canonical(path), workingDirectory: canonical(cwd), arguments: arguments)
    }

    private static func processGroupMembers(processGroupID: Int32) throws -> Set<Int32> {
        var members = [pid_t](repeating: 0, count: 4_096)
        let count = cps_process_group_members(processGroupID, &members, Int32(members.count))
        guard count >= 0 else { throw ManagerError.invalid("Process-group membership is unavailable.") }
        return Set(members[0..<Int(count)].map { Int32($0) })
    }

    private static func stringValue(_ body: (UnsafeMutablePointer<CChar>, UInt32) -> Int32) throws -> String {
        var buffer = [CChar](repeating: 0, count: 4 * 1_024)
        guard body(&buffer, UInt32(buffer.count)) == 0 else { throw ManagerError.invalid("Process path is unavailable.") }
        let bytes = buffer.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }
        return String(decoding: bytes, as: UTF8.self)
    }

    private static func parseArguments(_ bytes: [UInt8]) -> [String] {
        guard bytes.count >= MemoryLayout<Int32>.size else { return [] }
        let count = bytes.withUnsafeBytes { $0.loadUnaligned(as: Int32.self) }
        guard count > 0 && count < 1024 else { return [] }
        var index = MemoryLayout<Int32>.size
        while index < bytes.count && bytes[index] != 0 { index += 1 }
        while index < bytes.count && bytes[index] == 0 { index += 1 }
        var result: [String] = []
        while index < bytes.count && result.count < count {
            let start = index
            while index < bytes.count && bytes[index] != 0 { index += 1 }
            guard index > start else { break }
            result.append(String(decoding: bytes[start..<index], as: UTF8.self))
            while index < bytes.count && bytes[index] == 0 { index += 1 }
        }
        return result
    }
}
