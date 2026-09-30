using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace TabLink.Windows;

/// <summary>A narrow, reference-counted firewall lease for a single selected local interface.</summary>
/// <remarks>
/// Normal session shutdown must await DisposeAsync. A process crash can leave its
/// narrowly scoped persistent rule; opening the same program/IP/interface again
/// replaces that exact owned rule. Rules for other endpoints are never swept.
/// Reference counting applies within this app process, so the host should retain
/// its existing single-instance behavior.
/// </remarks>
public sealed class NetworkFirewall : IAsyncDisposable
{
    private const string OwnerGroup = "TabLink.PrivateDisplay.Session";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, int> ActiveLeases = new(StringComparer.Ordinal);
    private bool _disposed;
    public string RuleName { get; }
    private NetworkFirewall(string ruleName) => RuleName = ruleName;

    public static async Task<NetworkFirewall> OpenAsync(NetworkInterfaceChoice choice, CancellationToken cancellationToken = default, int port = 27184, string protocol = "TCP")
    {
        ArgumentNullException.ThrowIfNull(choice);
        ValidateChoice(choice);
        ValidateEndpoint(port, protocol);
        var program = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前 TabLink 程序路径。");
        program = Path.GetFullPath(program);
        if (!File.Exists(program)) throw new FileNotFoundException("找不到当前程序，无法限制防火墙规则。", program);
        var rule = GetRuleName(choice, program, port, protocol);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ActiveLeases.TryGetValue(rule, out var count))
            {
                ActiveLeases[rule] = checked(count + 1);
                return new(rule);
            }
            try { await RunAsync(BuildCreateScript(choice, program, rule, port, protocol), cancellationToken).ConfigureAwait(false); }
            catch (Exception original)
            {
                // Cancellation can arrive after New-NetFirewallRule succeeded.
                // Roll back only this exact owned rule before surfacing failure.
                try { await RunAsync(BuildRemoveScript(rule), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception cleanup) { throw new AggregateException("创建会话防火墙规则失败，清理其规则也失败。", original, cleanup); }
                throw;
            }
            ActiveLeases.Add(rule, 1);
            return new(rule);
        }
        finally { Gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (!ActiveLeases.TryGetValue(RuleName, out var count)) { _disposed = true; return; }
            if (count > 1) { ActiveLeases[RuleName] = count - 1; _disposed = true; return; }
            await RunAsync(BuildRemoveScript(RuleName), CancellationToken.None).ConfigureAwait(false);
            ActiveLeases.Remove(RuleName);
            _disposed = true;
        }
        finally { Gate.Release(); }
    }

    private static void ValidateChoice(NetworkInterfaceChoice choice)
    {
        if (!NetworkInterfaceCatalog.IsUsableAddress(choice.LocalAddress) || choice.PrefixLength is < 1 or > 32)
            throw new ArgumentException("会话必须使用有效的本地 IPv4 单播地址和子网。", nameof(choice));
        if (string.IsNullOrWhiteSpace(choice.InterfaceAlias) || choice.InterfaceAlias.IndexOfAny(['\0', '*', '?', '[', ']']) >= 0)
            throw new ArgumentException("网络接口名称无效，不能生成精确作用域规则。", nameof(choice));
        var nic = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n => string.Equals(n.Id, choice.InterfaceId, StringComparison.OrdinalIgnoreCase));
        if (nic is null || nic.OperationalStatus != OperationalStatus.Up || nic.Name != choice.InterfaceAlias)
            throw new InvalidOperationException("选中的网络接口已变化，请刷新并重新选择。");
        var properties = nic.GetIPProperties();
        if (properties.GetIPv4Properties().Index != choice.InterfaceIndex || !properties.UnicastAddresses.Any(a => a.Address.Equals(choice.LocalAddress) && a.PrefixLength == choice.PrefixLength))
            throw new InvalidOperationException("选中接口的 IPv4 地址或子网已变化，请刷新并重新选择。");
    }

    internal static string GetRuleName(NetworkInterfaceChoice choice, string program, int port = 27184, string protocol = "TCP")
    {
        ValidateEndpoint(port, protocol);
        var data = string.Join('\0', program.ToUpperInvariant(), choice.LocalAddress.ToString(), choice.InterfaceAlias, choice.PrefixLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return $"TabLink.Session.{protocol}{port}." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)))[..24];
    }

    static void ValidateEndpoint(int port, string protocol)
    {
        if(port is < 1024 or > 65535 || protocol is not ("TCP" or "UDP"))
            throw new ArgumentException("必须指定有效的会话端口和协议。");
    }

    internal static string GetSubnet(IPAddress address, int prefixLength)
    {
        if (!NetworkInterfaceCatalog.IsUsableAddress(address) || prefixLength is < 1 or > 32)
            throw new ArgumentException("An explicit IPv4 subnet is required.");
        var bytes = address.GetAddressBytes();
        var bits = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var mask = uint.MaxValue << (32 - prefixLength);
        var network = bits & mask;
        return new IPAddress(new byte[] { (byte)(network >> 24), (byte)(network >> 16), (byte)(network >> 8), (byte)network }) + "/" + prefixLength;
    }

    internal static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("NUL is not valid in a PowerShell string.");
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    internal static string BuildCreateScript(NetworkInterfaceChoice choice, string program, string rule, int port = 27184, string protocol = "TCP") =>
        BuildRemoveScript(rule) + Environment.NewLine +
        "New-NetFirewallRule -PolicyStore PersistentStore -Name " + Quote(rule) +
        " -DisplayName " + Quote("TabLink local screen session " + choice.LocalAddress) +
        " -Group " + Quote(OwnerGroup) + " -Direction Inbound -Action Allow -Enabled True -Profile Any -Protocol " + protocol + " -LocalPort " + port +
        " -Program " + Quote(program) + " -InterfaceAlias " + Quote(choice.InterfaceAlias) +
        " -LocalAddress " + Quote(choice.LocalAddress.ToString()) + " -RemoteAddress " + Quote(GetSubnet(choice.LocalAddress, choice.PrefixLength)) +
        " -EdgeTraversalPolicy Block -ErrorAction Stop | Out-Null";

    internal static string BuildRemoveScript(string rule) =>
        "$ErrorActionPreference = 'Stop'\nImport-Module NetSecurity -ErrorAction Stop\n" +
        "$tabLinkRuleName = " + Quote(rule) + "\n$tabLinkRuleGroup = " + Quote(OwnerGroup) + "\n" +
        // Enumerate with ErrorAction Stop: absence is normal, permission failures
        // must not be silently mistaken for a missing rule.
        "$tabLinkMatches = @(Get-NetFirewallRule -PolicyStore PersistentStore -ErrorAction Stop | Where-Object { $_.Name -ceq $tabLinkRuleName })\n" +
        "foreach ($tabLinkMatch in $tabLinkMatches) { if ($tabLinkMatch.Group -cne $tabLinkRuleGroup) { throw 'Firewall rule ownership mismatch; no rule removed.' } }\n" +
        "if ($tabLinkMatches.Count -gt 0) { Remove-NetFirewallRule -PolicyStore PersistentStore -Name $tabLinkRuleName -ErrorAction Stop }";

    private static async Task RunAsync(string script, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Windows 防火墙管理命令。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: false); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Windows 防火墙命令超时。");
        }
        var stdout = await output.ConfigureAwait(false);
        var stderr = await error.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException($"会话防火墙操作失败（{process.ExitCode}）：{stderr.Trim()} {stdout.Trim()}".Trim());
    }
}
