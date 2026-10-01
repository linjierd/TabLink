using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TabLink.Core;

namespace TabLink.Windows;

internal static class VideoPipeline
{
    internal static string FindFfmpeg()
    {
        var bundled=Path.Combine(AppContext.BaseDirectory,"tools","ffmpeg","ffmpeg.exe");
        if(File.Exists(bundled))return bundled;
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
        {
            var source=Path.Combine(directory.FullName,"third_party","ffmpeg-tablink","bin","ffmpeg.exe");
            if(File.Exists(source))return source;
        }
        throw new FileNotFoundException("缺少已验证的 FFmpeg 编码组件，请使用完整的 TabLink 交付目录。");
    }

    internal static async IAsyncEnumerable<VideoPacket> StreamAsync(VirtualDisplayInfo display,TabletDisplayProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken,Action<string>? status=null,DisplayLease? identity=null,
        bool browserCompatible=false,AdaptiveVideoSession? quality=null)
    {
        var lease=identity??VirtualDisplayManager.CaptureLease(display);
        var current=display;
        void ValidateIdentity()
        {
            var resolved=VirtualDisplayManager.ResolveCurrent(lease);
            if(resolved.Bounds.Width!=profile.Width||resolved.Bounds.Height!=profile.Height)
                throw new IOException("虚拟副屏尺寸已改变，请按平板原生分辨率重新连接。");
            if(resolved.Bounds!=current.Bounds)
            {
                current=resolved;
                throw new CaptureUnavailableException("虚拟副屏位置已移动，正在恢复同一副屏画面。");
            }
        }
        var timestamps=new MediaTimestampClock();
        await foreach(var packet in CaptureRecovery.StreamAsync(
            ct=>EncodeAdaptiveAsync(current,profile,ct,status,lease,browserCompatible,quality),
            ValidateIdentity,InputDesktopAvailability.Query,cancellationToken))
        {
            if(packet.IsFrame)
                BinaryPrimitives.WriteInt64BigEndian(packet.Payload,timestamps.Next(packet.SourceFps??profile.RequestedRefreshRate));
            if(packet.CapturePaused is bool paused)status?.Invoke(paused?"Windows 桌面切换，USB 连接保持，等待画面恢复。":"USB 副屏画面已恢复。");
            yield return packet;
        }
    }

    static async IAsyncEnumerable<VideoPacket> EncodeAdaptiveAsync(VirtualDisplayInfo display,TabletDisplayProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken,Action<string>? status,DisplayLease identity,
        bool browserCompatible,AdaptiveVideoSession? quality)
    {
        while(true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan=quality?.CurrentPlan;
            var changed=false;
            await foreach(var packet in EncodeAsync(display,profile,cancellationToken,status,identity,browserCompatible,plan))
            {
                yield return packet;
                if(plan is not null&&quality!.CurrentPlan.Generation!=plan.Generation)
                {
                    changed=true;
                    status?.Invoke($"画质计划已更新：{quality.CurrentPlan.BitrateKbps/1000d:F1} Mbps；保持同一连接与唯一副屏，重建本次编码器。");
                    break;
                }
            }
            if(!changed)yield break;
        }
    }

    static async IAsyncEnumerable<VideoPacket> EncodeAsync(VirtualDisplayInfo display,TabletDisplayProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken,Action<string>? status,DisplayLease identity,
        bool browserCompatible,VideoEncodingPlan? plan)
    {
        var width=plan?.Width??profile.Width;
        var height=plan?.Height??profile.Height;
        var fps=plan?.Fps??profile.RequestedRefreshRate;
        await using var encoder=new H264Encoder(display,width,height,fps,FindFfmpeg(),
            H264EncoderKind.Nvenc,H264CapturePreference.PreferDesktopDuplication,identity,browserCompatible,plan);
        if(status is not null)encoder.Status+=status;
        encoder.Start();
        var configured=false;
        await foreach(var unit in encoder.ReadAccessUnitsAsync(cancellationToken))
        {
            if(!configured||unit.ConfigurationChanged)
            {
                var config=JsonSerializer.SerializeToUtf8Bytes(new{codec="video/avc",width=encoder.Width,height=encoder.Height,fps=encoder.Fps,
                    bitrateKbps=plan?.BitrateKbps,generation=plan?.Generation,
                    csd0=Convert.ToBase64String(unit.Sps),csd1=Convert.ToBase64String(unit.Pps)});
                yield return new(0x20,config,false);configured=true;
            }
            var payload=new byte[unit.Data.Length+8];
            BinaryPrimitives.WriteInt64BigEndian(payload,unit.PtsUs);unit.Data.CopyTo(payload,8);
            yield return new(0x21,payload,true,SourceFps:encoder.Fps);
        }
    }
}
