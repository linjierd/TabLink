namespace TabLink.Windows;

internal enum AutomaticUpdateApplyDisposition { None, DeferredForActivity, ScheduleWhenIdle }

internal static class AutomaticUpdateApplyPolicy
{
    internal static AutomaticUpdateApplyDisposition Evaluate(bool updateReady, bool hasSessions, bool busy, bool stopping, bool closing)
    {
        if (!updateReady || closing) return AutomaticUpdateApplyDisposition.None;
        return hasSessions || busy || stopping
            ? AutomaticUpdateApplyDisposition.DeferredForActivity
            : AutomaticUpdateApplyDisposition.ScheduleWhenIdle;
    }
}
