using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TabLink.Core;

public sealed record AdbCommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Inject a fake runner in tests. Production only runs adb through ArgumentList, never a shell.</summary>
public interface IAdbProcessRunner
{
    Task<AdbCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class AdbProcessRunner : IAdbProcessRunner
{
    public async Task<AdbCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        if (!process.Start()) throw new IOException("Unable to start adb.");
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Only terminate this invocation, never the shared adb server or other client processes.
            try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("The adb command timed out.");
        }
    }
}

public sealed class AdbClient
{
    public const int DefaultPort = 27183;
    private readonly string _adbPath;
    private readonly DevicePolicy _policy;
    private readonly Func<CancellationToken, Task<IReadOnlyList<UsbDeviceIdentity>>> _inventoryProvider;
    private readonly IAdbProcessRunner _runner;
    private readonly SemaphoreSlim _commands = new(1, 1);

    public AdbClient(string adbPath, DevicePolicy policy,
        Func<CancellationToken, Task<IReadOnlyList<UsbDeviceIdentity>>> inventoryProvider,
        IAdbProcessRunner? runner = null)
    {
        if (string.IsNullOrWhiteSpace(adbPath)) throw new ArgumentException("An adb executable path is required.", nameof(adbPath));
        _adbPath = adbPath;
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _inventoryProvider = inventoryProvider ?? throw new ArgumentNullException(nameof(inventoryProvider));
        _runner = runner ?? new AdbProcessRunner();
    }

