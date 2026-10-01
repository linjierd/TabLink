using TabLink.Windows;

var assertions = 0;
var scenarios = 0;

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + message);
    assertions++;
}

void Run(string name, Action body)
{
    body();
    scenarios++;
    Console.WriteLine("PASS " + name);
}

ConnectionHealthTracker Tracker()
{
    var origin = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    long ticks = 0;
    DateTimeOffset Clock() => origin.AddTicks(Interlocked.Increment(ref ticks));
    return new ConnectionHealthTracker(Clock);
}

Run("typed stages start in deterministic waiting state", () =>
{
    var tracker = Tracker();
    var initial = tracker.Snapshot();
    Check(!initial.IsActive && initial.Attempt.IsEmpty && initial.Path == ConnectionHealthPath.None,
        "initial snapshot is inactive");
    Check(initial.Steps.Select(x => x.Stage).SequenceEqual(ConnectionHealthTracker.OrderedStages),
        "stage order is stable");
    Check(initial.Steps.All(x => x.State == ConnectionHealthState.Waiting && x.Reason == ConnectionHealthReason.Idle),
        "all initial stages wait without inferred progress");

    var attempt = tracker.BeginAttempt(ConnectionHealthPath.NativeNetwork, "选择线路并启动监听");
    var started = tracker.Snapshot();
    Check(!attempt.IsEmpty && started.IsActive && started.Path == ConnectionHealthPath.NativeNetwork,
        "begin creates an active typed attempt");
    Check(started[ConnectionHealthStage.RouteAndListener].State == ConnectionHealthState.Working,
        "route/listener is working first");
    Check(started.Steps.Skip(1).All(x => x.State == ConnectionHealthState.Waiting),
        "later stages stay waiting");
});

Run("normal path advances without parsing display text", () =>
{
    var tracker = Tracker();
    var attempt = tracker.BeginAttempt(ConnectionHealthPath.NativeNetwork, "arbitrary-localized-text");
    tracker.RouteAndListenerReady(attempt, "监听已就绪");
    tracker.AuthenticationStarted(attempt, "client hello");
    tracker.DisplayProfileReceived(attempt, "1200x1920@90");
    tracker.DisplayPreparing(attempt);
    tracker.DisplayReady(attempt);
    tracker.PipelineStarting(attempt);
    tracker.FrameSent(attempt);
    tracker.DecoderSubmitted(attempt, 41, "0x14 render-submitted");
    var complete = tracker.FramePresented(attempt, 39, "0x12 frame-presented");

    Check(complete.Steps.All(x => x.State == ConnectionHealthState.Healthy),
        "all typed stages become healthy");
    Check(complete.SubmittedFrames == 41 && complete.PresentedFrames == 39,
        "independent evidence counters are retained");
    Check(complete[ConnectionHealthStage.AuthenticationAndDisplayProfile].Reason == ConnectionHealthReason.DisplayProfileReceived,
        "profile reason is typed rather than inferred from text");
});

Run("decoder submission never claims physical presentation", () =>
{
    var tracker = Tracker();
    var attempt = tracker.BeginAttempt(ConnectionHealthPath.NativeNetwork);
    tracker.FrameSent(attempt);
    var submitted = tracker.DecoderSubmitted(attempt, 7, "任意中文也只是详情");

    Check(submitted[ConnectionHealthStage.AndroidDecodeSubmission].State == ConnectionHealthState.Healthy,
        "0x14 advances decoder submission");
    Check(submitted[ConnectionHealthStage.AndroidDecodeSubmission].Reason == ConnectionHealthReason.DecodeSubmitted,
        "0x14 keeps its own evidence reason");
    Check(submitted[ConnectionHealthStage.PhysicalPresentation].State == ConnectionHealthState.Waiting,
        "0x14 leaves physical presentation waiting");
    Check(submitted.PresentedFrames == 0, "0x14 cannot increment presented frames");
});

Run("physical presentation independently closes the last stage", () =>
{
    var tracker = Tracker();
    var attempt = tracker.BeginAttempt(ConnectionHealthPath.AdbCompatibility);
    tracker.FrameSent(attempt);
    var presented = tracker.FramePresented(attempt, 3, "surface callback");

    Check(presented[ConnectionHealthStage.AndroidDecodeSubmission].State == ConnectionHealthState.Healthy,
        "presentation implies decoder progress");
    Check(presented[ConnectionHealthStage.PhysicalPresentation].State == ConnectionHealthState.Healthy,
        "0x12 advances physical presentation");
    Check(presented.SubmittedFrames == 3 && presented.PresentedFrames == 3,
        "presentation evidence keeps counters internally consistent");
});

