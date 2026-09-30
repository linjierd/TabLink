using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TabLink.Windows;

internal static class NativeMotionProbe
{
    internal static async Task<JsonElement> RunAsync(string deviceName, int seconds)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var matches = VirtualDisplayManager.GetDisplays().Where(d => d.IsTabLinkCompatible && !d.IsPrimary &&
            d.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new IOException("Native motion probe requires one verified active TabLink VDD.");
        var display = matches[0];
        var target = DxgiCaptureTarget.SelectUnique(display, DxgiCaptureTarget.ReadOutputs(), out var reason)
                     ?? throw new IOException(reason);
        var helper = Path.Combine(AppContext.BaseDirectory, "d3d-motion-probe.exe");
        if (!File.Exists(helper)) throw new FileNotFoundException("Build the native motion helper first.", helper);
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            display.DeviceName, display.Bounds.X.ToString(CultureInfo.InvariantCulture),
            display.Bounds.Y.ToString(CultureInfo.InvariantCulture), display.Bounds.Width.ToString(CultureInfo.InvariantCulture),
            display.Bounds.Height.ToString(CultureInfo.InvariantCulture), Math.Clamp(seconds, 2, 150).ToString(CultureInfo.InvariantCulture),
            target.AdapterLuidLow.ToString(CultureInfo.InvariantCulture), target.AdapterLuidHigh.ToString(CultureInfo.InvariantCulture)
        }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new IOException("Cannot start native motion helper.");
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(seconds, 2, 150) + 5));
        var exited = child.WaitForExitAsync(timeout.Token);
        try
        {
            while (!exited.IsCompleted)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!VirtualDisplayManager.HasCurrentBounds(display.DeviceName, display.Bounds) ||
                    !VirtualDisplayManager.GetDisplays().Any(d => d.IsTabLinkCompatible && !d.IsPrimary &&
                        d.DeviceName == display.DeviceName && d.Bounds == display.Bounds) ||
                    !DxgiCaptureTarget.IsCurrent(display, target))
                    throw new IOException("Virtual display identity/bounds changed; native probe stopped.");
                await Task.WhenAny(exited, Task.Delay(150, timeout.Token));
            }
            await exited;
            var text = await output;
            var error = await errors;
            if (child.ExitCode != 0) throw new IOException("Native motion probe: " + error.Trim());
            return JsonSerializer.Deserialize<JsonElement>(text);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }
}
