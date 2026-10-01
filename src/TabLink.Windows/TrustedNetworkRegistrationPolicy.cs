namespace TabLink.Windows;

internal enum TrustedNetworkRegistrationStartMode
{
    PublishRegistration,
    TrustedReconnectOnly,
    RequireExplicitPairing
}

/// <summary>
/// Carries a still-live registration window across a listener replacement.
/// The service lifetime is reduced by both elapsed monotonic time and the
/// original UTC UI deadline, so retries cannot create a fresh five-minute
/// bearer or extend one after a wall-clock adjustment.
/// </summary>
internal sealed class TrustedNetworkRegistrationWindow
{
    readonly Func<long> monotonicMilliseconds;
    readonly long deadlineMilliseconds;
    readonly object sync=new();
    bool expired;

    TrustedNetworkRegistrationWindow(DateTime deadlineUtc,long deadlineMilliseconds,
        Func<long> monotonicMilliseconds)
    {
        DeadlineUtc=deadlineUtc;
        this.deadlineMilliseconds=deadlineMilliseconds;
        this.monotonicMilliseconds=monotonicMilliseconds;
    }

    internal DateTime DeadlineUtc { get; }

    internal static TrustedNetworkRegistrationWindow? TryCreate(TimeSpan serviceRemaining,
        DateTime deadlineUtc,DateTime nowUtc,Func<long>? monotonicMilliseconds=null)
    {
        ValidateUtc(deadlineUtc,nameof(deadlineUtc));
        ValidateUtc(nowUtc,nameof(nowUtc));
        var remainingMilliseconds=(long)Math.Floor(Math.Min(serviceRemaining.TotalMilliseconds,
            (deadlineUtc-nowUtc).TotalMilliseconds));
        if(remainingMilliseconds<=0)return null;
        var clock=monotonicMilliseconds??(()=>Environment.TickCount64);
        var now=clock();
        var deadline=now>long.MaxValue-remainingMilliseconds
            ?long.MaxValue
            :now+remainingMilliseconds;
        return new TrustedNetworkRegistrationWindow(deadlineUtc,deadline,clock);
    }

    internal TimeSpan? Remaining(DateTime nowUtc)
    {
        ValidateUtc(nowUtc,nameof(nowUtc));
        lock(sync)
        {
            if(expired)return null;
            var now=monotonicMilliseconds();
            if(now>=deadlineMilliseconds||nowUtc>=DeadlineUtc)
            {
                expired=true;
                return null;
            }
            var remainingMilliseconds=Math.Min(deadlineMilliseconds-now,
                (long)Math.Floor((DeadlineUtc-nowUtc).TotalMilliseconds));
            if(remainingMilliseconds>0)return TimeSpan.FromMilliseconds(remainingMilliseconds);
            expired=true;
            return null;
        }
    }

    static void ValidateUtc(DateTime value,string parameterName)
    {
        if(value.Kind!=DateTimeKind.Utc)
            throw new ArgumentException("Registration window requires UTC timestamps.",parameterName);
    }
}

/// <summary>
/// Resolves whether a network listener may publish a QR after a route restart.
/// The policy is pure: callers retain ownership of token retirement, trust
/// storage and listener resources.
/// </summary>
internal static class TrustedNetworkRegistrationPolicy
{
    internal static TrustedNetworkRegistrationStartMode Evaluate(
        bool autoTrusted,
        bool isRecovery,
        DateTime? registrationDeadlineUtc,
        int trustedDeviceCount,
        DateTime nowUtc)
    {
        if(nowUtc.Kind!=DateTimeKind.Utc)
            throw new ArgumentException("Registration policy requires a UTC observation.",nameof(nowUtc));
        if(registrationDeadlineUtc is {} deadline&&deadline.Kind!=DateTimeKind.Utc)
            throw new ArgumentException("Registration deadline must use UTC.",nameof(registrationDeadlineUtc));
        if(trustedDeviceCount<0)throw new ArgumentOutOfRangeException(nameof(trustedDeviceCount));

        var activeRegistration=!autoTrusted&&(!isRecovery||
            registrationDeadlineUtc is {} activeDeadline&&activeDeadline>nowUtc);
        if(activeRegistration)return TrustedNetworkRegistrationStartMode.PublishRegistration;
        return trustedDeviceCount>0
            ?TrustedNetworkRegistrationStartMode.TrustedReconnectOnly
            :TrustedNetworkRegistrationStartMode.RequireExplicitPairing;
    }
}
