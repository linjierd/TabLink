using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TabLink.Core;

namespace TabLink.Windows;

internal sealed record VideoEncoderRuntimeSnapshot(
    string Backend,
    string Codec,
    bool Hardware,
    int Width,
    int Height,
    int RequestedFps,
    int EffectiveFps,
    bool FrameRateCapped,
    string? DowngradeReason,
    string? Adapter,
    string? DriverVersion,
    string? DriverProvider,
    string? CaptureAdapter,
    uint? PreferredAdapterVendorId,
    IReadOnlyList<VideoEncoderProbeSnapshot> Probes);

internal sealed record VideoEncoderProbeSnapshot(string Backend, bool Compiled, bool Attempted,
    bool Available, string Detail);

internal static class VideoPipeline
{
    internal static string FindFfmpeg()=>FfmpegEncoderRuntime.FindPaths().HardwarePath;

    internal static async IAsyncEnumerable<VideoPacket> StreamAsync(VirtualDisplayInfo display,TabletDisplayProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken,Action<string>? status=null,DisplayLease? identity=null,
        bool browserCompatible=false,AdaptiveVideoSession? quality=null,VideoEncoderSelectionOptions? encoderOptions=null,
        Action<VideoEncoderRuntimeSnapshot>? encoderSelected=null)
    {
        var lease=identity??VirtualDisplayManager.CaptureLease(display);
        var current=display;
        var paths=FfmpegEncoderRuntime.FindPaths();
        DxgiOutputIdentity? captureOutput=null;
        try
        {
            captureOutput=DxgiCaptureTarget.SelectUnique(display,DxgiCaptureTarget.ReadOutputs(),out var captureReason);
            if(captureOutput is null)status?.Invoke("编码器检测未绑定显示 GPU："+captureReason);
        }
        catch(Exception error) when(error is IOException or System.Runtime.InteropServices.COMException or
            DllNotFoundException or EntryPointNotFoundException)
        {status?.Invoke("编码器检测无法读取显示 GPU，将按可用后端实测："+error.Message);}
        var requestedOptions=encoderOptions??new VideoEncoderSelectionOptions();
        var selectionOptions=new VideoEncoderSelectionOptions(requestedOptions.Preference,
            requestedOptions.AllowSoftwareFallback,captureOutput?.VendorId);
        status?.Invoke("正在实测本机 H.264 编码器；本次不会创建额外虚拟屏。");
        var probe=new FfmpegEncoderCapabilityProbe(paths,profile.Width,profile.Height,
            Math.Clamp(profile.RequestedRefreshRate,1,browserCompatible?60:144),browserCompatible);
        VideoEncoderSelection selection;
        using var encoderPreparation=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        encoderPreparation.CancelAfter(SessionPresentationDeadline.EncoderProbeBudget);
        try
        {
            var catalog=await FfmpegEncoderRuntime.ReadCatalogAsync(paths,selectionOptions,
                encoderPreparation.Token).ConfigureAwait(false);
            selection=await VideoEncoderSelector.SelectAsync(catalog,selectionOptions,probe,
                encoderPreparation.Token).ConfigureAwait(false);
        }
        catch(VideoEncoderSelectionException error)
        {
            throw new IOException(error.Message+" "+FormatAttempts(error.Attempts),error);
        }
        catch(OperationCanceledException error) when(!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"编码器能力检测超过 {SessionPresentationDeadline.EncoderProbeBudget.TotalSeconds:F0} 秒，已停止本次连接。",error);
        }
        var effectiveFps=probe.EffectiveFps(selection.Backend);
        var snapshot=BuildSnapshot(selection,profile,effectiveFps,captureOutput);
        status?.Invoke($"已选择 {selection.DisplayName}：{profile.Width} × {profile.Height} @ {effectiveFps} fps"+
            (snapshot.DowngradeReason is null?"":"（"+snapshot.DowngradeReason+"）")+"。"+
            " 本次连接内的画质与捕获恢复继续使用此后端。");
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
            ct=>EncodeAdaptiveAsync(current,profile,ct,status,lease,browserCompatible,quality,selection,paths,
                effectiveFps,snapshot,encoderSelected),
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
        bool browserCompatible,AdaptiveVideoSession? quality,VideoEncoderSelection selection,FfmpegEncoderPaths paths,
        int selectedFps,VideoEncoderRuntimeSnapshot selectionSnapshot,
        Action<VideoEncoderRuntimeSnapshot>? encoderSelected)
    {
        while(true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan=quality?.CurrentPlan;
            var changed=false;
            var effectivePlan=ApplyBackendLimits(plan,selection.Backend,selectedFps);
            encoderSelected?.Invoke(BuildCurrentSnapshot(selectionSnapshot,profile,effectivePlan,selectedFps));
            await foreach(var packet in EncodeAsync(display,profile,cancellationToken,status,identity,browserCompatible,
                effectivePlan,selection,paths,selectedFps))
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
        bool browserCompatible,VideoEncodingPlan? plan,VideoEncoderSelection selection,FfmpegEncoderPaths paths,
        int selectedFps)
    {
        var width=plan?.Width??profile.Width;
        var height=plan?.Height??profile.Height;
        var fps=plan?.Fps??selectedFps;
        var executable=paths.PathFor(selection.Backend)??throw new FileNotFoundException("所选编码器组件不存在。");
        await using var encoder=new H264Encoder(display,width,height,fps,executable,
            selection.Backend,H264CapturePreference.PreferDesktopDuplication,identity,browserCompatible,plan);
        if(status is not null)encoder.Status+=status;
        encoder.Start();
        var configured=false;
        await foreach(var unit in encoder.ReadAccessUnitsAsync(cancellationToken))
        {
            if(!configured||unit.ConfigurationChanged)
            {
                var config=JsonSerializer.SerializeToUtf8Bytes(new{codec="video/avc",width=encoder.Width,height=encoder.Height,fps=encoder.Fps,
                    bitrateKbps=plan?.BitrateKbps,generation=plan?.Generation,
                    encoder=selection.CodecName,frameRateCapped=fps<profile.RequestedRefreshRate,
                    downgradeReason=selection.Backend==VideoEncoderBackend.LibX264&&fps<profile.RequestedRefreshRate?"software-30fps-limit":null,
                    csd0=Convert.ToBase64String(unit.Sps),csd1=Convert.ToBase64String(unit.Pps)});
                yield return new(0x20,config,false);configured=true;
            }
            var payload=new byte[unit.Data.Length+8];
            BinaryPrimitives.WriteInt64BigEndian(payload,unit.PtsUs);unit.Data.CopyTo(payload,8);
            yield return new(0x21,payload,true,SourceFps:encoder.Fps);
        }
    }

    static VideoEncodingPlan? ApplyBackendLimits(VideoEncodingPlan? plan,VideoEncoderBackend backend,int selectedFps)
    {
        if(plan is null||backend!=VideoEncoderBackend.LibX264||plan.Fps<=selectedFps)return plan;
        var gop=Math.Max(1,(int)Math.Round(plan.GopFrames*selectedFps/(double)plan.Fps));
        return plan with{Fps=selectedFps,GopFrames=gop,Reason=plan.Reason+"；软件兼容模式最高 30 fps"};
    }

    static VideoEncoderRuntimeSnapshot BuildCurrentSnapshot(VideoEncoderRuntimeSnapshot selected,
        TabletDisplayProfile profile,VideoEncodingPlan? plan,int selectedFps)
    {
        var width=plan?.Width??profile.Width;
        var height=plan?.Height??profile.Height;
        var fps=plan?.Fps??selectedFps;
        string? reason=null;
        if(width!=profile.Width||height!=profile.Height||fps<profile.RequestedRefreshRate)
        {
            var mode=$"当前编码 {width} × {height} @ {fps} fps";
            reason=string.IsNullOrWhiteSpace(plan?.Reason)?mode:mode+"；"+plan!.Reason;
        }
        return selected with{Width=width,Height=height,EffectiveFps=fps,
            FrameRateCapped=fps<profile.RequestedRefreshRate,
            DowngradeReason=reason??selected.DowngradeReason};
    }

    static VideoEncoderRuntimeSnapshot BuildSnapshot(VideoEncoderSelection selection,TabletDisplayProfile profile,
        int effectiveFps,DxgiOutputIdentity? captureOutput)
    {
        var drivers=DisplayAdapterDriverCatalog.Read(out var driverWarning);
        var driver=drivers.FirstOrDefault(item=>item.VendorId==selection.VendorId&&
            (captureOutput is null||captureOutput.VendorId!=item.VendorId||captureOutput.DeviceId==item.DeviceId))
            ??drivers.FirstOrDefault(item=>item.VendorId==selection.VendorId);
        var adapter=TryReadSelectedAdapter(selection.VendorId);
        var provider=driver?.ProviderName;
        if(driverWarning is not null)provider=string.IsNullOrWhiteSpace(provider)?driverWarning:provider+"；"+driverWarning;
        var downgradeReason=effectiveFps>=profile.RequestedRefreshRate?null:
            selection.Backend==VideoEncoderBackend.LibX264
                ?$"设备请求 {profile.RequestedRefreshRate} fps，软件兼容模式最高 30 fps"
                :$"设备请求 {profile.RequestedRefreshRate} fps，当前编码路径安全上限为 {effectiveFps} fps";
        return new(selection.Backend.ToString(),selection.CodecName,selection.IsHardware,profile.Width,profile.Height,
            profile.RequestedRefreshRate,effectiveFps,effectiveFps<profile.RequestedRefreshRate,downgradeReason,
            adapter?.AdapterName??driver?.Description,driver?.DriverVersion,provider,captureOutput?.AdapterName,
            selection.PreferredAdapterVendorId,selection.Attempts.Select(attempt=>new VideoEncoderProbeSnapshot(
                attempt.Backend.ToString(),attempt.Compiled,attempt.ProbeAttempted,attempt.Available,attempt.Detail)).ToArray());
    }

    static DxgiAdapterIdentity? TryReadSelectedAdapter(uint? vendorId)
    {
        if(vendorId is null)return null;
        try{return DxgiCaptureTarget.ReadAdapters().FirstOrDefault(item=>item.VendorId==vendorId);}
        catch(Exception error) when(error is IOException or System.Runtime.InteropServices.COMException or
            DllNotFoundException or EntryPointNotFoundException){return null;}
    }

    static string FormatAttempts(IEnumerable<VideoEncoderProbeAttempt> attempts)=>string.Join("；",attempts.Select(attempt=>
        VideoEncoderBackendMetadata.DisplayName(attempt.Backend)+"："+attempt.Detail));
}
