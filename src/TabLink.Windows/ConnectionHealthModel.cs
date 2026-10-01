namespace TabLink.Windows;

// Connection health is deliberately represented by typed stages and reasons.
// UI text and transport log messages are outputs of this model, never inputs to
// it, so wording/localization changes cannot alter connection state.
internal enum ConnectionHealthStage
{
    RouteAndListener = 0,
    AuthenticationAndDisplayProfile = 1,
    SingleVirtualDisplay = 2,
    CaptureEncodeSend = 3,
    AndroidDecodeSubmission = 4,
    PhysicalPresentation = 5
}

internal enum ConnectionHealthState
{
    Waiting,
    Working,
    Healthy,
    Paused,
    Attention
}

internal enum ConnectionHealthPath
{
    None,
    NativeNetwork,
    AdbCompatibility,
    Browser
}

internal enum ConnectionHealthReason
{
    Idle,
    Starting,
    Ready,
    Authenticating,
    DisplayProfileReceived,
    DisplayPreparing,
    DisplayReady,
    PipelineStarting,
    FrameSent,
    CapturePaused,
    DecodeSubmitted,
    FramePresented,
    Reconnecting,
    NeedsAttention,
    Stopped
}

internal enum ConnectionHealthRecovery
{
    None,
    RefreshRoute,
    RecreatePairing,
    ConfigureDisplayMode,
    ReclaimOwnedDisplay,
    RestartVideo,
    OpenLogs
}

internal readonly record struct ConnectionHealthAttempt(Guid Id)
{
    internal bool IsEmpty => Id == Guid.Empty;
}

internal sealed record ConnectionHealthStep(
    ConnectionHealthStage Stage,
    ConnectionHealthState State,
    ConnectionHealthReason Reason,
    string? Detail,
    ConnectionHealthRecovery Recovery,
    DateTimeOffset ChangedAt);

internal sealed record ConnectionHealthSnapshot(
    ConnectionHealthAttempt Attempt,
    ConnectionHealthPath Path,
    bool IsActive,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt,
    long SubmittedFrames,
    long PresentedFrames,
    IReadOnlyList<ConnectionHealthStep> Steps)
{
    internal ConnectionHealthStep this[ConnectionHealthStage stage]
    {
        get
        {
            var index = ConnectionHealthTracker.StageIndex(stage);
            return Steps[index];
        }
    }
}

internal sealed class ConnectionHealthTracker
{
    internal const int MaximumDetailLength = 512;
    static readonly ConnectionHealthStage[] StageSequence =
    [
        ConnectionHealthStage.RouteAndListener,
        ConnectionHealthStage.AuthenticationAndDisplayProfile,
        ConnectionHealthStage.SingleVirtualDisplay,
        ConnectionHealthStage.CaptureEncodeSend,
        ConnectionHealthStage.AndroidDecodeSubmission,
        ConnectionHealthStage.PhysicalPresentation
    ];
    internal static IReadOnlyList<ConnectionHealthStage> OrderedStages { get; } = Array.AsReadOnly(StageSequence);

    readonly object gate = new();
    readonly Func<DateTimeOffset> clock;
    readonly MutableStep[] steps;
    ConnectionHealthAttempt attempt;
    ConnectionHealthPath path;
    DateTimeOffset? startedAt;
    DateTimeOffset updatedAt;
    long submittedFrames;
    long presentedFrames;
    int? pausedFrom;
    bool active;

    internal ConnectionHealthTracker(Func<DateTimeOffset>? clock = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        updatedAt = this.clock();
        steps = StageSequence.Select(stage => new MutableStep(stage, ConnectionHealthState.Waiting,
            ConnectionHealthReason.Idle, null, ConnectionHealthRecovery.None, updatedAt)).ToArray();
    }

    internal ConnectionHealthAttempt BeginAttempt(ConnectionHealthPath connectionPath, string? detail = null)
    {
        if (connectionPath == ConnectionHealthPath.None)
            throw new ArgumentOutOfRangeException(nameof(connectionPath), "A concrete connection path is required.");
        lock (gate)
        {
            var now = clock();
            attempt = new ConnectionHealthAttempt(Guid.NewGuid());
            path = connectionPath;
            startedAt = updatedAt = now;
            submittedFrames = presentedFrames = 0;
            pausedFrom = null;
            active = true;
            ResetAll(now, ConnectionHealthReason.Idle);
            Set(0, ConnectionHealthState.Working, ConnectionHealthReason.Starting, detail,
                ConnectionHealthRecovery.RefreshRoute, now);
            return attempt;
        }
    }

