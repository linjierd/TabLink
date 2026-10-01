using System.Net;
using System.Net.Sockets;

namespace TabLink.Windows;

internal readonly record struct TrustedNetworkRouteGeneration(long Value)
{
    internal bool IsEmpty => Value == 0;
}

internal readonly record struct TrustedNetworkRouteProbe(
    TrustedNetworkRouteGeneration Generation, long Sequence);

internal readonly record struct TrustedNetworkRouteRecoveryToken(
    TrustedNetworkRouteGeneration Generation, long ProbeSequence, long RecoverySequence);

internal sealed record TrustedNetworkRouteRecovery(
    TrustedNetworkRouteRecoveryToken Token,
    NetworkInterfaceChoice Original,
    NetworkInterfaceChoice Replacement);

internal enum TrustedNetworkRouteProbeState
{
    Stale,
    Present,
    SuppressedByPresentation,
    Unavailable,
    Missing,
    Ambiguous,
    RecoveryReady
}

internal readonly record struct TrustedNetworkRouteProbeResult(
    TrustedNetworkRouteProbeState State,
    int ConsecutiveMissing,
    TimeSpan MissingDuration,
    TrustedNetworkRouteRecovery? Recovery = null);

/// <summary>
/// Pure state machine that converts policy-filtered network inventory snapshots
/// into a single, explicitly claimed route-migration request. It performs no
/// inventory, WMI, firewall, ADB or display operation itself.
/// </summary>
internal sealed class TrustedNetworkRouteMonitor
{
    internal const int RequiredSuccessfulMissingProbes = 3;
    internal static readonly TimeSpan RequiredMissingDuration = TimeSpan.FromSeconds(8);

    readonly object sync = new();
    long generationSequence;
    long probeSequence;
    long recoverySequence;
    long lastCompletedProbeSequence;
    long activeProbeSequence;
    TrustedNetworkRouteGeneration activeGeneration;
    NetworkInterfaceChoice? trustedRoute;
    NetworkInterfaceChoice? missingCandidate;
    DateTime firstMissingUtc;
    DateTime lastMissingUtc;
    int consecutiveMissing;
    TrustedNetworkRouteRecovery? pendingRecovery;

    internal TrustedNetworkRouteGeneration Start(NetworkInterfaceChoice route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ValidateRoute(route, nameof(route));
        lock (sync)
        {
            ResetActiveUnsafe();
            activeGeneration = new(NextPositive(ref generationSequence));
            trustedRoute = route;
            return activeGeneration;
        }
    }

    internal bool IsCurrent(TrustedNetworkRouteGeneration generation)
    {
        lock (sync)
            return !generation.IsEmpty && activeGeneration == generation && trustedRoute is not null;
    }

    internal TrustedNetworkRouteProbe? BeginProbe(TrustedNetworkRouteGeneration generation)
    {
        lock (sync)
        {
            if (generation.IsEmpty || activeGeneration != generation || trustedRoute is null ||
                pendingRecovery is not null || activeProbeSequence != 0)
                return null;
            activeProbeSequence = NextPositive(ref probeSequence);
            return new(generation, activeProbeSequence);
        }
    }

    internal TrustedNetworkRouteProbeResult CompleteSuccessfulProbe(
        TrustedNetworkRouteProbe probe,
        DateTime nowUtc,
        IReadOnlyList<NetworkInterfaceChoice> routes,
        bool hasRecentPresentation)
    {
        RequireUtc(nowUtc);
        ArgumentNullException.ThrowIfNull(routes);
        lock (sync)
        {
            if (!AcceptProbeUnsafe(probe)) return Stale();
            var current = trustedRoute!;

            if (routes.Any(route => route is null || !IsValidRoute(route)))
            {
                ResetMissingUnsafe();
                return Result(TrustedNetworkRouteProbeState.Unavailable);
            }

            if (routes.Any(route => SameRoute(route, current)))
            {
                ResetMissingUnsafe();
                return Result(TrustedNetworkRouteProbeState.Present);
            }

            if (hasRecentPresentation)
            {
                ResetMissingUnsafe();
                return Result(TrustedNetworkRouteProbeState.SuppressedByPresentation);
            }

            var candidates = new List<NetworkInterfaceChoice>();
            foreach (var route in routes)
            {
                if (!SameStableIdentity(route, current) || candidates.Any(existing => SameRoute(existing, route)))
                    continue;
                candidates.Add(route);
            }

            if (candidates.Count == 0)
            {
                ResetMissingUnsafe();
                return Result(TrustedNetworkRouteProbeState.Missing);
            }
            if (candidates.Count != 1)
            {
                ResetMissingUnsafe();
                return Result(TrustedNetworkRouteProbeState.Ambiguous);
            }

            var candidate = candidates[0];
            if (missingCandidate is null || !SameRoute(missingCandidate, candidate) ||
                nowUtc < lastMissingUtc)
            {
                missingCandidate = candidate;
                firstMissingUtc = lastMissingUtc = nowUtc;
                consecutiveMissing = 1;
            }
            else
            {
                consecutiveMissing = checked(consecutiveMissing + 1);
                lastMissingUtc = nowUtc;
            }

            var duration = nowUtc - firstMissingUtc;
            if (consecutiveMissing < RequiredSuccessfulMissingProbes || duration < RequiredMissingDuration)
                return new(TrustedNetworkRouteProbeState.Missing, consecutiveMissing, duration);

            var token = new TrustedNetworkRouteRecoveryToken(activeGeneration, probe.Sequence,
                NextPositive(ref recoverySequence));
            pendingRecovery = new(token, current, candidate);
            return new(TrustedNetworkRouteProbeState.RecoveryReady, consecutiveMissing, duration, pendingRecovery);
        }
    }

