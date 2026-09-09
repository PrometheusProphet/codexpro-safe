import XCTest
@testable import ManagerCore

final class ManagerCoreTests: XCTestCase {
    func testSafeProfilesRemainConservative() {
        XCTAssertEqual(AccessProfile.planning.launchArguments, ["--mode", "handoff", "--write", "handoff", "--bash", "off"])
        XCTAssertEqual(Array(AccessProfile.edit.launchArguments.suffix(2)), ["--bash", "off"])
        XCTAssertEqual(Array(AccessProfile.develop.launchArguments.suffix(2)), ["--bash", "safe"])
        XCTAssertEqual(Array(AccessProfile.full.launchArguments.suffix(2)), ["--bash", "full"])
    }
    func testTokenIsNotPersistedInSettings() throws {
        let data = try JSONEncoder().encode(ManagerSettings.defaults(repository: "/repo", nodePath: "/node"))
        XCTAssertFalse(String(decoding: data, as: UTF8.self).localizedCaseInsensitiveContains("token"))
    }
    func testExistingSettingsMigrateWithAutoStartOff() throws {
        let original = ManagerSettings.defaults(repository: "/repo", nodePath: "/node")
        var json = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(original)) as? [String: Any])
        json.removeValue(forKey: "autoStartServices")
        let decoded = try JSONDecoder().decode(ManagerSettings.self, from: JSONSerialization.data(withJSONObject: json))
        XCTAssertFalse(decoded.autoStartServices)
    }
    func testSynchronousSettingsLoadEliminatesStartupOverwriteWindow() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let url = root.appendingPathComponent("settings.json")
        defer { try? FileManager.default.removeItem(at: root) }
        var saved = ManagerSettings.defaults(repository: "/saved", nodePath: "/saved/node")
        saved.autoStartServices = true
        try SettingsFileStore.saveSynchronously(saved, url: url)
        let loaded = SettingsFileStore.loadSynchronously(
            url: url,
            defaults: ManagerSettings.defaults(repository: "/default", nodePath: "/default/node")
        )
        XCTAssertEqual(loaded, saved)
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        XCTAssertEqual(attributes[.posixPermissions] as? NSNumber, 0o600)
    }
    func testFirstManagerSliceIsLocalOnly() {
        XCTAssertEqual(TunnelMode.allCases, [.none])
        let settings = ManagerSettings.defaults(repository: "/repo", nodePath: "/node")
        XCTAssertEqual(settings.tunnelMode, .none)
    }
    func testLogSanitization() {
        let value = LogSanitizer.sanitize("Authorization: Bearer secret-value /Users/example/private/file")
        XCTAssertFalse(value.contains("secret-value")); XCTAssertFalse(value.contains("/Users/example"))
    }
    func testContainmentUsesPathComponents() {
        XCTAssertTrue(contains("/repo/child", within: "/repo")); XCTAssertFalse(contains("/repository", within: "/repo"))
    }
    func testExactExternalConnectorMatchAllowsOptionReordering() {
        let settings = ManagerSettings(repository: "/repo", workspaceRoot: "/workspace", allowedRoot: "/",
                                       nodePath: "/usr/bin/node", port: 8787, accessProfile: .planning)
        var command = settings.launcherArguments()
        let diagnosticIndex = command.firstIndex(of: "--codex-diagnostic-read")!
        let diagnostic = Array(command[diagnosticIndex...command.index(after: diagnosticIndex)])
        command.removeSubrange(diagnosticIndex...command.index(after: diagnosticIndex))
        command.append(contentsOf: diagnostic)
        let owner = process(pid: 100, parent: 10, group: 100, args: ["node"] + command, cwd: "/repo")
        let listener = process(pid: 101, parent: 100, group: 100, args: ["node", "/repo/dist/http.js"], cwd: "/repo")
        XCTAssertTrue(ExternalConnectorInspector.matches(owner: owner, listener: listener, settings: settings))
    }
    func testExternalConnectorMismatchAndSharedGroupAreRejected() {
        let settings = ManagerSettings(repository: "/repo", workspaceRoot: "/workspace", allowedRoot: "/",
                                       nodePath: "/usr/bin/node", port: 8787, accessProfile: .planning)
        let expected = ["node"] + settings.launcherArguments()
        let listener = process(pid: 101, parent: 100, group: 100, args: ["node", "/repo/dist/http.js"], cwd: "/repo")
        let sharedGroup = process(pid: 100, parent: 10, group: 10, args: expected, cwd: "/repo")
        XCTAssertFalse(ExternalConnectorInspector.matches(owner: sharedGroup, listener: listener, settings: settings))
        var mismatch = expected
        mismatch[mismatch.firstIndex(of: "handoff")!] = "agent"
        let wrongProfile = process(pid: 100, parent: 10, group: 100, args: mismatch, cwd: "/repo")
        XCTAssertFalse(ExternalConnectorInspector.matches(owner: wrongProfile, listener: listener, settings: settings))
    }
    func testTakeoverGroupRejectsEveryThirdProcess() {
        XCTAssertTrue(ExternalConnectorInspector.groupMembersMatch([100, 101], ownerPID: 100, listenerPID: 101))
        XCTAssertFalse(ExternalConnectorInspector.groupMembersMatch([100, 101, 102], ownerPID: 100, listenerPID: 101))
    }

    private func process(pid: Int32, parent: Int32, group: Int32, args: [String], cwd: String) -> ProcessSnapshot {
        ProcessSnapshot(pid: pid, parentPID: parent, processGroupID: group, startTimeMicroseconds: 1,
                        executablePath: "/usr/bin/node", workingDirectory: cwd, arguments: args)
    }
}