    internal ConnectionHealthSnapshot Snapshot()
    {
        lock (gate) return SnapshotUnsafe();
    }

    internal ConnectionHealthSnapshot RouteAndListenerReady(ConnectionHealthAttempt current, string? detail = null) =>
        Advance(current, ConnectionHealthStage.RouteAndListener, ConnectionHealthState.Healthy,
            ConnectionHealthReason.Ready, detail, ConnectionHealthRecovery.None);

    internal ConnectionHealthSnapshot AuthenticationStarted(ConnectionHealthAttempt current, string? detail = null) =>
        Advance(current, ConnectionHealthStage.AuthenticationAndDisplayProfile, ConnectionHealthState.Working,
            ConnectionHealthReason.Authenticating, detail, ConnectionHealthRecovery.RecreatePairing);

    internal ConnectionHealthSnapshot DisplayProfileReceived(ConnectionHealthAttempt current, string? detail = null) =>
        Advance(current, ConnectionHealthStage.AuthenticationAndDisplayProfile, ConnectionHealthState.Healthy,
            ConnectionHealthReason.DisplayProfileReceived, detail, ConnectionHealthRecovery.None);

    internal ConnectionHealthSnapshot DisplayPreparing(ConnectionHealthAttempt current, string? detail = null) =>
        Advance(current, ConnectionHealthStage.SingleVirtualDisplay, ConnectionHealthState.Working,
            ConnectionHealthReason.DisplayPreparing, detail, ConnectionHealthRecovery.ConfigureDisplayMode);

    internal ConnectionHealthSnapshot DisplayReady(ConnectionHealthAttempt current, string? detail = null) =>
        Advance(current, ConnectionHealthStage.SingleVirtualDisplay, ConnectionHealthState.Healthy,
            ConnectionHealthReason.DisplayReady, detail, ConnectionHealthRecovery.None);

    internal ConnectionHealthSnapshot PipelineStarting(ConnectionHealthAttempt current, string? detail = null) =>
        Advance(current, ConnectionHealthStage.CaptureEncodeSend, ConnectionHealthState.Working,
            ConnectionHealthReason.PipelineStarting, detail, ConnectionHealthRecovery.RestartVideo);

    internal ConnectionHealthSnapshot FrameSent(ConnectionHealthAttempt current, string? detail = null) =>
        Advance(current, ConnectionHealthStage.CaptureEncodeSend, ConnectionHealthState.Healthy,
            ConnectionHealthReason.FrameSent, detail, ConnectionHealthRecovery.None);

    // This method is the only entry point for protocol 0x14 evidence. It advances
    // decoder submission only and deliberately never changes presentation state.
    internal ConnectionHealthSnapshot DecoderSubmitted(ConnectionHealthAttempt current, long frames, string? detail = null)
    {
        if (frames < 1) throw new ArgumentOutOfRangeException(nameof(frames));
        lock (gate)
        {
            if (!IsCurrent(current) || frames <= submittedFrames) return SnapshotUnsafe();
            submittedFrames = frames;
            var now = clock();
            AdvanceUnsafe(ConnectionHealthStage.AndroidDecodeSubmission, ConnectionHealthState.Healthy,
                ConnectionHealthReason.DecodeSubmitted, detail, ConnectionHealthRecovery.None, now);
            return SnapshotUnsafe();
        }
    }

    // Physical presentation must be called only from independent presentation
    // evidence such as protocol 0x12. Presentation implies decoder progress, but
    // decoder submission never implies presentation.
    internal ConnectionHealthSnapshot FramePresented(ConnectionHealthAttempt current, long frames, string? detail = null)
    {
        if (frames < 1) throw new ArgumentOutOfRangeException(nameof(frames));
        lock (gate)
        {
            if (!IsCurrent(current) || frames <= presentedFrames) return SnapshotUnsafe();
            presentedFrames = frames;
            submittedFrames = Math.Max(submittedFrames, frames);
            var now = clock();
            AdvanceUnsafe(ConnectionHealthStage.PhysicalPresentation, ConnectionHealthState.Healthy,
                ConnectionHealthReason.FramePresented, detail, ConnectionHealthRecovery.None, now);
            return SnapshotUnsafe();
        }
    }

