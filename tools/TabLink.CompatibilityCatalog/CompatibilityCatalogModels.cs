namespace TabLink.CompatibilityCatalog;

public sealed record CompatibilityCatalogDocument(
    int SchemaVersion,
    IReadOnlyList<CompatibilityReport> Reports);

public sealed record CompatibilityReport(
    string Id,
    string Result,
    DateOnly VerifiedOn,
    string Source,
    TabLinkRelease TabLink,
    HostConfiguration Host,
    ReceiverConfiguration Receiver,
    string Connection,
    DisplayConfiguration Display,
    VideoConfiguration Video,
    IReadOnlyList<string> VerifiedFeatures,
    IReadOnlyList<string> Limitations,
    EvidenceReference Evidence);

public sealed record TabLinkRelease(string Version, string Channel, string ReleaseTag);

public sealed record HostConfiguration(
    string OperatingSystem,
    string Architecture,
    string GpuVendor,
    string GpuModel);

public sealed record ReceiverConfiguration(
    string Platform,
    string Client,
    string Manufacturer,
    string Model,
    string OperatingSystem,
    string Decoder);

public sealed record DisplayConfiguration(
    int LogicalWidth,
    int LogicalHeight,
    int NativeWidth,
    int NativeHeight,
    int RotationQuarterTurns,
    int ActiveRefreshHz,
    int RequestedRefreshHz,
    IReadOnlyList<int> SupportedRefreshHz);

public sealed record VideoConfiguration(
    string Codec,
    string Encoder,
    string Decoder,
    decimal RequestedFps,
    decimal EffectiveFps,
    decimal SubmittedFps,
    decimal PresentationCallbackFps,
    decimal? PhysicalPresentationFps,
    string? PhysicalPresentationMethod);

public sealed record EvidenceReference(string Document, string SourceCommit);

public static class CompatibilityCatalogContract
{
    public const int SchemaVersion = 1;
    public const int MaximumCatalogBytes = 512 * 1024;

    public static IReadOnlyList<string> Results { get; } =
        Array.AsReadOnly(["failed", "limited", "verified"]);

    public static IReadOnlyList<string> Sources { get; } =
        Array.AsReadOnly(["community-verification", "maintainer-verification"]);

    public static IReadOnlyList<string> Channels { get; } =
        Array.AsReadOnly(["development", "preview", "stable"]);

    public static IReadOnlyList<string> Architectures { get; } =
        Array.AsReadOnly(["arm64", "x64"]);

    public static IReadOnlyList<string> ReceiverPlatforms { get; } =
        Array.AsReadOnly(["android", "harmonyos", "ipados", "ios", "linux", "macos", "windows"]);

    public static IReadOnlyList<string> ReceiverClients { get; } =
        Array.AsReadOnly(["android-native", "browser"]);

    public static IReadOnlyList<string> Connections { get; } =
        Array.AsReadOnly(["adb", "browser", "native-network", "usb-tethering", "wifi"]);

    public static IReadOnlyList<string> Codecs { get; } =
        Array.AsReadOnly(["h264"]);

    public static IReadOnlyList<string> Encoders { get; } =
        Array.AsReadOnly(["amf", "nvenc", "quicksync", "software", "unknown"]);

    public static IReadOnlyList<string> DecoderKinds { get; } =
        Array.AsReadOnly(["hardware", "software", "unknown"]);

    public static IReadOnlyList<string> PhysicalPresentationMethods { get; } =
        Array.AsReadOnly(["external-camera", "surfaceflinger"]);

    public static IReadOnlyList<string> VerifiedFeatures { get; } = Array.AsReadOnly(
    [
        "app-process-restart-reconnect",
        "browser-mode",
        "extended-desktop",
        "hardware-decoding",
        "native-orientation",
        "ninety-hz",
        "single-display-cleanup",
        "touch-input",
        "trusted-reconnect",
        "trusted-registration",
        "usb-no-debug"
    ]);

    public static IReadOnlyList<string> Limitations { get; } = Array.AsReadOnly(
    [
        "active-revocation-not-tested",
        "reduced-refresh-rate",
        "route-migration-not-tested",
        "software-decode-only",
        "system-restart-not-tested",
        "token-replay-not-tested",
        "unstable",
        "usb-debug-required"
    ]);
}
