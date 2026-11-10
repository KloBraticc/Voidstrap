// swift-tools-version: 5.10

import PackageDescription

let package = Package(
    name: "Voidstrap",
    platforms: [
        .iOS(.v16)
    ],
    products: [
        .library(name: "Voidstrap", targets: ["Voidstrap"])
    ],
    targets: [
        .target(name: "VoidstrapCore"),
        .target(
            name: "Voidstrap",
            dependencies: ["VoidstrapCore"],
            resources: [.copy("Resources"), .copy("ModAssets")]
        )
    ]
)