Run("capture pause and resume retain upstream ownership", () =>
{
    var tracker = Tracker();
    var attempt = tracker.BeginAttempt(ConnectionHealthPath.NativeNetwork);
    tracker.FramePresented(attempt, 8);
    var paused = tracker.PauseFrom(attempt, ConnectionHealthStage.CaptureEncodeSend, "安全桌面暂停");

    Check(paused.Steps.Take(3).All(x => x.State == ConnectionHealthState.Healthy),
        "route, authentication and display stay healthy");
    Check(paused.Steps.Skip(3).All(x => x.State == ConnectionHealthState.Paused),
        "pipeline and downstream stages are paused explicitly");

    var resumed = tracker.ResumeFrom(attempt, ConnectionHealthStage.CaptureEncodeSend, "普通桌面恢复");
    Check(resumed[ConnectionHealthStage.CaptureEncodeSend].State == ConnectionHealthState.Working,
        "capture resumes as working");
    Check(resumed.Steps.Skip(4).All(x => x.State == ConnectionHealthState.Waiting),
        "new decoder and presentation evidence is awaited after resume");
    Check(resumed.PresentedFrames == 8, "pause/resume preserves cumulative evidence for diagnostics");
});

Run("in-flight frame evidence cannot clear an explicit pause", () =>
{
    var tracker = Tracker();
    var attempt = tracker.BeginAttempt(ConnectionHealthPath.NativeNetwork);
    tracker.FramePresented(attempt, 8);
    tracker.PauseFrom(attempt, ConnectionHealthStage.CaptureEncodeSend, "protected desktop");
    tracker.DecoderSubmitted(attempt, 12, "late 0x14");
    var paused = tracker.FramePresented(attempt, 10, "late 0x12");

    Check(paused.Steps.Skip(3).All(x => x.State == ConnectionHealthState.Paused),
        "late progress remains diagnostic evidence and cannot resume the pipeline");
    Check(paused.SubmittedFrames == 12 && paused.PresentedFrames == 10,
        "late counters remain available for diagnostics");
});

Run("attention identifies a stage and a safe recovery", () =>
{
    var tracker = Tracker();
    var attempt = tracker.BeginAttempt(ConnectionHealthPath.NativeNetwork);
    tracker.DisplayProfileReceived(attempt);
    var failed = tracker.NeedsAttention(attempt, ConnectionHealthStage.SingleVirtualDisplay,
        ConnectionHealthRecovery.ConfigureDisplayMode, "缺少请求模式");

    Check(failed[ConnectionHealthStage.SingleVirtualDisplay].State == ConnectionHealthState.Attention,
        "failing stage requires attention");
    Check(failed[ConnectionHealthStage.SingleVirtualDisplay].Recovery == ConnectionHealthRecovery.ConfigureDisplayMode,
        "repair is a typed action");
    Check(failed.Steps.Skip(3).All(x => x.State == ConnectionHealthState.Waiting),
        "downstream work is not falsely healthy");

    var repaired = tracker.DisplayReady(attempt, "mode verified");
    Check(repaired[ConnectionHealthStage.SingleVirtualDisplay].State == ConnectionHealthState.Healthy &&
          repaired[ConnectionHealthStage.SingleVirtualDisplay].Recovery == ConnectionHealthRecovery.None,
        "fresh typed evidence clears attention");
});

Run("owned display cleanup failure remains actionable with exact recovery", () =>
{
    var tracker = Tracker();
    var attempt = tracker.BeginAttempt(ConnectionHealthPath.NativeNetwork);
    tracker.FramePresented(attempt, 12);
    const string detail = "receipt DEVNODE-7 cleanup failed: access denied";
    var failed = tracker.OwnedDisplayCleanupFailed(attempt, detail);

    var display = failed[ConnectionHealthStage.SingleVirtualDisplay];
    Check(failed.IsActive, "cleanup failure keeps the stopped attempt visible");
    Check(display.State == ConnectionHealthState.Attention &&
          display.Reason == ConnectionHealthReason.NeedsAttention,
        "the unique virtual display stage requires attention");
    Check(display.Recovery == ConnectionHealthRecovery.ReclaimOwnedDisplay,
        "recovery is limited to reclaiming the owned display");
    Check(display.Detail == detail, "the cleanup error detail is retained");
    Check(failed.Steps.Skip(3).All(x => x.State == ConnectionHealthState.Waiting),
        "stopped downstream video stages are not left healthy");
});

