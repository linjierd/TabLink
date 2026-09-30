// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "TabLinkProtocol",
    platforms: [.macOS(.v13), .iOS(.v17)],
    products: [.library(name: "TabLinkProtocol", targets: ["TabLinkProtocol"])],
    targets: [
        .target(name: "TabLinkProtocol", path: "Sources/Protocol"),
        .testTarget(name: "TabLinkProtocolTests", dependencies: ["TabLinkProtocol"],
                    path: "Tests/Protocol", resources: [.copy("Fixtures")])
    ]
)
