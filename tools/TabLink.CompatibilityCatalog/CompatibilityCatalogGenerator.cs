using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TabLink.CompatibilityCatalog;

public static class CompatibilityCatalogGenerator
{
    public static string GenerateReadme(CompatibilityCatalogDocument catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var builder = new StringBuilder();
        builder.Append("# TabLink 设备兼容性目录\n\n");
        builder.Append("此目录由 `compatibility/catalog.json` 自动生成，请勿手工编辑。Schema v")
            .Append(catalog.SchemaVersion).Append("，当前 ").Append(catalog.Reports.Count).Append(" 条记录。\n");
        builder.Append("只收录经过人工审查的非唯一设备型号和能力信息。\n");
        builder.Append("每条记录只证明表中完全相同的软件、硬件和连接配置；不能据此推断同型号的其他系统版本或连接方式。\n\n");
        builder.Append("请求刷新率不等于解码提交、呈现回调或物理呈现帧率；未测量的物理呈现显示为 `—`。\n");
        builder.Append("目录不接受自动遥测、Issue 或支持包自动导入、设备序列号、网络地址、USB 标识符或配对凭据。自动校验不能代替人工确认公开型号与测试结论。\n");
        builder.Append("修改源数据后运行 `dotnet run --project tools/TabLink.CompatibilityCatalog/TabLink.CompatibilityCatalog.csproj -c Release -- --root . --write`，并提交源数据、Schema 和本页。\n\n");
        builder.Append("## 已审核记录\n\n");
        builder.Append("| 日期 | 结果 | 电脑 | 接收设备 | 连接 | 显示 | 刷新率 | 视频 | 证据 |\n");
        builder.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (var report in catalog.Reports)
        {
            builder.Append("| ").Append(report.VerifiedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            builder.Append(" | `").Append(report.Result).Append("` | ");
            builder.Append(EscapeCell(report.Host.OperatingSystem)).Append(" / `")
                .Append(report.Host.Architecture).Append("` / ")
                .Append(EscapeCell(report.Host.GpuVendor)).Append(' ')
                .Append(EscapeCell(report.Host.GpuModel));
            builder.Append(" | `").Append(report.Receiver.Platform).Append("` / ")
                .Append('`').Append(report.Receiver.Client).Append("` / ")
                .Append(EscapeCell(report.Receiver.Manufacturer)).Append(' ')
                .Append(EscapeCell(report.Receiver.Model)).Append(" / ")
                .Append(EscapeCell(report.Receiver.OperatingSystem)).Append(" / ")
                .Append(EscapeCell(report.Receiver.Decoder));
            builder.Append(" | `").Append(report.Connection).Append("` | ")
                .Append(report.Display.LogicalWidth).Append('×').Append(report.Display.LogicalHeight)
                .Append("（原生 ").Append(report.Display.NativeWidth).Append('×')
                .Append(report.Display.NativeHeight).Append("，旋转 ")
                .Append(report.Display.RotationQuarterTurns).Append("/4 圈）");
            builder.Append(" | ").Append(report.Display.ActiveRefreshHz).Append(" Hz（请求 ")
                .Append(report.Display.RequestedRefreshHz).Append(" Hz；支持 ")
                .Append(string.Join('/', report.Display.SupportedRefreshHz)).Append(" Hz）");
            builder.Append(" | `").Append(report.Video.Codec).Append("` / `")
                .Append(report.Video.Encoder).Append("` / `").Append(report.Video.Decoder)
                .Append("`；呈现回调 ").Append(FormatDecimal(report.Video.PresentationCallbackFps))
                .Append(" fps；物理呈现 ").Append(FormatPhysicalPresentation(report.Video));
            builder.Append(" | [").Append(report.Evidence.Document).Append("](../")
                .Append(report.Evidence.Document).Append(") |\n");
        }

        builder.Append("\n## 能力与限制\n\n");
        foreach (var report in catalog.Reports)
        {
            builder.Append("### `").Append(report.Id).Append("`\n\n");
            builder.Append("- TabLink：`").Append(report.TabLink.Version).Append("` / `")
                .Append(report.TabLink.Channel).Append("` / `").Append(report.TabLink.ReleaseTag).Append("`\n");
            builder.Append("- 来源：`").Append(report.Source).Append("`，提交 `")
                .Append(report.Evidence.SourceCommit).Append("`\n");
            builder.Append("- 视频测量：请求 ").Append(FormatDecimal(report.Video.RequestedFps))
                .Append(" fps；有效 ").Append(FormatDecimal(report.Video.EffectiveFps))
                .Append(" fps；提交 ").Append(FormatDecimal(report.Video.SubmittedFps))
                .Append(" fps；呈现回调 ").Append(FormatDecimal(report.Video.PresentationCallbackFps))
                .Append(" fps；物理呈现 ").Append(FormatPhysicalPresentation(report.Video)).Append("\n");
            builder.Append("- 已验证：").Append(InlineCodeList(report.VerifiedFeatures)).Append("\n");
            builder.Append("- 限制：")
                .Append(report.Limitations.Count == 0 ? "无已记录限制" : InlineCodeList(report.Limitations))
                .Append("\n\n");
        }

        return NormalizeLf(builder.ToString()).TrimEnd('\n') + "\n";
    }

    public static string GenerateSchemaJson()
    {
        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "TabLink curated compatibility catalog",
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = StringArray("schemaVersion", "reports"),
            ["properties"] = new JsonObject
            {
                ["schemaVersion"] = new JsonObject { ["const"] = CompatibilityCatalogContract.SchemaVersion },
                ["reports"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["maxItems"] = 1000,
                    ["items"] = Reference("#/$defs/report"),
                    ["description"] = "Records must have unique ids and be sorted by id using ordinal order."
                }
            },
            ["$defs"] = BuildDefinitions()
        };

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return NormalizeLf(json).TrimEnd('\n') + "\n";
    }

