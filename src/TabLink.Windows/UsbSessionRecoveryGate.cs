namespace TabLink.Windows;

internal readonly record struct UsbSessionRecoveryAttempt(long Generation, int Number);

/// <summary>
/// Bounds repair work for one authenticated USB session. The gate only schedules
/// retries; it has no presentation-deadline or display-lifetime authority.
/// </summary>
internal sealed class UsbSessionRecoveryGate
{
    internal const int MaximumAttempts = 3;

    static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
    static readonly DateTime NoFurtherAttemptUtc =
        DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

    readonly object sync = new();
    long generation;
    int failures;
    bool armed, episodeActive, disabled, launchReserved;
    UsbSessionRecoveryAttempt? activeAttempt;
    DateTime nextAttemptUtc = DateTime.MinValue;

    internal int Failures { get { lock (sync) return failures; } }
    internal bool Armed { get { lock (sync) return armed; } }
    internal bool InFlight { get { lock (sync) return activeAttempt.HasValue; } }
    internal bool Disabled { get { lock (sync) return disabled; } }
    internal bool RequiresSessionStop { get { lock (sync) return disabled || failures >= MaximumAttempts; } }
    internal DateTime NextAttemptUtc { get { lock (sync) return nextAttemptUtc; } }

    /// <summary>Starts a new unauthenticated session and invalidates late completions.</summary>
    internal void Reset()
    {
        lock (sync) ResetCore();
    }

    /// <summary>
    /// Records an authenticated connection. Repeated healthy observations are
    /// intentionally no-ops; the first healthy observation after a disconnect
    /// closes that fault episode and invalidates any attempt still completing.
    /// </summary>
    internal void ObserveConnected()
    {
        lock (sync) ObserveConnectedCore();
    }

    internal UsbSessionRecoveryAttempt? TryBegin(
        DateTime nowUtc, bool sessionActive, bool clientConnected)
    {
        RequireUtc(nowUtc);
        lock (sync)
        {
            if (!sessionActive)
            {
                ResetCore();
                return null;
            }
            if (clientConnected)
            {
                ObserveConnectedCore();
                return null;
            }
            if (!armed || disabled || activeAttempt.HasValue ||
                failures >= MaximumAttempts || nowUtc < nextAttemptUtc)
                return null;

            if (!episodeActive)
            {
                generation++;
                episodeActive = true;
                failures = 0;
                launchReserved = false;
                nextAttemptUtc = DateTime.MinValue;
            }

            launchReserved = false;
            var attempt = new UsbSessionRecoveryAttempt(generation, failures + 1);
            activeAttempt = attempt;
            return attempt;
        }
    }

    /// <summary>
    /// Reserves at most one client launch for this exact bounded attempt.
    /// A later backoff attempt may try again only while the client is still
    /// disconnected; three total attempts remain the hard episode limit.
    /// </summary>
    internal bool TryReserveLaunch(UsbSessionRecoveryAttempt attempt)
    {
        lock (sync)
        {
            if (!episodeActive || activeAttempt != attempt || launchReserved)
                return false;
            launchReserved = true;
            return true;
        }
    }

    internal void Complete(UsbSessionRecoveryAttempt attempt, DateTime nowUtc,
        bool clientConnected, bool retryable)
    {
        RequireUtc(nowUtc);
        lock (sync)
        {
            // Equality with the active token prevents a duplicate or late
            // completion from completing a newer in-flight attempt.
            if (!episodeActive || activeAttempt != attempt) return;
            activeAttempt = null;
            if (clientConnected)
            {
                ObserveConnectedCore();
                return;
            }

            failures = attempt.Number;
            if (!retryable)
            {
                disabled = true;
                nextAttemptUtc = NoFurtherAttemptUtc;
                return;
            }

            nextAttemptUtc = failures >= MaximumAttempts
                ? NoFurtherAttemptUtc
                : AddSaturating(nowUtc, RetryDelays[failures - 1]);
        }
    }

    void ObserveConnectedCore()
    {
        if (!armed)
        {
            armed = true;
            return;
        }
        if (!episodeActive) return;

        generation++;
        episodeActive = false;
        activeAttempt = null;
        failures = 0;
        disabled = false;
        launchReserved = false;
        nextAttemptUtc = DateTime.MinValue;
    }

    void ResetCore()
    {
        generation++;
        failures = 0;
        armed = episodeActive = disabled = launchReserved = false;
        activeAttempt = null;
        nextAttemptUtc = DateTime.MinValue;
    }

    static DateTime AddSaturating(DateTime value, TimeSpan delay) =>
        value.Ticks > DateTime.MaxValue.Ticks - delay.Ticks
            ? NoFurtherAttemptUtc
            : value.Add(delay);

    static void RequireUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("USB recovery timestamps must use UTC.");
    }
}
