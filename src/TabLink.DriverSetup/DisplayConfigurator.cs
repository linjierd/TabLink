using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TabLink.DriverSetup;

internal static partial class DriverInstaller
{
    internal static InstallResult PrepareSingleDisplay(int width, int height, int refreshRate)
        => WithDriverLifecycleLock(() => PrepareSingleDisplayCore(width, height, refreshRate));

    // The Windows host holds the same named mutex on a dedicated thread from
    // before this helper starts until the display guard has been retired.
    internal static InstallResult PrepareSingleDisplayWithCallerLease(int width, int height, int refreshRate)
        => PrepareSingleDisplayCore(width, height, refreshRate);

    private static InstallResult PrepareSingleDisplayCore(int width, int height, int refreshRate)
    {
        ValidateProfile(width, height, refreshRate);
        using var leaseLock = DisplayConfigurationActivity.AcquireLeaseLock();
        DisplayConfigurationActivity.AssertNoLiveDisplayLeases();

        var existingBefore = FindExistingDevices();
        var protectedReceipt = ReadOwnedInstanceReceipt();
        if (existingBefore.Count > 1)
            throw new InvalidOperationException("发现多个 MttVDD 设备。TabLink 只允许一块虚拟副屏，未接管或删除任何设备。");
        if (existingBefore.Count == 1 && !IsOwnedConfiguration())
            throw new InvalidOperationException("现有 MttVDD 不是由 TabLink 配置，未接管、重载或移除该设备。");
        if (existingBefore.Count == 1 && !existingBefore[0].Equals(protectedReceipt, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("现有 MttVDD 缺少 TabLink 创建时保存的精确设备回执，未接管或移除该设备。请先显式检查旧安装。");
        var configurationExists = Directory.Exists(ConfigurationDirectory) || File.Exists(ConfigurationDirectory);
        var ownedConfiguration = IsOwnedConfiguration();
        if (configurationExists && !ownedConfiguration)
            throw new InvalidOperationException(@"C:\VirtualDisplayDriver 已存在且不属于 TabLink，未覆盖其配置。");
        var migrateWeakLegacyConfiguration = false;
        if (ownedConfiguration)
        {
            try { VerifyOwnedConfigurationSecurity(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
            {
                if (existingBefore.Count != 0 || protectedReceipt is not null)
                    throw new InvalidOperationException("现有 TabLink 配置权限不安全，且设备或受保护回执仍存在；自动迁移已拒绝。请先停止连接并显式检查旧安装。", ex);
                migrateWeakLegacyConfiguration = true;
            }
        }

        var settings = Path.Combine(ConfigurationDirectory, "vdd_settings.xml");
        byte[]? original = null;
        byte[]? desired = null;
        var originalWasMulti = false;
        var configurationCreated = false;
        var configurationReplaced = false;
        string? staged = null;
        string? backup = null;
        string? legacyConfigurationBackup = null;
        string? installedInstance = null;
        try
        {
            // A previous TabLink version or a crashed owner may have left the
            // exact receipted node attached. With no live lease and while the
            // cross-process lifecycle mutex is held, retire only its proven,
            // non-primary targets before changing XML or restarting the node.
            if (existingBefore.Count == 1)
                RetireOwnedResidualOutputs(existingBefore[0]);

            if (migrateWeakLegacyConfiguration)
            {
                // Never read or grant privileges inside the legacy writable
                // tree. Move the whole directory aside on the same volume,
                // keep it as a user-visible backup, then exclusively create a
                // new fixed path with the strict ACL already in CreateDirectory.
                desired = BuildSingleDisplayConfiguration(Encoding.UTF8.GetBytes(ConfigurationXml), width, height, refreshRate);
                legacyConfigurationBackup = MoveWeakLegacyConfigurationAside();
                CreateSecureConfigurationDirectoryExclusive();
                configurationCreated = true;
                WriteNew(settings, Encoding.UTF8.GetString(desired));
                WriteNew(Path.Combine(ConfigurationDirectory, "tablink-owner.txt"), "Created by TabLink 0.1; official VirtualDrivers package 25.7.23.\n");
                HardenOwnedConfigurationSecurity();
            }
            else if (ownedConfiguration)
            {
                VerifyOwnedConfigurationSecurity();
                original = File.ReadAllBytes(settings);
                originalWasMulti = ReadConfiguredCount(original) != 1;
                // A legacy no-node directory had a writable ACL. Do not trust
                // or preserve its options; rebuild the one-screen baseline,
                // then add only the authenticated receiver's requested mode.
                desired = BuildSingleDisplayConfiguration(
                    existingBefore.Count == 0 && protectedReceipt is null ? Encoding.UTF8.GetBytes(ConfigurationXml) : original,
                    width, height, refreshRate);
                HardenOwnedConfigurationSecurity();
                if (!original.AsSpan().SequenceEqual(desired))
                {
                    var suffix = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                    staged = Path.Combine(ConfigurationDirectory, "tablink-single-" + suffix + ".tmp");
                    backup = Path.Combine(ConfigurationDirectory, "vdd_settings.before-single-" + suffix + ".xml");
                    WriteDurableNew(staged, desired);
                    if (!IsOwnedConfiguration() || !File.ReadAllBytes(settings).AsSpan().SequenceEqual(original))
                        throw new IOException("虚拟显示配置在准备期间发生变化，未覆盖该文件。");
                    DisplayConfigurationActivity.AssertNoLiveDisplayLeases();
                    File.Replace(staged, settings, backup, false);
                    configurationReplaced = true;
                }
            }
            else
            {
                var source = Path.Combine(AppContext.BaseDirectory, "drivers", "VirtualDisplayDriver");
                ValidatePackage(source);
                desired = BuildSingleDisplayConfiguration(Encoding.UTF8.GetBytes(ConfigurationXml), width, height, refreshRate);
                CreateSecureConfigurationDirectoryExclusive();
                configurationCreated = true;
                if ((File.GetAttributes(ConfigurationDirectory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("驱动配置目录是重解析点，无法安全写入。");
                ApplyConfigurationDirectorySecurity();
                WriteNew(settings, Encoding.UTF8.GetString(desired));
                WriteNew(Path.Combine(ConfigurationDirectory, "tablink-owner.txt"), "Created by TabLink 0.1; official VirtualDrivers package 25.7.23.\n");
                HardenOwnedConfigurationSecurity();
            }

            DisplayConfigurationActivity.AssertNoLiveDisplayLeases();
            var current = FindExistingDevices();
            if (current.Count > 1 || existingBefore.Count == 1 &&
                (current.Count != 1 || !current[0].Equals(existingBefore[0], StringComparison.OrdinalIgnoreCase)))
                throw new IOException("虚拟显示设备身份在准备期间发生变化，未继续连接。");

            bool rebootRequired;
            string exactInstance;
            if (current.Count == 0)
            {
                var installed = Install();
                if (!installed.Success || installed.State != "installed" || installed.InstanceId is null)
                    throw new IOException(installed.Message);
                installedInstance = installed.InstanceId;
                exactInstance = installed.InstanceId;
                rebootRequired = installed.RebootRequired;
            }
            else
            {
                exactInstance = current[0];
                AssertNoActiveOutput(exactInstance);
                DisplayConfigurationActivity.AssertNoLiveDisplayLeases();
                if (!GetSingleExistingInstance().Equals(exactInstance, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("虚拟显示设备身份在重载前发生变化。");
                rebootRequired = PnpCommand.Run("/restart-device", exactInstance).RebootRequired;
            }

            CollectPoolTargets(exactInstance, 1, rebootRequired, allowConnectionListener: true);
            var targets = TabLink.Windows.VirtualDisplayManager.GetTargets()
                .Where(t => t.AdapterInstanceId.Equals(exactInstance, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (targets.Length != 1 || targets[0].IsPrimary || targets[0].IsActive)
                throw new IOException("唯一虚拟副屏的数量、非主屏状态或空闲状态未通过最终验证。");
            return new(true, "singleDisplayReady",
                $"已按 {width} × {height}、{refreshRate} Hz 准备唯一一块 TabLink 虚拟副屏；设备连接后才会加入桌面。" +
                    (legacyConfigurationBackup is null ? "" : "\n旧弱权限配置已保留为迁移前备份：" + legacyConfigurationBackup),
                exactInstance, false, DateTimeOffset.UtcNow);
        }
        catch (Exception originalError)
        {
            Exception? rollbackError = null;
            try
            {
                var rollbackInstance = installedInstance ?? existingBefore.SingleOrDefault();
                if (rollbackInstance is not null && FindExistingDevices().Any(x => x.Equals(rollbackInstance, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!rollbackInstance.Equals(ReadOwnedInstanceReceipt(), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("回滚设备与受保护的精确实例回执不匹配，未扩大删除范围。");
                    AssertNoActiveOutput(rollbackInstance);
                    RemoveExactInstanceAndWait(rollbackInstance, "单屏准备失败回滚");
                }
                if (rollbackInstance is not null && rollbackInstance.Equals(ReadOwnedInstanceReceipt(), StringComparison.OrdinalIgnoreCase))
                    DeleteOwnedInstanceReceipt(rollbackInstance);

                if (originalWasMulti)
                {
                    // Never reactivate a legacy 3/4-screen configuration as a
                    // rollback. Keep (or rewrite) count=1 and leave the exact
                    // owned node removed, so a failure cannot make the desktop
                    // heavy again.
                    if (desired is null || !IsOwnedConfiguration())
                        throw new IOException("无法证明单屏配置可安全保留。");
                    if (!File.ReadAllBytes(settings).AsSpan().SequenceEqual(desired))
                    {
                        var singleFallback = Path.Combine(ConfigurationDirectory, "tablink-single-fallback-" + Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            WriteDurableNew(singleFallback, desired);
                            File.Replace(singleFallback, settings, null, false);
                        }
                        finally { try { File.Delete(singleFallback); } catch { } }
                    }
                    HardenOwnedConfigurationSecurity();
                }
                else if (configurationReplaced && original is not null)
                {
                    if (!IsOwnedConfiguration()) throw new IOException("配置所有权已变化，不能自动恢复原配置。");
                    staged ??= Path.Combine(ConfigurationDirectory, "tablink-single-rollback-" + Guid.NewGuid().ToString("N") + ".tmp");
                    WriteDurableNew(staged, original);
                    File.Replace(staged, settings, null, false);
                    HardenOwnedConfigurationSecurity();
                    // The node was deliberately removed above. A later
                    // connection will install it again only after preparation.
                }
                if (configurationCreated) CleanupConfiguration();
            }
            catch (Exception ex) { rollbackError = ex; }
            if (rollbackError is not null)
                throw new InvalidOperationException(originalError.Message + "\n单屏准备失败，且回滚未完成：" + rollbackError.Message +
                    (legacyConfigurationBackup is null ? "" : "\n旧配置备份保留在：" + legacyConfigurationBackup), originalError);
            throw new InvalidOperationException(originalError.Message + (originalWasMulti
                ? "\n已移除本次精确设备并保留 count=1；没有恢复旧的多屏配置。"
                : "\n本次创建的设备和配置更改已安全回收。") +
                (legacyConfigurationBackup is null ? "" : "\n旧配置备份保留在：" + legacyConfigurationBackup), originalError);
        }
        finally
        {
            if (staged is not null && Path.GetDirectoryName(Path.GetFullPath(staged))!.Equals(ConfigurationDirectory, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(staged); } catch { }
        }
    }

    internal static InstallResult RemoveSessionDisplay() => WithDriverLifecycleLock(RemoveSessionDisplayCore);

    // The Windows host keeps the lifecycle mutex for the complete session and
    // invokes this form before handing the mutex to any waiting process.
    internal static InstallResult RemoveSessionDisplayWithCallerLease() => RemoveSessionDisplayCore();

    private static InstallResult RemoveSessionDisplayCore()
    {
        using var leaseLock = DisplayConfigurationActivity.AcquireLeaseLock();
        DisplayConfigurationActivity.AssertNoLiveDisplayLeases();
        var instances = FindExistingDevices();
        if (instances.Count == 0)
        {
            var staleReceipt = ReadOwnedInstanceReceipt();
            if (staleReceipt is not null) DeleteOwnedInstanceReceipt(staleReceipt);
            return new(true, "notPresent", "TabLink 虚拟显示设备已不存在。", null, false, DateTimeOffset.UtcNow);
        }
        if (instances.Count != 1)
            throw new InvalidOperationException("发现多个 MttVDD 设备；自动清理不会猜测或批量移除。");
        if (!IsOwnedConfiguration())
            throw new InvalidOperationException("现有 MttVDD 配置不属于 TabLink，自动清理未移除它。");
        var exactInstance = instances[0];
        if (!exactInstance.Equals(ReadOwnedInstanceReceipt(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("现有 MttVDD 与 TabLink 保存的精确设备回执不匹配，自动清理未移除它。");
        AssertNoActiveOutput(exactInstance);
        DisplayConfigurationActivity.AssertNoLiveDisplayLeases();
        var current = FindExistingDevices();
        if (current.Count != 1 || !current[0].Equals(exactInstance, StringComparison.OrdinalIgnoreCase))
            throw new IOException("虚拟显示设备身份在移除前发生变化，已停止。");
        AssertNoActiveOutput(exactInstance);
        RemoveExactInstanceAndWait(exactInstance, "最后一个连接断开");
        try { DeleteOwnedInstanceReceipt(exactInstance); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { throw new IOException("设备已移除，但未能清除精确设备回执；下次连接将拒绝接管，避免误删其他设备。", ex); }
        return new(true, "sessionDisplayRemoved", "最后一个连接已结束，TabLink 虚拟显示设备已卸载。驱动包和安全配置保留供下次按需安装。",
            exactInstance, false, DateTimeOffset.UtcNow);
    }

    private static void WriteDurableNew(string path, byte[] bytes)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(true);
    }

    private static void RetireOwnedResidualOutputs(string exactInstance)
    {
        if (!HasActiveOutput(exactInstance)) return;
        DisplayConfigurationActivity.AssertNoLiveDisplayLeases();
        if (!exactInstance.Equals(ReadOwnedInstanceReceipt(), StringComparison.OrdinalIgnoreCase) ||
            FindExistingDevices() is not [var current] || !current.Equals(exactInstance, StringComparison.OrdinalIgnoreCase))
            throw new IOException("残留输出回收前，虚拟显示设备身份或受保护回执已变化。");
        var targets = TabLink.Windows.VirtualDisplayManager.GetTargets()
            .Where(t => t.AdapterInstanceId.Equals(exactInstance, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (targets.Length == 0 || targets.Any(t => t.IsPrimary))
            throw new IOException("无法将活动残留输出完整证明为非主屏，未更改配置或驱动。");
        foreach (var target in targets.Where(t => t.IsActive))
        {
            DisplayConfigurationActivity.AssertNoLiveDisplayLeases();
            if (!exactInstance.Equals(ReadOwnedInstanceReceipt(), StringComparison.OrdinalIgnoreCase))
                throw new IOException("残留输出回收期间，受保护设备回执已变化。");
            var detached = TabLink.Windows.VirtualDisplayManager.DetachIdleTarget(target);
            if (!detached.Success)
                throw new IOException("无法安全收回上次异常退出留下的虚拟输出：" + detached.Message);
        }
        AssertNoActiveOutput(exactInstance);
        DisplayConfigurationActivity.AssertNoLiveDisplayLeases();
        if (FindExistingDevices() is not [var verified] || !verified.Equals(exactInstance, StringComparison.OrdinalIgnoreCase) ||
            !exactInstance.Equals(ReadOwnedInstanceReceipt(), StringComparison.OrdinalIgnoreCase))
            throw new IOException("残留输出回收后，虚拟显示设备身份或受保护回执已变化。");
    }

    private static string MoveWeakLegacyConfigurationAside()
    {
        if (!Directory.Exists(ConfigurationDirectory) || File.Exists(ConfigurationDirectory) ||
            (File.GetAttributes(ConfigurationDirectory) & FileAttributes.ReparsePoint) != 0 || !IsOwnedConfiguration())
            throw new IOException("旧虚拟显示配置在迁移前已变化，未读取、提权或覆盖该路径。");
        var backup = ConfigurationDirectory + ".legacy-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(backup) || File.Exists(backup))
            throw new IOException("唯一迁移备份路径已被占用，未移动旧配置。");
        Directory.Move(ConfigurationDirectory, backup);
        if (Directory.Exists(ConfigurationDirectory) || File.Exists(ConfigurationDirectory))
            throw new IOException("旧配置移走后固定路径被其他程序占用；备份已保留，未覆盖新路径：" + backup);
        return backup;
    }

    private static void RemoveExactInstanceAndWait(string exactInstance, string operation)
    {
        AssertNoActiveOutput(exactInstance);
        var current = FindExistingDevices();
        if (current.Count != 1 || !current[0].Equals(exactInstance, StringComparison.OrdinalIgnoreCase) ||
            !exactInstance.Equals(ReadOwnedInstanceReceipt(), StringComparison.OrdinalIgnoreCase))
            throw new IOException(operation + "前，精确设备实例或受保护回执已变化。");
        var result = PnpCommand.Run("/remove-device", exactInstance);
        if (result.RebootRequired)
            throw new IOException(operation + "要求重启才能完成设备移除；没有把该节点报告为已卸载。");
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var remaining = FindExistingDevices();
            if (remaining.Count == 0) return;
            if (remaining.Count != 1 || !remaining[0].Equals(exactInstance, StringComparison.OrdinalIgnoreCase))
                throw new IOException(operation + "期间出现了另一个 MttVDD 实例，停止等待且未扩大删除范围。");
            Thread.Sleep(100);
        }
        throw new IOException(operation + "后，Windows 未在限定时间内确认精确虚拟显示设备已移除。");
    }

    private static T WithDriverLifecycleLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, @"Global\TabLink.SingleDisplayDriverLifecycle.1");
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("另一个 TabLink 进程正在准备或使用唯一虚拟显示设备。");
            return action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    internal static InstallResult CollectOwnedIdlePool()
    {
        using var leaseLock = DisplayConfigurationActivity.AcquireLeaseLock();
        DisplayConfigurationActivity.AssertIdle();
        if (!IsOwnedConfiguration())
            throw new InvalidOperationException("仅允许收回 TabLink 自有配置的虚拟显示池。");
        VerifyOwnedConfigurationSecurity();
        var exactInstance = GetSingleExistingInstance();
        if (!exactInstance.Equals(ReadOwnedInstanceReceipt(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("虚拟显示设备与受保护的 TabLink 精确实例回执不匹配，未收回任何输出。");
        var expectedCount = ReadConfiguredCount(File.ReadAllBytes(Path.Combine(ConfigurationDirectory, "vdd_settings.xml")));
        if (expectedCount != 1)
            throw new InvalidOperationException("旧配置包含多块虚拟屏；此维护命令不会继续使用多屏配置，请改用连接时的单屏准备流程。");
        var targets = TabLink.Windows.VirtualDisplayManager.GetTargets()
            .Where(t => t.AdapterInstanceId.Equals(exactInstance, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (targets.Length != expectedCount || targets.Any(t => t.IsPrimary))
            throw new InvalidOperationException("显示池数量、独立目标身份或非主屏状态未通过核对，未收回任何输出。");
        CollectPoolTargets(exactInstance, expectedCount, false);
        return new(true, "idlePoolCollected", "已收回所有已确认且无人占用的 TabLink 虚拟输出，未重启驱动或修改主屏。", exactInstance, false, DateTimeOffset.UtcNow);
    }

    internal static InstallResult ConfigureDisplay(int width, int height, int refreshRate)
    {
        ValidateProfile(width, height, refreshRate);
        return ConfigureOwnedDisplay(bytes => BuildSingleDisplayConfiguration(bytes, width, height, refreshRate),
            $"配置唯一虚拟屏，并添加 {width} × {height} 与 {height} × {width}、{refreshRate} Hz 和60 Hz模式", null);
    }

    internal static InstallResult ConfigurePool(int targetCount)
    {
        if (targetCount != 1) throw new ArgumentOutOfRangeException(nameof(targetCount), "TabLink 只允许配置一块虚拟扩展屏。");
        return ConfigureOwnedDisplay(bytes => BuildPoolConfiguration(bytes, targetCount),
            "配置唯一一块虚拟显示目标，并保留已有模式及添加浏览器横竖屏30/60 Hz模式", targetCount);
    }

    private static InstallResult ConfigureOwnedDisplay(Func<byte[], byte[]> transform, string description, int? poolCount)
    {
        using var leaseLock = DisplayConfigurationActivity.AcquireLeaseLock();
        DisplayConfigurationActivity.AssertIdle();
        if (!IsOwnedConfiguration())
            throw new InvalidOperationException(@"C:\VirtualDisplayDriver 不是 TabLink 创建的配置目录，未写入配置或重启任何驱动。");
        VerifyOwnedConfigurationSecurity();
        var exactInstance = GetSingleExistingInstance();
        if (!exactInstance.Equals(ReadOwnedInstanceReceipt(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("虚拟显示设备与受保护的 TabLink 精确实例回执不匹配，未更改配置。");
        // Verification must precede any SetAccessControl call. A legacy weak
        // directory may migrate only in PrepareSingleDisplay's no-node,
        // no-receipt transaction; maintenance commands fail closed.
        HardenOwnedConfigurationSecurity();
        AssertNoActiveOutput(exactInstance);
        var settings = Path.Combine(ConfigurationDirectory, "vdd_settings.xml");
        var original = File.ReadAllBytes(settings);
        var updated = transform(original);
        var oldPoolCount = ReadConfiguredCount(original);
        var updatedPoolCount = ReadConfiguredCount(updated);
        if (original.AsSpan().SequenceEqual(updated))
        {
            AssertNoActiveOutput(exactInstance);
            DisplayConfigurationActivity.AssertIdle();
            if (!GetSingleExistingInstance().Equals(exactInstance, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("驱动身份在重新加载前发生变化，未重启任何设备。");
            var refreshed = PnpCommand.Run("/restart-device", exactInstance);
            CollectPoolTargets(exactInstance, updatedPoolCount, refreshed.RebootRequired);
            return new(true, "alreadyConfigured", "指定配置已存在，已仅重新加载该虚拟驱动并收回全部空闲输出。请刷新后创建设备会话。", exactInstance, refreshed.RebootRequired, DateTimeOffset.UtcNow);
        }

        var suffix = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        var staged = Path.Combine(ConfigurationDirectory, "tablink-profile-" + suffix + ".tmp");
        var backup = Path.Combine(ConfigurationDirectory, "vdd_settings.before-profile-" + suffix + ".xml");
        var replaced = false;
        try
        {
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(updated); output.Flush(true); }
            if (!IsOwnedConfiguration() || !File.ReadAllBytes(settings).AsSpan().SequenceEqual(original) ||
                !GetSingleExistingInstance().Equals(exactInstance, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("驱动或配置在准备期间发生变化，未写入显示模式。");
            AssertNoActiveOutput(exactInstance);
            DisplayConfigurationActivity.AssertIdle();
            // Replace only the fixed, verified owned file. Keep an exact backup.
            File.Replace(staged, settings, backup, false);
            replaced = true;
            if (!GetSingleExistingInstance().Equals(exactInstance, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("驱动身份在重新加载前发生变化，未重启任何设备。");
            AssertNoActiveOutput(exactInstance);
            DisplayConfigurationActivity.AssertIdle();
            var restart = PnpCommand.Run("/restart-device", exactInstance);
            CollectPoolTargets(exactInstance, updatedPoolCount, restart.RebootRequired);
            return new(true, poolCount.HasValue ? "poolConfigured" : "displayConfigured", "已为 TabLink " + description +
                "。仅重新加载了 " + exactInstance + "，已收回全部空闲虚拟输出。请刷新显示器并创建新会话；旧 LUID 可能已失效。\n原配置备份：" + backup +
                (restart.RebootRequired ? "\nWindows 要求重启后生效，请自行保存工作并重启。" : ""), exactInstance, restart.RebootRequired, DateTimeOffset.UtcNow);
        }
        catch (Exception originalError)
        {
            if (replaced)
            {
                var retiredLegacyPool = false;
                try
                {
                    if (!IsOwnedConfiguration() || !File.ReadAllBytes(settings).AsSpan().SequenceEqual(updated))
                        throw new InvalidOperationException("当前配置已被其他程序改变，未覆盖该文件。");
                    if (oldPoolCount != 1)
                    {
                        // Recreating the old pool would violate the global
                        // one-screen invariant. Keep the already-written
                        // count=1 configuration and remove only the exact
                        // receipted node instead of restarting 3/4 outputs.
                        if (!exactInstance.Equals(ReadOwnedInstanceReceipt(), StringComparison.OrdinalIgnoreCase))
                            throw new IOException("精确设备回执在单屏失败回收前发生变化。");
                        RemoveExactInstanceAndWait(exactInstance, "旧多屏配置失败回收");
                        DeleteOwnedInstanceReceipt(exactInstance);
                        HardenOwnedConfigurationSecurity();
                        retiredLegacyPool = true;
                    }
                    else
                    {
                        using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        { output.Write(original); output.Flush(true); }
                        File.Replace(staged, settings, null, false);
                        HardenOwnedConfigurationSecurity();
                        AssertNoActiveOutput(exactInstance);
                        DisplayConfigurationActivity.AssertIdle();
                        if (!GetSingleExistingInstance().Equals(exactInstance, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("原XML已恢复，但适配器身份已改变，未重启其他设备。");
                        var rollbackRestart = PnpCommand.Run("/restart-device", exactInstance);
                        CollectPoolTargets(exactInstance, oldPoolCount, rollbackRestart.RebootRequired);
                    }
                }
                catch (Exception rollback)
                { throw new InvalidOperationException(originalError.Message + "\n原配置备份：" + backup + "\n恢复原配置或驱动状态未完成：" + rollback.Message, originalError); }
                if (retiredLegacyPool)
                    throw new InvalidOperationException(originalError.Message + "\n已保留 count=1 并移除旧多屏设备；未恢复多屏配置。", originalError);
                throw new InvalidOperationException(originalError.Message + "\n已恢复原配置和原显示池数量，并收回其空闲输出；未更改其他设备。原配置备份：" + backup, originalError);
            }
            throw;
        }
        finally
        {
            // Only the exact GUID temporary file created above; never recursive.
            if (Path.GetDirectoryName(Path.GetFullPath(staged))!.Equals(ConfigurationDirectory, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(staged); } catch { }
        }
    }

    private static string GetSingleExistingInstance()
    {
        var instances = FindExistingDevices();
        if (instances.Count != 1)
            throw new InvalidOperationException("需要且仅允许一个已安装的 MttVDD 设备，未更改显示模式。");
        return instances[0];
    }

    private static void CollectPoolTargets(string exactInstance, int expectedCount, bool rebootRequired,
        bool allowConnectionListener = false)
    {
        if (rebootRequired) throw new IOException("Windows要求重启才能完成驱动配置，尚不能验证及收回显示池；未自动重启电脑。");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? idleSince = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            AssertConfigurationActivity(allowConnectionListener);
            if (!IsOwnedConfiguration()) throw new IOException("自有配置标记已变化，停止回收输出。");
            if (!GetSingleExistingInstance().Equals(exactInstance, StringComparison.OrdinalIgnoreCase))
                throw new IOException("显示池初始化期间适配器身份发生变化，未操作其他设备。");
            var targets = TabLink.Windows.VirtualDisplayManager.GetTargets().Where(t => t.AdapterInstanceId.Equals(exactInstance, StringComparison.OrdinalIgnoreCase)).ToArray();
            // Driver targets can arrive one by one. Collect each proven output
            // immediately even when the complete configured count is not ready.
            foreach (var target in targets.Where(t => t.IsActive))
            {
                AssertConfigurationActivity(allowConnectionListener);
                var result = TabLink.Windows.VirtualDisplayManager.DetachIdleTarget(target);
                if (!result.Success) throw new IOException("回收显示池空闲输出失败：" + result.Message);
            }
            if (targets.Length == expectedCount)
            {
                if (!TabLink.Windows.VirtualDisplayManager.GetTargets().Any(t => t.AdapterInstanceId.Equals(exactInstance, StringComparison.OrdinalIgnoreCase) && t.IsActive))
                {
                    idleSince ??= watch.Elapsed;
                    if (watch.Elapsed - idleSince.Value >= TimeSpan.FromMilliseconds(1500)) return;
                }
                else idleSince = null;
            }
            else idleSince = null;
            Thread.Sleep(150);
        }
        throw new IOException("显示池数量或全部空闲状态未在15秒内通过验证，未将本次配置报告为成功。");
    }

    private static void AssertConfigurationActivity(bool allowConnectionListener)
    {
        if (allowConnectionListener) DisplayConfigurationActivity.AssertNoLiveDisplayLeases();
        else DisplayConfigurationActivity.AssertIdle();
    }

}
