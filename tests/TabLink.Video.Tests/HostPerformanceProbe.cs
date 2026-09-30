using System.Diagnostics;
using System.Text.Json;
using TabLink.Windows;

// Read-only metadata timing. This probe never creates a capture source, owns a
// display reservation, or changes Windows topology. Identity changes abort it.
internal static class HostPerformanceProbe
{
    internal static void Run(string outputPath)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        RequireDesktop();
        var candidates = VirtualDisplayManager.GetDisplays()
            .Where(display => display.IsTabLinkCompatible && !display.IsPrimary).ToArray();
        if (candidates.Length != 1)
            throw new IOException("Host timing requires exactly one active, independent TabLink virtual display.");
        var display = candidates[0];
        // CaptureLease reads identity and modes; no SessionGuard or allocator
        // is created, so the running application's ownership is unaffected.
        var identity = VirtualDisplayManager.CaptureLease(display);
        var target = DxgiCaptureTarget.SelectUnique(display, DxgiCaptureTarget.ReadOutputs(), out var reason)
            ?? throw new IOException(reason);
        var results = new List<Timing>();
        Measure("InputDesktopAvailability.Query", () => RequireDesktop());
        Measure("VirtualDisplayManager.HasCurrentBounds", () =>
        {
            if (!VirtualDisplayManager.HasCurrentBounds(display.DeviceName, display.Bounds))
                throw new IOException("The selected display bounds changed during timing.");
        });
        Measure("VirtualDisplayManager.ResolveCurrent", () =>
        {
            var current = VirtualDisplayManager.ResolveCurrent(identity);
            if (current != display) throw new IOException("The selected display identity or bounds changed during timing.");
        });
        Measure("DxgiCaptureTarget.ReadOutputs", () =>
        {
            var current = DxgiCaptureTarget.SelectUnique(display, DxgiCaptureTarget.ReadOutputs(), out var detail);
            if (current != target) throw new IOException("The selected DXGI mapping changed during timing: " + detail);
        });
        RequireDesktop();
        if (VirtualDisplayManager.ResolveCurrent(identity) != display)
            throw new IOException("The selected display changed before timing completed.");
        var report = new
        {
            timestamp = DateTimeOffset.Now,
            mode = "read-only-display-metadata",
            display,
            warmupIterations = 5,
            measuredIterations = 20,
            timings = results
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, json);
        Console.WriteLine(json);

        void Measure(string name, Action action)
        {
            RequireDesktop();
            for (var index = 0; index < 5; index++) action();
            var samples = new double[20];
            for (var index = 0; index < samples.Length; index++)
            {
                RequireDesktop();
                var started = Stopwatch.GetTimestamp();
                action();
                samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            var sorted = samples.Order().ToArray();
            results.Add(new(name, samples.Length, samples.Average(),
                (sorted[9] + sorted[10]) / 2, sorted[18], sorted[^1], samples));
        }
    }

    static void RequireDesktop()
    {
        if (!InputDesktopAvailability.Query().IsAvailable)
            throw new IOException("Normal input desktop is unavailable; timing stopped without capture.");
    }

    internal sealed record Timing(string Operation, int Count, double MeanMs,
        double MedianMs, double P95Ms, double MaxMs, double[] SamplesMs);
}
