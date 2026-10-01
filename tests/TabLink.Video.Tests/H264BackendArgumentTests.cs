using TabLink.Windows;

internal static class H264BackendArgumentTests
{
    internal static async Task<IReadOnlyList<string>> RunAsync()
    {
        var results=new List<string>();
        var executable=Environment.ProcessPath??throw new InvalidOperationException("missing test executable path");
        foreach(var backend in Enum.GetValues<VideoEncoderBackend>())
        {
            await using var encoder=H264Encoder.CreateSynthetic(1280,720,60,executable,backend,2);
            var args=encoder.BuildArgumentsForTesting();
            RequirePair(args,"-c:v",VideoEncoderBackendMetadata.CodecName(backend));
            RequirePair(args,"-bf","0");
            RequirePair(args,"-bsf:v","h264_metadata=aud=insert");
            RequirePair(args,"-profile:v","high");
            if(backend is VideoEncoderBackend.Qsv or VideoEncoderBackend.Amf)RequirePair(args,"-pix_fmt","nv12");
            else RequirePair(args,"-pix_fmt","yuv420p");
        }
        results.Add("all four backends use exhaustive codec arguments, no B frames and AUD normalization");

        await using(var qsv=H264Encoder.CreateSynthetic(1280,720,60,executable,VideoEncoderBackend.Qsv,2))
        {
            var args=qsv.BuildArgumentsForTesting();
            RequirePair(args,"-async_depth","1");RequirePair(args,"-look_ahead","0");RequirePair(args,"-forced_idr","1");
        }
        await using(var amf=H264Encoder.CreateSynthetic(1280,720,60,executable,VideoEncoderBackend.Amf,2))
        {
            var args=amf.BuildArgumentsForTesting();
            RequirePair(args,"-usage","ultralowlatency");RequirePair(args,"-preanalysis","0");RequirePair(args,"-frame_skipping","0");
        }
        await using(var x264=H264Encoder.CreateSynthetic(1280,720,30,executable,VideoEncoderBackend.LibX264,2))
        {
            var args=x264.BuildArgumentsForTesting();
            RequirePair(args,"-preset","ultrafast");RequirePair(args,"-tune","zerolatency");
            RequirePair(args,"-x264-params","repeat-headers=1:scenecut=0:rc-lookahead=0:sync-lookahead=0");
        }
        results.Add("QSV, AMF and x264 argument sets retain their low-latency backend-specific controls");

        var sample=new byte[]{0,0,0,1,9,0xf0,0,0,0,1,0x41,1};
        var config=new AnnexBAccessUnit(sample,new byte[]{0,0,0,1,0x67,1},new byte[]{0,0,0,1,0x68,1},false,true);
        Reject(()=>H264Encoder.ValidateAccessUnitForTesting(config,0),"first/config access unit without IDR");
        var idr=config with{IsKeyFrame=true};
        H264Encoder.ValidateAccessUnitForTesting(idr,0);
        results.Add("every encoder instance and every configuration change must begin on an IDR access unit");

        Reject(()=>H264Encoder.CreateSynthetic(4096,4096,30,executable),"H.264 area above 16 million pixels");
        results.Add("Windows rejects display modes outside the Android decoder pixel-area contract");

        var startupFallback=new H264CaptureStartupFallback(VideoEncoderBackend.Nvenc);
        Check(startupFallback.TryBegin(usingDesktopDuplication:true,emittedFrames:0),
            "DDA startup did not permit its single pre-frame capture fallback");
        Check(startupFallback.Attempted&&startupFallback.SelectedBackend==VideoEncoderBackend.Nvenc,
            "capture fallback changed the selected encoder backend");
        Check(!startupFallback.TryBegin(usingDesktopDuplication:true,emittedFrames:0),
            "DDA startup fallback was permitted twice");
        var afterFrame=new H264CaptureStartupFallback(VideoEncoderBackend.Nvenc);
        Check(!afterFrame.TryBegin(usingDesktopDuplication:true,emittedFrames:1)&&afterFrame.FrameObserved&&
              !afterFrame.TryBegin(usingDesktopDuplication:true,emittedFrames:0),
            "capture fallback remained available after a frame crossed the boundary");
        var alreadyGdi=new H264CaptureStartupFallback(VideoEncoderBackend.Nvenc);
        Check(!alreadyGdi.TryBegin(usingDesktopDuplication:false,emittedFrames:0),
            "GDI startup incorrectly requested another GDI retry");
        results.Add("DDA can fall back to GDI exactly once before frame one while retaining the selected encoder backend");

        Check(!H264Encoder.TryExpectedKillForTesting(()=>throw new System.ComponentModel.Win32Exception(5))&&
              !H264Encoder.TryExpectedKillForTesting(()=>throw new InvalidOperationException("already exited")),
            "expected process kill races escaped cleanup");
        var unexpectedKillEscaped=false;
        try { H264Encoder.TryExpectedKillForTesting(()=>throw new IOException("unexpected")); }
        catch(IOException) { unexpectedKillEscaped=true; }
        Check(unexpectedKillEscaped,"kill cleanup swallowed an unrelated failure");
        results.Add("owned-process cleanup absorbs Win32 and exit races without broad exception swallowing");

        var blockedPump=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed=await H264Encoder.WaitForErrorPumpForTestingAsync(blockedPump.Task,TimeSpan.FromMilliseconds(20));
        Check(!completed,"blocked stderr cleanup was not bounded");
        var originalFailure=new IOException("original encoder failure");
        Exception? observed=null;
        try
        {
            try { throw originalFailure; }
            finally
            {
                _=await H264Encoder.WaitForErrorPumpForTestingAsync(blockedPump.Task,TimeSpan.FromMilliseconds(20));
            }
        }
        catch(Exception error) { observed=error; }
        Check(ReferenceEquals(observed,originalFailure),"stderr cleanup timeout replaced the encoder failure");
        Check(await H264Encoder.WaitForErrorPumpForTestingAsync(Task.CompletedTask,TimeSpan.FromMilliseconds(20)),
            "completed stderr pump was reported incomplete");
        results.Add("stderr cleanup is bounded and cannot replace the original encoder failure");
        return results;
    }

    static void RequirePair(IReadOnlyList<string> args,string option,string value)
    {
        for(var i=0;i+1<args.Count;i++)if(args[i]==option&&args[i+1]==value)return;
        throw new InvalidOperationException($"missing encoder argument {option} {value}");
    }

    static void Reject(Action action,string label)
    {
        try{action();throw new InvalidOperationException("accepted "+label);}
        catch(Exception error) when(error is InvalidDataException or ArgumentOutOfRangeException){}
    }

    static void Check(bool value,string message)
    {
        if(!value)throw new InvalidOperationException(message);
    }
}