    /// <summary>
    /// Completes a failed or indeterminate inventory attempt. It deliberately
    /// breaks the consecutive-success evidence chain and never counts as loss.
    /// </summary>
    internal TrustedNetworkRouteProbeResult CompleteUnavailableProbe(TrustedNetworkRouteProbe probe)
    {
        lock (sync)
        {
            if (!AcceptProbeUnsafe(probe)) return Stale();
            ResetMissingUnsafe();
            return Result(TrustedNetworkRouteProbeState.Unavailable);
        }
    }

    /// <summary>
    /// Claims one emitted recovery exactly once. Claiming invalidates the
    /// generation before any external recovery work begins, so a delayed probe,
    /// duplicate callback or stale cleanup cannot start another migration.
    /// </summary>
    internal bool TryClaimRecovery(TrustedNetworkRouteRecovery recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        lock (sync)
        {
            if (!ReferenceEquals(pendingRecovery, recovery) || trustedRoute is null ||
                activeGeneration != recovery.Token.Generation)
                return false;
            ResetActiveUnsafe();
            return true;
        }
    }

    internal bool Invalidate(TrustedNetworkRouteGeneration generation)
    {
        lock (sync)
        {
            if (generation.IsEmpty || activeGeneration != generation || trustedRoute is null)
                return false;
            ResetActiveUnsafe();
            return true;
        }
    }

    bool AcceptProbeUnsafe(TrustedNetworkRouteProbe probe)
    {
        if (trustedRoute is null || pendingRecovery is not null || probe.Generation.IsEmpty ||
            activeGeneration != probe.Generation || probe.Sequence <= lastCompletedProbeSequence ||
            probe.Sequence <= 0 || probe.Sequence != activeProbeSequence)
            return false;
        activeProbeSequence = 0;
        lastCompletedProbeSequence = probe.Sequence;
        return true;
    }

    TrustedNetworkRouteProbeResult Result(TrustedNetworkRouteProbeState state) =>
        new(state, consecutiveMissing, MissingDurationUnsafe());

    TimeSpan MissingDurationUnsafe() => consecutiveMissing == 0
        ? TimeSpan.Zero
        : lastMissingUtc - firstMissingUtc;

    static TrustedNetworkRouteProbeResult Stale() =>
        new(TrustedNetworkRouteProbeState.Stale, 0, TimeSpan.Zero);

    void ResetMissingUnsafe()
    {
        missingCandidate = null;
        firstMissingUtc = lastMissingUtc = default;
        consecutiveMissing = 0;
    }

    void ResetActiveUnsafe()
    {
        activeGeneration = default;
        trustedRoute = null;
        pendingRecovery = null;
        lastCompletedProbeSequence = 0;
        activeProbeSequence = 0;
        ResetMissingUnsafe();
    }

    static bool SameStableIdentity(NetworkInterfaceChoice left, NetworkInterfaceChoice right)
    {
        if (left.Kind != right.Kind) return false;
        return left.Kind == NetworkInterfaceKind.Usb
            ? string.Equals(left.UsbSerial, right.UsbSerial, StringComparison.OrdinalIgnoreCase)
            : string.Equals(left.InterfaceId, right.InterfaceId, StringComparison.OrdinalIgnoreCase);
    }

    static bool SameRoute(NetworkInterfaceChoice left, NetworkInterfaceChoice right) =>
        left.HasSameBinding(right);

    static bool IsValidRoute(NetworkInterfaceChoice route)
    {
        if (route.LocalAddress.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(route.LocalAddress) || route.PrefixLength is < 1 or > 32 ||
            route.InterfaceIndex <= 0 || string.IsNullOrWhiteSpace(route.InterfaceAlias) ||
            string.IsNullOrWhiteSpace(route.InterfaceId))
            return false;
        var bytes = route.LocalAddress.GetAddressBytes();
        if (bytes[0] is 0 or >= 224 || bytes[0] == 169 && bytes[1] == 254) return false;
        return route.Kind != NetworkInterfaceKind.Usb || !string.IsNullOrWhiteSpace(route.UsbSerial);
    }

    static void ValidateRoute(NetworkInterfaceChoice route, string parameterName)
    {
        if (!IsValidRoute(route))
            throw new ArgumentException("A trusted route requires a stable identity and usable local IPv4 endpoint.", parameterName);
    }

    static long NextPositive(ref long sequence)
    {
        sequence = sequence == long.MaxValue ? 1 : sequence + 1;
        return sequence;
    }

    static void RequireUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Network route observations must use UTC.");
    }
}
