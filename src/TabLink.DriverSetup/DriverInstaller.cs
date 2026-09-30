using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TabLink.DriverSetup;

internal static partial class DriverInstaller
{
    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
    private const string HardwareId = @"Root\MttVDD";
    private const string ConfigurationDirectory = @"C:\VirtualDisplayDriver";
    private static readonly Dictionary<string, string> ExpectedHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MttVDD.inf"] = "550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D",
        ["MttVDD.dll"] = "C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05",
        ["mttvdd.cat"] = "08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6"
    };

    internal static InstallResult Install()
    {
        if (!Environment.Is64BitOperatingSystem || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("此驱动包仅支持 Windows x64。");

        // Include non-present/disabled nodes: never create duplicates or silently
        // take over another installation's configuration.
        var existing = FindExisting();
        if (existing is not null)
            return new(true, "alreadyPresent", "已存在 Virtual Display Driver，未重复安装或覆盖其配置。请返回 TabLink 刷新；如副屏未启用，请在 Windows 显示设置中检查。", existing, false, DateTimeOffset.UtcNow);

        var source = Path.Combine(AppContext.BaseDirectory, "drivers", "VirtualDisplayDriver");
        ValidatePackage(source);
        if ((Directory.Exists(ConfigurationDirectory) || File.Exists(ConfigurationDirectory)) && !IsOwnedConfiguration())
            throw new InvalidOperationException(@"C:\VirtualDisplayDriver 已存在。为了保留已有配置，TabLink 没有写入任何文件。请先检查现有 Virtual Display Driver 安装。");

        // A unique staging directory under Windows Temp prevents installation from
        // using the downloaded, mutable package after it has been verified. Files
        // stay open without write/delete sharing through the installation call.
        var staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "TabLink.DriverSetup." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var set = new IntPtr(-1);
        var info = new SP_DEVINFO_DATA();
        var registered = false;
        var configurationCreated = false;
        var locks = new List<FileStream>();
        var copiedFiles = new List<string>();
        try
        {
            foreach (var name in ExpectedHashes.Keys)
            {
                var path = Path.Combine(staging, name);
                File.Copy(Path.Combine(source, name), path, false);
                copiedFiles.Add(path);
                locks.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            ValidatePackage(staging);

            // Recheck immediately before mutation, after verification/staging.
            if (FindExisting() is not null)
                throw new InvalidOperationException("检测到其他程序刚创建了 Virtual Display Driver；本次安装已停止。");
            if ((Directory.Exists(ConfigurationDirectory) || File.Exists(ConfigurationDirectory)) && !IsOwnedConfiguration())
                throw new InvalidOperationException("显示驱动配置目录刚被其他程序创建；本次安装已停止。");

            if (!IsOwnedConfiguration())
            {
                // Win32 create is exclusive: a concurrently created directory is
                // never adopted as ours. Existing TabLink configuration is reused.
                CreateSecureConfigurationDirectoryExclusive();
                configurationCreated = true;
                if ((File.GetAttributes(ConfigurationDirectory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("驱动配置目录是重解析点，无法安全写入。");
                ApplyConfigurationDirectorySecurity();
                WriteNew(Path.Combine(ConfigurationDirectory, "vdd_settings.xml"), ConfigurationXml);
                WriteNew(Path.Combine(ConfigurationDirectory, "tablink-owner.txt"), "Created by TabLink 0.1; official VirtualDrivers package 25.7.23.\n");
                HardenOwnedConfigurationSecurity();
            }
            else VerifyOwnedConfigurationSecurity();

            var classId = DisplayClass;
            set = SetupDiCreateDeviceInfoList(ref classId, IntPtr.Zero);
            if (set == new IntPtr(-1)) ThrowLastError("创建设备信息集合失败");
            info.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();
            if (!SetupDiCreateDeviceInfo(set, "Display", ref classId, "TabLink Virtual Display (VirtualDrivers)", IntPtr.Zero, 1, ref info))
                ThrowLastError("创建虚拟设备记录失败");

            var hardwareIds = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryProperty(set, ref info, 1, hardwareIds, (uint)hardwareIds.Length))
                ThrowLastError("设置虚拟设备硬件标识失败");
            if (!SetupDiCallClassInstaller(0x19, set, ref info)) // DIF_REGISTERDEVICE
                ThrowLastError("注册虚拟显示设备失败");
            registered = true;

            var instance = new StringBuilder(1024);
            if (!SetupDiGetDeviceInstanceId(set, ref info, instance, instance.Capacity, out _))
                ThrowLastError("读取新虚拟显示设备标识失败");
            // The root ID was checked twice above; this exact ID is unique to this
            // driver. Never use FORCE, reboot flags, wildcards, or vendor drivers.
            if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HardwareId, Path.Combine(staging, "MttVDD.inf"), 0, out var reboot))
                ThrowLastError("Windows 未能安装已签名的虚拟显示驱动");

            // Persist the only exact instance this installation created. The
            // automatic disconnect path requires this receipt in addition to
            // the owned configuration marker and never removes a sole foreign
            // Root\MttVDD merely because it is the only one present.
            WriteOwnedInstanceReceipt(instance.ToString());

            return new(true, "installed", reboot
                ? "虚拟显示驱动已安装。Windows 要求重启后才能完成，请保存工作后自行重启。"
                : "虚拟显示驱动已安装。请返回 TabLink 刷新显示器；如未显示可用副屏，在 Windows 显示设置中将新增显示器设为“扩展桌面”。",
                instance.ToString(), reboot, DateTimeOffset.UtcNow);
        }
        catch (Exception original)
        {
            if (registered && !SetupDiRemoveDevice(set, ref info))
                throw new InvalidOperationException(original.Message + "\n回滚新创建设备失败（Windows 错误 " + Marshal.GetLastWin32Error() + "）。仅本次创建的 MttVDD 设备可能需要在设备管理器中手动移除。配置文件已保留。", original);
            if (configurationCreated)
                CleanupConfiguration();
            throw;
        }
        finally
        {
            if (set != new IntPtr(-1)) SetupDiDestroyDeviceInfoList(set);
            foreach (var file in locks) file.Dispose();
            // Only known files in the GUID directory are removed, never recursively.
            foreach (var path in copiedFiles) { try { File.Delete(path); } catch { } }
            try { Directory.Delete(staging, false); } catch { }
        }
    }

    internal static InstallResult Control(string operation)
    {
        // The disconnect/uninstall path is never allowed to infer ownership
        // merely from there being one Root\MttVDD node. It requires the
        // protected exact-instance receipt and the owned configuration.
        if (operation == "--uninstall") return RemoveSessionDisplay();
        throw new ArgumentException("Unknown virtual display operation.", nameof(operation));
    }

    private static bool IsOwnedConfiguration()
    {
        if (!Directory.Exists(ConfigurationDirectory) || (File.GetAttributes(ConfigurationDirectory) & FileAttributes.ReparsePoint) != 0) return false;
        var marker = Path.Combine(ConfigurationDirectory, "tablink-owner.txt");
        var settings = Path.Combine(ConfigurationDirectory, "vdd_settings.xml");
        if (!File.Exists(marker) || !File.Exists(settings)) return false;
        if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(settings) & FileAttributes.ReparsePoint) != 0) return false;
        return File.ReadAllText(marker).StartsWith("Created by TabLink 0.1; official VirtualDrivers package 25.7.23.", StringComparison.Ordinal);
    }

    private static void WriteNew(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void CleanupConfiguration()
    {
        // Remove only our freshly created, recognizable files. If anything else
        // appeared concurrently, the nonrecursive directory delete will refuse it.
        try
        {
            if (!IsOwnedConfiguration() || (File.GetAttributes(ConfigurationDirectory) & FileAttributes.ReparsePoint) != 0) return;
            VerifyOwnedConfigurationSecurity();
            var settings = Path.Combine(ConfigurationDirectory, "vdd_settings.xml");
            if (File.Exists(settings)) File.Delete(settings);
            var owner = Path.Combine(ConfigurationDirectory, "tablink-owner.txt");
            if (File.Exists(owner)) File.Delete(owner);
            Directory.Delete(ConfigurationDirectory, false);
        }
        catch { /* Preserve files when rollback cannot positively establish ownership. */ }
    }

    private static void ValidatePackage(string directory)
    {
        foreach (var pair in ExpectedHashes)
        {
            var path = Path.Combine(directory, pair.Key);
            using var file = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(file));
            if (!actual.Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("驱动文件校验失败：" + pair.Key + "。请使用完整的原始 TabLink 安装包。");
        }
        Authenticode.Verify(Path.Combine(directory, "MttVDD.dll"));
        Authenticode.Verify(Path.Combine(directory, "mttvdd.cat"));
    }

    private static string? FindExisting() => FindExistingDevices().FirstOrDefault();

    private static List<string> FindExistingDevices()
    {
        var instances = new List<string>();
        var classId = DisplayClass;
        var set = SetupDiGetClassDevs(ref classId, null, IntPtr.Zero, 0);
        if (set == new IntPtr(-1)) ThrowLastError("读取现有显示设备失败");
        try
        {
            for (uint index = 0; ; index++)
            {
                var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    if (Marshal.GetLastWin32Error() == 259) return instances;
                    ThrowLastError("读取显示设备记录失败");
                }
                var buffer = new byte[8192];
                if (!SetupDiGetDeviceRegistryProperty(set, ref info, 1, out _, buffer, (uint)buffer.Length, out var needed))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 13) continue; // Device has no hardware ID property.
                    throw new Win32Exception(error, "读取现有显示设备硬件标识失败");
                }
                var ids = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(needed, buffer.Length)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (!ids.Any(id => id.Equals(HardwareId, StringComparison.OrdinalIgnoreCase) || id.Equals("MttVDD", StringComparison.OrdinalIgnoreCase))) continue;
                var instance = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceId(set, ref info, instance, instance.Capacity, out _))
                    ThrowLastError("读取现有虚拟显示设备标识失败");
                instances.Add(instance.ToString());
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static void ThrowLastError(string message) => throw new Win32Exception(Marshal.GetLastWin32Error(), message);

    private const string ConfigurationXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <!-- Created by TabLink 0.1. One display only; no physical display changes. -->
        <vdd_settings>
          <monitors><count>1</count></monitors>
          <gpu><friendlyname>default</friendlyname></gpu>
          <global><g_refresh_rate>60</g_refresh_rate></global>
          <resolutions>
            <resolution><width>1600</width><height>900</height><refresh_rate>60</refresh_rate></resolution>
            <resolution><width>1920</width><height>1080</height><refresh_rate>60</refresh_rate></resolution>
            <resolution><width>1280</width><height>800</height><refresh_rate>60</refresh_rate></resolution>
          </resolutions>
          <options>
            <CustomEdid>false</CustomEdid><PreventSpoof>false</PreventSpoof><EdidCeaOverride>false</EdidCeaOverride>
            <HardwareCursor>true</HardwareCursor><SDR10bit>false</SDR10bit><HDRPlus>false</HDRPlus>
            <logging>false</logging><debuglogging>false</debuglogging>
          </options>
        </vdd_settings>
        """;

    [StructLayout(LayoutKind.Sequential)] private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classId, IntPtr parent);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiCreateDeviceInfo(IntPtr set, string name, ref Guid classId, string description, IntPtr parent, uint flags, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA info, uint property, byte[] buffer, uint size);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiRemoveDevice(IntPtr set, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA info, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid classId, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA info, uint property, out uint type, byte[] buffer, uint size, out uint required);
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr parent, string hardwareId, string infPath, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
}
