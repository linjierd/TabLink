using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using TabLink.Core;

// Child-process modes exercise ArgumentList and timeout handling without ever executing adb.
if (args.FirstOrDefault() == "--test-process")
{
    if (args.ElementAtOrDefault(1) == "sleep") await Task.Delay(TimeSpan.FromSeconds(30));
    else Console.Write(JsonSerializer.Serialize(args.Skip(1).ToArray()));
    return;
}

int passed = 0;
var failures = new List<string>();
var good = new AdbDevice("TEST-TABLET-SERIAL-0001", "device", "Test_Tablet");
var endpoint = new AdbReverseEndpoint(54321);
IReadOnlyList<UsbDeviceIdentity> inventory = [new("TEST-TABLET-SERIAL-0001", "18D1", "4EE7")];
string temporary = Path.Combine(Path.GetTempPath(), "TabLink-Core-Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    await Test("Language preference follows system culture until explicitly saved", () =>
    {
        var path = Path.Combine(temporary, "language-preference", "language.json");
        var preferences = new LanguagePreferencesStore(path);
        var missingChinese = preferences.Load(CultureInfo.GetCultureInfo("zh-SG"));
        var missingEnglish = preferences.Load(CultureInfo.GetCultureInfo("en-SG"));
        Assert(missingChinese.Mode == ProductLanguageMode.System &&
            missingChinese.EffectiveLanguage == ProductLanguage.SimplifiedChinese &&
            missingChinese.Status == LanguagePreferencesLoadStatus.MissingSystemDefault);
        Assert(missingEnglish.Mode == ProductLanguageMode.System &&
            missingEnglish.EffectiveLanguage == ProductLanguage.English &&
            missingEnglish.Status == LanguagePreferencesLoadStatus.MissingSystemDefault);
        preferences.Save(ProductLanguageMode.English);
        var explicitEnglish = preferences.Load(CultureInfo.GetCultureInfo("zh-CN"));
        Assert(explicitEnglish.Mode == ProductLanguageMode.English &&
            explicitEnglish.EffectiveLanguage == ProductLanguage.English &&
            explicitEnglish.Status == LanguagePreferencesLoadStatus.Loaded);
        preferences.Save(ProductLanguageMode.SimplifiedChinese);
        Assert(preferences.Load(CultureInfo.GetCultureInfo("en-US")).EffectiveLanguage == ProductLanguage.SimplifiedChinese);
        preferences.Save(ProductLanguageMode.System);
        var explicitSystem = preferences.Load(CultureInfo.GetCultureInfo("zh-TW"));
        Assert(explicitSystem.Mode == ProductLanguageMode.System &&
            explicitSystem.EffectiveLanguage == ProductLanguage.SimplifiedChinese &&
            explicitSystem.Status == LanguagePreferencesLoadStatus.Loaded);
    });
    await Test("Damaged language preference safely returns to the system language", () =>
    {
        var folder = Path.Combine(temporary, "damaged-language-preference");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "language.json");
        var preferences = new LanguagePreferencesStore(path);
        foreach (var damaged in new[]
        {
            "{", "{}", "{\"schemaVersion\":1,\"language\":\"fr\"}",
            "{\"schemaVersion\":1,\"language\":\"en\",\"extra\":true}",
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"language\":\"en\"}"
        })
        {
            File.WriteAllText(path, damaged);
            var result = preferences.Load(CultureInfo.GetCultureInfo("zh-HK"));
            Assert(result.Mode == ProductLanguageMode.System &&
                result.EffectiveLanguage == ProductLanguage.SimplifiedChinese &&
                result.Status == LanguagePreferencesLoadStatus.InvalidSystemDefault);
        }
        File.WriteAllBytes(path, [0xff, 0xfe, 0xfd]);
        var invalidUtf8 = preferences.Load(CultureInfo.GetCultureInfo("en-GB"));
        Assert(invalidUtf8.Mode == ProductLanguageMode.System &&
            invalidUtf8.EffectiveLanguage == ProductLanguage.English &&
            invalidUtf8.Status == LanguagePreferencesLoadStatus.InvalidSystemDefault);
    });
    await Test("ADB child process ignores caller-controlled routing and serial environment", () =>
    {
        var names = new[] { "ADB_SERVER_SOCKET", "ADB_VENDOR_KEYS", "ANDROID_ADB_SERVER_PORT", "ANDROID_SERIAL" };
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable,
            StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, "UNTRUSTED_TEST_VALUE");
            var start = AdbProcessRunner.CreateStartInfo(@"E:\trusted\adb.exe", ["devices", "-l"]);
            Assert(names.All(name => !start.Environment.ContainsKey(name)));
            Assert(start.ArgumentList.SequenceEqual(["devices", "-l"]));
            Assert(start.WorkingDirectory == @"E:\trusted");
        }
        finally
        {
            foreach (var item in saved) Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    });
    await Test("Default exclusions contain only the two F50 VID/PID identities", () =>
    {
        var settings = new DevicePolicySettings();
        Assert(settings.ExcludedDevices.Count == 2);
        Assert(settings.ExcludedDevices.All(rule => string.IsNullOrWhiteSpace(rule.Serial)));
        Assert(settings.ExcludedDevices.Any(rule => rule.Vid == "19D2" && rule.Pid == "0246"));
        Assert(settings.ExcludedDevices.Any(rule => rule.Vid == "19D2" && rule.Pid == "0621"));
        var fictionalF50Serial = "TEST-F50-SERIAL-0001";
        Assert(new DevicePolicy(settings).Evaluate(
            new(fictionalF50Serial, "device"), [new(fictionalF50Serial, "18D1", "2D00")]).Allowed);
    });
    await Test("Measurement device binding is stable, case-sensitive and USB-only", () =>
    {
        const string serial = "TEST-TABLET-SERIAL-0001";
        const string expected = "d0798da604e052599b47f73139db7a378687c7383991af733d3ae65240010f9f";
        var actual = DeviceSerialBinding.ComputeSha256(serial);
        Assert(DeviceSerialBinding.Algorithm == "sha256-utf8-tablink-device-serial-v1");
        Assert(actual == expected && actual.Length == 64 && !actual.Contains(serial, StringComparison.Ordinal));
        Assert(DeviceSerialBinding.ComputeSha256(serial.ToLowerInvariant()) != actual);
        Throws<ArgumentException>(() => DeviceSerialBinding.ComputeSha256("192.0.2.1:5555"));
        Throws<ArgumentException>(() => DeviceSerialBinding.ComputeSha256("-not-a-target"));
    });
    await Test("VID/PID exclusion overrides an otherwise valid tablet", () =>
    {
        var policy = new DevicePolicy(new());
        Assert(!policy.Evaluate(good, [new(good.Serial, "19d2", "0246")]).Allowed);
        Assert(!policy.Evaluate(good, [new(good.Serial, "19D2", "0621")]).Allowed);
    });
    await Test("Serial exclusions ignore case and have deny priority", () =>
    {
        var settings = new DevicePolicySettings();
        settings.ExcludedDevices.Add(new() { Serial = "test-tablet-serial-0001", Label = "User excluded" });
        Assert(!new DevicePolicy(settings).Evaluate(good, inventory).Allowed);
    });
    await Test("ADB USB hint cannot replace Windows USB inventory", () =>
    {
        var hinted = good with { Details = "usb:1-2 model:Test_Tablet" };
        var policy = new DevicePolicy(new());
        Assert(!policy.Evaluate(hinted, []).Allowed);
        Assert(!policy.Evaluate(hinted, [new("OTHER", "18D1", "4EE7")]).Allowed);
        Assert(policy.Evaluate(good, inventory).Allowed); // Windows ADB often has no usb: field.
    });
    await Test("Network, emulator, unauthorized and ambiguous identities denied", () =>
    {
        var policy = new DevicePolicy(new());
        foreach (var serial in new[] { "192.168.1.2:5555", "emulator-5554", "adb-abc._adb-tls-connect._tcp" })
            Assert(!policy.Evaluate(new(serial, "device"), [new(serial, "18D1", "4EE7")]).Allowed);
        Assert(!policy.Evaluate(good with { State = "unauthorized" }, inventory).Allowed);
        Assert(!policy.Evaluate(good, [new("TEST-TABLET-SERIAL-0001", "18D1", "4EE7"), new("TEST-TABLET-SERIAL-0001", "1234", "0001")]).Allowed);
    });
    await Test("Parse devices filters network but retains unauthorized and offline USB entries", () =>
    {
        var parsed = AdbClient.ParseDevices("List of devices attached\nTEST-TABLET-SERIAL-0001 device product:p model:Test_Tablet transport_id:7\n192.168.1.2:5555 device\nemulator-5554 device\nadb-abc._adb-tls-connect._tcp device\nWAIT unauthorized\nOFF offline\n");
        Assert(parsed.Count == 3 && parsed[0].Serial == "TEST-TABLET-SERIAL-0001" && parsed[0].Model == "Test_Tablet" && parsed[0].TransportId == "7");
        Assert(parsed[1].Serial == "WAIT" && parsed[1].State == "unauthorized" && parsed[2].Serial == "OFF" && parsed[2].State == "offline");
        foreach (var device in parsed.Skip(1))
            Assert(!new DevicePolicy(new()).Evaluate(device, [new(device.Serial, "18D1", "4EE7")]).Allowed);
    });
    await Test("Settings roundtrip preserves exclusions and configurable adb path", () =>
    {
        var store = new SettingsStore(Path.Combine(temporary, "settings.json"));
        var settings = store.Load();
        Assert(settings.ExcludedDevices.Count == 2);
        settings.AdbPath = @"C:\Android SDK\platform-tools\adb.exe";
        settings.ExcludedDevices.Add(new() { Serial = "TEST-USER-EXCLUDED-0001", Label = "Keep untouched" });
        store.Save(settings);
        var read = store.Load();
        Assert(read.AdbPath == settings.AdbPath && read.ExcludedDevices.Count == 3 && read.ExcludedDevices[2].Serial == "TEST-USER-EXCLUDED-0001");
    });
    await Test("Damaged and incomplete settings fail closed", () =>
    {
        var path = Path.Combine(temporary, "broken.json");
        foreach (var json in new[] { "{", "{}", "null", "{\"SchemaVersion\":1,\"ExcludedDevices\":null}",
            "{\"SchemaVersion\":2,\"ExcludedDevices\":[]}",
            "{\"SchemaVersion\":1,\"ExcludedDevices\":[{\"Vid\":\"19D2\"}]}",
            "{\"SchemaVersion\":1,\"ExcludedDevices\":[],\"excludedDevices\":[]}",
            "{\"SchemaVersion\":1,\"ExcludedDevices\":[{\"Serial\":\"A\",\"serial\":\"B\"}]}" })
        {
            File.WriteAllText(path, json);
            Throws<SettingsLoadException>(() => new SettingsStore(path).Load());
            Assert(File.ReadAllText(path) == json); // Loading must not rewrite the user's rules.
        }
    });
    await Test("Missing update preferences preserve the automatic default without creating a file", () =>
    {
        var path = Path.Combine(temporary, "update-preferences-missing.json");
        var store = new UpdatePreferencesStore(path);
        var result = store.LoadWithStatus();
        Assert(result.Preferences == UpdatePreferences.Default);
        Assert(result.Preferences.Mode == UpdateMode.Automatic);
        Assert(result.Status == UpdatePreferencesLoadStatus.MissingDefault && !result.HasError);
        Assert(!File.Exists(path));
    });
    await Test("Update preferences roundtrip every mode as canonical JSON strings", () =>
    {
        var path = Path.Combine(temporary, "update-preferences-roundtrip.json");
        var store = new UpdatePreferencesStore(path);
        var cases = new[]
        {
            (UpdateMode.Automatic, "automatic"),
            (UpdateMode.DownloadThenAsk, "downloadThenAsk"),
            (UpdateMode.Never, "never")
        };
        foreach (var (mode, storedMode) in cases)
        {
            store.Save(new UpdatePreferences(mode));
            var result = store.LoadWithStatus();
            Assert(result.Preferences.Mode == mode);
            Assert(result.Status == UpdatePreferencesLoadStatus.Loaded && !result.HasError);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert(document.RootElement.EnumerateObject().Count() == 2);
            Assert(document.RootElement.GetProperty("schemaVersion").GetInt32() == 1);
            Assert(document.RootElement.GetProperty("mode").ValueKind == JsonValueKind.String);
            Assert(document.RootElement.GetProperty("mode").GetString() == storedMode);
            Assert(Directory.GetFiles(Path.GetDirectoryName(path)!,
                "." + Path.GetFileName(path) + ".*.tmp").Length == 0);
        }
    });
    await Test("Damaged update preferences fail closed without rewriting the evidence", () =>
    {
        var path = Path.Combine(temporary, "update-preferences-damaged.json");
        var invalidFiles = new[]
        {
            "{",
            "{}",
            "null",
            "{\"schemaVersion\":1}",
            "{\"mode\":\"automatic\"}",
            "{\"schemaVersion\":2,\"mode\":\"automatic\"}",
            "{\"schemaVersion\":\"1\",\"mode\":\"automatic\"}",
            "{\"schemaVersion\":1,\"mode\":0}",
            "{\"schemaVersion\":1,\"mode\":\"Automatic\"}",
            "{\"schemaVersion\":1,\"mode\":\"futureMode\"}",
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"mode\":\"automatic\"}",
            "{\"schemaVersion\":1,\"mode\":\"automatic\",\"Mode\":\"never\"}",
            "{\"schemaVersion\":1,\"mode\":\"automatic\",\"extra\":true}"
        };
        foreach (var json in invalidFiles)
        {
            File.WriteAllText(path, json);
            var store = new UpdatePreferencesStore(path);
            var result = store.LoadWithStatus();
            Assert(result.Preferences.Mode == UpdateMode.Never);
            Assert(result.Status == UpdatePreferencesLoadStatus.InvalidFailClosed && result.HasError);
            Assert(store.Load().Mode == UpdateMode.Never);
            Assert(File.ReadAllText(path) == json);
        }

        byte[] invalidUtf8 = [0x7B, 0x22, 0xFF, 0x22, 0x7D];
        File.WriteAllBytes(path, invalidUtf8);
        Assert(new UpdatePreferencesStore(path).LoadWithStatus().Status ==
            UpdatePreferencesLoadStatus.InvalidFailClosed);
        Assert(File.ReadAllBytes(path).SequenceEqual(invalidUtf8));
    });
    await Test("Update preferences remain separate from USB device policy settings", () =>
    {
        var settingsPath = Path.Combine(temporary, "update-separation-settings.json");
        const string settingsJson = "{\"SchemaVersion\":1,\"AdbPath\":null,\"ExcludedDevices\":[]}";
        File.WriteAllText(settingsPath, settingsJson);
        var updatePath = Path.Combine(temporary, "update-separation-preferences.json");
        new UpdatePreferencesStore(updatePath).Save(new(UpdateMode.DownloadThenAsk));

        Assert(File.ReadAllText(settingsPath) == settingsJson);
        Assert(new SettingsStore(settingsPath).Load().ExcludedDevices.Count == 0);
        var updateJson = File.ReadAllText(updatePath);
        Assert(!updateJson.Contains(nameof(DevicePolicySettings.ExcludedDevices), StringComparison.Ordinal));
        Assert(!updateJson.Contains(nameof(DevicePolicySettings.AdbPath), StringComparison.Ordinal));
        Assert(new UpdatePreferencesStore(updatePath).Load().Mode == UpdateMode.DownloadThenAsk);
    });
    await Test("Invalid in-memory update modes cannot replace a committed preference", () =>
    {
        var path = Path.Combine(temporary, "update-preferences-invalid-save.json");
        var store = new UpdatePreferencesStore(path);
        store.Save(new(UpdateMode.Automatic));
        var original = File.ReadAllBytes(path);
        Throws<ArgumentOutOfRangeException>(() => store.Save(new((UpdateMode)999)));
        Assert(File.ReadAllBytes(path).SequenceEqual(original));
        Assert(store.Load().Mode == UpdateMode.Automatic);
        Assert(Directory.GetFiles(Path.GetDirectoryName(path)!,
            "." + Path.GetFileName(path) + ".*.tmp").Length == 0);
    });
    await Test("Unreadable update preferences fail closed without changing the file", () =>
    {
        var path = Path.Combine(temporary, "update-preferences-unreadable.json");
        var store = new UpdatePreferencesStore(path);
        store.Save(new(UpdateMode.Automatic));
        var original = File.ReadAllBytes(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = store.LoadWithStatus();
            Assert(result.Preferences.Mode == UpdateMode.Never);
            Assert(result.Status == UpdatePreferencesLoadStatus.InvalidFailClosed && result.HasError);
        }
        Assert(File.ReadAllBytes(path).SequenceEqual(original));
    });
    await Test("Failed update preference replacement preserves the committed file", () =>
    {
        var path = Path.Combine(temporary, "update-preferences-locked-save.json");
        var store = new UpdatePreferencesStore(path);
        store.Save(new(UpdateMode.Automatic));
        var original = File.ReadAllBytes(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                store.Save(new(UpdateMode.Never));
                throw new Exception("Expected the locked destination to reject replacement.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Assert(File.ReadAllBytes(path).SequenceEqual(original));
        Assert(store.Load().Mode == UpdateMode.Automatic);
        Assert(Directory.GetFiles(Path.GetDirectoryName(path)!,
            "." + Path.GetFileName(path) + ".*.tmp").Length == 0);
    });
    await Test("Author footer defaults reproduce the public attribution", () =>
    {
        var path = Path.Combine(temporary, "author-footer-default.json");
        var preferences = new AuthorFooterPreferencesStore(path).Load();
        Assert(preferences.Enabled);
        Assert(preferences.AuthorText == "作者：张林杰（Jey / @linjierd）");
        Assert(preferences.GitHubLabel == "GitHub" && preferences.GitHubUrl == "https://github.com/linjierd");
        Assert(preferences.BlogLabel == "博客：linjie.space" && preferences.BlogUrl == "https://linjie.space/");
        Assert(!File.Exists(path));
    });
    await Test("Author footer roundtrip normalizes custom values and supports hidden links", () =>
    {
        var path = Path.Combine(temporary, "author-footer-roundtrip.json");
        var store = new AuthorFooterPreferencesStore(path);
        store.Save(AuthorFooterPreferences.CreateDefault());
        store.Save(new AuthorFooterPreferences
        {
            Enabled = false,
            AuthorText = "  Custom author  ",
            GitHubLabel = " ",
            GitHubUrl = " ",
            BlogLabel = "  Project site  ",
            BlogUrl = "  HTTPS://Example.COM/tablink  "
        });
        var read = store.Load();
        Assert(!read.Enabled && read.AuthorText == "Custom author");
        Assert(read.GitHubLabel == "" && read.GitHubUrl == "");
        Assert(read.BlogLabel == "Project site" && read.BlogUrl == "https://example.com/tablink");
        Assert(read.NormalizeAndValidate().BlogUrl == read.BlogUrl);
        var json = File.ReadAllText(path);
        Assert(json.Contains("\"SchemaVersion\": 1", StringComparison.Ordinal));
        Assert(!json.Contains("ExcludedDevices", StringComparison.Ordinal));
        Assert(Directory.GetFiles(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".*.tmp").Length == 0);
    });
    await Test("Old device settings remain compatible and separate from author preferences", () =>
    {
        var settingsPath = Path.Combine(temporary, "old-settings.json");
        const string oldSettings = "{\"SchemaVersion\":1,\"AdbPath\":null,\"ExcludedDevices\":[]}";
        File.WriteAllText(settingsPath, oldSettings);
        var deviceSettings = new SettingsStore(settingsPath).Load();
        Assert(deviceSettings.SchemaVersion == 1 && deviceSettings.ExcludedDevices.Count == 0);

        var authorPath = Path.Combine(temporary, "old-settings-author-footer.json");
        var authorStore = new AuthorFooterPreferencesStore(authorPath);
        Assert(authorStore.Load().Enabled);
        authorStore.Save(AuthorFooterPreferences.CreateDefault());
        Assert(File.ReadAllText(settingsPath) == oldSettings);
        Assert(File.Exists(authorPath));
    });
    await Test("Damaged or unsafe persisted author preferences fall back without rewriting", () =>
    {
        var path = Path.Combine(temporary, "author-footer-damaged.json");
        var invalidFiles = new[]
        {
            "{",
            "{}",
            "{\"SchemaVersion\":1,\"AuthorText\":\"A\",\"GitHubLabel\":\"\",\"GitHubUrl\":\"\",\"BlogLabel\":\"\",\"BlogUrl\":\"\"}",
            "{\"SchemaVersion\":2,\"Enabled\":true,\"AuthorText\":\"A\",\"GitHubLabel\":\"\",\"GitHubUrl\":\"\",\"BlogLabel\":\"\",\"BlogUrl\":\"\"}",
            "{\"SchemaVersion\":1,\"Enabled\":true,\"AuthorText\":\"A\",\"GitHubLabel\":\"GitHub\",\"GitHubUrl\":\"javascript:alert(1)\",\"BlogLabel\":\"\",\"BlogUrl\":\"\"}",
            "{\"SchemaVersion\":1,\"Enabled\":true,\"AuthorText\":\"A\",\"GitHubLabel\":\"GitHub\",\"GitHubUrl\":\"https://user:password@example.com/\",\"BlogLabel\":\"\",\"BlogUrl\":\"\"}",
            "{\"SchemaVersion\":1,\"Enabled\":true,\"enabled\":false,\"AuthorText\":\"A\",\"GitHubLabel\":\"\",\"GitHubUrl\":\"\",\"BlogLabel\":\"\",\"BlogUrl\":\"\"}",
            "{\"SchemaVersion\":1,\"Enabled\":true,\"AuthorText\":\"A\",\"GitHubLabel\":\"\",\"GitHubUrl\":\"\",\"BlogLabel\":\"\",\"BlogUrl\":\"\",\"Extra\":1}",
            "{\"SchemaVersion\":1,\"Enabled\":true,\"AuthorText\":\"A\",\"GitHubLabel\":\"\",\"GitHubUrl\":\"\",\"BlogLabel\":\"\",\"BlogUrl\":\"\",\"Extra\":{\"Value\":1,\"value\":2}}"
        };
        foreach (var json in invalidFiles)
        {
            File.WriteAllText(path, json);
            var read = new AuthorFooterPreferencesStore(path).Load();
            Assert(read.Enabled && read.AuthorText == "作者：张林杰（Jey / @linjierd）");
            Assert(File.ReadAllText(path) == json);
        }
    });
    await Test("Author footer rejects dangerous URLs, partial links and oversized text atomically", () =>
    {
        var path = Path.Combine(temporary, "author-footer-invalid-save.json");
        var store = new AuthorFooterPreferencesStore(path);
        store.Save(AuthorFooterPreferences.CreateDefault());
        var original = File.ReadAllText(path);

        void Reject(Action<AuthorFooterPreferences> change)
        {
            var candidate = AuthorFooterPreferences.CreateDefault();
            change(candidate);
            Throws<ArgumentException>(() => store.Save(candidate));
            Assert(File.ReadAllText(path) == original);
        }

        Reject(value => value.GitHubUrl = "http://github.com/linjierd");
        Reject(value => value.GitHubUrl = "file:///E:/secret.txt");
        Reject(value => value.GitHubUrl = "https://user:password@example.com/");
        Reject(value => value.GitHubLabel = "");
        Reject(value => value.AuthorText = "line one\nline two");
        Reject(value => value.AuthorText = "line one\u2028line two");
        Reject(value => value.AuthorText = "safe\u202Etxt.exe");
        Reject(value => value.AuthorText = "safe\U000E0001txt.exe");
        Reject(value => value.AuthorText = "broken\uD800surrogate");
        Reject(value => value.AuthorText = new string('x', AuthorFooterPreferences.MaximumAuthorTextLength + 1));
        Reject(value => value.BlogLabel = new string('x', AuthorFooterPreferences.MaximumLinkLabelLength + 1));
        Reject(value => value.BlogUrl = "https://example.com/" + new string('x', AuthorFooterPreferences.MaximumUrlLength));
        Assert(Directory.GetFiles(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".*.tmp").Length == 0);
    });
    await Test("Author footer failed replacement preserves the committed file and cleans temporary files", () =>
    {
        var path = Path.Combine(temporary, "author-footer-locked.json");
        var store = new AuthorFooterPreferencesStore(path);
        store.Save(AuthorFooterPreferences.CreateDefault());
        var original = File.ReadAllBytes(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                store.Save(new AuthorFooterPreferences
                {
                    Enabled = false,
                    AuthorText = "Custom author",
                    GitHubLabel = "",
                    GitHubUrl = "",
                    BlogLabel = "",
                    BlogUrl = ""
                });
                throw new Exception("Expected the locked destination to reject replacement.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Assert(File.ReadAllBytes(path).SequenceEqual(original));
        Assert(Directory.GetFiles(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".*.tmp").Length == 0);
    });
    await Test("Invalid in-memory settings block approval", () =>
    {
        var settings = new DevicePolicySettings();
        settings.ExcludedDevices.Add(new() { Vid = "19D2", Pid = "invalid" });
        Assert(!new DevicePolicy(settings).Evaluate(good, inventory).Allowed);
    });
    await Test("Current Android user parsing accepts one nonnegative integer only", () =>
    {
        Assert(AdbClient.ParseCurrentAndroidUser("0\r\n") == 0);
        Assert(AdbClient.ParseCurrentAndroidUser(" 12 \n") == 12);
        foreach (var invalid in new[] { "", "-1", "0\n1", "all", "2147483648", new string('1', 11) })
            Throws<AdbResponseException>(() => AdbClient.ParseCurrentAndroidUser(invalid));
    });
    await TestAsync("Install and one bound session use the selected current Android user", async () =>
    {
        var apk = Path.Combine(temporary, "multi-user-client.apk");
        File.WriteAllText(apk, "test-only");
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner
        {
            CurrentAndroidUserOutput = "12\n",
            TargetHandler = command => command.Contains("install")
                ? new(0, "Success\n", "")
                : command.Contains("query")
                    ? new(0, "Row: 0 json=" + DisplayProfileJson() + "\n", "")
                    : new(0, "Starting: Intent { cmp=com.tablink.client/.MainActivity }", "")
        };
        var client = Client(policy, runner);
        var approved = policy.Approve(good, inventory);
        await client.InstallApkAsync(approved, apk);
        var sessionUser = await client.BindCurrentAndroidUserAsync(approved);
        _ = await client.ReadDisplayProfileAsync(sessionUser);
        await client.LaunchAsync(sessionUser, "0123456789abcdef", endpoint);
        var operations = runner.Calls.Where(c => c.Contains("install") || c.Contains("query") || c.Contains("start")).ToArray();
        Assert(operations.Length == 3 && operations.All(c =>
        {
            var user = Array.IndexOf(c, "--user");
            return user >= 0 && user + 1 < c.Length && c[user + 1] == "12";
        }));
    });
    await TestAsync("Bound session rejects an Android user change before provider read or launch", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner
        {
            CurrentAndroidUserOutput = "12\n",
            TargetHandler = command => command.Contains("query")
                ? new(0, "Row: 0 json=" + DisplayProfileJson() + "\n", "")
                : new(0, "Starting: Intent { cmp=com.tablink.client/.MainActivity }", "")
        };
        var client = Client(policy, runner);
        var sessionUser = await client.BindCurrentAndroidUserAsync(policy.Approve(good, inventory));
        runner.CurrentAndroidUserOutput = "13\n";
        await ThrowsAsync<AndroidUserChangedException>(() => client.ReadDisplayProfileAsync(sessionUser));
        await ThrowsAsync<AndroidUserChangedException>(() =>
            client.LaunchAsync(sessionUser, "0123456789abcdef", endpoint));
        Assert(!runner.Calls.Any(call => call.Contains("query") || call.Contains("start")));
    });
    await TestAsync("ADB handoff rechecks the Android user between protected publication and activation", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { CurrentAndroidUserOutput = "12\n" };
        runner.TargetHandler = command =>
        {
            if (command.Contains("insert")) runner.CurrentAndroidUserOutput = "13\n";
            return new(0, "", "");
        };
        var client = Client(policy, runner);
        var sessionUser = await client.BindCurrentAndroidUserAsync(policy.Approve(good, inventory));
        await ThrowsAsync<AndroidUserChangedException>(() =>
            client.LaunchAsync(sessionUser, "0123456789abcdef", endpoint));
        Assert(runner.Calls.Count(call => call.Contains("insert")) == 1);
        Assert(!runner.Calls.Any(call => call.Contains("start")));
    });
    await TestAsync("All target commands use explicitly approved serial and bounded reverse", async () =>
    {
        var runner = new FakeRunner();
        var policy = new DevicePolicy(new());
        var client = Client(policy, runner);
        var approved = await client.ApproveAsync(good);
        await client.ReversePortAsync(approved, endpoint);
        var sessionUser = await client.BindCurrentAndroidUserAsync(approved);
        await client.LaunchAsync(sessionUser, "0123456789abcdef0123456789abcdef", endpoint);
        await client.RemoveReverseAsync(approved, endpoint);
        var targets = runner.Calls.Where(x => x[0] != "devices").ToArray();
        Assert(targets.Length == 7 && targets.All(x => x[0] == "-s" && x[1] == "TEST-TABLET-SERIAL-0001"));
        Assert(targets[0].SequenceEqual(new[] { "-s", "TEST-TABLET-SERIAL-0001", "reverse", "--no-rebind", "tcp:54321", "tcp:27183" }));
        Assert(targets[1].SequenceEqual(new[] { "-s", "TEST-TABLET-SERIAL-0001", "shell", "am", "get-current-user" }));
        Assert(targets[2].SequenceEqual(targets[1]));
        var publication = targets[3];
        Assert(publication.Contains("content") && publication.Contains("insert")
            && publication.Contains("content://com.tablink.client.adb/session")
            && publication.Contains("token:s:0123456789abcdef0123456789abcdef")
            && publication.Contains("port:i:54321"));
        var publicationUser = Array.IndexOf(publication, "--user");
        Assert(publicationUser >= 0 && publication[publicationUser + 1] == "0");
        var activationBinding = publication.Single(value => value.StartsWith("activation:s:", StringComparison.Ordinal));
        var activation = activationBinding["activation:s:".Length..];
        Assert(Regex.IsMatch(activation, "\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant));
        Assert(targets[4].SequenceEqual(targets[1]));
        var launch = targets[5];
        Assert(launch.Contains("com.tablink.client.APPLY_ADB_SESSION") && launch.Contains("adbActivation")
            && launch.Contains(activation) && !launch.Any(value => value.Contains("0123456789abcdef0123456789abcdef", StringComparison.Ordinal))
            && !launch.Any(value => value.Contains("54321", StringComparison.Ordinal)));
        var launchUser = Array.IndexOf(launch, "--user");
        Assert(launchUser >= 0 && launch[launchUser + 1] == "0");
        Assert(targets[6].SequenceEqual(new[] { "-s", "TEST-TABLET-SERIAL-0001", "reverse", "--remove", "tcp:54321" }));
        Assert(!runner.Calls.SelectMany(x => x).Any(x => x is "kill-server" or "tcpip" or "--remove-all"));
    });
    await Test("Per-session endpoints use the cryptographic ephemeral range", () =>
    {
        var generated = Enumerable.Range(0, 128).Select(_ => AdbReverseEndpoint.CreateRandom()).ToArray();
        Assert(generated.All(value => value.IsValid && value.DevicePort is >= 49152 and <= 65535));
        Assert(generated.Select(value => value.DevicePort).Distinct().Count() > 100);
        Assert(!default(AdbReverseEndpoint).IsValid);
    });
    await TestAsync("Random endpoint reservation skips occupied candidates without takeover", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner();
        var candidates = new[] { new AdbReverseEndpoint(50001), new AdbReverseEndpoint(50002), new AdbReverseEndpoint(50003) };
        var issued = 0;
        AdbReverseEndpoint current = default;
        AdbReverseEndpoint Next() => current = candidates[issued++];
        runner.TargetHandler = command => command.Contains("--list")
            ? new(0, issued switch
            {
                1 => $"UsbFfs tcp:{current.DevicePort} tcp:27183\n",
                2 => $"UsbFfs tcp:{current.DevicePort} tcp:30000\n",
                _ => ""
            }, "")
            : new(0, "", "");
        var reserved = await Client(policy, runner).ReserveRandomReverseEndpointAsync(
            policy.Approve(good, inventory), Next);
        var targets = runner.Calls.Where(call => call[0] != "devices").ToArray();
        Assert(reserved == candidates[2] && issued == 3);
        Assert(targets.Count(call => call.Contains("--list")) == 3);
        Assert(targets.Count(call => call.Contains("--no-rebind")) == 1);
        Assert(targets.Last().SequenceEqual(new[] { "-s", good.Serial, "reverse", "--no-rebind", "tcp:50003", "tcp:27183" }));
        Assert(!targets.Any(call => call.Contains("--remove")));
    });
    await TestAsync("Random endpoint reservation is bounded when every candidate is occupied", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner();
        var nextPort = 51000;
        AdbReverseEndpoint current = default;
        AdbReverseEndpoint Next() => current = new(++nextPort);
        runner.TargetHandler = command => command.Contains("--list")
            ? new(0, $"UsbFfs tcp:{current.DevicePort} tcp:27183\n", "")
            : throw new Exception("Occupied candidates must never be mutated.");
        await ThrowsAsync<IOException>(() => Client(policy, runner).ReserveRandomReverseEndpointAsync(
            policy.Approve(good, inventory), Next));
        var targets = runner.Calls.Where(call => call[0] != "devices").ToArray();
        Assert(targets.Length == AdbClient.MaximumEndpointReservationAttempts);
        Assert(targets.All(call => call.Contains("--list")) && !targets.Any(call => call.Contains("--no-rebind") || call.Contains("--remove")));
    });
    await TestAsync("Pending cleanup endpoints are excluded before any ADB access", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner
        {
            TargetHandler = _ => throw new Exception("A reserved endpoint must never reach ADB.")
        };
        var allEndpoints = Enumerable.Range(AdbReverseEndpoint.MinimumDevicePort,
            AdbReverseEndpoint.MaximumDevicePort - AdbReverseEndpoint.MinimumDevicePort + 1)
            .Select(port => new AdbReverseEndpoint(port)).ToHashSet();
        await ThrowsAsync<IOException>(() => Client(policy, runner).ReserveRandomReverseEndpointAsync(
            policy.Approve(good, inventory), allEndpoints));
        Assert(runner.Calls.Count == 0);
    });
    await TestAsync("Pending cleanup predicate skips only retained candidates", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner();
        var retained = new AdbReverseEndpoint(52001);
        var available = new AdbReverseEndpoint(52002);
        var candidates = new[] { retained, available };
        var issued = 0;
        var reserved = new HashSet<AdbReverseEndpoint> { retained };
        var result = await Client(policy, runner).ReserveRandomReverseEndpointAsync(
            policy.Approve(good, inventory), () => candidates[issued++], candidate => !reserved.Contains(candidate));
        var targets = runner.Calls.Where(call => call[0] != "devices").ToArray();
        Assert(result == available && issued == 2);
        Assert(targets.Length == 2 && targets[0].Contains("--list"));
        Assert(targets[1].SequenceEqual(new[] { "-s", good.Serial, "reverse", "--no-rebind", "tcp:52002", "tcp:27183" }));
        Assert(!targets.SelectMany(value => value).Contains("tcp:52001"));
        Assert(!targets.Any(call => call.Contains("--remove")));
    });
    await TestAsync("Endpoint reservation race fails without retry or takeover", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner
        {
            TargetHandler = command => command.Contains("--list")
                ? new(0, "", "")
                : new(1, "", "error: cannot rebind existing socket")
        };
        var issued = 0;
        await ThrowsAsync<AdbCommandException>(() => Client(policy, runner).ReserveRandomReverseEndpointAsync(
            policy.Approve(good, inventory), () => new AdbReverseEndpoint(53001 + issued++), static _ => true));
        var targets = runner.Calls.Where(call => call[0] != "devices").ToArray();
        Assert(issued == 1 && targets.Count(call => call.Contains("--list")) == 1);
        Assert(targets.Count(call => call.Contains("--no-rebind")) == 1);
        Assert(!targets.Any(call => call.Contains("--remove")));
    });
    await TestAsync("Missing TabLink reverse mapping is recreated without global adb mutation", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetHandler = command => command.Contains("--list")
            ? new(0, "OtherTransport tcp:12345 tcp:12345\n", "") : new(0, "", "") };
        var result = await Client(policy, runner).EnsureReversePortAsync(policy.Approve(good, inventory), endpoint);
        var targets = runner.Calls.Where(call => call[0] != "devices").ToArray();
        Assert(result.Status == AdbReversePortStatus.Created && targets.Length == 2);
        Assert(targets[0].SequenceEqual(new[] { "-s", good.Serial, "reverse", "--list" }));
        Assert(targets[1].SequenceEqual(new[] { "-s", good.Serial, "reverse", "--no-rebind", "tcp:54321", "tcp:27183" }));
        Assert(!runner.Calls.SelectMany(call => call).Any(value => value is "kill-server" or "start-server" or "--remove-all" or "tcpip"));
    });
    await TestAsync("Existing exact reverse mapping is reused without rebinding", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetStandardOutput = "UsbFfs tcp:54321 tcp:27183\n" };
        var result = await Client(policy, runner).EnsureReversePortAsync(policy.Approve(good, inventory), endpoint);
        Assert(result.Status == AdbReversePortStatus.Existing);
        Assert(runner.Calls.Count(call => call.Contains("--list")) == 1 && !runner.Calls.Any(call => call.Contains("--no-rebind")));
    });
    await TestAsync("Conflicting or malformed reverse tables fail closed without replacement", async () =>
    {
        var policy = new DevicePolicy(new());
        var conflict = new FakeRunner { TargetStandardOutput = "UsbFfs tcp:54321 tcp:30000\n" };
        var result = await Client(policy, conflict).EnsureReversePortAsync(policy.Approve(good, inventory), endpoint);
        Assert(result.Status == AdbReversePortStatus.Conflicting && !conflict.Calls.Any(call => call.Contains("--no-rebind")));
        var malformed = new FakeRunner { TargetStandardOutput = "unexpected reverse output\n" };
        await ThrowsAsync<AdbResponseException>(() => Client(policy, malformed).EnsureReversePortAsync(policy.Approve(good, inventory), endpoint));
        Assert(!malformed.Calls.Any(call => call.Contains("--no-rebind")));
    });
    await TestAsync("Recovery revalidates approval again before recreating the mapping", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner();
        runner.TargetHandler = command =>
        {
            if(command.Contains("--list")){runner.DeviceState="offline";return new(0,"","");}
            return new(0,"","");
        };
        await ThrowsAsync<DevicePolicyException>(() => Client(policy, runner).EnsureReversePortAsync(policy.Approve(good, inventory), endpoint));
        Assert(runner.Calls.Count(call => call.Contains("--list")) == 1
            && !runner.Calls.Any(call => call.Contains("--no-rebind")));
    });
    await TestAsync("ADB absence and offline state are typed as retryable without mutation", async () =>
    {
        var policy = new DevicePolicy(new());
        foreach (var runner in new[]
        {
            new FakeRunner { DevicesStandardOutput = "List of devices attached\n" },
            new FakeRunner { DeviceState = "offline" }
        })
        {
            await ThrowsAsync<AdbDeviceTemporarilyUnavailableException>(() =>
                Client(policy, runner).InspectReversePortAsync(policy.Approve(good, inventory), endpoint));
            Assert(runner.Calls.All(call => call[0] == "devices"));
        }
    });
    await TestAsync("Unauthorized and duplicate ADB identities remain terminal policy failures", async () =>
    {
        var policy = new DevicePolicy(new());
        foreach (var runner in new[]
        {
            new FakeRunner { DeviceState = "unauthorized" },
            new FakeRunner { DevicesStandardOutput = "List of devices attached\nTEST-TABLET-SERIAL-0001 device\nTEST-TABLET-SERIAL-0001 device\n" }
        })
        {
            try
            {
                await Client(policy, runner).InspectReversePortAsync(policy.Approve(good, inventory), endpoint);
                throw new Exception("Expected terminal DevicePolicyException");
            }
            catch (AdbDeviceTemporarilyUnavailableException)
            { throw new Exception("Terminal identity failure was misclassified as retryable"); }
            catch (DevicePolicyException) { }
            Assert(runner.Calls.All(call => call[0] == "devices"));
        }
    });
    await TestAsync("Disconnect and hardware change revoke target execution", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner();
        var approved = policy.Approve(good, inventory);
        var client = new AdbClient("fake-adb", policy, _ => Task.FromResult<IReadOnlyList<UsbDeviceIdentity>>([]), runner);
        await ThrowsAsync<DevicePolicyException>(() => client.ReversePortAsync(approved, endpoint));
        Assert(runner.Calls.All(x => x[0] == "devices"));
        var changed = new AdbClient("fake-adb", policy, _ => Task.FromResult<IReadOnlyList<UsbDeviceIdentity>>([new("TEST-TABLET-SERIAL-0001", "18D1", "2D00")]), runner);
        await ThrowsAsync<DevicePolicyException>(() => changed.ReversePortAsync(approved, endpoint));
    });
    await TestAsync("Policy changes and approvals from another policy are rejected", async () =>
    {
        var settings = new DevicePolicySettings();
        var policy = new DevicePolicy(settings);
        var approved = policy.Approve(good, inventory);
        var runner = new FakeRunner();
        settings.ExcludedDevices.Add(new() { Serial = "OTHER", Label = "new rule" });
        await ThrowsAsync<DevicePolicyException>(() => Client(policy, runner).ReversePortAsync(approved, endpoint));
        await ThrowsAsync<DevicePolicyException>(() => Client(new DevicePolicy(new()), runner).ReversePortAsync(approved, endpoint));
        Assert(runner.Calls.All(x => x[0] == "devices"));
    });
    await TestAsync("Injection strings and non-TabLink ports are rejected", async () =>
    {
        var policy = new DevicePolicy(new());
        Assert(!policy.Evaluate(new("TEST-TABLET-SERIAL-0001; whoami", "device"), inventory).Allowed);
        var runner = new FakeRunner();
        var client = Client(policy, runner);
        var approved = policy.Approve(good, inventory);
        var sessionUser = await client.BindCurrentAndroidUserAsync(approved);
        runner.Calls.Clear();
        await ThrowsAsync<ArgumentException>(() => client.LaunchAsync(sessionUser, "validtoken1234567;reboot", endpoint));
        Throws<ArgumentOutOfRangeException>(() => _ = new AdbReverseEndpoint(5555));
        Assert(runner.Calls.Count == 0);
    });
    await TestAsync("APK path is one literal argument including spaces and metacharacters", async () =>
    {
        var apk = Path.Combine(temporary, "client & sample.apk");
        File.WriteAllText(apk, "test-only");
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetStandardOutput = "Performing Push Install\nSuccess\n" };
        await Client(policy, runner).InstallApkAsync(policy.Approve(good, inventory), apk);
        var install = runner.Calls.Single(c => c.Contains("install"));
        Assert(install.SequenceEqual(new[] { "-s", "TEST-TABLET-SERIAL-0001", "install", "--user", "0", "--no-streaming", "-r", apk }));
    });
    await TestAsync("APK install requires an unambiguous Success result", async () =>
    {
        var apk = Path.Combine(temporary, "client.apk");
        File.WriteAllText(apk, "test-only");
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetStandardOutput = "Performing Push Install\n" };
        await ThrowsAsync<AdbResponseException>(() =>
            Client(policy, runner).InstallApkAsync(policy.Approve(good, inventory), apk));
        Assert(runner.Calls.Count(c => c[0] != "devices") == 2);
    });
    await TestAsync("Process ArgumentList preserves metacharacters literally", async () =>
    {
        var runner = new AdbProcessRunner();
        string[] values = ["space value", "& echo unexpected", "$(injection)", "quote\"value"];
        var result = await runner.RunAsync(Environment.ProcessPath!, new[] { "--test-process", "echo" }.Concat(values).ToArray(), TimeSpan.FromSeconds(5), default);
        var echoed = JsonSerializer.Deserialize<string[]>(result.StandardOutput)!;
        Assert(result.ExitCode == 0 && echoed.SequenceEqual(new[] { "echo" }.Concat(values)));
    });
    await TestAsync("Process timeout terminates only invoked test child", async () =>
    {
        try
        {
            await new AdbProcessRunner().RunAsync(Environment.ProcessPath!, ["--test-process", "sleep"], TimeSpan.FromMilliseconds(250), default);
            throw new Exception("Expected an ADB execution timeout.");
        }
        catch (AdbExecutionException ex) { Assert(ex.InnerException is TimeoutException); }
    });
    await TestAsync("Process cancellation propagates as cancellation", async () =>
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await ThrowsAsync<OperationCanceledException>(() => new AdbProcessRunner().RunAsync(Environment.ProcessPath!, ["--test-process", "sleep"], TimeSpan.FromSeconds(10), cancellation.Token));
    });
    await TestAsync("Nonzero adb exit is surfaced without a second target command", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetExitCode = 1 };
        await ThrowsAsync<AdbCommandException>(() => Client(policy, runner).ReversePortAsync(policy.Approve(good, inventory), endpoint));
        Assert(runner.Calls.Count == 2);
    });
    await TestAsync("ADB summaries never expose fake stderr, path or serial markers", async () =>
    {
        const string marker = "SENSITIVE-ADB-MARKER serial=PRIVATE-SERIAL path=C:\\Users\\Private\\adb.exe";
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetExitCode = 73, TargetStandardError = marker, TargetStandardOutput = marker };
        try
        {
            await Client(policy, runner).ReversePortAsync(policy.Approve(good, inventory), endpoint);
            throw new Exception("Expected an ADB command failure.");
        }
        catch (AdbCommandException ex)
        {
            var summary = SafeErrorSummary.ForUser(ex);
            Assert(summary.Contains(nameof(AdbCommandException), StringComparison.Ordinal));
            Assert(summary.Contains("73", StringComparison.Ordinal));
            Assert(!summary.Contains(marker, StringComparison.Ordinal));
            Assert(!ex.Message.Contains(marker, StringComparison.Ordinal));
            Assert(ex.StandardError == marker && ex.StandardOutput == marker);
        }
    });
    await TestAsync("ADB runner failures keep sensitive details out of safe summaries", async () =>
    {
        const string marker = "SENSITIVE-RUNNER-MARKER C:\\Users\\Private\\platform-tools\\adb.exe PRIVATE-SERIAL";
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetException = new IOException(marker) };
        try
        {
            await Client(policy, runner).ReversePortAsync(policy.Approve(good, inventory), endpoint);
            throw new Exception("Expected an ADB execution failure.");
        }
        catch (AdbExecutionException ex)
        {
            var summary = SafeErrorSummary.ForUser(ex);
            Assert(summary.Contains(nameof(AdbExecutionException), StringComparison.Ordinal));
            Assert(!summary.Contains(marker, StringComparison.Ordinal));
            Assert(!ex.Message.Contains(marker, StringComparison.Ordinal));
            Assert(ex.InnerException?.Message == marker);
        }
    });
    await TestAsync("Android launch errors fail even when adb returns exit zero", async () =>
    {
        foreach (var error in new[] { "Starting: Intent\nError: Activity class {com.tablink.client/.MainActivity} does not exist.",
            "java.lang.SecurityException: Permission Denial", "Error type 3" })
        {
            var policy = new DevicePolicy(new());
            var runner = new FakeRunner
            {
                TargetHandler = command => command.Contains("start")
                    ? new(0, error, "") : new(0, "", "")
            };
            var client = Client(policy, runner);
            var sessionUser = await client.BindCurrentAndroidUserAsync(policy.Approve(good, inventory));
            await ThrowsAsync<AdbCommandException>(() => client.LaunchAsync(sessionUser, "0123456789abcdef", endpoint));
            Assert(runner.Calls.Count == 10);
            Assert(runner.Calls.Count(call => call.Contains("start")) == 1);
        }
    });
    await TestAsync("Android launch stderr exception also fails with exit zero", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner
        {
            TargetHandler = command => command.Contains("start")
                ? new(0, "", "Exception occurred while executing 'start':") : new(0, "", "")
        };
        var client = Client(policy, runner);
        var sessionUser = await client.BindCurrentAndroidUserAsync(policy.Approve(good, inventory));
        await ThrowsAsync<AdbCommandException>(() => client.LaunchAsync(sessionUser, "0123456789abcdef", endpoint));
    });
    await TestAsync("Protected provider errors fail closed even when content exits zero", async () =>
    {
        const string privateMarker = "PRIVATE-PROVIDER-ERROR-MARKER";
        foreach (var error in new[]
        {
            "Error while accessing provider:\njava.lang.SecurityException: " + privateMarker,
            "[ERROR] Unsupported type: private-provider-value " + privateMarker
        })
        {
            var policy = new DevicePolicy(new());
            var runner = new FakeRunner
            {
                TargetHandler = command => command.Contains("insert")
                    ? new(0, "", error)
                    : new(0, "Starting: Intent { cmp=com.tablink.client/.MainActivity }", "")
            };
            var client = Client(policy, runner);
            var sessionUser = await client.BindCurrentAndroidUserAsync(policy.Approve(good, inventory));
            try
            {
                await client.LaunchAsync(sessionUser, "0123456789abcdef", endpoint);
                throw new Exception("Expected the protected publication to fail closed.");
            }
            catch (AdbResponseException ex)
            {
                Assert(!ex.Message.Contains(privateMarker, StringComparison.Ordinal));
                Assert(!SafeErrorSummary.ForUser(ex).Contains(privateMarker, StringComparison.Ordinal));
            }
            Assert(runner.Calls.Count(call => call.Contains("insert")) == 1);
            Assert(!runner.Calls.Any(call => call.Contains("start")));
        }
    });
    await TestAsync("Concurrent protected launches cannot overwrite each other's one-shot marker", async () =>
    {
        var policy = new DevicePolicy(new());
        var publicationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publications = 0;
        var runner = new FakeRunner
        {
            AsyncTargetHandler = async command =>
            {
                if (command.Contains("insert") && Interlocked.Increment(ref publications) == 1)
                {
                    publicationEntered.TrySetResult();
                    await releasePublication.Task;
                }
                return command.Contains("start")
                    ? new(0, "Starting: Intent { cmp=com.tablink.client/.MainActivity }", "")
                    : new(0, "", "");
            }
        };
        var client = Client(policy, runner);
        var sessionUser = await client.BindCurrentAndroidUserAsync(policy.Approve(good, inventory));
        var first = client.LaunchAsync(sessionUser, "aaaaaaaaaaaaaaaa", endpoint);
        await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.LaunchAsync(sessionUser, "bbbbbbbbbbbbbbbb", endpoint);
        try
        {
            await Task.Delay(50);
            Assert(Volatile.Read(ref publications) == 1);
        }
        finally { releasePublication.TrySetResult(); }
        await Task.WhenAll(first, second);
        var transactions = runner.Calls.Where(call => call.Contains("insert") || call.Contains("start")).ToArray();
        Assert(transactions.Length == 4);
        for (var index = 0; index < transactions.Length; index += 2)
        {
            Assert(transactions[index].Contains("insert") && transactions[index + 1].Contains("start"));
            var binding = transactions[index].Single(value => value.StartsWith("activation:s:", StringComparison.Ordinal));
            var activation = binding["activation:s:".Length..];
            Assert(transactions[index + 1].Contains(activation));
        }
    });
    await TestAsync("Existing reverse mapping is neither replaced nor automatically removed", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetExitCode = 1, TargetStandardError = "error: cannot rebind existing socket" };
        try { await Client(policy, runner).ReversePortAsync(policy.Approve(good, inventory), endpoint); throw new Exception("Expected reservation failure."); }
        catch (AdbCommandException ex)
        {
            Assert(ex.ExitCode == 1 && ex.Message.Contains(nameof(AdbCommandException), StringComparison.Ordinal));
            Assert(!ex.Message.Contains("cannot rebind", StringComparison.Ordinal));
        }
        Assert(runner.Calls.Count == 2 && runner.Calls.Last().Contains("--no-rebind"));
        Assert(!runner.Calls.SelectMany(x => x).Any(x => x is "--remove" or "--remove-all"));
    });
    await TestAsync("Approval cannot execute after device becomes unauthorized or offline", async () =>
    {
        foreach (var state in new[] { "unauthorized", "offline" })
        {
            var policy = new DevicePolicy(new());
            var runner = new FakeRunner { DeviceState = state };
            await ThrowsAsync<DevicePolicyException>(() => Client(policy, runner).ReversePortAsync(policy.Approve(good, inventory), endpoint));
            Assert(runner.Calls.Count == 1 && runner.Calls[0][0] == "devices");
        }
    });
    await Test("Tablet native portrait chooses supported 90 Hz over active 60 Hz", () =>
    {
        var profile = TabletDisplayProfile.Parse(DisplayProfileJson());
        Assert(profile.Width == 1200 && profile.Height == 1920 && profile.Rotation == 0);
        Assert(profile.NativeWidth == 1200 && profile.NativeHeight == 1920 && profile.RefreshRate == 60);
        Assert(profile.RequestedRefreshRate == 90 && profile.SupportedModes.Count == 3);
        // A faster unrelated resolution must not raise this panel's requested Hz.
        Assert(profile.SupportedModes.Any(m => m.RefreshRate == 120));
    });
    await Test("Landscape rotation keeps native panel identity and supported rate", () =>
    {
        var profile = TabletDisplayProfile.Parse(DisplayProfileJson(landscape: true));
        Assert(profile.Width == 1920 && profile.Height == 1200 && profile.Rotation == 1);
        Assert(profile.NativeWidth == 1200 && profile.NativeHeight == 1920 && profile.RequestedRefreshRate == 90);
    });
    await Test("Malformed tablet profiles fail closed before display configuration", () =>
    {
        foreach (var json in new[] { "{", "null", "{}", new string('x', 65537),
            DisplayProfileJson().Replace("\"rotation\":0", "\"rotation\":4"),
            DisplayProfileJson().Replace("\"width\":1200", "\"width\":1201"),
            DisplayProfileJson().Replace("\"refreshRate\":60", "\"refreshRate\":0"),
            DisplayProfileJson().Replace("\"nativeWidth\":1200", "\"nativeWidth\":1440"),
            DisplayProfileJson().Replace("\"height\":1920", "\"height\":9000"),
            "{\"width\":4096,\"height\":4096,\"rotation\":0,\"activeModeId\":1,\"refreshRate\":60,\"nativeWidth\":4096,\"nativeHeight\":4096,\"supportedModes\":[{\"width\":4096,\"height\":4096,\"refreshRate\":60,\"modeId\":1}]}",
            "{\"width\":1200,\"height\":1920,\"rotation\":0,\"refreshRate\":60,\"nativeWidth\":1200,\"nativeHeight\":1920,\"supportedModes\":null}" })
        {
            try { TabletDisplayProfile.Parse(json); throw new Exception("Malformed profile was accepted: " + json[..Math.Min(json.Length, 100)]); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException) { }
        }
    });
    await TestAsync("Display provider query targets only the approved USB serial", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetStandardOutput = "Row: 0 json=" + DisplayProfileJson() + "\n" };
        var client = Client(policy, runner);
        var sessionUser = await client.BindCurrentAndroidUserAsync(policy.Approve(good, inventory));
        var result = await client.ReadDisplayProfileAsync(sessionUser);
        Assert(result.RequestedRefreshRate == 90);
        var target = runner.Calls.Where(c => c.Contains("query")).Single();
        Assert(target.SequenceEqual(new[] { "-s", "TEST-TABLET-SERIAL-0001", "shell", "content", "query", "--uri", "content://com.tablink.client.display/capabilities", "--user", "0" }));
    });
    await TestAsync("Stopped provider fallback launches only TabLink and requeries approved serial", async () =>
    {
        var policy = new DevicePolicy(new());
        var queries = 0;
        var runner = new FakeRunner
        {
            TargetHandler = command => command.Contains("query")
                ? new(0, ++queries == 1 ? "No result found." : "Row: 0 json=" + DisplayProfileJson(true), "")
                : new(0, "Starting: Intent { cmp=com.tablink.client/.MainActivity }", "")
        };
        var client = Client(policy, runner);
        var sessionUser = await client.BindCurrentAndroidUserAsync(policy.Approve(good, inventory));
        var result = await client.ReadDisplayProfileAsync(sessionUser);
        var targets = runner.Calls.Where(c => c[0] != "devices").ToArray();
        Assert(queries == 2 && targets.Length == 5 && result.Rotation == 1);
        Assert(targets.All(c => c[0] == "-s" && c[1] == "TEST-TABLET-SERIAL-0001"));
        Assert(targets[2].SequenceEqual(targets[4]));
        Assert(targets[3].SequenceEqual(new[] { "-s", "TEST-TABLET-SERIAL-0001", "shell", "am", "start", "--user", "0", "-n", "com.tablink.client/.MainActivity", "--ez", "profileOnly", "true" }));
        Assert(runner.Calls.Count(c => c[0] == "devices") == 5);
    });
    await TestAsync("Display provider fallback rechecks USB approval before activation", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner();
        runner.TargetHandler = _ => { runner.DeviceState = "offline"; return new(0, "No result found.", ""); };
        var client = Client(policy, runner);
        var sessionUser = await client.BindCurrentAndroidUserAsync(policy.Approve(good, inventory));
        await ThrowsAsync<DevicePolicyException>(() => client.ReadDisplayProfileAsync(sessionUser));
        Assert(runner.Calls.Count(c => c[0] != "devices") == 3 && !runner.Calls.Any(c => c.Contains("start")));
    });
    await TestAsync("Malformed provider result does not trigger activation or guess a display mode", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetStandardOutput = "Row: 0 json={}" };
        var client = Client(policy, runner);
        var sessionUser = await client.BindCurrentAndroidUserAsync(policy.Approve(good, inventory));
        await ThrowsAsync<AdbResponseException>(() => client.ReadDisplayProfileAsync(sessionUser));
        Assert(runner.Calls.Count(c => c[0] != "devices") == 3);
    });
    await TestAsync("Malformed display payload never exposes fake ADB output markers", async () =>
    {
        const string marker = "SENSITIVE-PROVIDER-MARKER serial=PRIVATE-SERIAL path=C:\\Users\\Private\\adb.exe";
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetStandardOutput = "Row: 0 json={\"marker\":\"" + marker.Replace("\\", "\\\\") + "\"}" };
        try
        {
            var client = Client(policy, runner);
            var sessionUser = await client.BindCurrentAndroidUserAsync(policy.Approve(good, inventory));
            await client.ReadDisplayProfileAsync(sessionUser);
            throw new Exception("Expected malformed ADB response failure.");
        }
        catch (AdbResponseException ex)
        {
            var summary = SafeErrorSummary.ForUser(ex);
            Assert(summary.Contains(nameof(AdbResponseException), StringComparison.Ordinal));
            Assert(!summary.Contains(marker, StringComparison.Ordinal));
            Assert(!ex.Message.Contains(marker, StringComparison.Ordinal));
            Assert(ex.InnerException is not null && runner.TargetStandardOutput.Contains("SENSITIVE-PROVIDER-MARKER", StringComparison.Ordinal));
        }
    });
    await TestAsync("Provider fallback surfaces Android activation errors", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetHandler = command => command.Contains("query")
            ? new(0, "No result found.", "") : new(0, "Error type 3\nActivity class does not exist", "") };
        var client = Client(policy, runner);
        var sessionUser = await client.BindCurrentAndroidUserAsync(policy.Approve(good, inventory));
        await ThrowsAsync<AdbCommandException>(() => client.ReadDisplayProfileAsync(sessionUser));
        Assert(runner.Calls.Count(c => c[0] != "devices") == 4);
    });
}
finally
{
    // Delete only this test's freshly generated temporary directory.
    Directory.Delete(temporary, recursive: true);
}
Console.WriteLine($"{passed} passed; {failures.Count} failed. No real adb or USB device was accessed.");
foreach (var failure in failures) Console.Error.WriteLine(failure);
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

