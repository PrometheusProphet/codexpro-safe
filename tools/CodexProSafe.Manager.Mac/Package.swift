// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "CodexProSafeManagerMac",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "CodexProSafeManager", targets: ["CodexProSafeManager"]),
        .executable(name: "CodexProSafeLauncher", targets: ["CodexProSafeLauncher"]),
        .executable(name: "CodexProSafeTakeoverHarness", targets: ["CodexProSafeTakeoverHarness"]),
        .library(name: "ManagerCore", targets: ["ManagerCore"])
    ],
    targets: [
        .target(name: "ProcessInspectionC", publicHeadersPath: "include", linkerSettings: [.linkedLibrary("proc")]),
        .target(name: "ManagerCore", dependencies: ["ProcessInspectionC"]),
        .executableTarget(name: "CodexProSafeLauncher"),
        .executableTarget(name: "CodexProSafeManager", dependencies: ["ManagerCore"]),
        .executableTarget(name: "CodexProSafeTakeoverHarness", dependencies: ["ManagerCore"]),
        .testTarget(name: "ManagerCoreTests", dependencies: ["ManagerCore"])
    ]
)
