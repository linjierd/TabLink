using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TabLink.Core;

public sealed record AdbCommandResult(int ExitCode, string StandardOutput, string StandardError);
public enum AdbReversePortStatus { Missing, Existing, Created, Conflicting }
public sealed record AdbReversePortResult(AdbReversePortStatus Status);

/// <summary>
/// A non-forgeable binding between one approved physical USB device and the
/// Android user that was active when the TabLink session started. Provider
/// reads and client launches use this exact user for the entire session.
/// </summary>
public sealed class ApprovedAndroidUser
{
    internal ApprovedAndroidUser(AdbClient owner, ApprovedUsbDevice device, int userId)
        => (Owner, Device, UserId) = (owner, device, userId);

    internal AdbClient Owner { get; }
    public ApprovedUsbDevice Device { get; }
    public int UserId { get; }
}

/// <summary>Inject a fake runner in tests. Production only runs adb through ArgumentList, never a shell.</summary>
public interface IAdbProcessRunner
{
    Task<AdbCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class AdbProcessRunner : IAdbProcessRunner
{
    internal static ProcessStartInfo CreateStartInfo(string executable,
        IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        var executableDirectory = Path.GetDirectoryName(executable);
        if (!string.IsNullOrWhiteSpace(executableDirectory))
            start.WorkingDirectory = executableDirectory;
        foreach (var name in start.Environment.Keys.Where(name =>
                     name.StartsWith("ADB_", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("ANDROID_ADB_", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("ANDROID_SERIAL", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    public async Task<AdbCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try { return await RunCoreAsync(executable, arguments, timeout, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (AdbExecutionException) { throw; }
        catch (Exception ex) when (SafeErrorSummary.IsExecutionFailure(ex))
        { throw new AdbExecutionException(ex); }
    }

    static async Task<AdbCommandResult> RunCoreAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = CreateStartInfo(executable, arguments);
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
    private readonly SemaphoreSlim _launches = new(1, 1);

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
        { throw new AdbExecutionException(new TimeoutException("The bounded ADB reverse mutation did not complete within 15 seconds.")); }
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

    /// <summary>
    /// Chooses a random endpoint while excluding endpoints whose earlier
    /// cleanup receipts are still pending. The exclusion snapshot is checked
    /// before any ADB inspection, so a retained endpoint is never observed,
    /// rebound, or otherwise touched by the new session.
    /// </summary>
    public async Task<AdbReverseEndpoint> ReserveRandomReverseEndpointAsync(
        ApprovedUsbDevice device, IReadOnlySet<AdbReverseEndpoint> reservedEndpoints,
        CancellationToken cancellationToken = default)
        => await ReserveRandomReverseEndpointAsync(device, AdbReverseEndpoint.CreateRandom,
            SnapshotCandidatePredicate(reservedEndpoints), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Selects and inspects an unused random endpoint without mutating ADB. The
    /// caller can durably publish a non-removal intent before separately using
    /// ReversePortAsync and promoting that intent after definite success.
    /// </summary>
    public async Task<AdbReverseEndpoint> SelectRandomUnusedReverseEndpointAsync(
        ApprovedUsbDevice device, IReadOnlySet<AdbReverseEndpoint> reservedEndpoints,
        CancellationToken cancellationToken = default)
        => await SelectRandomUnusedReverseEndpointAsync(device, AdbReverseEndpoint.CreateRandom,
            SnapshotCandidatePredicate(reservedEndpoints), cancellationToken).ConfigureAwait(false);

    internal async Task<AdbReverseEndpoint> SelectRandomUnusedReverseEndpointAsync(
        ApprovedUsbDevice device, Func<AdbReverseEndpoint> nextEndpoint,
        Func<AdbReverseEndpoint, bool> isCandidateAvailable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nextEndpoint);
        ArgumentNullException.ThrowIfNull(isCandidateAvailable);
        for (var attempt = 0; attempt < MaximumEndpointReservationAttempts; attempt++)
        {
            var candidate = nextEndpoint();
            candidate.Validate();
            if (!isCandidateAvailable(candidate)) continue;
            var state = await InspectReversePortAsync(device, candidate, cancellationToken).ConfigureAwait(false);
            if (state.Status == AdbReversePortStatus.Missing) return candidate;
        }
        throw new IOException("Unable to find an unused per-session USB endpoint after bounded random attempts.");
    }

    internal async Task<AdbReverseEndpoint> ReserveRandomReverseEndpointAsync(
        ApprovedUsbDevice device, Func<AdbReverseEndpoint> nextEndpoint,
        CancellationToken cancellationToken = default)
        => await ReserveRandomReverseEndpointAsync(device, nextEndpoint, static _ => true, cancellationToken)
            .ConfigureAwait(false);

    internal async Task<AdbReverseEndpoint> ReserveRandomReverseEndpointAsync(
        ApprovedUsbDevice device, Func<AdbReverseEndpoint> nextEndpoint,
        Func<AdbReverseEndpoint, bool> isCandidateAvailable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nextEndpoint);
        ArgumentNullException.ThrowIfNull(isCandidateAvailable);
        for (var attempt = 0; attempt < MaximumEndpointReservationAttempts; attempt++)
        {
            var candidate = nextEndpoint();
            candidate.Validate();
            if (!isCandidateAvailable(candidate)) continue;
            var state = await InspectReversePortAsync(device, candidate, cancellationToken).ConfigureAwait(false);
            if (state.Status != AdbReversePortStatus.Missing) continue;
            // A race after inspection is intentionally surfaced by
            // --no-rebind. Never retry by deleting or replacing the winner.
            await ReversePortAsync(device, candidate, cancellationToken).ConfigureAwait(false);
            return candidate;
        }
        throw new IOException("Unable to find an unused per-session USB endpoint after bounded random attempts.");
    }

    private static Func<AdbReverseEndpoint, bool> SnapshotCandidatePredicate(
        IReadOnlySet<AdbReverseEndpoint> reservedEndpoints)
    {
        ArgumentNullException.ThrowIfNull(reservedEndpoints);
        var snapshot = new HashSet<AdbReverseEndpoint>(reservedEndpoints);
        foreach (var endpoint in snapshot) endpoint.Validate();
        return candidate => !snapshot.Contains(candidate);
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
        try { return new(ParseReversePortStatus(listed.StandardOutput, endpoint)); }
        catch (InvalidDataException ex) { throw new AdbResponseException(ex); }
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

    public async Task<ApprovedAndroidUser> BindCurrentAndroidUserAsync(ApprovedUsbDevice device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var userId = await ReadCurrentAndroidUserAsync(device, cancellationToken).ConfigureAwait(false);
        return new(this, device, userId);
    }

    public async Task ValidateCurrentAndroidUserAsync(ApprovedAndroidUser user,
        CancellationToken cancellationToken = default)
    {
        ValidateUserBinding(user);
        var current = await ReadCurrentAndroidUserAsync(user.Device, cancellationToken).ConfigureAwait(false);
        if (current != user.UserId)
            throw new AndroidUserChangedException();
    }

    public async Task<TabletDisplayProfile> ReadDisplayProfileAsync(ApprovedAndroidUser user,CancellationToken cancellationToken=default)
    {
        ValidateUserBinding(user);
        await ValidateCurrentAndroidUserAsync(user,cancellationToken).ConfigureAwait(false);
        var device=user.Device;
        var androidUser=user.UserId.ToString(CultureInfo.InvariantCulture);
        var command=new[]{"shell","content","query","--uri","content://com.tablink.client.display/capabilities","--user",androidUser};
        var result=await TargetAsync(device,command,TimeSpan.FromSeconds(12),cancellationToken).ConfigureAwait(false);
        if(!result.StandardOutput.Contains("json=",StringComparison.Ordinal))
        {
            // Some Android vendors cannot start a stopped package's provider.
            // Launch only our installed client, then repeat the read-only query.
            await TargetAsync(device,["shell","am","start","--user",androidUser,"-n","com.tablink.client/.MainActivity","--ez","profileOnly","true"],TimeSpan.FromSeconds(12),cancellationToken,true).ConfigureAwait(false);
            await Task.Delay(350,cancellationToken).ConfigureAwait(false);
            result=await TargetAsync(device,command,TimeSpan.FromSeconds(12),cancellationToken).ConfigureAwait(false);
        }
        var marker=result.StandardOutput.IndexOf("json=",StringComparison.Ordinal);
        if(marker<0)throw new IOException("无法从 APK 读取平板屏幕信息，请先安装交付目录中的新版安卓客户端。");
        try { return TabletDisplayProfile.Parse(result.StandardOutput[(marker+5)..].Trim()); }
        catch(Exception ex) when(ex is InvalidDataException or System.Text.Json.JsonException or NotSupportedException or ArgumentException)
        { throw new AdbResponseException(ex); }
    }

    public async Task InstallApkAsync(ApprovedUsbDevice device, string apkPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apkPath)) throw new ArgumentException("APK path is required.", nameof(apkPath));
        var fullPath = Path.GetFullPath(apkPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".apk", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new FileNotFoundException("APK file does not exist or is not an .apk file.", fullPath);
        // Push the APK before asking Package Manager to commit it. Some vendor
        // installers show their confirmation/result UI while a streaming
        // install is still open and never return the final result to adb. The
        // official --no-streaming path keeps transfer and commit separate,
        // while preserving the same approved-device checks and -s targeting.
        var user = (await BindCurrentAndroidUserAsync(device, cancellationToken).ConfigureAwait(false)).UserId
            .ToString(CultureInfo.InvariantCulture);
        var result = await TargetAsync(device,
            ["install", "--user", user, "--no-streaming", "-r", fullPath],
            TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (!Regex.IsMatch(result.StandardOutput, @"(?m)^\s*Success\s*$", RegexOptions.CultureInvariant))
            throw new AdbResponseException(new InvalidDataException(
                "ADB package installation did not return an unambiguous Success result."));
    }

    public async Task LaunchAsync(ApprovedAndroidUser user, string token, AdbReverseEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        endpoint.Validate();
        if (token is null || !Regex.IsMatch(token, @"\A[A-Za-z0-9_-]{16,256}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("The session token must contain 16–256 URL-safe alphanumeric characters.", nameof(token));
        ValidateUserBinding(user);
        await _launches.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateCurrentAndroidUserAsync(user,cancellationToken).ConfigureAwait(false);
            var androidUser=user.UserId.ToString(CultureInfo.InvariantCulture);
            var activation=Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var publication=await TargetAsync(user.Device,
                ["shell", "content", "insert", "--uri", "content://com.tablink.client.adb/session",
                    "--user", androidUser, "--bind", "activation:s:"+activation,
                    "--bind", "token:s:"+token,
                    "--bind", "port:i:"+endpoint.DevicePort.ToString(CultureInfo.InvariantCulture)],
                TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            if (HasAndroidContentFailure(publication))
                throw new AdbResponseException(new InvalidDataException(
                    "Android rejected the protected session publication."));
            // The provider write and Activity activation are separate shell calls.
            // Revalidate the selected foreground Android user immediately before the
            // activation so a user switch cannot redirect the one-shot marker.
            await ValidateCurrentAndroidUserAsync(user,cancellationToken).ConfigureAwait(false);
            await TargetAsync(user.Device,
                ["shell", "am", "start", "--user", androidUser, "-n", "com.tablink.client/.MainActivity",
                    "-a", "com.tablink.client.APPLY_ADB_SESSION", "--es", "adbActivation", activation],
                TimeSpan.FromSeconds(15), cancellationToken, validateLaunchOutput: true).ConfigureAwait(false);
        }
        finally { _launches.Release(); }
    }

    void ValidateUserBinding(ApprovedAndroidUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!ReferenceEquals(user.Owner, this))
            throw new DevicePolicyException("Android user binding belongs to another ADB client.");
    }

    async Task<int> ReadCurrentAndroidUserAsync(ApprovedUsbDevice device, CancellationToken cancellationToken)
    {
        var result = await TargetAsync(device, ["shell", "am", "get-current-user"],
            TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        return ParseCurrentAndroidUser(result.StandardOutput);
    }

    public static int ParseCurrentAndroidUser(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var match = Regex.Match(output, @"\A\s*(?<user>[0-9]{1,10})\s*\z", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["user"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var userId) || userId < 0)
            throw new AdbResponseException(new InvalidDataException("ADB returned an invalid current Android user."));
        return userId;
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
                throw new AdbCommandException(result.ExitCode, result.StandardError, result.StandardOutput);
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

    private static bool HasAndroidContentFailure(AdbCommandResult result)
    {
        var output = result.StandardOutput + "\n" + result.StandardError;
        return output.Contains("Error while accessing provider", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(output, @"(?im)^\s*(?:Error\s*:|\[ERROR\])", RegexOptions.CultureInvariant);
    }

    private async Task<AdbCommandResult> RunCheckedAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        AdbCommandResult result;
        try { result = await _runner.RunAsync(_adbPath, arguments, timeout, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (AdbCommandException) { throw; }
        catch (AdbExecutionException) { throw; }
        catch (Exception ex) when (SafeErrorSummary.IsExecutionFailure(ex))
        { throw new AdbExecutionException(ex); }
        if (result.ExitCode != 0) throw new AdbCommandException(result.ExitCode, result.StandardError, result.StandardOutput);
        return result;
    }
}

public sealed class AdbCommandException : IOException
{
    public int ExitCode { get; }
    public string StandardError { get; }
    public string StandardOutput { get; }
    public AdbCommandException(int exitCode, string standardError, string standardOutput)
        : base($"ADB command failed ({nameof(AdbCommandException)}, exit code {exitCode}).")
        => (ExitCode, StandardError, StandardOutput) = (exitCode, standardError, standardOutput);
}
