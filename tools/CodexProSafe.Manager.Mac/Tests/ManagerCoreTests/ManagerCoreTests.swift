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
}
