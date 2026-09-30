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
        [EnumeratorCancellation] CancellationToken cancellationToken,Action<string>? status=null,DisplayLease? identity=null,bool browserCompatible=false)
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
        long sequence=0;
        await foreach(var packet in CaptureRecovery.StreamAsync(
            ct=>EncodeAsync(current,profile,ct,status,lease,browserCompatible),ValidateIdentity,InputDesktopAvailability.Query,cancellationToken))
        {
            if(packet.IsFrame)
                BinaryPrimitives.WriteInt64BigEndian(packet.Payload,checked(sequence++*1_000_000L/profile.RequestedRefreshRate));
            if(packet.CapturePaused is bool paused)status?.Invoke(paused?"Windows 桌面切换，USB 连接保持，等待画面恢复。":"USB 副屏画面已恢复。");
            yield return packet;
        }
    }

    static async IAsyncEnumerable<VideoPacket> EncodeAsync(VirtualDisplayInfo display,TabletDisplayProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken,Action<string>? status,DisplayLease identity,bool browserCompatible)
    {
        await using var encoder=new H264Encoder(display,profile.Width,profile.Height,profile.RequestedRefreshRate,FindFfmpeg(),
            H264EncoderKind.Nvenc,H264CapturePreference.PreferDesktopDuplication,identity,browserCompatible);
        if(status is not null)encoder.Status+=status;
        encoder.Start();
        var configured=false;
        await foreach(var unit in encoder.ReadAccessUnitsAsync(cancellationToken))
        {
            if(!configured||unit.ConfigurationChanged)
            {
                var config=JsonSerializer.SerializeToUtf8Bytes(new{codec="video/avc",width=encoder.Width,height=encoder.Height,fps=encoder.Fps,
                    csd0=Convert.ToBase64String(unit.Sps),csd1=Convert.ToBase64String(unit.Pps)});
                yield return new(0x20,config,false);configured=true;
            }
            var payload=new byte[unit.Data.Length+8];
            BinaryPrimitives.WriteInt64BigEndian(payload,unit.PtsUs);unit.Data.CopyTo(payload,8);
            yield return new(0x21,payload,true);
        }
    }
}