    private static JsonObject BuildDefinitions() => new()
    {
        ["publicLabel"] = new JsonObject
        {
            ["type"] = "string",
            ["minLength"] = 1,
            ["maxLength"] = 80,
            ["description"] = "Normalized public model or capability label. The validator rejects markup and private identifiers."
        },
        ["report"] = ObjectSchema(
            [
                "id", "result", "verifiedOn", "source", "tabLink", "host", "receiver", "connection",
                "display", "video", "verifiedFeatures", "limitations", "evidence"
            ],
            new JsonObject
            {
                ["id"] = new JsonObject
                {
                    ["type"] = "string",
                    ["minLength"] = 10,
                    ["maxLength"] = 10,
                    ["pattern"] = "^tlc-[0-9]{6}$"
                },
                ["result"] = EnumSchema(CompatibilityCatalogContract.Results),
                ["verifiedOn"] = new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^[0-9]{4}-[0-9]{2}-[0-9]{2}$"
                },
                ["source"] = EnumSchema(CompatibilityCatalogContract.Sources),
                ["tabLink"] = Reference("#/$defs/tabLink"),
                ["host"] = Reference("#/$defs/host"),
                ["receiver"] = Reference("#/$defs/receiver"),
                ["connection"] = EnumSchema(CompatibilityCatalogContract.Connections),
                ["display"] = Reference("#/$defs/display"),
                ["video"] = Reference("#/$defs/video"),
                ["verifiedFeatures"] = SortedEnumArraySchema(CompatibilityCatalogContract.VerifiedFeatures, true),
                ["limitations"] = SortedEnumArraySchema(CompatibilityCatalogContract.Limitations, false),
                ["evidence"] = Reference("#/$defs/evidence")
            }),
        ["tabLink"] = ObjectSchema(
            ["version", "channel", "releaseTag"],
            new JsonObject
            {
                ["version"] = new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$"
                },
                ["channel"] = EnumSchema(CompatibilityCatalogContract.Channels),
                ["releaseTag"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 64 }
            }),
        ["host"] = ObjectSchema(
            ["operatingSystem", "architecture", "gpuVendor", "gpuModel"],
            new JsonObject
            {
                ["operatingSystem"] = Reference("#/$defs/publicLabel"),
                ["architecture"] = EnumSchema(CompatibilityCatalogContract.Architectures),
                ["gpuVendor"] = Reference("#/$defs/publicLabel"),
                ["gpuModel"] = Reference("#/$defs/publicLabel")
            }),
        ["receiver"] = ObjectSchema(
            ["platform", "client", "manufacturer", "model", "operatingSystem", "decoder"],
            new JsonObject
            {
                ["platform"] = EnumSchema(CompatibilityCatalogContract.ReceiverPlatforms),
                ["client"] = EnumSchema(CompatibilityCatalogContract.ReceiverClients),
                ["manufacturer"] = Reference("#/$defs/publicLabel"),
                ["model"] = Reference("#/$defs/publicLabel"),
                ["operatingSystem"] = Reference("#/$defs/publicLabel"),
                ["decoder"] = Reference("#/$defs/publicLabel")
            }),
        ["display"] = ObjectSchema(
            [
                "logicalWidth", "logicalHeight", "nativeWidth", "nativeHeight", "rotationQuarterTurns",
                "activeRefreshHz", "requestedRefreshHz", "supportedRefreshHz"
            ],
            new JsonObject
            {
                ["logicalWidth"] = IntegerSchema(320, 16384),
                ["logicalHeight"] = IntegerSchema(320, 16384),
                ["nativeWidth"] = IntegerSchema(320, 16384),
                ["nativeHeight"] = IntegerSchema(320, 16384),
                ["rotationQuarterTurns"] = IntegerSchema(0, 3),
                ["activeRefreshHz"] = IntegerSchema(1, 480),
                ["requestedRefreshHz"] = IntegerSchema(1, 480),
                ["supportedRefreshHz"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["maxItems"] = 16,
                    ["uniqueItems"] = true,
                    ["items"] = IntegerSchema(1, 480),
                    ["description"] = "Values must be sorted in ascending numeric order."
                }
            }),
        ["video"] = BuildVideoSchema(),
        ["evidence"] = ObjectSchema(
            ["document", "sourceCommit"],
            new JsonObject
            {
                ["document"] = new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^VERIFICATION-(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.md$"
                },
                ["sourceCommit"] = new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^[0-9a-f]{40}$"
                }
            })
    };

    private static JsonObject ObjectSchema(IEnumerable<string> required, JsonObject properties) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = StringArray(required),
        ["properties"] = properties
    };

    private static JsonObject BuildVideoSchema()
    {
        var schema = ObjectSchema(
            [
                "codec", "encoder", "decoder", "requestedFps", "effectiveFps", "submittedFps",
                "presentationCallbackFps"
            ],
            new JsonObject
            {
                ["codec"] = EnumSchema(CompatibilityCatalogContract.Codecs),
                ["encoder"] = EnumSchema(CompatibilityCatalogContract.Encoders),
                ["decoder"] = EnumSchema(CompatibilityCatalogContract.DecoderKinds),
                ["requestedFps"] = NumberSchema(0, 480),
                ["effectiveFps"] = NumberSchema(0, 480),
                ["submittedFps"] = NumberSchema(0, 480),
                ["presentationCallbackFps"] = NumberSchema(0, 480),
                ["physicalPresentationFps"] = NumberSchema(0, 480),
                ["physicalPresentationMethod"] =
                    EnumSchema(CompatibilityCatalogContract.PhysicalPresentationMethods)
            });
        schema["dependentRequired"] = new JsonObject
        {
            ["physicalPresentationFps"] = StringArray("physicalPresentationMethod"),
            ["physicalPresentationMethod"] = StringArray("physicalPresentationFps")
        };
        return schema;
    }

    private static JsonObject Reference(string reference) => new() { ["$ref"] = reference };

    private static JsonObject EnumSchema(IEnumerable<string> values) => new()
    {
        ["type"] = "string",
        ["enum"] = StringArray(values)
    };

    private static JsonObject SortedEnumArraySchema(IReadOnlyList<string> values, bool requireNonEmpty) => new()
    {
        ["type"] = "array",
        ["minItems"] = requireNonEmpty ? 1 : 0,
        ["maxItems"] = values.Count,
        ["uniqueItems"] = true,
        ["items"] = EnumSchema(values),
        ["description"] = "Values must be sorted using ordinal order."
    };

    private static JsonObject IntegerSchema(int minimum, int maximum) => new()
    {
        ["type"] = "integer",
        ["minimum"] = minimum,
        ["maximum"] = maximum
    };

    private static JsonObject NumberSchema(int minimum, int maximum) => new()
    {
        ["type"] = "number",
        ["minimum"] = minimum,
        ["maximum"] = maximum,
        ["description"] = "The strict validator accepts at most three fractional digits and no exponent notation."
    };

    private static JsonArray StringArray(params string[] values) => StringArray((IEnumerable<string>)values);

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
            result.Add(value);
        return result;
    }

    private static string InlineCodeList(IEnumerable<string> values) =>
        string.Join("、", values.Select(static value => "`" + value + "`"));

    private static string EscapeCell(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string FormatDecimal(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatPhysicalPresentation(VideoConfiguration video) =>
        video.PhysicalPresentationFps is null || video.PhysicalPresentationMethod is null
            ? "—"
            : FormatDecimal(video.PhysicalPresentationFps.Value) + " fps (`" +
              video.PhysicalPresentationMethod + "`)";

    private static string NormalizeLf(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');
}