    internal ConnectionHealthSnapshot PauseFrom(ConnectionHealthAttempt current, ConnectionHealthStage stage, string? detail = null)
    {
        var index = StageIndex(stage);
        lock (gate)
        {
            if (!IsCurrent(current)) return SnapshotUnsafe();
            var now = clock();
            pausedFrom = pausedFrom is null ? index : Math.Min(pausedFrom.Value, index);
            for (var i = index; i < steps.Length; i++)
                Set(i, ConnectionHealthState.Paused, ConnectionHealthReason.CapturePaused, detail,
                    ConnectionHealthRecovery.None, now);
            updatedAt = now;
            return SnapshotUnsafe();
        }
    }

    internal ConnectionHealthSnapshot ResumeFrom(ConnectionHealthAttempt current, ConnectionHealthStage stage, string? detail = null)
    {
        var index = StageIndex(stage);
        lock (gate)
        {
            if (!IsCurrent(current)) return SnapshotUnsafe();
            var now = clock();
            pausedFrom = null;
            MarkPriorHealthy(index, now);
            Set(index, ConnectionHealthState.Working, ConnectionHealthReason.Reconnecting, detail,
                stage <= ConnectionHealthStage.CaptureEncodeSend ? ConnectionHealthRecovery.RestartVideo : ConnectionHealthRecovery.None, now);
            ResetAfter(index, now, ConnectionHealthReason.Idle);
            updatedAt = now;
            return SnapshotUnsafe();
        }
    }

    internal ConnectionHealthSnapshot RestartFrom(ConnectionHealthAttempt current, ConnectionHealthStage stage, string? detail = null)
    {
        var index = StageIndex(stage);
        lock (gate)
        {
            if (!IsCurrent(current)) return SnapshotUnsafe();
            var now = clock();
            pausedFrom = null;
            MarkPriorHealthy(index, now);
            Set(index, ConnectionHealthState.Working, ConnectionHealthReason.Reconnecting, detail,
                SuggestedRecovery(stage), now);
            ResetAfter(index, now, ConnectionHealthReason.Idle);
            if (index <= StageIndex(ConnectionHealthStage.AndroidDecodeSubmission))
                submittedFrames = presentedFrames = 0;
            else if (index == StageIndex(ConnectionHealthStage.PhysicalPresentation))
                presentedFrames = 0;
            updatedAt = now;
            return SnapshotUnsafe();
        }
    }

    internal ConnectionHealthSnapshot NeedsAttention(ConnectionHealthAttempt current, ConnectionHealthStage stage,
        ConnectionHealthRecovery recovery, string? detail = null)
    {
        var index = StageIndex(stage);
        lock (gate)
        {
            if (!IsCurrent(current)) return SnapshotUnsafe();
            var now = clock();
            pausedFrom = null;
            MarkPriorHealthy(index, now);
            Set(index, ConnectionHealthState.Attention, ConnectionHealthReason.NeedsAttention, detail, recovery, now);
            ResetAfter(index, now, ConnectionHealthReason.Idle);
            updatedAt = now;
            return SnapshotUnsafe();
        }
    }

    // Cleanup failures keep the stopped session visible until the exact owned
    // reservation has been reclaimed.  This typed event prevents callers from
    // accidentally suggesting a mode change or a broad display-device repair.
    internal ConnectionHealthSnapshot OwnedDisplayCleanupFailed(ConnectionHealthAttempt current, string? detail = null) =>
        NeedsAttention(current, ConnectionHealthStage.SingleVirtualDisplay,
            ConnectionHealthRecovery.ReclaimOwnedDisplay, detail);

    internal ConnectionHealthSnapshot Stop(ConnectionHealthAttempt current, string? detail = null)
    {
        lock (gate)
        {
            if (!IsCurrent(current)) return SnapshotUnsafe();
            var now = clock();
            foreach (var step in steps)
            {
                step.State = ConnectionHealthState.Waiting;
                step.Reason = ConnectionHealthReason.Stopped;
                step.Detail = NormalizeDetail(detail);
                step.Recovery = ConnectionHealthRecovery.None;
                step.ChangedAt = now;
            }
            active = false;
            pausedFrom = null;
            updatedAt = now;
            return SnapshotUnsafe();
        }
    }

    ConnectionHealthSnapshot Advance(ConnectionHealthAttempt current, ConnectionHealthStage stage,
        ConnectionHealthState state, ConnectionHealthReason reason, string? detail, ConnectionHealthRecovery recovery)
    {
        lock (gate)
        {
            if (!IsCurrent(current)) return SnapshotUnsafe();
            var now = clock();
            AdvanceUnsafe(stage, state, reason, detail, recovery, now);
            return SnapshotUnsafe();
        }
    }

