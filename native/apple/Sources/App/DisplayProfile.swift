import UIKit

struct DisplayModeProfile: Encodable { let width: Int; let height: Int; let refreshRate: Int; let modeId: Int }
struct DisplayProfile: Encodable {
    let width: Int, height: Int, rotation: Int, activeModeId: Int
    let refreshRate: Int, nativeWidth: Int, nativeHeight: Int
    let requestedModeId: Int, requestedRefreshRate: Int
    let physicalNativeWidth: Int, physicalNativeHeight: Int
    let supportedModes: [DisplayModeProfile]

    /// Call on the main queue. H.264 requires even dimensions; report original hardware dimensions too.
    static func read(window: UIWindow?) -> DisplayProfile {
        let screen = window?.screen ?? UIScreen.main
        let physicalWidth = Int(screen.nativeBounds.width), physicalHeight = Int(screen.nativeBounds.height)
        let nativeWidth = min(physicalWidth, physicalHeight) & ~1
        let nativeHeight = max(physicalWidth, physicalHeight) & ~1
        let orientation = window?.windowScene?.interfaceOrientation ?? .portrait
        let landscape = orientation.isLandscape
        let rotation: Int
        switch orientation { case .landscapeLeft: rotation = 1; case .portraitUpsideDown: rotation = 2
        case .landscapeRight: rotation = 3; default: rotation = 0 }
        let hz = max(24, min(240, screen.maximumFramesPerSecond))
        return DisplayProfile(width: landscape ? nativeHeight : nativeWidth,
            height: landscape ? nativeWidth : nativeHeight, rotation: rotation, activeModeId: 1,
            refreshRate: hz, nativeWidth: nativeWidth, nativeHeight: nativeHeight,
            requestedModeId: 1, requestedRefreshRate: hz,
            physicalNativeWidth: physicalWidth, physicalNativeHeight: physicalHeight,
            supportedModes: [DisplayModeProfile(width: nativeWidth, height: nativeHeight, refreshRate: hz, modeId: 1)])
    }
}
