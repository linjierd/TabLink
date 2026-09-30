using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TabLink.DriverSetup;

internal static partial class DriverInstaller
{
    internal static void ValidateProfile(int width, int height, int refreshRate)
    {
        if (width is < 320 or > 7680 || height is < 320 or > 7680 || (long)width * height > 33_177_600 || refreshRate is < 30 or > 240)
            throw new ArgumentOutOfRangeException(nameof(width), "分辨率每边须为320至7680像素，总像素不超过33177600，刷新率须为30至240的整数。");
    }

    internal static byte[] BuildProfileConfiguration(byte[] original, int width, int height, int refreshRate)
    {
        ValidateProfile(width, height, refreshRate);
        if (original.Length > 131072) throw new InvalidDataException("驱动配置过大，未尝试解析或修改。");
        using var input = new MemoryStream(original, false);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 131072 });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root?.Name != "vdd_settings" || root.Elements("resolutions").Count() != 1 ||
            root.Elements("monitors").Count() != 1 || root.Element("monitors")!.Elements("count").Count() != 1 ||
            !int.TryParse(root.Element("monitors")!.Element("count")!.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 8)
            throw new InvalidDataException("不是预期的 1 至 8 屏 MttVDD XML 配置，未更改文件。");
        var resolutions = root.Element("resolutions")!;
        if (resolutions.Elements("resolution").Count() > 32)
            throw new InvalidDataException("配置已有过多显示模式，未继续增加模式。");
        var changed = false;
        foreach (var (w, h) in new[] { (width, height), (height, width) }.Distinct())
        {
            var matches = resolutions.Elements("resolution").Where(e =>
                e.Element("width")?.Value.Trim() == w.ToString(CultureInfo.InvariantCulture) &&
                e.Element("height")?.Value.Trim() == h.ToString(CultureInfo.InvariantCulture)).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("目标分辨率有重复配置，未猜测或覆盖条目。");
            var entry = matches.SingleOrDefault();
            if (entry is null)
            {
                entry = new XElement("resolution", new XElement("width", w), new XElement("height", h));
                resolutions.Add(entry);
                changed = true;
            }
            foreach (var hz in new[] { refreshRate, 60 }.Distinct())
            {
                if (entry.Elements("refresh_rate").Any(e => e.Value.Trim() == hz.ToString(CultureInfo.InvariantCulture))) continue;
                entry.Add(new XElement("refresh_rate", hz));
                changed = true;
            }
        }
        if (!changed) return original;
        ValidateModeCount(root);
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false }))
            document.Save(writer);
        return output.ToArray();
    }

    internal static byte[] BuildPoolConfiguration(byte[] original, int targetCount)
    {
        if (targetCount != 1) throw new ArgumentOutOfRangeException(nameof(targetCount), "TabLink 只允许配置一块虚拟扩展屏。");
        // BuildProfile adds both orientations, the requested rate and 60 Hz.
        var updated = BuildProfileConfiguration(original, 720, 1280, 30);
        updated = BuildProfileConfiguration(updated, 1080, 1920, 30);
        using var input = new MemoryStream(updated, false);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 131072 });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = document.Root!;
        ValidateModeCount(root);
        var count = root.Element("monitors")!.Element("count")!;
        if (count.Value.Trim() == targetCount.ToString(CultureInfo.InvariantCulture)) return updated;
        count.Value = targetCount.ToString(CultureInfo.InvariantCulture);
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false })) document.Save(writer);
        return output.ToArray();
    }

    internal static byte[] BuildSingleDisplayConfiguration(byte[] original, int width, int height, int refreshRate)
    {
        // Add the receiver's exact native modes first, then converge the old
        // multi-output setting to one.  This function is intentionally usable
        // before a device node exists so first connection never has to create
        // several displays and shrink the pool afterwards.
        var updated = BuildProfileConfiguration(original, width, height, refreshRate);
        using var input = new MemoryStream(updated, false);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 131072
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("虚拟显示配置缺少根元素。");
        ValidateModeCount(root);
        var count = root.Element("monitors")?.Element("count")
            ?? throw new InvalidDataException("虚拟显示配置缺少显示数量。");
        if (count.Value.Trim() == "1") return updated;
        count.Value = "1";
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            CloseOutput = false
        })) document.Save(writer);
        return output.ToArray();
    }

    internal static int ReadConfiguredCount(byte[] xml)
    {
        using var input = new MemoryStream(xml, false);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 131072 });
        return (int)XDocument.Load(reader).Root!.Element("monitors")!.Element("count")!;
    }

    private static void ValidateModeCount(XElement root)
    {
        var resolutions = root.Element("resolutions")!.Elements("resolution").ToArray();
        if (resolutions.Length > 32) throw new InvalidDataException("合并后的分辨率超过32项，未更改原配置。");
        var globalRates = root.Element("global")?.Elements("g_refresh_rate").Select(e => e.Value.Trim()).ToArray() ?? [];
        var combinations = resolutions.SelectMany(r => r.Elements("refresh_rate").Select(e => e.Value.Trim()).Concat(globalRates)
            .Select(rate => (Width: r.Element("width")?.Value.Trim(), Height: r.Element("height")?.Value.Trim(), Rate: rate))).Distinct().Count();
        if (combinations > 64) throw new InvalidDataException("合并后的显示模式超过64个组合，未删除或改写原有模式；请先整理驱动配置。");
    }
}
