using TabLink.Windows;

internal static class EncoderSelectionGenerationGateTests
{
    internal static IEnumerable<string> Run()
    {
        var gate=new EncoderSelectionGenerationGate();
        var first=gate.Begin();var commits=0;
        if(!gate.TryCommitSnapshot(first,()=>commits++)||commits!=1)
            throw new Exception("current encoder generation did not commit");
        yield return "current connection generation commits encoder status";

        var second=gate.Begin();
        if(gate.TryCommitSnapshot(first,()=>commits++)||!gate.TryCommitSnapshot(second,()=>commits++)||commits!=2)
            throw new Exception("replacement generation did not reject the stale callback");
        yield return "a new connection rejects callbacks queued by the preceding connection";

        if(gate.Invalidate(first)||!gate.IsCurrent(second))
            throw new Exception("stale invalidation canceled a newer connection");
        yield return "late cleanup from an old connection cannot invalidate the current connection";

        if(!gate.Invalidate(second)||gate.TryCommitSnapshot(second,()=>commits++)||commits!=2)
            throw new Exception("stopped generation still committed encoder status");
        yield return "stopping a connection rejects its delayed encoder callback";

        var cleared=0;
        if(!gate.TryCommitIfIdle(()=>cleared++)||cleared!=1)
            throw new Exception("idle encoder state could not be cleared");
        var third=gate.Begin();
        if(gate.TryCommitIfIdle(()=>cleared++)||cleared!=1||!gate.IsCurrent(third))
            throw new Exception("queued idle clear overwrote a new connection");
        yield return "a delayed idle clear cannot erase the next connection state";

        var state=0;
        if(!gate.TryCommitSnapshot(third,()=>state=7)||gate.TryInitialize(third,()=>state=0)||state!=7)
            throw new Exception("delayed initial clear erased a snapshot from the same generation");
        yield return "a delayed initial clear cannot erase a snapshot already committed by the same connection";
    }
}
