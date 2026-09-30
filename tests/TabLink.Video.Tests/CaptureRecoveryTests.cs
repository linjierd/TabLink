using System.Diagnostics;
using System.Runtime.CompilerServices;
using TabLink.Windows;

internal static class CaptureRecoveryTests
{
    static readonly InputDesktopStatus Available=new(InputDesktopState.Available,"simulated-default");
    static readonly InputDesktopStatus Unavailable=new(InputDesktopState.Unavailable,"simulated-desktop-switch");
    static CaptureRecoveryOptions Fast(TimeSpan? limit=null)=>new(TimeSpan.FromMilliseconds(5),TimeSpan.FromMilliseconds(20),
        limit??TimeSpan.FromSeconds(2),TimeSpan.FromMilliseconds(10),TimeSpan.FromMilliseconds(40));
    static void Check(bool condition,string message) { if(!condition)throw new InvalidOperationException(message); }

    internal static async Task<IReadOnlyList<string>> RunAsync()
    {
        var log=new List<string>();
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct=limit.Token;
        {
            var desktop=Available;var attempts=0;var disposals=0;var validations=0;
            async IAsyncEnumerable<VideoPacket> Source([EnumeratorCancellation]CancellationToken token)
            {
                var id=++attempts;
                try
                {
                    yield return new(0x20,[(byte)id],false);
                    yield return new(0x21,[(byte)id],true);
                    await Task.Delay(Timeout.Infinite,token);
                }
                finally { disposals++; }
            }
            void Validate(){Check(desktop.IsAvailable,"identity queried while desktop unavailable");validations++;}
            await using var stream=CaptureRecovery.StreamAsync(Source,Validate,()=>desktop,ct,Fast()).GetAsyncEnumerator(ct);
            Check(await stream.MoveNextAsync()&&stream.Current.Type==0x20,"initial config missing");
            Check(await stream.MoveNextAsync()&&stream.Current.IsFrame,"initial frame missing");
            desktop=Unavailable;var before=validations;
            Check(await stream.MoveNextAsync()&&stream.Current.CapturePaused==true,"local unavailability did not pause");
            Check(disposals==1&&attempts==1,"old source not disposed on pause");
            Check(await stream.MoveNextAsync()&&stream.Current.CapturePaused==true,"pause heartbeat missing");
            Check(validations==before&&attempts==1,"paused desktop caused validation or capture restart");
            desktop=Available;
            await UntilAsync(stream,p=>p.Type==0x20);
            Check(stream.Current.Payload[0]==2,"recovery did not create a fresh encoder config");
            await UntilAsync(stream,p=>p.CapturePaused==false);
            Check(await stream.MoveNextAsync()&&stream.Current.IsFrame&&stream.Current.Payload[0]==2,"recovered frame missing");
            log.Add("PASS local desktop unavailability stops source, skips protected topology, heartbeats, and reconfigures before resumed frame");
        }
        {
            var location=0;var requested=0;var attempts=0;var stops=0;
            async IAsyncEnumerable<VideoPacket> Source([EnumeratorCancellation]CancellationToken token)
            {
                attempts++;var capturedLocation=location;
                try {yield return new(0x20,[(byte)capturedLocation],false);yield return new(0x21,[(byte)capturedLocation],true);await Task.Delay(Timeout.Infinite,token);}
                finally {stops++;}
            }
            void Validate(){if(location!=requested){location=requested;throw new CaptureUnavailableException("verified original display moved");}}
            await using var stream=CaptureRecovery.StreamAsync(Source,Validate,()=>Available,ct,Fast()).GetAsyncEnumerator(ct);
            await UntilAsync(stream,p=>p.IsFrame);requested=7;
            await UntilAsync(stream,p=>p.CapturePaused==true);
            await UntilAsync(stream,p=>p.Type==0x20);
            Check(stream.Current.Payload[0]==7&&attempts==2&&stops==1,"position change reused stale capture coordinates/source");
            await UntilAsync(stream,p=>p.IsFrame);
            Check(stream.Current.Payload[0]==7,"new display position not used after reconfigure");
            log.Add("PASS verified position-only change cancels old capture and recreates source at new coordinates");
        }
        {
            var attempts=0;
            async IAsyncEnumerable<VideoPacket> Retry([EnumeratorCancellation]CancellationToken token)
            {attempts++;await Task.Yield();token.ThrowIfCancellationRequested();throw new CaptureUnavailableException("DXGI_ERROR_ACCESS_LOST");
#pragma warning disable CS0162
                yield break;
#pragma warning restore CS0162
            }
            var clock=Stopwatch.StartNew();
            try {await foreach(var _ in CaptureRecovery.StreamAsync(Retry,()=>{},()=>Available,ct,Fast(TimeSpan.FromMilliseconds(150)))){}throw new Exception("unbounded retry accepted");}
            catch(IOException){Check(attempts>=2&&attempts<=8&&clock.Elapsed<TimeSpan.FromSeconds(2),"retry deadline/backoff not bounded");}
            log.Add("PASS transient DXGI retry has backoff and a finite normal-desktop recovery deadline");
        }
        {
            var attempts=0;
            async IAsyncEnumerable<VideoPacket> Permanent([EnumeratorCancellation]CancellationToken token)
            {attempts++;await Task.Yield();throw new InvalidDataException("unsupported encoder parameter");
#pragma warning disable CS0162
                yield break;
#pragma warning restore CS0162
            }
            try {await foreach(var _ in CaptureRecovery.StreamAsync(Permanent,()=>{},()=>Available,ct,Fast())){}throw new Exception("permanent failure retried");}
            catch(InvalidDataException){Check(attempts==1,"permanent encoder error restarted");}
            log.Add("PASS permanent encoder errors fail immediately instead of falling back or retrying indefinitely");
            attempts=0;
            try {await foreach(var _ in CaptureRecovery.StreamAsync(Permanent,()=>throw new IOException("identity replaced"),()=>Available,ct,Fast())){}throw new Exception("identity mismatch accepted");}
            catch(IOException){Check(attempts==0,"identity mismatch started a replacement capture");}
            log.Add("PASS permanent identity/primary/mode validation failure prevents capture creation");
        }
        {
            var captures=0;var validations=0;
            IAsyncEnumerable<VideoPacket> Forbidden(CancellationToken _) {captures++;throw new Exception("unknown desktop capture attempted");}
            try {await foreach(var _ in CaptureRecovery.StreamAsync(Forbidden,()=>validations++,()=>new(InputDesktopState.Unknown,"simulated-api-error"),ct,Fast(TimeSpan.FromMilliseconds(80)))){}throw new Exception("unknown desktop waits forever");}
            catch(IOException){Check(captures==0&&validations==0,"unknown desktop touched capture/topology");}
            log.Add("PASS unknown desktop status never captures and cannot extend the watchdog forever");
        }
        {
            var validations=0;var captures=0;var elapsed=Stopwatch.StartNew();
            IAsyncEnumerable<VideoPacket> Forbidden(CancellationToken _) {captures++;throw new Exception("unstable layout was captured");}
            try
            {
                await foreach(var _ in CaptureRecovery.StreamAsync(Forbidden,
                    ()=>{validations++;throw new DisplayLayoutChangingException("position changed during snapshot");},
                    ()=>Available,ct,Fast(TimeSpan.FromMilliseconds(100)))){}
                throw new Exception("layout race retried indefinitely");
            }
            catch(IOException)
            {Check(captures==0&&validations>=2&&validations<50&&elapsed.Elapsed<TimeSpan.FromSeconds(2),"layout race was not bounded or bypassed identity check");}
            log.Add("PASS position snapshot races retry without capture and stop at a fixed recovery deadline");
        }
        Check(H264Encoder.IsDesktopAccessFailure(new IOException("wrapped DXGI",new System.Runtime.InteropServices.COMException("denied",unchecked((int)0x80070005))))&&
              !H264Encoder.IsDesktopAccessFailure(new IOException("encoder unsupported")),"wrapped DXGI access loss fell back to GDI");
        log.Add("PASS wrapped DXGI access failures remain recoverable without broadening permanent encoder errors");
        Check(H264Encoder.IsTransientDesktopFailure("Desktop duplication access denied")&&
              H264Encoder.IsTransientDesktopFailure("Failed to acquire frame: 0x887a0026")&&
              !H264Encoder.IsTransientDesktopFailure("NVENC API version unsupported")&&
              !H264Encoder.IsTransientDesktopFailure("Invalid encoder parameter: operation not permitted"),"DDA failure classification too broad");
        log.Add("PASS transient classification is capture-specific and excludes permanent NVENC/parameter failures");
        return log;
    }

    static async Task UntilAsync(IAsyncEnumerator<VideoPacket> stream,Func<VideoPacket,bool> predicate)
    {
        while(await stream.MoveNextAsync())if(predicate(stream.Current))return;
        throw new IOException("Recovery stream ended before expected packet");
    }
}
