using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TabLink.Windows
{
    internal enum VideoEncoderBackend
    {
        Nvenc = 0,
        Qsv = 1,
        Amf = 2,
        LibX264 = 3
    }

    internal enum VideoEncoderPreference
    {
        Automatic = 0,
        Nvenc = 1,
        Qsv = 2,
        Amf = 3,
        LibX264 = 4
    }

    internal static class VideoEncoderBackendMetadata
    {
        internal const uint NvidiaVendorId = 0x10DE;
        internal const uint IntelVendorId = 0x8086;
        internal const uint AmdVendorId = 0x1002;

        internal static string CodecName(VideoEncoderBackend backend)
        {
            switch (backend)
            {
                case VideoEncoderBackend.Nvenc: return "h264_nvenc";
                case VideoEncoderBackend.Qsv: return "h264_qsv";
                case VideoEncoderBackend.Amf: return "h264_amf";
                case VideoEncoderBackend.LibX264: return "libx264";
                default: throw UnknownBackend(backend);
            }
        }

        internal static string DisplayName(VideoEncoderBackend backend)
        {
            switch (backend)
            {
                case VideoEncoderBackend.Nvenc: return "NVIDIA NVENC";
                case VideoEncoderBackend.Qsv: return "Intel Quick Sync (QSV)";
                case VideoEncoderBackend.Amf: return "AMD AMF";
                case VideoEncoderBackend.LibX264: return "软件 x264";
                default: throw UnknownBackend(backend);
            }
        }

        internal static bool IsHardware(VideoEncoderBackend backend)
        {
            switch (backend)
            {
                case VideoEncoderBackend.Nvenc:
                case VideoEncoderBackend.Qsv:
                case VideoEncoderBackend.Amf:
                    return true;
                case VideoEncoderBackend.LibX264:
                    return false;
                default:
                    throw UnknownBackend(backend);
            }
        }

        internal static uint? VendorId(VideoEncoderBackend backend)
        {
            switch (backend)
            {
                case VideoEncoderBackend.Nvenc: return NvidiaVendorId;
                case VideoEncoderBackend.Qsv: return IntelVendorId;
                case VideoEncoderBackend.Amf: return AmdVendorId;
                case VideoEncoderBackend.LibX264: return null;
                default: throw UnknownBackend(backend);
            }
        }

        static ArgumentOutOfRangeException UnknownBackend(VideoEncoderBackend backend)
        {
            return new ArgumentOutOfRangeException("backend", backend, "未知的视频编码后端。");
        }
    }

    internal static class VideoEncoderPreferenceMetadata
    {
        internal static string DisplayName(VideoEncoderPreference preference)
        {
            switch (preference)
            {
                case VideoEncoderPreference.Automatic: return "自动选择";
                case VideoEncoderPreference.Nvenc: return "NVIDIA NVENC";
                case VideoEncoderPreference.Qsv: return "Intel Quick Sync (QSV)";
                case VideoEncoderPreference.Amf: return "AMD AMF";
                case VideoEncoderPreference.LibX264: return "软件 x264";
                default: throw UnknownPreference(preference);
            }
        }

        internal static bool TryGetForcedBackend(VideoEncoderPreference preference, out VideoEncoderBackend backend)
        {
            switch (preference)
            {
                case VideoEncoderPreference.Automatic:
                    backend = default(VideoEncoderBackend);
                    return false;
                case VideoEncoderPreference.Nvenc:
                    backend = VideoEncoderBackend.Nvenc;
                    return true;
                case VideoEncoderPreference.Qsv:
                    backend = VideoEncoderBackend.Qsv;
                    return true;
                case VideoEncoderPreference.Amf:
                    backend = VideoEncoderBackend.Amf;
                    return true;
                case VideoEncoderPreference.LibX264:
                    backend = VideoEncoderBackend.LibX264;
                    return true;
                default:
                    throw UnknownPreference(preference);
            }
        }

        internal static void Validate(VideoEncoderPreference preference)
        {
            VideoEncoderBackend ignored;
            TryGetForcedBackend(preference, out ignored);
        }

        static ArgumentOutOfRangeException UnknownPreference(VideoEncoderPreference preference)
        {
            return new ArgumentOutOfRangeException("preference", preference, "未知的视频编码偏好。");
        }
    }

    internal sealed class FfmpegEncoderCatalog
    {
        readonly HashSet<string> encoderNames;
        readonly ReadOnlyCollection<string> readOnlyNames;
        readonly IReadOnlyDictionary<VideoEncoderBackend, string> unavailableDetails;

        FfmpegEncoderCatalog(IEnumerable<string> names,
            IReadOnlyDictionary<VideoEncoderBackend, string>? unavailableDetails = null)
        {
            encoderNames = new HashSet<string>(names, StringComparer.Ordinal);
            var ordered = encoderNames.OrderBy(delegate(string value) { return value; }, StringComparer.Ordinal).ToArray();
            readOnlyNames = Array.AsReadOnly(ordered);
            this.unavailableDetails = unavailableDetails is null
                ? new ReadOnlyDictionary<VideoEncoderBackend, string>(new Dictionary<VideoEncoderBackend, string>())
                : new ReadOnlyDictionary<VideoEncoderBackend, string>(
                    new Dictionary<VideoEncoderBackend, string>(unavailableDetails));
        }

        internal IReadOnlyList<string> EncoderNames { get { return readOnlyNames; } }

        internal bool Supports(VideoEncoderBackend backend)
        {
            return encoderNames.Contains(VideoEncoderBackendMetadata.CodecName(backend));
        }

        internal bool SupportsCodec(string codecName)
        {
            if (codecName == null) throw new ArgumentNullException("codecName");
            return encoderNames.Contains(codecName);
        }

        internal string UnavailableDetail(VideoEncoderBackend backend)
        {
            return unavailableDetails.TryGetValue(backend, out var detail) && !string.IsNullOrWhiteSpace(detail)
                ? detail
                : "FFmpeg 未包含 " + VideoEncoderBackendMetadata.CodecName(backend) + "。";
        }

        internal static FfmpegEncoderCatalog Parse(string output,
            IReadOnlyDictionary<VideoEncoderBackend, string>? unavailableDetails = null)
        {
            if (output == null) throw new ArgumentNullException("output");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var lines = output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var separators = new[] { ' ', '\t' };
            foreach (var rawLine in lines)
            {
                var parts = rawLine.Trim().Split(separators, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !IsEncoderFlags(parts[0]) || !IsCodecIdentifier(parts[1])) continue;
                names.Add(parts[1]);
            }
            return new FfmpegEncoderCatalog(names, unavailableDetails);
        }

        static bool IsEncoderFlags(string value)
        {
            if (value.Length != 6 || (value[0] != 'V' && value[0] != 'A' && value[0] != 'S')) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var current = value[index];
                if (current != '.' && (current < 'A' || current > 'Z')) return false;
            }
            return true;
        }

        static bool IsCodecIdentifier(string value)
        {
            if (value.Length == 0) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var current = value[index];
                var valid = current >= 'a' && current <= 'z' || current >= 'A' && current <= 'Z' ||
                    current >= '0' && current <= '9' || current == '_';
                if (!valid) return false;
            }
            return true;
        }
    }

    internal sealed class VideoEncoderSelectionOptions
    {
        internal VideoEncoderSelectionOptions()
            : this(VideoEncoderPreference.Automatic, false, null)
        {
        }

        internal VideoEncoderSelectionOptions(VideoEncoderPreference preference, bool allowSoftwareFallback,
            uint? preferredAdapterVendorId)
        {
            VideoEncoderPreferenceMetadata.Validate(preference);
            Preference = preference;
            AllowSoftwareFallback = allowSoftwareFallback;
            PreferredAdapterVendorId = preferredAdapterVendorId;
        }

        internal VideoEncoderPreference Preference { get; private set; }
        internal bool AllowSoftwareFallback { get; private set; }
        internal uint? PreferredAdapterVendorId { get; private set; }
    }

    internal sealed class VideoEncoderProbeResult
    {
        VideoEncoderProbeResult(bool available, string detail)
        {
            Available = available;
            Detail = detail ?? string.Empty;
        }

        internal bool Available { get; private set; }
        internal string Detail { get; private set; }

        internal static VideoEncoderProbeResult Success(string detail)
        {
            return new VideoEncoderProbeResult(true, detail);
        }

        internal static VideoEncoderProbeResult Failure(string detail)
        {
            return new VideoEncoderProbeResult(false, detail);
        }
    }

    internal interface IVideoEncoderCapabilityProbe
    {
        Task<VideoEncoderProbeResult> ProbeAsync(VideoEncoderBackend backend, CancellationToken cancellationToken);
    }

    internal sealed class VideoEncoderProbeAttempt
    {
        internal VideoEncoderProbeAttempt(VideoEncoderBackend backend, bool compiled, bool probeAttempted,
            bool available, string detail)
        {
            Backend = backend;
            Compiled = compiled;
            ProbeAttempted = probeAttempted;
            Available = available;
            Detail = detail ?? string.Empty;
        }

        internal VideoEncoderBackend Backend { get; private set; }
        internal bool Compiled { get; private set; }
        internal bool ProbeAttempted { get; private set; }
        internal bool Available { get; private set; }
        internal string Detail { get; private set; }
    }

    /// <summary>
    /// Immutable connection-scoped decision. The caller may reuse it for every
    /// encoder rebuild without running backend selection again.
    /// </summary>
    internal sealed class VideoEncoderSelection
    {
        readonly ReadOnlyCollection<VideoEncoderProbeAttempt> attempts;

        internal VideoEncoderSelection(VideoEncoderBackend backend, uint? preferredAdapterVendorId,
            IEnumerable<VideoEncoderProbeAttempt> probeAttempts)
        {
            if (probeAttempts == null) throw new ArgumentNullException("probeAttempts");
            Backend = backend;
            CodecName = VideoEncoderBackendMetadata.CodecName(backend);
            DisplayName = VideoEncoderBackendMetadata.DisplayName(backend);
            IsHardware = VideoEncoderBackendMetadata.IsHardware(backend);
            VendorId = VideoEncoderBackendMetadata.VendorId(backend);
            PreferredAdapterVendorId = preferredAdapterVendorId;
            MatchesPreferredAdapter = VendorId.HasValue && preferredAdapterVendorId.HasValue &&
                VendorId.Value == preferredAdapterVendorId.Value;
            attempts = Array.AsReadOnly(probeAttempts.ToArray());
        }

        internal VideoEncoderBackend Backend { get; private set; }
        internal string CodecName { get; private set; }
        internal string DisplayName { get; private set; }
        internal bool IsHardware { get; private set; }
        internal uint? VendorId { get; private set; }
        internal uint? PreferredAdapterVendorId { get; private set; }
        internal bool MatchesPreferredAdapter { get; private set; }
        internal IReadOnlyList<VideoEncoderProbeAttempt> Attempts { get { return attempts; } }
    }

    internal sealed class VideoEncoderSelectionException : InvalidOperationException
    {
        readonly ReadOnlyCollection<VideoEncoderProbeAttempt> attempts;

        internal VideoEncoderSelectionException(string message, IEnumerable<VideoEncoderProbeAttempt> probeAttempts)
            : base(message)
        {
            if (probeAttempts == null) throw new ArgumentNullException("probeAttempts");
            attempts = Array.AsReadOnly(probeAttempts.ToArray());
        }

        internal IReadOnlyList<VideoEncoderProbeAttempt> Attempts { get { return attempts; } }
    }

    internal static class VideoEncoderSelector
    {
        static readonly VideoEncoderBackend[] HardwareOrder =
        {
            VideoEncoderBackend.Nvenc,
            VideoEncoderBackend.Qsv,
            VideoEncoderBackend.Amf
        };

        internal static async Task<VideoEncoderSelection> SelectAsync(FfmpegEncoderCatalog catalog,
            VideoEncoderSelectionOptions options, IVideoEncoderCapabilityProbe probe,
            CancellationToken cancellationToken)
        {
            if (catalog == null) throw new ArgumentNullException("catalog");
            if (options == null) throw new ArgumentNullException("options");
            if (probe == null) throw new ArgumentNullException("probe");
            VideoEncoderPreferenceMetadata.Validate(options.Preference);

            VideoEncoderBackend forcedBackend;
            var forced = VideoEncoderPreferenceMetadata.TryGetForcedBackend(options.Preference, out forcedBackend);
            var candidates = forced
                ? new[] { forcedBackend }
                : BuildAutomaticOrder(options.PreferredAdapterVendorId, options.AllowSoftwareFallback);
            var attempts = new List<VideoEncoderProbeAttempt>();

            foreach (var backend in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var compiled = catalog.Supports(backend);
                if (backend == VideoEncoderBackend.LibX264 && !options.AllowSoftwareFallback)
                {
                    attempts.Add(new VideoEncoderProbeAttempt(backend, compiled, false, false,
                        "软件编码未获明确允许。"));
                    break;
                }
                if (!compiled)
                {
                    attempts.Add(new VideoEncoderProbeAttempt(backend, false, false, false,
                        catalog.UnavailableDetail(backend)));
                    if (forced) break;
                    continue;
                }

                VideoEncoderProbeResult result;
                try
                {
                    result = await probe.ProbeAsync(backend, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException error)
                {
                    var detail = string.IsNullOrWhiteSpace(error.Message)
                        ? "编码器探测清理超时。"
                        : "编码器探测清理超时：" + error.Message;
                    attempts.Add(new VideoEncoderProbeAttempt(backend, true, true, false, detail));
                    if (forced) break;
                    continue;
                }
                if (result == null)
                {
                    attempts.Add(new VideoEncoderProbeAttempt(backend, true, true, false, "编码器探测未返回结果。"));
                    if (forced) break;
                    continue;
                }

                attempts.Add(new VideoEncoderProbeAttempt(backend, true, true, result.Available, result.Detail));
                if (result.Available)
                    return new VideoEncoderSelection(backend, options.PreferredAdapterVendorId, attempts);
                if (forced) break;
            }

            var suffix = options.AllowSoftwareFallback ||
                forced && forcedBackend != VideoEncoderBackend.LibX264
                ? string.Empty
                : " 软件 x264 回退默认关闭，且本次未获明确允许。";
            throw new VideoEncoderSelectionException(
                forced
                    ? "指定的视频编码器不可用，未切换到其他后端。" + suffix
                    : "没有可用的视频编码器。" + suffix,
                attempts);
        }

        static VideoEncoderBackend[] BuildAutomaticOrder(uint? preferredAdapterVendorId,
            bool allowSoftwareFallback)
        {
            var ordered = new List<VideoEncoderBackend>();
            if (preferredAdapterVendorId.HasValue)
            {
                foreach (var backend in HardwareOrder)
                {
                    var vendorId = VideoEncoderBackendMetadata.VendorId(backend);
                    if (vendorId.HasValue && vendorId.Value == preferredAdapterVendorId.Value)
                    {
                        ordered.Add(backend);
                        break;
                    }
                }
            }
            foreach (var backend in HardwareOrder)
                if (!ordered.Contains(backend)) ordered.Add(backend);
            if (allowSoftwareFallback) ordered.Add(VideoEncoderBackend.LibX264);
            return ordered.ToArray();
        }
    }
}
