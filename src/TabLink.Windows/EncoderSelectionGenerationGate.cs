namespace TabLink.Windows;

internal readonly record struct EncoderSelectionGeneration(long Value)
{
    internal bool IsEmpty => Value == 0;
}

/// <summary>Linearizes encoder callbacks with connection replacement and stop.</summary>
internal sealed class EncoderSelectionGenerationGate
{
    readonly object sync = new();
    long sequence;
    EncoderSelectionGeneration active;
    bool snapshotCommitted;

    internal EncoderSelectionGeneration Begin()
    {
        lock (sync)
        {
            do { sequence++; } while (sequence == 0);
            snapshotCommitted = false;
            return active = new(sequence);
        }
    }

    internal bool IsCurrent(EncoderSelectionGeneration generation)
    {
        lock (sync) return !generation.IsEmpty && active == generation;
    }

    internal bool Invalidate(EncoderSelectionGeneration generation)
    {
        lock (sync)
        {
            if (generation.IsEmpty || active != generation) return false;
            active = default;
            snapshotCommitted = false;
            return true;
        }
    }

    internal bool TryInitialize(EncoderSelectionGeneration generation, Action initialize)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        lock (sync)
        {
            if (generation.IsEmpty || active != generation || snapshotCommitted) return false;
            initialize();
            return true;
        }
    }

    internal bool TryCommitSnapshot(EncoderSelectionGeneration generation, Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (sync)
        {
            if (generation.IsEmpty || active != generation) return false;
            commit();
            snapshotCommitted = true;
            return true;
        }
    }

    internal bool TryCommitIfIdle(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (sync)
        {
            if (!active.IsEmpty) return false;
            commit();
            return true;
        }
    }
}
