namespace TabLink.Windows;

/// <summary>Local capture recovery never counts as a tablet presentation acknowledgement.</summary>
internal sealed class SessionPresentationDeadline
{
    bool initialized, recoveryGrantedForProgress, desktopWasUnavailable;
    internal DateTime LastProgressUtc { get; private set; }
    internal DateTime RecoveryDeadlineUtc { get; private set; }

    internal void Reset(DateTime startedUtc)
    {
        RequireUtc(startedUtc);
        LastProgressUtc = startedUtc;
        RecoveryDeadlineUtc = DateTime.MinValue;
        recoveryGrantedForProgress = desktopWasUnavailable = false;
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
            recoveryGrantedForProgress = false;
            RecoveryDeadlineUtc = DateTime.MinValue;
        }
        if (inputDesktop.IsUnavailable) desktopWasUnavailable = true;
        else if (inputDesktop.IsAvailable && desktopWasUnavailable)
        {
            desktopWasUnavailable = false;
            RecoveryDeadlineUtc = nowUtc.AddSeconds(20);
            recoveryGrantedForProgress = true;
        }
        // A capture restart gets one bounded opportunity per actual-ACK progress episode.
        // Paused/running transitions alone must never create an indefinitely renewable lease.
        if (hostCapturePaused && !inputDesktop.IsUnavailable && !recoveryGrantedForProgress)
        {
            RecoveryDeadlineUtc = nowUtc.AddSeconds(20);
            recoveryGrantedForProgress = true;
        }
        var deadline = LastProgressUtc.AddSeconds(20);
        if (RecoveryDeadlineUtc > deadline) deadline = RecoveryDeadlineUtc;
        // Only confirmed local desktop unavailability permits an open-ended capture pause.
        // The independent watcher performs its own local probe before accepting that pause.
        if (inputDesktop.IsUnavailable) deadline = nowUtc.AddSeconds(20);
        return new(deadline, inputDesktop.IsUnavailable || hostCapturePaused);
    }

    static void RequireUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc) throw new ArgumentException("Presentation timestamps must use UTC.");
    }
}

internal readonly record struct PresentationDeadline(DateTime DeadlineUtc, bool CapturePaused);