    /// <summary>Read-only enumeration of USB-looking ADB entries, including authorization/offline states for the UI.</summary>
    public async Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunCheckedAsync(["devices", "-l"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        return ParseDevices(result.StandardOutput);
    }

    public static IReadOnlyList<AdbDevice> ParseDevices(string output)
    {
        var devices = new List<AdbDevice>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[1] is not ("device" or "unauthorized" or "offline")) continue;
            if (!DevicePolicy.IsSafeSerial(parts[0]) || DevicePolicy.IsNetworkSerial(parts[0])) continue;
            string? value(string key) => parts.Skip(2).FirstOrDefault(x => x.StartsWith(key + ":", StringComparison.Ordinal))?[(key.Length + 1)..];
            var device = new AdbDevice(parts[0], parts[1], value("model"), value("transport_id"), string.Join(' ', parts.Skip(2)));
            if (!device.IsNetworkTransport) devices.Add(device);
        }
        return devices;
    }

    /// <summary>Call only in response to the user's explicit selection and approval.</summary>
    public async Task<ApprovedUsbDevice> ApproveAsync(AdbDevice device, CancellationToken cancellationToken = default)
        => _policy.Approve(device, await _inventoryProvider(cancellationToken).ConfigureAwait(false));

    public async Task ReversePortAsync(ApprovedUsbDevice device, int port = DefaultPort, CancellationToken cancellationToken = default)
    {
        ValidatePort(port);
        try
        {
            await TargetAsync(device, ["reverse", "--no-rebind", "tcp:" + port, "tcp:" + port], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
        catch (AdbCommandException ex)
        {
            throw new AdbCommandException(ex.ExitCode, ex.StandardError, ex.StandardOutput,
                "Cannot reserve TabLink USB port 27183 without replacing an existing mapping. If an old mapping exists, inspect and explicitly remove it before retrying.");
        }
    }

    public Task RemoveReverseAsync(ApprovedUsbDevice device, int port = DefaultPort, CancellationToken cancellationToken = default)
    {
        ValidatePort(port);
        return TargetAsync(device, ["reverse", "--remove", "tcp:" + port], TimeSpan.FromSeconds(15), cancellationToken);
    }

    public async Task<TabletDisplayProfile> ReadDisplayProfileAsync(ApprovedUsbDevice device,CancellationToken cancellationToken=default)
    {
        var command=new[]{"shell","content","query","--uri","content://com.tablink.client.display/capabilities","--user","0"};
        var result=await TargetAsync(device,command,TimeSpan.FromSeconds(12),cancellationToken).ConfigureAwait(false);
        if(!result.StandardOutput.Contains("json=",StringComparison.Ordinal))
        {
            // Some Android vendors cannot start a stopped package's provider.
            // Launch only our installed client, then repeat the read-only query.
            await TargetAsync(device,["shell","am","start","-n","com.tablink.client/.MainActivity","--ez","profileOnly","true"],TimeSpan.FromSeconds(12),cancellationToken,true).ConfigureAwait(false);
            await Task.Delay(350,cancellationToken).ConfigureAwait(false);
            result=await TargetAsync(device,command,TimeSpan.FromSeconds(12),cancellationToken).ConfigureAwait(false);
        }
        var marker=result.StandardOutput.IndexOf("json=",StringComparison.Ordinal);
        if(marker<0)throw new IOException("无法从 APK 读取平板屏幕信息，请先安装交付目录中的新版安卓客户端。");
        return TabletDisplayProfile.Parse(result.StandardOutput[(marker+5)..].Trim());
    }

    public Task InstallApkAsync(ApprovedUsbDevice device, string apkPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apkPath)) throw new ArgumentException("APK path is required.", nameof(apkPath));
        var fullPath = Path.GetFullPath(apkPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".apk", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new FileNotFoundException("APK file does not exist or is not an .apk file.", fullPath);
        return TargetAsync(device, ["install", "-r", fullPath], TimeSpan.FromMinutes(2), cancellationToken);
    }

    public Task LaunchAsync(ApprovedUsbDevice device, string token, int port = DefaultPort, CancellationToken cancellationToken = default)
    {
        ValidatePort(port);
        if (token is null || !Regex.IsMatch(token, @"\A[A-Za-z0-9_-]{16,256}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("The session token must contain 16–256 URL-safe alphanumeric characters.", nameof(token));
        return TargetAsync(device,
            ["shell", "am", "start", "-n", "com.tablink.client/.MainActivity", "--es", "token", token, "--ei", "port", port.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(15), cancellationToken, validateLaunchOutput: true);
    }

    private static void ValidatePort(int port)
    {
        if (port != DefaultPort) throw new ArgumentOutOfRangeException(nameof(port), "Only TabLink port 27183 is allowed.");
    }

    private async Task<AdbCommandResult> TargetAsync(ApprovedUsbDevice approved, IReadOnlyList<string> command, TimeSpan timeout, CancellationToken cancellationToken, bool validateLaunchOutput = false)
    {
        ArgumentNullException.ThrowIfNull(approved);
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-read both inventories immediately before each target command. No global/default-device commands.
            var devices = await ListDevicesAsync(cancellationToken).ConfigureAwait(false);
            var matches = devices.Where(x => string.Equals(x.Serial, approved.Serial, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1) throw new DevicePolicyException("Approved USB device is no longer uniquely present and authorized in adb.");
            var inventory = await _inventoryProvider(cancellationToken).ConfigureAwait(false);
            _policy.ValidateApproval(approved, matches[0], inventory);
            var args = new List<string> { "-s", approved.Serial };
            args.AddRange(command);
            var result = await RunCheckedAsync(args, timeout, cancellationToken).ConfigureAwait(false);
            if (validateLaunchOutput && HasAndroidLaunchFailure(result))
                throw new AdbCommandException(result.ExitCode, result.StandardError, result.StandardOutput,
                    "Android could not launch the TabLink app. Check that the client APK is installed on the approved tablet.");
            return result;
        }
        finally { _commands.Release(); }
    }

    private static bool HasAndroidLaunchFailure(AdbCommandResult result)
    {
        var output = result.StandardOutput + "\n" + result.StandardError;
        return output.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(output, @"(?im)^\s*Error\s+type\s+\d+\b", RegexOptions.CultureInvariant);
    }

    private async Task<AdbCommandResult> RunCheckedAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(_adbPath, arguments, timeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new AdbCommandException(result.ExitCode, result.StandardError, result.StandardOutput);
        return result;
    }
}

public sealed class AdbCommandException : IOException
{
    public int ExitCode { get; }
    public string StandardError { get; }
    public string StandardOutput { get; }
    public AdbCommandException(int exitCode, string standardError, string standardOutput, string? context = null)
        : base($"{(string.IsNullOrWhiteSpace(context) ? "adb command failed." : context)} Exit code {exitCode}: {(string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError).Trim()}")
        => (ExitCode, StandardError, StandardOutput) = (exitCode, standardError, standardOutput);
}
