// swift-tools-version: 5.9
import PackageDescription

var targets: [Target] = [
    .target(name: "NBALiveCore"),
    .testTarget(name: "NBALiveCoreTests", dependencies: ["NBALiveCore"], resources: [.copy("Fixtures")])
]
var products: [Product] = [.library(name: "NBALiveCore", targets: ["NBALiveCore"])]
#if os(macOS)
targets.append(.executableTarget(name: "NBALiveMac", dependencies: ["NBALiveCore"]))
products.append(.executable(name: "NBALiveMac", targets: ["NBALiveMac"]))
#elseif os(Linux)
targets.append(.target(name: "NBALiveMacLogic", dependencies: ["NBALiveCore"], path: "Sources/NBALiveMac", exclude: ["AppMain.swift", "Hotkeys.swift", "Views.swift"]))
targets.append(.testTarget(name: "NBALiveMacLogicTests", dependencies: ["NBALiveMacLogic", "NBALiveCore"]))
#endif

let package = Package(name: "NBALiveMac", platforms: [.macOS(.v12)], products: products, targets: targets)
