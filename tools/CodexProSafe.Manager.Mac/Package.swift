// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "CodexProSafeManagerMac",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "CodexProSafeManager", targets: ["CodexProSafeManager"]),
        .executable(name: "CodexProSafeLauncher", targets: ["CodexProSafeLauncher"]),
        .library(name: "ManagerCore", targets: ["ManagerCore"])
    ],
    targets: [
        .target(name: "ManagerCore"),
        .executableTarget(name: "CodexProSafeLauncher"),
        .executableTarget(name: "CodexProSafeManager", dependencies: ["ManagerCore"]),
        .testTarget(name: "ManagerCoreTests", dependencies: ["ManagerCore"])
    ]
)
