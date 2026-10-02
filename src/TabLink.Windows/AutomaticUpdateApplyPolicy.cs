namespace TabLink.Windows;

internal enum AutomaticUpdateApplyDisposition { None, DeferredForActivity, ScheduleWhenIdle }

internal static class AutomaticUpdateApplyPolicy
{
    internal static bool ShouldAllowPackageDownload(bool hasSessions, bool busy, bool stopping, bool connectionStarting) =>
        !hasSessions && !busy && !stopping && !connectionStarting;

    internal static bool ShouldLaunchOnNormalExit(bool automaticApplyEnabled, bool updateReady) =>
        automaticApplyEnabled && updateReady;

    internal static AutomaticUpdateApplyDisposition Evaluate(bool automaticApplyEnabled, bool updateReady, bool hasSessions, bool busy, bool stopping, bool closing)
    {
        if (!automaticApplyEnabled || !updateReady || closing) return AutomaticUpdateApplyDisposition.None;
        return hasSessions || busy || stopping
            ? AutomaticUpdateApplyDisposition.DeferredForActivity
            : AutomaticUpdateApplyDisposition.ScheduleWhenIdle;
    }
}
