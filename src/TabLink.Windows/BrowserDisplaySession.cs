using TabLink.Core;

namespace TabLink.Windows;

internal sealed class BrowserDisplaySession(
    Func<CancellationToken,IAsyncEnumerable<VideoPacket>> videoFactory,
    Action<InputMessage>? input, Action releaseInput, Func<ValueTask> dispose) : IAsyncDisposable
{
    internal Func<CancellationToken,IAsyncEnumerable<VideoPacket>> VideoFactory { get; } = videoFactory;
    internal Action<InputMessage>? Input { get; } = input;
    internal Action ReleaseInput { get; } = releaseInput;
    readonly object gate = new();
    Task? disposal;
    public ValueTask DisposeAsync()
    {
        lock(gate) return new(disposal ??= Task.Run(async()=>
        {
            try { ReleaseInput(); }
            finally { await dispose().ConfigureAwait(false); }
        }));
    }
}

internal sealed record BrowserPairingOffer(string Uri, DateTimeOffset ExpiresUtc);
internal sealed record BrowserSessionStatus(Guid Id, string State, string Message,
    bool CapturePaused = false, DateTime? LastPresentedUtc = null, long PresentedFrames = 0);

internal static class BrowserProfile
{
    internal static TabletDisplayProfile Parse(string json)
    {
        var profile=TabletDisplayProfile.Parse(json);
        var width=Math.Min(profile.Width,profile.Height);
        var height=Math.Max(profile.Width,profile.Height);
        if((width,height) is not ((720,1280) or (1080,1920)) ||
            profile.RefreshRate is not (30 or 60) || profile.RequestedRefreshRate!=(int)profile.RefreshRate ||
            profile.NativeWidth!=profile.Width || profile.NativeHeight!=profile.Height ||
            profile.SupportedModes.Count!=1)
            throw new InvalidDataException("浏览器副屏请选择 720p 或 1080p、30 或 60 帧。");
        return profile;
    }
}