    void AdvanceUnsafe(ConnectionHealthStage stage, ConnectionHealthState state,
        ConnectionHealthReason reason, string? detail, ConnectionHealthRecovery recovery, DateTimeOffset now)
    {
        var index = StageIndex(stage);
        // In-flight frames may arrive after capture entered a protected-desktop
        // pause. Keep their counters for diagnostics, but only an explicit
        // ResumeFrom/RestartFrom may change the paused state.
        if (pausedFrom is int paused && index >= paused)
        {
            updatedAt = now;
            return;
        }
        MarkPriorHealthy(index, now);
        Set(index, state, reason, detail, recovery, now);
        updatedAt = now;
    }

    void MarkPriorHealthy(int index, DateTimeOffset now)
    {
        for (var i = 0; i < index; i++)
        {
            if (steps[i].State == ConnectionHealthState.Healthy) continue;
            Set(i, ConnectionHealthState.Healthy, ConnectionHealthReason.Ready, null,
                ConnectionHealthRecovery.None, now);
        }
    }

    void ResetAfter(int index, DateTimeOffset now, ConnectionHealthReason reason)
    {
        for (var i = index + 1; i < steps.Length; i++)
            Set(i, ConnectionHealthState.Waiting, reason, null, ConnectionHealthRecovery.None, now);
    }

    void ResetAll(DateTimeOffset now, ConnectionHealthReason reason)
    {
        for (var i = 0; i < steps.Length; i++)
            Set(i, ConnectionHealthState.Waiting, reason, null, ConnectionHealthRecovery.None, now);
    }

    void Set(int index, ConnectionHealthState state, ConnectionHealthReason reason,
        string? detail, ConnectionHealthRecovery recovery, DateTimeOffset now)
    {
        var step = steps[index];
        step.State = state;
        step.Reason = reason;
        step.Detail = NormalizeDetail(detail);
        step.Recovery = recovery;
        step.ChangedAt = now;
    }

    bool IsCurrent(ConnectionHealthAttempt current) => active && !current.IsEmpty && current == attempt;

    ConnectionHealthSnapshot SnapshotUnsafe()
    {
        var copies = steps.Select(step => new ConnectionHealthStep(step.Stage, step.State, step.Reason,
            step.Detail, step.Recovery, step.ChangedAt)).ToArray();
        return new(attempt, path, active, startedAt, updatedAt, submittedFrames, presentedFrames,
            Array.AsReadOnly(copies));
    }

    static string? NormalizeDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;
        var trimmed = detail.Trim();
        return trimmed.Length <= MaximumDetailLength ? trimmed : trimmed[..MaximumDetailLength];
    }

    static ConnectionHealthRecovery SuggestedRecovery(ConnectionHealthStage stage) => stage switch
    {
        ConnectionHealthStage.RouteAndListener => ConnectionHealthRecovery.RefreshRoute,
        ConnectionHealthStage.AuthenticationAndDisplayProfile => ConnectionHealthRecovery.RecreatePairing,
        ConnectionHealthStage.SingleVirtualDisplay => ConnectionHealthRecovery.ConfigureDisplayMode,
        ConnectionHealthStage.CaptureEncodeSend or ConnectionHealthStage.AndroidDecodeSubmission or
            ConnectionHealthStage.PhysicalPresentation => ConnectionHealthRecovery.RestartVideo,
        _ => ConnectionHealthRecovery.OpenLogs
    };

    internal static int StageIndex(ConnectionHealthStage stage)
    {
        var index = (int)stage;
        if (index < 0 || index >= StageSequence.Length || StageSequence[index] != stage)
            throw new ArgumentOutOfRangeException(nameof(stage));
        return index;
    }

    sealed class MutableStep(ConnectionHealthStage stage, ConnectionHealthState state,
        ConnectionHealthReason reason, string? detail, ConnectionHealthRecovery recovery, DateTimeOffset changedAt)
    {
        internal ConnectionHealthStage Stage { get; } = stage;
        internal ConnectionHealthState State { get; set; } = state;
        internal ConnectionHealthReason Reason { get; set; } = reason;
        internal string? Detail { get; set; } = detail;
        internal ConnectionHealthRecovery Recovery { get; set; } = recovery;
        internal DateTimeOffset ChangedAt { get; set; } = changedAt;
    }
}