Run("new attempts ignore late events from an old connection", () =>
{
    var tracker = Tracker();
    var oldAttempt = tracker.BeginAttempt(ConnectionHealthPath.NativeNetwork);
    tracker.FramePresented(oldAttempt, 50);
    var currentAttempt = tracker.BeginAttempt(ConnectionHealthPath.Browser);
    var before = tracker.Snapshot();
    tracker.FramePresented(oldAttempt, 500);
    tracker.DecoderSubmitted(oldAttempt, 600);
    tracker.NeedsAttention(oldAttempt, ConnectionHealthStage.RouteAndListener,
        ConnectionHealthRecovery.RefreshRoute, "late close");
    var after = tracker.Snapshot();

    Check(after.Attempt == currentAttempt && after.Path == ConnectionHealthPath.Browser,
        "current attempt identity is unchanged");
    Check(after.SubmittedFrames == before.SubmittedFrames && after.PresentedFrames == before.PresentedFrames,
        "late evidence cannot enter the new attempt");
    Check(after.Steps.Select(x => x.State).SequenceEqual(before.Steps.Select(x => x.State)),
        "late failure cannot overwrite the current stages");
});

Run("reconnect resets only the requested downstream evidence", () =>
{
    var tracker = Tracker();
    var attempt = tracker.BeginAttempt(ConnectionHealthPath.NativeNetwork);
    tracker.FramePresented(attempt, 91);
    var reconnect = tracker.RestartFrom(attempt, ConnectionHealthStage.AuthenticationAndDisplayProfile, "new TLS session");

    Check(reconnect[ConnectionHealthStage.RouteAndListener].State == ConnectionHealthState.Healthy,
        "listener remains healthy");
    Check(reconnect[ConnectionHealthStage.AuthenticationAndDisplayProfile].State == ConnectionHealthState.Working &&
          reconnect[ConnectionHealthStage.AuthenticationAndDisplayProfile].Reason == ConnectionHealthReason.Reconnecting,
        "authentication restarts explicitly");
    Check(reconnect.Steps.Skip(2).All(x => x.State == ConnectionHealthState.Waiting),
        "downstream stages await fresh evidence");
    Check(reconnect.SubmittedFrames == 0 && reconnect.PresentedFrames == 0,
        "connection-scoped evidence is reset");
});

Run("duplicate and reordered progress cannot regress health", () =>
{
    var tracker = Tracker();
    var attempt = tracker.BeginAttempt(ConnectionHealthPath.NativeNetwork);
    tracker.DecoderSubmitted(attempt, 12);
    tracker.FramePresented(attempt, 10);
    var stable = tracker.DecoderSubmitted(attempt, 11);
    stable = tracker.FramePresented(attempt, 9);

    Check(stable.SubmittedFrames == 12 && stable.PresentedFrames == 10,
        "counters remain monotonic");
    Check(stable[ConnectionHealthStage.PhysicalPresentation].State == ConnectionHealthState.Healthy,
        "late decoder evidence cannot regress presentation");
});

Run("details are bounded and stop is explicit", () =>
{
    var tracker = Tracker();
    var attempt = tracker.BeginAttempt(ConnectionHealthPath.NativeNetwork, new string('x', 2048));
    var began = tracker.Snapshot();
    Check(began[ConnectionHealthStage.RouteAndListener].Detail?.Length == ConnectionHealthTracker.MaximumDetailLength,
        "untrusted detail is bounded");
    var stopped = tracker.Stop(attempt, "clean stop");
    Check(!stopped.IsActive && stopped.Steps.All(x => x.State == ConnectionHealthState.Waiting &&
        x.Reason == ConnectionHealthReason.Stopped && x.Recovery == ConnectionHealthRecovery.None),
        "clean stop returns every stage to an explicit waiting state");
});

Run("concurrent progress remains monotonic and thread safe", () =>
{
    var tracker = Tracker();
    var attempt = tracker.BeginAttempt(ConnectionHealthPath.NativeNetwork);
    Parallel.For(1, 201, frame =>
    {
        tracker.DecoderSubmitted(attempt, frame);
        if (frame % 2 == 0) tracker.FramePresented(attempt, frame / 2);
    });
    var snapshot = tracker.Snapshot();
    Check(snapshot.SubmittedFrames == 200 && snapshot.PresentedFrames == 100,
        "maximum concurrent evidence wins");
    Check(snapshot[ConnectionHealthStage.PhysicalPresentation].State == ConnectionHealthState.Healthy,
        "concurrent submission cannot overwrite presentation");
});

Console.WriteLine($"PASS: {scenarios} connection-health scenarios, {assertions} assertions. Pure logic only; no UI, network, ADB or display operations were performed.");
