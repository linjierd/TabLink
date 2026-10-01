using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TabLink.Windows
{
    internal static class VideoEncoderSelectionTests
    {
        internal static async Task<IReadOnlyList<string>> RunAsync()
        {
            var results = new List<string>();
            Action<bool, string> check = delegate(bool value, string message)
            {
                if (!value) throw new InvalidOperationException(message);
            };

            var ffmpegOutput = string.Join("\r\n", new[]
            {
                "Encoders:",
                " V..... = Video",
                " ------",
                " V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)",
                " V..... h264_qsv             H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (Intel Quick Sync Video acceleration)",
                " V....D h264_amf             AMD AMF H.264 Encoder (codec h264)",
                " V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10",
                " V....D h264_nvenc           duplicate must be ignored",
                " not-an-encoder h264_fake    ignored"
            });
            var all = FfmpegEncoderCatalog.Parse(ffmpegOutput);
            check(all.EncoderNames.Count == 4 &&
                  all.Supports(VideoEncoderBackend.Nvenc) &&
                  all.Supports(VideoEncoderBackend.Qsv) &&
                  all.Supports(VideoEncoderBackend.Amf) &&
                  all.Supports(VideoEncoderBackend.LibX264),
                "FFmpeg encoder output parser missed or invented a supported backend");
            results.Add("FFmpeg -encoders output is parsed into the four exact H.264 backend names");

            var expected = new Dictionary<VideoEncoderBackend, string>
            {
                { VideoEncoderBackend.Nvenc, "h264_nvenc" },
                { VideoEncoderBackend.Qsv, "h264_qsv" },
                { VideoEncoderBackend.Amf, "h264_amf" },
                { VideoEncoderBackend.LibX264, "libx264" }
            };
            var enumValues = (VideoEncoderBackend[])Enum.GetValues(typeof(VideoEncoderBackend));
            check(enumValues.Length == expected.Count && enumValues.All(delegate(VideoEncoderBackend value)
                {
                    return expected.ContainsKey(value) && VideoEncoderBackendMetadata.CodecName(value) == expected[value] &&
                        !string.IsNullOrWhiteSpace(VideoEncoderBackendMetadata.DisplayName(value));
                }),
                "backend enum and codec-name map are not exhaustive");
            check(VideoEncoderBackendMetadata.IsHardware(VideoEncoderBackend.Nvenc) &&
                  VideoEncoderBackendMetadata.IsHardware(VideoEncoderBackend.Qsv) &&
                  VideoEncoderBackendMetadata.IsHardware(VideoEncoderBackend.Amf) &&
                  !VideoEncoderBackendMetadata.IsHardware(VideoEncoderBackend.LibX264),
                "hardware/software backend classification is wrong");
            var expectedPreferences = new Dictionary<VideoEncoderPreference, VideoEncoderBackend>
            {
                { VideoEncoderPreference.Nvenc, VideoEncoderBackend.Nvenc },
                { VideoEncoderPreference.Qsv, VideoEncoderBackend.Qsv },
                { VideoEncoderPreference.Amf, VideoEncoderBackend.Amf },
                { VideoEncoderPreference.LibX264, VideoEncoderBackend.LibX264 }
            };
            var preferenceValues = (VideoEncoderPreference[])Enum.GetValues(typeof(VideoEncoderPreference));
            check(preferenceValues.Length == expectedPreferences.Count + 1 && preferenceValues.All(
                delegate(VideoEncoderPreference value)
                {
                    if (string.IsNullOrWhiteSpace(VideoEncoderPreferenceMetadata.DisplayName(value))) return false;
                    VideoEncoderBackend mapped;
                    var forcedPreference = VideoEncoderPreferenceMetadata.TryGetForcedBackend(value, out mapped);
                    return value == VideoEncoderPreference.Automatic
                        ? !forcedPreference
                        : forcedPreference && expectedPreferences.ContainsKey(value) && expectedPreferences[value] == mapped;
                }), "preference enum and display/backend maps are not exhaustive");
            results.Add("backend and preference metadata exhaustively map Auto, NVENC, QSV, AMF and libx264");

            var defaults = new VideoEncoderSelectionOptions();
            check(defaults.Preference == VideoEncoderPreference.Automatic && !defaults.AllowSoftwareFallback,
                "software fallback is not disabled by default");
            results.Add("automatic selection defaults to hardware-only operation");

            var automaticHardwareOnly = FfmpegEncoderRuntime.PlanCatalogRead(defaults);
            var automaticWithSoftware = FfmpegEncoderRuntime.PlanCatalogRead(
                new VideoEncoderSelectionOptions(VideoEncoderPreference.Automatic, true, null));
            var forcedHardware = FfmpegEncoderRuntime.PlanCatalogRead(
                new VideoEncoderSelectionOptions(VideoEncoderPreference.Nvenc, true, null));
            var forcedSoftware = FfmpegEncoderRuntime.PlanCatalogRead(
                new VideoEncoderSelectionOptions(VideoEncoderPreference.LibX264, true, null));
            var deniedSoftware = FfmpegEncoderRuntime.PlanCatalogRead(
                new VideoEncoderSelectionOptions(VideoEncoderPreference.LibX264, false, null));
            check(automaticHardwareOnly == new FfmpegCatalogReadPlan(true, false) &&
                  automaticWithSoftware == new FfmpegCatalogReadPlan(true, true) &&
                  forcedHardware == new FfmpegCatalogReadPlan(true, false) &&
                  forcedSoftware == new FfmpegCatalogReadPlan(false, true) &&
                  deniedSoftware == new FfmpegCatalogReadPlan(false, false),
                "catalog reader touches an unneeded or unauthorized helper");
            results.Add("catalog inspection reads only helpers authorized and required by the selected policy");

            var missingCatalog = FfmpegEncoderCatalog.Parse(" V....D h264_nvenc NVIDIA NVENC H.264 encoder");
            var missingProbe = new FakeVideoEncoderProbe();
            var missing = await ExpectSelectionFailure(delegate
            {
                return VideoEncoderSelector.SelectAsync(missingCatalog,
                    new VideoEncoderSelectionOptions(VideoEncoderPreference.Qsv, false, null),
                    missingProbe, CancellationToken.None);
            });
            check(missing.Attempts.Count == 1 && missing.Attempts[0].Backend == VideoEncoderBackend.Qsv &&
                  !missing.Attempts[0].Compiled && !missing.Attempts[0].ProbeAttempted &&
                  missingProbe.Calls.Count == 0,
                "a missing forced encoder was probed or silently replaced");
            results.Add("a forced backend missing from FFmpeg fails before probing and never falls back");

            var forcedProbe = new FakeVideoEncoderProbe();
            forcedProbe.Set(VideoEncoderBackend.Qsv, VideoEncoderProbeResult.Failure("QSV driver unavailable"));
            forcedProbe.Set(VideoEncoderBackend.Nvenc, VideoEncoderProbeResult.Success("NVENC ready"));
            var forced = await ExpectSelectionFailure(delegate
            {
                return VideoEncoderSelector.SelectAsync(all,
                    new VideoEncoderSelectionOptions(VideoEncoderPreference.Qsv, true,
                        VideoEncoderBackendMetadata.IntelVendorId),
                    forcedProbe, CancellationToken.None);
            });
            check(forced.Attempts.Count == 1 && forced.Attempts[0].Backend == VideoEncoderBackend.Qsv &&
                  forcedProbe.Calls.SequenceEqual(new[] { VideoEncoderBackend.Qsv }),
                "forced QSV failure silently tried another backend");
            results.Add("a forced backend probe failure never switches to another hardware or software backend");

            var preferredProbe = new FakeVideoEncoderProbe();
            preferredProbe.Set(VideoEncoderBackend.Amf, VideoEncoderProbeResult.Failure("AMF runtime unavailable"));
            preferredProbe.Set(VideoEncoderBackend.Nvenc, VideoEncoderProbeResult.Success("NVENC ready"));
            var preferred = await VideoEncoderSelector.SelectAsync(all,
                new VideoEncoderSelectionOptions(VideoEncoderPreference.Automatic, false,
                    VideoEncoderBackendMetadata.AmdVendorId),
                preferredProbe, CancellationToken.None);
            check(preferred.Backend == VideoEncoderBackend.Nvenc &&
                  preferredProbe.Calls.SequenceEqual(new[] { VideoEncoderBackend.Amf, VideoEncoderBackend.Nvenc }) &&
                  !preferred.MatchesPreferredAdapter && preferred.IsHardware,
                "automatic selection did not probe matching-vendor hardware before other hardware");
            results.Add("automatic mode tries matching-vendor hardware first, then deterministic hardware alternatives");

            var timeoutProbe = new FakeVideoEncoderProbe();
            timeoutProbe.SetTimeout(VideoEncoderBackend.Amf, "stderr cleanup exceeded its deadline");
            timeoutProbe.Set(VideoEncoderBackend.Nvenc, VideoEncoderProbeResult.Success("NVENC ready"));
            var afterTimeout = await VideoEncoderSelector.SelectAsync(all,
                new VideoEncoderSelectionOptions(VideoEncoderPreference.Automatic, false,
                    VideoEncoderBackendMetadata.AmdVendorId),
                timeoutProbe, CancellationToken.None);
            check(afterTimeout.Backend == VideoEncoderBackend.Nvenc &&
                  timeoutProbe.Calls.SequenceEqual(new[] { VideoEncoderBackend.Amf, VideoEncoderBackend.Nvenc }) &&
                  afterTimeout.Attempts.Count == 2 && !afterTimeout.Attempts[0].Available &&
                  afterTimeout.Attempts[0].Detail.Contains("超时", StringComparison.Ordinal),
                "automatic selection aborted instead of recording a timed-out probe candidate");
            var forcedTimeoutProbe = new FakeVideoEncoderProbe();
            forcedTimeoutProbe.SetTimeout(VideoEncoderBackend.Qsv, "stderr cleanup exceeded its deadline");
            forcedTimeoutProbe.Set(VideoEncoderBackend.Nvenc, VideoEncoderProbeResult.Success("NVENC ready"));
            var forcedTimeout = await ExpectSelectionFailure(delegate
            {
                return VideoEncoderSelector.SelectAsync(all,
                    new VideoEncoderSelectionOptions(VideoEncoderPreference.Qsv, false, null),
                    forcedTimeoutProbe, CancellationToken.None);
            });
            check(forcedTimeout.Attempts.Count == 1 &&
                  forcedTimeout.Attempts[0].Backend == VideoEncoderBackend.Qsv &&
                  forcedTimeout.Attempts[0].Detail.Contains("超时", StringComparison.Ordinal) &&
                  forcedTimeoutProbe.Calls.SequenceEqual(new[] { VideoEncoderBackend.Qsv }),
                "forced timed-out probe switched to another backend");
            results.Add("probe cleanup timeouts fail one candidate; Auto continues while forced selection stays pinned");

            var hardwareFails = new FakeVideoEncoderProbe();
            hardwareFails.Set(VideoEncoderBackend.Nvenc, VideoEncoderProbeResult.Failure("NVENC unavailable"));
            hardwareFails.Set(VideoEncoderBackend.Qsv, VideoEncoderProbeResult.Failure("QSV unavailable"));
            hardwareFails.Set(VideoEncoderBackend.Amf, VideoEncoderProbeResult.Failure("AMF unavailable"));
            hardwareFails.Set(VideoEncoderBackend.LibX264, VideoEncoderProbeResult.Success("x264 ready"));
            var softwareDenied = await ExpectSelectionFailure(delegate
            {
                return VideoEncoderSelector.SelectAsync(all,
                    new VideoEncoderSelectionOptions(VideoEncoderPreference.Automatic, false, null),
                    hardwareFails, CancellationToken.None);
            });
            check(softwareDenied.Attempts.Count == 3 &&
                  !hardwareFails.Calls.Contains(VideoEncoderBackend.LibX264),
                "automatic mode probed x264 without explicit permission");
            var softwareAllowed = await VideoEncoderSelector.SelectAsync(all,
                new VideoEncoderSelectionOptions(VideoEncoderPreference.Automatic, true, null),
                hardwareFails, CancellationToken.None);
            check(softwareAllowed.Backend == VideoEncoderBackend.LibX264 && !softwareAllowed.IsHardware &&
                  hardwareFails.Calls.Last() == VideoEncoderBackend.LibX264,
                "explicit software fallback permission did not allow x264 after hardware failures");
            results.Add("x264 is excluded by default and used only after explicit software-fallback permission");

            var x264DeniedProbe = new FakeVideoEncoderProbe();
            x264DeniedProbe.Set(VideoEncoderBackend.LibX264, VideoEncoderProbeResult.Success("x264 ready"));
            var forcedSoftwareDenied = await ExpectSelectionFailure(delegate
            {
                return VideoEncoderSelector.SelectAsync(all,
                    new VideoEncoderSelectionOptions(VideoEncoderPreference.LibX264, false, null),
                    x264DeniedProbe, CancellationToken.None);
            });
            check(forcedSoftwareDenied.Attempts.Count == 1 && !forcedSoftwareDenied.Attempts[0].ProbeAttempted &&
                  x264DeniedProbe.Calls.Count == 0,
                "forced x264 bypassed the explicit software permission gate");
            results.Add("even a forced x264 preference requires the explicit software permission gate");

            var stickyProbe = new FakeVideoEncoderProbe();
            stickyProbe.Set(VideoEncoderBackend.Nvenc, VideoEncoderProbeResult.Success("NVENC ready"));
            var sticky = await VideoEncoderSelector.SelectAsync(all,
                new VideoEncoderSelectionOptions(VideoEncoderPreference.Automatic, false,
                    VideoEncoderBackendMetadata.NvidiaVendorId),
                stickyProbe, CancellationToken.None);
            stickyProbe.Set(VideoEncoderBackend.Nvenc, VideoEncoderProbeResult.Failure("changed after selection"));
            check(sticky.Backend == VideoEncoderBackend.Nvenc && sticky.CodecName == "h264_nvenc" &&
                  sticky.MatchesPreferredAdapter && sticky.Attempts.Count == 1 && sticky.Attempts[0].Available,
                "connection-scoped selection changed with later probe state");
            results.Add("the immutable selection remains sticky after the probe state changes");

            ExpectArgumentOutOfRange(delegate { VideoEncoderBackendMetadata.CodecName((VideoEncoderBackend)99); });
            ExpectArgumentOutOfRange(delegate { VideoEncoderBackendMetadata.DisplayName((VideoEncoderBackend)99); });
            ExpectArgumentOutOfRange(delegate { VideoEncoderBackendMetadata.IsHardware((VideoEncoderBackend)99); });
            ExpectArgumentOutOfRange(delegate { VideoEncoderBackendMetadata.VendorId((VideoEncoderBackend)99); });
            ExpectArgumentOutOfRange(delegate { VideoEncoderPreferenceMetadata.DisplayName((VideoEncoderPreference)99); });
            ExpectArgumentOutOfRange(delegate
            {
                new VideoEncoderSelectionOptions((VideoEncoderPreference)99, false, null);
            });
            results.Add("unknown backend and preference enum values fail closed in every public mapping path");

            return results;
        }

        static async Task<VideoEncoderSelectionException> ExpectSelectionFailure(
            Func<Task<VideoEncoderSelection>> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (VideoEncoderSelectionException error)
            {
                return error;
            }
            throw new InvalidOperationException("expected video encoder selection failure");
        }

        static void ExpectArgumentOutOfRange(Action action)
        {
            try
            {
                action();
            }
            catch (ArgumentOutOfRangeException)
            {
                return;
            }
            throw new InvalidOperationException("unknown enum value was accepted");
        }

        sealed class FakeVideoEncoderProbe : IVideoEncoderCapabilityProbe
        {
            readonly Dictionary<VideoEncoderBackend, VideoEncoderProbeResult> results =
                new Dictionary<VideoEncoderBackend, VideoEncoderProbeResult>();
            readonly Dictionary<VideoEncoderBackend, string> timeouts =
                new Dictionary<VideoEncoderBackend, string>();
            readonly List<VideoEncoderBackend> calls = new List<VideoEncoderBackend>();

            internal IReadOnlyList<VideoEncoderBackend> Calls { get { return calls.AsReadOnly(); } }

            internal void Set(VideoEncoderBackend backend, VideoEncoderProbeResult result)
            {
                results[backend] = result;
            }

            internal void SetTimeout(VideoEncoderBackend backend, string message)
            {
                timeouts[backend] = message;
            }

            public Task<VideoEncoderProbeResult> ProbeAsync(VideoEncoderBackend backend,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                calls.Add(backend);
                if (timeouts.ContainsKey(backend)) throw new TimeoutException(timeouts[backend]);
                if (results.ContainsKey(backend)) return Task.FromResult(results[backend]);
                return Task.FromResult(VideoEncoderProbeResult.Failure("not configured"));
            }
        }
    }
}
