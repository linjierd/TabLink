using TabLink.Core;

namespace TabLink.Windows;

internal static class VideoQualityTests
{
    internal static IReadOnlyList<string> Run()
    {
        var results = new List<string>();
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        var profile = new TabletDisplayProfile(1200, 1920, 0, 7, 90, 1200, 1920,
            [new TabletDisplayMode(1200, 1920, 60, 6), new TabletDisplayMode(1200, 1920, 90, 7)]);

        var automatic = new AdaptiveVideoSession(profile, VideoQualityPreset.Automatic, VideoTransportKind.Usb);
        var lowLatency = new AdaptiveVideoSession(profile, VideoQualityPreset.LowLatency, VideoTransportKind.Usb);
        var balanced = new AdaptiveVideoSession(profile, VideoQualityPreset.Balanced, VideoTransportKind.Usb);
        var highQuality = new AdaptiveVideoSession(profile, VideoQualityPreset.HighQuality, VideoTransportKind.Usb);
        Check(automatic.CurrentPlan is { Width: 1200, Height: 1920, Fps: 90 },
            "automatic quality changed the tablet's native portrait mode");
        Check(lowLatency.CurrentPlan is { Width: 1200, Height: 1920, Fps: 90 } &&
              balanced.CurrentPlan is { Width: 1200, Height: 1920, Fps: 90 } &&
              highQuality.CurrentPlan is { Width: 1200, Height: 1920, Fps: 90 },
            "a manual preset changed native dimensions or requested refresh rate");
        Check(lowLatency.CurrentPlan.BitrateKbps < balanced.CurrentPlan.BitrateKbps &&
              balanced.CurrentPlan.BitrateKbps < highQuality.CurrentPlan.BitrateKbps &&
              lowLatency.CurrentPlan.GopFrames < balanced.CurrentPlan.GopFrames,
            "manual presets do not form the expected low-latency/balanced/high-quality ladder");
        results.Add("quality presets preserve 1200x1920@90 and provide an ordered bitrate/GOP ladder");

        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var adaptive = NewAdaptive(profile);
        var sample = Sample(start, 1, 0, 0, 0, 0);
        adaptive.Observe(sample);
        for (var window = 1; window <= 3; window++)
        {
            sample = Advance(sample, start.AddSeconds(window * 2), overflowDrops: window);
            adaptive.Observe(sample);
        }
        var firstDrop = adaptive.Snapshot();
        Check(firstDrop.AdaptiveLevel == 1 && firstDrop.Plan.BitrateKbps < automatic.CurrentPlan.BitrateKbps &&
              firstDrop.LastChangedUtc == start.AddSeconds(6),
            "three consecutive bad windows did not lower exactly one bitrate level");

        for (var window = 4; window <= 7; window++)
        {
            sample = Advance(sample, start.AddSeconds(window * 2), overflowDrops: window);
            adaptive.Observe(sample);
        }
        Check(adaptive.Snapshot().AdaptiveLevel == 1,
            "a second bitrate reduction bypassed the ten-second cooldown");
        sample = Advance(sample, start.AddSeconds(16), overflowDrops: 8);
        Check(adaptive.Observe(sample).AdaptiveLevel == 2,
            "an accumulated bad trend did not resume when the ten-second cooldown expired");
        results.Add("three bad windows lower one level and the next change waits for the ten-second cooldown");

        var frozen = NewAdaptive(profile);
        var frozenSample = Sample(start, 1, 0, 0, 0, 0);
        frozen.Observe(frozenSample);
        frozenSample = Advance(frozenSample, start.AddSeconds(2), overflowDrops: 10, capturePaused: true);
        var paused = frozen.Observe(frozenSample);
        frozenSample = Advance(frozenSample, start.AddSeconds(4), overflowDrops: 20, feedbackFresh: false);
        var stale = frozen.Observe(frozenSample);
        Check(paused.AdaptiveLevel == 0 && paused.BadWindows == 0 && paused.StableWindows == 0 &&
              stale.AdaptiveLevel == 0 && stale.BadWindows == 0 && stale.StableWindows == 0,
            "capture pause or stale receiver feedback affected adaptive quality");
        results.Add("capture pause and stale receiver feedback freeze and reset adaptive trends");

        var recovering = NewAdaptive(profile);
        var recoverySample = Sample(start, 1, 0, 0, 0, 0);
        recovering.Observe(recoverySample);
        for (var window = 1; window <= 3; window++)
        {
            recoverySample = Advance(recoverySample, start.AddSeconds(window * 2), overflowDrops: window);
            recovering.Observe(recoverySample);
        }
        Check(recovering.Snapshot().AdaptiveLevel == 1, "recovery test did not enter a reduced level");
        for (var window = 4; window <= 18; window++)
        {
            recoverySample = Advance(recoverySample, start.AddSeconds(window * 2), overflowDrops: 3);
            var state = recovering.Observe(recoverySample);
            if (window < 18) Check(state.AdaptiveLevel == 1, "quality recovered before fifteen stable windows");
        }
        Check(recovering.Snapshot().AdaptiveLevel == 0 && recovering.Snapshot().StableWindows == 0,
            "fifteen stable windows did not restore one bitrate level");
        results.Add("fifteen stable windows restore one bitrate level after cooldown");

        var clock = new MediaTimestampClock();
        var timestamps = new List<long>();
        foreach (var fps in new[] { 90, 90, 90, 45, 45, 24, 24, 90, 90 }) timestamps.Add(clock.Next(fps));
        Check(timestamps.Zip(timestamps.Skip(1), (left, right) => right > left).All(value => value),
            "media timestamps stopped increasing across encoder-rate changes");
        Check(timestamps[3] > timestamps[2] && timestamps[7] > timestamps[6],
            "media timestamp clock reset at an encoder plan boundary");
        results.Add("one media clock stays strictly increasing across simulated encoder-rate changes");
        return results;
    }

    static AdaptiveVideoSession NewAdaptive(TabletDisplayProfile profile)
    {
        var session = new AdaptiveVideoSession(profile, VideoQualityPreset.Automatic, VideoTransportKind.Usb);
        session.BeginConnection(allowRuntimeChanges: true);
        return session;
    }

    static VideoQualityObservation Sample(DateTime observedUtc, long sequence, long sent, long received,
        long submitted, long presented, long overflowDrops = 0, bool capturePaused = false,
        bool feedbackFresh = true) => new(
            Guid.Parse("d9c0d921-1be5-4ae2-8414-27534ecf19fa"), observedUtc, capturePaused,
            sent, sent * 1000, sent, sent, 0, feedbackFresh, sequence, received, submitted,
            presented, 1, 0, 12, 0, 0, 0, overflowDrops, 0, 0, 0, false);

    static VideoQualityObservation Advance(VideoQualityObservation previous, DateTime observedUtc,
        long overflowDrops, bool capturePaused = false, bool feedbackFresh = true)
    {
        const int frames = 180;
        return Sample(observedUtc, previous.FeedbackSequence + 1, previous.FramesSent + frames,
            previous.ReceivedVideoFrames + frames, previous.SubmittedFrames + frames,
            previous.PresentedFrames + frames, overflowDrops, capturePaused, feedbackFresh);
    }
}