AdbClient Client(DevicePolicy policy, FakeRunner runner) => new("fake-adb", policy, _ => Task.FromResult(inventory), runner);
Task Test(string name, Action action) => TestAsync(name, () => { action(); return Task.CompletedTask; });
async Task TestAsync(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex); Console.WriteLine("FAIL " + name); }
}
static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

static string DisplayProfileJson(bool landscape = false) => JsonSerializer.Serialize(new
{
    width = landscape ? 1920 : 1200, height = landscape ? 1200 : 1920, rotation = landscape ? 1 : 0,
    activeModeId = 1, refreshRate = 60, nativeWidth = 1200, nativeHeight = 1920,
    supportedModes = new[] { new { width = 1200, height = 1920, refreshRate = 60, modeId = 1 },
        new { width = 1200, height = 1920, refreshRate = 90, modeId = 2 },
        new { width = 800, height = 1280, refreshRate = 120, modeId = 3 } }
});

sealed class FakeRunner : IAdbProcessRunner
{
    public List<string[]> Calls { get; } = [];
    public int TargetExitCode { get; set; }
    public string DeviceState { get; set; } = "device";
    public string? DevicesStandardOutput { get; set; }
    public string TargetStandardOutput { get; set; } = "";
    public string TargetStandardError { get; set; } = "";
    public string CurrentAndroidUserOutput { get; set; } = "0\n";
    public Exception? TargetException { get; set; }
    public Func<string[], AdbCommandResult>? TargetHandler { get; set; }
    public Func<string[], Task<AdbCommandResult>>? AsyncTargetHandler { get; set; }
    public Task<AdbCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(arguments.ToArray());
        if (arguments[0] == "devices")
            return Task.FromResult(new AdbCommandResult(0, DevicesStandardOutput ?? $"List of devices attached\nTEST-TABLET-SERIAL-0001 {DeviceState} model:Test_Tablet transport_id:1\n", ""));
        if (arguments.Count >= 5 && arguments[^2] == "am" && arguments[^1] == "get-current-user")
            return Task.FromResult(new AdbCommandResult(0, CurrentAndroidUserOutput, ""));
        if (TargetException is not null) return Task.FromException<AdbCommandResult>(TargetException);
        if (AsyncTargetHandler is not null) return AsyncTargetHandler(arguments.ToArray());
        return Task.FromResult(TargetHandler?.Invoke(arguments.ToArray()) ??
            new AdbCommandResult(TargetExitCode, TargetStandardOutput, TargetStandardError));
    }
}
