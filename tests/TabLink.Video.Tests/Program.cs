using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using TabLink.Windows;

if(args.Length==2&&args[0]=="--stream-throughput")
{
    await StreamThroughputProbe.RunAsync(args[1]);
    return;
}

if(args.Length==2&&args[0]=="--profile-host")
{
    try { HostPerformanceProbe.Run(args[1]); }
    catch(Exception error) { Console.Error.WriteLine(error.Message);Environment.ExitCode=1; }
    return;
}

if(args.Length==3&&args[0]=="--browser-fixtures")
{
    Directory.CreateDirectory(args[2]);
    foreach(var mode in new[]{(W:1280,H:720,Fps:30,Name:"baseline-synthetic.h264"),(W:1080,H:1920,Fps:60,Name:"baseline-1080portrait60.h264")})
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var encoder=H264Encoder.CreateSynthetic(mode.W,mode.H,mode.Fps,args[1],frameCount:mode.Fps*2,browserCompatible:true);
        var target=Path.Combine(Path.GetFullPath(args[2]),mode.Name);
        await using var output=File.Create(target);
        encoder.Start();int count=0;
        await foreach(var unit in encoder.ReadAccessUnitsAsync(timeout.Token))
        {
            int offset=Array.IndexOf(unit.Sps,(byte)0x67);
            if(offset<0||unit.Sps[offset+1]!=66||unit.Sps[offset+3]!=42)throw new Exception("Browser source must emit Baseline level4.2 SPS.");
            await output.WriteAsync(unit.Data,timeout.Token);count++;
        }
        if(count!=mode.Fps*2)throw new Exception("Browser encoder lost frames.");
        Console.WriteLine($"PASS production H264Encoder browser mode {mode.W}x{mode.H}@{mode.Fps}: {count} Baseline level4.2 frames -> {target}");
    }
    return;
}

if(args.Length==1&&args[0]=="--transport-legacy-tests")
{
    await SelfTests.RunAsync();
    return;
}

if(args.Length==1&&args[0]=="--recovery-tests")
{
    foreach(var result in await CaptureRecoveryTests.RunAsync())Console.WriteLine(result);
    return;
}
if(args.Length==1&&args[0]=="--recovery-tcp-tests")
{
    foreach(var result in await SelfTests.RunCaptureRecoveryProtocolAsync())Console.WriteLine(result);
    return;
}

if (args.Length == 3 && args[0] == "--d3d-motion-probe")
{
    try
    {
        var report = await NativeMotionProbe.RunAsync(args[1], int.Parse(args[2]));
        Console.WriteLine(report.GetRawText());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length == 3 && args[0] == "--motion-probe")
{
    try
    {
        var report = await MotionProbe.RunAsync(args[1], int.Parse(args[2]));
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented=true }));
        Environment.ExitCode = report.Success ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length == 1 && args[0] == "--dxgi-probe")
{
    Console.WriteLine(JsonSerializer.Serialize(DxgiCaptureTarget.ReadOutputs(), new JsonSerializerOptions { WriteIndented=true }));
    return;
}

if (args.Length == 2 && args[0] == "--hold-owned-encoder")
{
    await using var held = H264Encoder.CreateSynthetic(1200, 1920, 90, args[1], frameCount: 10000);
    held.Start();
    Console.WriteLine(held.ProcessId);
    Console.Out.Flush();
    await Task.Delay(Timeout.Infinite);
    return;
}

var results = new List<string>();
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
void Pass(string message) => results.Add("PASS " + message);
foreach (var result in VideoQualityTests.Run()) Pass(result);
byte[] Join(params byte[][] chunks) => chunks.SelectMany(x => x).ToArray();
byte[] aud = [0,0,0,1,9,0xf0], sps = [0,0,0,1,0x67,0x42,0,0x1f], pps = [0,0,1,0x68,0xab,0xcd];
byte[] idr = [0,0,1,0x65,0x88,0,0,3,1,0x90], predicted = [0,0,0,1,0x41,0x88,0x90];
var first = Join(aud,sps,pps,idr,idr);
var second = Join(aud,predicted);
var bytes = Join(first,second);
for(var chunk=1;chunk<=bytes.Length;chunk++)
{
    var parser=new AnnexBParser();var units=new List<AnnexBAccessUnit>();
    for(var offset=0;offset<bytes.Length;offset+=chunk)units.AddRange(parser.Append(bytes.AsSpan(offset,Math.Min(chunk,bytes.Length-offset))));
    if(parser.Complete() is {} final)units.Add(final);
    Check(units.Count==2&&units[0].Data.SequenceEqual(first)&&units[1].Data.SequenceEqual(second),"chunk-boundary AU framing");
    Check(units[0].IsKeyFrame&&!units[1].IsKeyFrame,"keyframe detection");
    Check(units[0].Sps.SequenceEqual(sps)&&units[0].Pps.SequenceEqual(new byte[]{0,0,0,1,0x68,0xab,0xcd}),"SPS/PPS Annex B normalization");
    Check(units[0].ConfigurationChanged&&!units[1].ConfigurationChanged,"SPS/PPS change detection");
}
Pass("all stdout chunk boundaries preserve complete multi-slice AUs, emulation prevention, SPS/PPS and keyframes");
{
    var parser=new AnnexBParser();var changedSps=sps.ToArray();changedSps[^1]=0x20;
    var units=parser.Append(Join(first,aud,changedSps,pps,idr,aud,predicted));
    Check(units.Count==2&&units[1].ConfigurationChanged&&units[1].Sps[^1]==0x20,"configuration update missing");
    Pass("new SPS configuration is attached to the correct access unit");
}
void Reject(byte[] bad,string label,bool appendOnly=false)
{
    try{var parser=new AnnexBParser();parser.Append(bad);if(!appendOnly)parser.Complete();throw new Exception("accepted "+label);}
    catch(InvalidDataException){Pass(label);}
}
Reject(Join(sps,pps,idr),"missing AUD fails closed");
Reject(Join(aud,idr),"missing SPS/PPS fails closed");
Reject(Join(first,new byte[]{0,0,1}),"truncated NAL header fails closed");
Reject(Join(aud,sps,pps,new byte[]{0,0,1,0xe5,0x80}),"forbidden NAL bit fails closed");
Reject(new byte[AnnexBParser.MaxAccessUnitBytes+1],"oversized input rejected before growth",true);

