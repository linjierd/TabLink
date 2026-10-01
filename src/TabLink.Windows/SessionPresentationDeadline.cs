namespace TabLink.Windows;

/// <summary>Local capture recovery never counts as a tablet presentation acknowledgement.</summary>
internal sealed class SessionPresentationDeadline
{
    // Encoder discovery is a single bounded startup transaction. Keep its
    // budget below the first-presentation deadline so cleanup always remains
    // owned by the session rather than racing the display watchdog.
    internal static readonly TimeSpan EncoderProbeBudget = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan InitialPresentationTimeout = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan ProgressTimeout = TimeSpan.FromSeconds(20);

    bool initialized, hasPresentedFrame, recoveryGrantedForProgress, desktopWasUnavailable;
    internal DateTime LastProgressUtc { get; private set; }
    internal DateTime RecoveryDeadlineUtc { get; private set; }

    internal void Reset(DateTime startedUtc)
    {
        RequireUtc(startedUtc);
        LastProgressUtc = startedUtc;
        RecoveryDeadlineUtc = DateTime.MinValue;
        hasPresentedFrame = recoveryGrantedForProgress = desktopWasUnavailable = false;
        initialized = true;
    }

    internal PresentationDeadline Evaluate(DateTime nowUtc, DateTime? lastPresentedUtc,
        bool hostCapturePaused, InputDesktopStatus inputDesktop)
    {
        RequireUtc(nowUtc);
        if (!initialized) throw new InvalidOperationException("Presentation deadline has not been initialized.");
        if (lastPresentedUtc is { } presented && presented > LastProgressUtc)
        {
            RequireUtc(presented);
            LastProgressUtc = presented;
            hasPresentedFrame = true;
            recoveryGrantedForProgress = false;
            RecoveryDeadlineUtc = DateTime.MinValue;
        }
        if (inputDesktop.IsUnavailable) desktopWasUnavailable = true;
        else if (inputDesktop.IsAvailable && desktopWasUnavailable)
        {
            desktopWasUnavailable = false;
            RecoveryDeadlineUtc = nowUtc.Add(ProgressTimeout);
            recoveryGrantedForProgress = true;
        }
        // A capture restart gets one bounded opportunity per actual-ACK progress episode.
        // Paused/running transitions alone must never create an indefinitely renewable lease.
        if (hostCapturePaused && !inputDesktop.IsUnavailable && !recoveryGrantedForProgress)
        {
            RecoveryDeadlineUtc = nowUtc.Add(ProgressTimeout);
            recoveryGrantedForProgress = true;
        }
        var deadline = LastProgressUtc.Add(hasPresentedFrame ? ProgressTimeout : InitialPresentationTimeout);
        if (RecoveryDeadlineUtc > deadline) deadline = RecoveryDeadlineUtc;
        // Only confirmed local desktop unavailability permits an open-ended capture pause.
        // The independent watcher performs its own local probe before accepting that pause.
        if (inputDesktop.IsUnavailable) deadline = nowUtc.Add(ProgressTimeout);
        return new(deadline, inputDesktop.IsUnavailable || hostCapturePaused);
    }

    internal static bool HasSafeEncoderProbeBudget => EncoderProbeBudget < InitialPresentationTimeout;

    static void RequireUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc) throw new ArgumentException("Presentation timestamps must use UTC.");
    }
}

internal readonly record struct PresentationDeadline(DateTime DeadlineUtc, bool CapturePaused);
