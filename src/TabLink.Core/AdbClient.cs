using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TabLink.Core;

public sealed record AdbCommandResult(int ExitCode, string StandardOutput, string StandardError);
public enum AdbReversePortStatus { Missing, Existing, Created, Conflicting }
public sealed record AdbReversePortResult(AdbReversePortStatus Status);

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
    public const int DefaultPort = AdbReverseEndpoint.LocalServerPort;
    public const int MaximumEndpointReservationAttempts = 8;
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

    public async Task ReversePortAsync(ApprovedUsbDevice device, AdbReverseEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        endpoint.Validate();
        // Cancellation is observed immediately before the mutating transaction.
        // Once it starts, give it one private bounded deadline: a Stop caller
        // waits for the result and can then publish/remove the exact endpoint.
        // Linking this command to Stop could let adb create the mapping while
        // the caller sees only cancellation and therefore loses its receipt.
        cancellationToken.ThrowIfCancellationRequested();
        using var mutationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await TargetAsync(device, ["reverse", "--no-rebind", endpoint.DeviceAddress, AdbReverseEndpoint.LocalAddress],
                TimeSpan.FromSeconds(15), mutationDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (mutationDeadline.IsCancellationRequested)
        { throw new TimeoutException("The bounded ADB reverse mutation did not complete within 15 seconds."); }
        catch (AdbCommandException ex)
        {
            throw new AdbCommandException(ex.ExitCode, ex.StandardError, ex.StandardOutput,
                "Cannot reserve the assigned TabLink USB endpoint without replacing an existing mapping. If an old mapping exists, inspect and explicitly remove it before retrying.");
        }
    }

    /// <summary>
    /// Chooses a cryptographically random per-session device endpoint. An
    /// endpoint already present in the approved device's reverse table is
    /// skipped without taking ownership, replacing it, or removing it. Once
    /// --no-rebind succeeds the returned endpoint belongs to this session and
    /// must remain unchanged through launch, recovery, and cleanup.
    /// </summary>
    public async Task<AdbReverseEndpoint> ReserveRandomReverseEndpointAsync(
        ApprovedUsbDevice device, CancellationToken cancellationToken = default)
        => await ReserveRandomReverseEndpointAsync(device, AdbReverseEndpoint.CreateRandom, cancellationToken)
            .ConfigureAwait(false);

    internal async Task<AdbReverseEndpoint> ReserveRandomReverseEndpointAsync(
        ApprovedUsbDevice device, Func<AdbReverseEndpoint> nextEndpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nextEndpoint);
        for (var attempt = 0; attempt < MaximumEndpointReservationAttempts; attempt++)
        {
            var candidate = nextEndpoint();
            candidate.Validate();
            var state = await InspectReversePortAsync(device, candidate, cancellationToken).ConfigureAwait(false);
            if (state.Status != AdbReversePortStatus.Missing) continue;
            // A race after inspection is intentionally surfaced by
            // --no-rebind. Never retry by deleting or replacing the winner.
            await ReversePortAsync(device, candidate, cancellationToken).ConfigureAwait(false);
            return candidate;
        }
        throw new IOException("Unable to find an unused per-session USB endpoint after bounded random attempts.");
    }

    /// <summary>
    /// Revalidates the approved USB identity, inspects the selected device's
    /// reverse table and recreates only TabLink's exact mapping when absent.
    /// Starting an adb client also restarts a stopped shared adb server; this
    /// method never kills the server, removes all mappings or replaces a port.
    /// </summary>
    public async Task<AdbReversePortResult> EnsureReversePortAsync(ApprovedUsbDevice device,
        AdbReverseEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        var state = await InspectReversePortAsync(device, endpoint, cancellationToken).ConfigureAwait(false);
        if (state.Status != AdbReversePortStatus.Missing) return state;
        await ReversePortAsync(device, endpoint, cancellationToken).ConfigureAwait(false);
        return new(AdbReversePortStatus.Created);
    }

    /// <summary>Read-only inspection through the same approved-device checks as every target command.</summary>
    public async Task<AdbReversePortResult> InspectReversePortAsync(ApprovedUsbDevice device,
        AdbReverseEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        endpoint.Validate();
        var listed = await TargetAsync(device, ["reverse", "--list"],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        return new(ParseReversePortStatus(listed.StandardOutput, endpoint));
    }

    /// <summary>
    /// Strictly parses `adb reverse --list`. Unknown or oversized output is
    /// rejected instead of being interpreted as an absent TabLink mapping.
    /// </summary>
    public static AdbReversePortStatus ParseReversePortStatus(string output, AdbReverseEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(output);
        endpoint.Validate();
        if (output.Length > 1024 * 1024)
            throw new InvalidDataException("ADB reverse 列表过大，未修改任何映射。");
        var mappings = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (mappings.Length > 4096 || mappings.Any(parts => parts.Length != 3
            || !IsReverseEndpoint(parts[1]) || !IsReverseEndpoint(parts[2])))
            throw new InvalidDataException("ADB reverse 列表格式无法确认，未修改任何映射。");
        var selected = mappings.Where(parts => parts[1] == endpoint.DeviceAddress).ToArray();
        if (selected.Length == 0) return AdbReversePortStatus.Missing;
        return selected.Length == 1 && selected[0][2] == AdbReverseEndpoint.LocalAddress
            ? AdbReversePortStatus.Existing
            : AdbReversePortStatus.Conflicting;
    }

    static bool IsReverseEndpoint(string value)
    {
        if (Regex.IsMatch(value, @"\Atcp:(?:0|[1-9][0-9]{0,4})\z", RegexOptions.CultureInvariant)
            && int.TryParse(value.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            return port is >= 0 and <= 65535;
        return Regex.IsMatch(value,
            @"\A(?:localabstract|localreserved|localfilesystem|dev):[^\s]{1,4096}\z|\Ajdwp:[1-9][0-9]{0,9}\z",
            RegexOptions.CultureInvariant);
    }

    public Task RemoveReverseAsync(ApprovedUsbDevice device, AdbReverseEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        endpoint.Validate();
        return TargetAsync(device, ["reverse", "--remove", endpoint.DeviceAddress], TimeSpan.FromSeconds(15), cancellationToken);
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

    public Task LaunchAsync(ApprovedUsbDevice device, string token, AdbReverseEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        endpoint.Validate();
        if (token is null || !Regex.IsMatch(token, @"\A[A-Za-z0-9_-]{16,256}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("The session token must contain 16–256 URL-safe alphanumeric characters.", nameof(token));
        return TargetAsync(device,
            ["shell", "am", "start", "-n", "com.tablink.client/.MainActivity", "--es", "token", token, "--ei", "port", endpoint.DevicePort.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(15), cancellationToken, validateLaunchOutput: true);
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
            if (matches.Length == 0)
                throw new AdbDeviceTemporarilyUnavailableException("Approved USB device is temporarily absent from adb.");
            if (matches.Length != 1)
                throw new DevicePolicyException("Approved USB device is no longer uniquely present in adb.");
            if (string.Equals(matches[0].State, "offline", StringComparison.OrdinalIgnoreCase))
                throw new AdbDeviceTemporarilyUnavailableException("Approved USB device is temporarily offline in adb.");
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