{
    var bounds=new Rectangle(2560,0,1200,1920);
    var selected=new VirtualDisplayInfo(@"\\.\DISPLAY2","test VDD",false,bounds,true);
    var exact=new DxgiOutputIdentity(3,0,"render GPU",12,34,selected.DeviceName,bounds,true,1);
    Check(DxgiCaptureTarget.SelectUnique(selected,[exact],out _) == exact,"exact DXGI identity not selected");
    Check(DxgiCaptureTarget.SelectUnique(selected with {IsPrimary=true},[exact],out _) is null,"primary display accepted");
    Check(DxgiCaptureTarget.SelectUnique(selected with {IsTabLinkCompatible=false},[exact],out _) is null,"unverified display accepted");
    Check(DxgiCaptureTarget.SelectUnique(selected,[exact with {DeviceName=@"\\.\DISPLAY1"}],out _) is null,"index guessed from wrong display");
    Check(DxgiCaptureTarget.SelectUnique(selected,[exact with {Bounds=new Rectangle(0,0,1200,1920)}],out _) is null,"changed bounds accepted");
    Check(DxgiCaptureTarget.SelectUnique(selected,[exact with {Attached=false}],out _) is null,"detached output accepted");
    Check(DxgiCaptureTarget.SelectUnique(selected,[exact,exact with {AdapterIndex=4}],out _) is null,"ambiguous adapter accepted");
    Pass("DXGI selection requires unique attached VDD identity and exact native bounds; primary and guessed indices rejected");
}

if(args.Length==1)
{
    var path=Path.GetFullPath(args[0]);
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var output=Path.Combine(Path.GetTempPath(),"TabLink-H264-synthetic-"+Guid.NewGuid().ToString("N")+".h264");
    var timer=Stopwatch.StartNew();
    var count=0;long bytesOut=0;long previousPts=-1;
    await using(var encoder=H264Encoder.CreateSynthetic(1200,1920,90,path,frameCount:450))
    await using(var stream=File.Create(output))
    {
        encoder.Start();
        await foreach(var frame in encoder.ReadAccessUnitsAsync(deadline.Token))
        {
            count++;
            Check(frame.Sequence==count&&frame.PtsUs>previousPts,"sequence/PTS not strictly increasing");
            Check(frame.Sps.Length>5&&(frame.Sps[4]&0x1f)==7&&frame.Pps.Length>5&&(frame.Pps[4]&0x1f)==8,"invalid real codec config");
            previousPts=frame.PtsUs;bytesOut+=frame.Data.Length;
            await stream.WriteAsync(frame.Data,deadline.Token);
        }
        Check(count==450&&previousPts==449*1_000_000L/90,"encoded frame loss or wrong clock");
        await Task.WhenAll(encoder.DisposeAsync().AsTask(),encoder.DisposeAsync().AsTask());
    }
    timer.Stop();
    var report=new{frames=count,width=1200,height=1920,requestedFps=90,elapsedSeconds=timer.Elapsed.TotalSeconds,throughputFps=count/timer.Elapsed.TotalSeconds,bytes=bytesOut,output};
    Pass("real NVENC process + stdout parser delivered every 1200x1920@90 synthetic frame");
    Pass("concurrent repeated encoder disposal completed");
    Console.WriteLine(JsonSerializer.Serialize(report));

    // Kill only the test owner, deliberately NOT its process tree. The owned
    // JobObject must collect the exact FFmpeg child after an abrupt host exit.
    var ownerStart = new ProcessStartInfo(Environment.ProcessPath!)
    { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
    ownerStart.ArgumentList.Add("--hold-owned-encoder");
    ownerStart.ArgumentList.Add(path);
    using var owner = Process.Start(ownerStart) ?? throw new IOException("cannot start crash test owner");
    Process? ffmpegChild = null;
    try
    {
        var childPid = await owner.StandardOutput.ReadLineAsync(deadline.Token);
        Check(int.TryParse(childPid,out var pid),"crash test owner did not publish its child PID");
        ffmpegChild = Process.GetProcessById(pid);
        owner.Kill();
        await owner.WaitForExitAsync(deadline.Token);
        await ffmpegChild.WaitForExitAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Pass("abrupt owner exit closes its JobObject and terminates only its FFmpeg child");
    }
    finally
    {
        if (!owner.HasExited) owner.Kill();
        if (ffmpegChild is {HasExited:false}) ffmpegChild.Kill();
        ffmpegChild?.Dispose();
    }
}
Console.WriteLine(string.Join(Environment.NewLine,results));
