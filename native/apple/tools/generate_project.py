"""Generate the checked-in, dependency-free Xcode application project. No SDK/tool installation."""
from pathlib import Path
import hashlib

ROOT = Path(__file__).resolve().parents[1]

def ident(value):
    return hashlib.sha256(value.encode()).hexdigest()[:24].upper()

def generate():
    sources = sorted(p.relative_to(ROOT).as_posix() for p in (ROOT / "Sources").rglob("*") if p.suffix in (".swift", ".metal"))
    frameworks = "UIKit Foundation Network Security CryptoKit VideoToolbox CoreMedia CoreVideo Metal MetalKit AVFoundation ImageIO CoreGraphics UniformTypeIdentifiers".split()
    objects = []
    def obj(key, body):
        objects.append(f"\t\t{ident(key)} = {{ {body} }};")
    def ref(key): return ident(key)
    def refs(keys): return "(" + ", ".join(ref(k) for k in keys) + ",)"
    for source in sources:
        kind = "sourcecode.swift" if source.endswith(".swift") else "sourcecode.metal"
        obj(source, f'isa = PBXFileReference; lastKnownFileType = {kind}; path = "{source}"; sourceTree = "<group>";')
        obj("build:" + source, f"isa = PBXBuildFile; fileRef = {ref(source)};")
    for name in frameworks:
        obj("framework:" + name, f'isa = PBXFileReference; lastKnownFileType = wrapper.framework; name = {name}.framework; path = System/Library/Frameworks/{name}.framework; sourceTree = SDKROOT;')
        obj("frameworkbuild:" + name, f'isa = PBXBuildFile; fileRef = {ref("framework:" + name)};')
    obj("Info.plist", 'isa = PBXFileReference; lastKnownFileType = text.plist.xml; path = Info.plist; sourceTree = "<group>";')
    obj("app", 'isa = PBXFileReference; explicitFileType = wrapper.application; includeInIndex = 0; path = TabLink.app; sourceTree = BUILT_PRODUCTS_DIR;')
    obj("products", f'isa = PBXGroup; children = {refs(["app"])}; name = Products; sourceTree = "<group>";')
    obj("frameworks", f'isa = PBXGroup; children = {refs(["framework:" + n for n in frameworks])}; name = Frameworks; sourceTree = "<group>";')
    obj("main", f'isa = PBXGroup; children = {refs(sources + ["Info.plist", "frameworks", "products"])}; sourceTree = "<group>";')
    obj("sources", f'isa = PBXSourcesBuildPhase; buildActionMask = 2147483647; files = {refs(["build:" + s for s in sources])}; runOnlyForDeploymentPostprocessing = 0;')
    obj("link", f'isa = PBXFrameworksBuildPhase; buildActionMask = 2147483647; files = {refs(["frameworkbuild:" + n for n in frameworks])}; runOnlyForDeploymentPostprocessing = 0;')
    obj("resources", 'isa = PBXResourcesBuildPhase; buildActionMask = 2147483647; files = (); runOnlyForDeploymentPostprocessing = 0;')
    for config in ("Debug", "Release"):
        project_settings = 'CLANG_ENABLE_MODULES = YES; CLANG_ENABLE_OBJC_ARC = YES; SDKROOT = iphoneos; IPHONEOS_DEPLOYMENT_TARGET = 17.0; SWIFT_VERSION = 5.0; GCC_C_LANGUAGE_STANDARD = gnu17; CLANG_CXX_LANGUAGE_STANDARD = "gnu++20";'
        project_settings += ' DEBUG_INFORMATION_FORMAT = dwarf; SWIFT_OPTIMIZATION_LEVEL = "-Onone"; ENABLE_TESTABILITY = YES;' if config == "Debug" else ' DEBUG_INFORMATION_FORMAT = "dwarf-with-dsym"; SWIFT_COMPILATION_MODE = wholemodule; SWIFT_OPTIMIZATION_LEVEL = "-O";'
        target_settings = 'PRODUCT_NAME = "$(TARGET_NAME)"; PRODUCT_BUNDLE_IDENTIFIER = com.tablink.apple; INFOPLIST_FILE = Info.plist; GENERATE_INFOPLIST_FILE = NO; CODE_SIGN_STYLE = Automatic; DEVELOPMENT_TEAM = ""; TARGETED_DEVICE_FAMILY = "1,2"; SUPPORTED_PLATFORMS = "iphoneos iphonesimulator"; SUPPORTS_MACCATALYST = NO; MARKETING_VERSION = 0.8.0; CURRENT_PROJECT_VERSION = 2; SWIFT_EMIT_LOC_STRINGS = YES; SWIFT_STRICT_CONCURRENCY = minimal; LD_RUNPATH_SEARCH_PATHS = "$(inherited) @executable_path/Frameworks"; MTL_FAST_MATH = YES;'
        obj("project:" + config, f'isa = XCBuildConfiguration; buildSettings = {{ {project_settings} }}; name = {config};')
        obj("target:" + config, f'isa = XCBuildConfiguration; buildSettings = {{ {target_settings} }}; name = {config};')
    for name in ("project", "target"):
        obj(name + ":configs", f'isa = XCConfigurationList; buildConfigurations = {refs([name + ":Debug", name + ":Release"])}; defaultConfigurationIsVisible = 0; defaultConfigurationName = Release;')
    obj("target", f'isa = PBXNativeTarget; buildConfigurationList = {ref("target:configs")}; buildPhases = {refs(["sources", "link", "resources"])}; buildRules = (); dependencies = (); name = TabLink; productName = TabLink; productReference = {ref("app")}; productType = "com.apple.product-type.application";')
    obj("project", f'isa = PBXProject; attributes = {{ LastUpgradeCheck = 1500; }}; buildConfigurationList = {ref("project:configs")}; compatibilityVersion = "Xcode 14.0"; developmentRegion = zh_CN; hasScannedForEncodings = 0; knownRegions = (en, zh_CN, Base,); mainGroup = {ref("main")}; productRefGroup = {ref("products")}; projectDirPath = ""; projectRoot = ""; targets = {refs(["target"])};')
    project = ROOT / "TabLink.xcodeproj"
    project.mkdir(exist_ok=True)
    (project / "project.pbxproj").write_text('// !$*UTF8*$!\n{\n\tarchiveVersion = 1;\n\tclasses = {};\n\tobjectVersion = 56;\n\tobjects = {\n' + "\n".join(objects) + f'\n\t}};\n\trootObject = {ref("project")};\n}}\n', encoding="utf-8")
    schemes = project / "xcshareddata" / "xcschemes"
    schemes.mkdir(parents=True, exist_ok=True)
    build_ref = f'<BuildableReference BuildableIdentifier="primary" BlueprintIdentifier="{ref("target")}" BuildableName="TabLink.app" BlueprintName="TabLink" ReferencedContainer="container:TabLink.xcodeproj"/>'
    (schemes / "TabLink.xcscheme").write_text(f'''<?xml version="1.0" encoding="UTF-8"?>
<Scheme LastUpgradeVersion="1500" version="1.3">
 <BuildAction parallelizeBuildables="YES" buildImplicitDependencies="YES"><BuildActionEntries><BuildActionEntry buildForTesting="YES" buildForRunning="YES" buildForProfiling="YES" buildForArchiving="YES" buildForAnalyzing="YES">{build_ref}</BuildActionEntry></BuildActionEntries></BuildAction>
 <TestAction buildConfiguration="Debug" selectedDebuggerIdentifier="Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier="Xcode.IDEFoundation.Launcher.LLDB" shouldUseLaunchSchemeArgsEnv="YES"><Testables/></TestAction>
 <LaunchAction buildConfiguration="Debug" selectedDebuggerIdentifier="Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier="Xcode.IDEFoundation.Launcher.LLDB" launchStyle="0" useCustomWorkingDirectory="NO" ignoresPersistentStateOnLaunch="NO" debugDocumentVersioning="YES" debugServiceExtension="internal" allowLocationSimulation="YES"><BuildableProductRunnable runnableDebuggingMode="0">{build_ref}</BuildableProductRunnable></LaunchAction>
 <ProfileAction buildConfiguration="Release" shouldUseLaunchSchemeArgsEnv="YES" savedToolIdentifier="" useCustomWorkingDirectory="NO" debugDocumentVersioning="YES"><BuildableProductRunnable runnableDebuggingMode="0">{build_ref}</BuildableProductRunnable></ProfileAction>
 <AnalyzeAction buildConfiguration="Debug"/>
 <ArchiveAction buildConfiguration="Release" revealArchiveInOrganizer="YES"/>
</Scheme>
''', encoding="utf-8")
    print(f"Generated Xcode project with {len(sources)} source files and {len(frameworks)} system frameworks.")

if __name__ == "__main__":
    generate()
