using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using TabLink.Windows;

using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));
var ct=deadline.Token;
static void Check(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
static int FreePort(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
static byte[] Profile()=>JsonSerializer.SerializeToUtf8Bytes(new{width=1200,height=1920,rotation=0,activeModeId=2,refreshRate=90,nativeWidth=1200,nativeHeight=1920,
    supportedModes=new[]{new{width=1200,height=1920,refreshRate=90,modeId=2}}});
static byte[] HarmonyProfile()=>JsonSerializer.SerializeToUtf8Bytes(new{width=1200,height=1920,rotation=0,activeModeId=2,refreshRate=90,nativeWidth=1200,nativeHeight=1920,
    supportedModes=new[]{new{width=1200,height=1920,refreshRate=90,modeId=2}},clientPlatform="harmony",progressEvidence="render-submitted"});
static Task Hello(Stream stream,string token,CancellationToken ct,string[]? features=null)=>FrameServer.WritePacketAsync(stream,0x10,
    features is null?JsonSerializer.SerializeToUtf8Bytes(new{protocol=1,token}):JsonSerializer.SerializeToUtf8Bytes(new{protocol=1,token,features}),ct);
static async Task WaitFor(Func<bool> condition,CancellationToken ct){while(!condition())await Task.Delay(10,ct);}
static async Task Closed(Stream stream,CancellationToken ct)
{
    try {while(await stream.ReadAsync(new byte[4096],ct)!=0){}}
    catch(Exception ex) when(ex is IOException or SocketException or ObjectDisposedException){}
}
static async Task<(TcpClient Client,SslStream Stream)> Connect(NetworkSessionOptions options,string fingerprint,CancellationToken ct)
{
    var client=new TcpClient();
    try
    {
        await client.ConnectAsync(IPAddress.Loopback,options.Port,ct);
        var stream=new SslStream(client.GetStream(),false,(_,cert,_,_)=>cert is not null&&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()),Convert.FromHexString(fingerprint)));
        try
        {
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost="127.0.0.1",EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,
                CertificateRevocationCheckMode=System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck},ct);
            return(client,stream);
        }
        catch {stream.Dispose();throw;}
    }
    catch {client.Dispose();throw;}
}
static async Task ReadUntil(Stream stream,byte type,CancellationToken ct)
{while((await FrameServer.ReadPacketAsync(stream,8192,ct)).Type!=type){}}
static byte[] RefreshConfiguration()=>JsonSerializer.SerializeToUtf8Bytes(new
{
    codec="video/avc",width=1200,height=1920,fps=90,
    csd0=Convert.ToBase64String(new byte[]{0,0,0,1,0x67,0x42,0,0x1f}),
    csd1=Convert.ToBase64String(new byte[]{0,0,0,1,0x68,0xab,0xcd})
});
static byte[] AccessUnit(long pts,byte type,bool includeSps=false,bool includePps=false)
{
    var bytes=new List<byte>();
    if(includeSps)bytes.AddRange(new byte[]{0,0,0,1,0x67,0x42,0,0x1f});
    if(includePps)bytes.AddRange(new byte[]{0,0,0,1,0x68,0xab,0xcd});
    bytes.AddRange(new byte[]{0,0,0,1,type,0x55});
    var payload=new byte[8+bytes.Count];
    BinaryPrimitives.WriteInt64BigEndian(payload,pts);bytes.CopyTo(payload,8);
    return payload;
}
static byte[] Generation(long value){var bytes=new byte[8];BinaryPrimitives.WriteInt64BigEndian(bytes,value);return bytes;}
static byte[] Feedback(long sequence=1,long decoderEpoch=1,long recoveryEpoch=0,long receivedVideoFrames=0,
    long receivedVideoBytes=0,long submittedFrames=0,long presentedFrames=0,int queueDepth=0,int queueCapacity=12,
    int queueHighWaterMark=0,long inputDroppedFrames=0,long backpressureTimeouts=0,
    long overflowEvents=0,long overflowDrops=0,long expiredDrops=0,long awaitingKeyFrameDrops=0,
    long renderDrops=0,long decoderFallbacks=0,bool awaitingKeyFrame=false)=>JsonSerializer.SerializeToUtf8Bytes(new
    {
        kind="receiver-feedback",sequence,decoderEpoch,recoveryEpoch,receivedVideoFrames,receivedVideoBytes,
        submittedFrames,presentedFrames,queueDepth,queueCapacity,queueHighWaterMark,inputDroppedFrames,
        backpressureTimeouts,overflowEvents,overflowDrops,expiredDrops,
        awaitingKeyFrameDrops,renderDrops,decoderFallbacks,awaitingKeyFrame
    });
static string[] CheckNegotiationStatus((byte Type,byte[] Payload) packet,string reason)
{
    Check(packet.Type==0x02,reason+": negotiation status did not precede media");
    using var json=JsonDocument.Parse(packet.Payload);
    Check(json.RootElement.GetProperty("protocol").GetInt32()==1,reason+": protocol mismatch");
    return json.RootElement.GetProperty("features").EnumerateArray().Select(value=>value.GetString()??"").ToArray();
}

foreach(var address in new[]{IPAddress.Any,IPAddress.IPv6Loopback,IPAddress.Broadcast,IPAddress.Parse("239.1.2.3"),IPAddress.Parse("192.0.2.123")})
{
    try {using var invalid=new NetworkSessionOptions(address);throw new Exception("unsafe/nonlocal bind accepted");}
    catch(ArgumentException){}
}
Console.WriteLine("PASS explicit local IPv4 required; wildcard, broadcast, multicast, IPv6 and nonlocal addresses rejected");

// Diagnostic snapshots must remain cumulative and privately owned while a
// connection runs, and must never carry its measurements into another one.
{
    var stats=new FrameSendPerformance();stats.Reset(true);
    long T(double ms)=>(long)(ms*Stopwatch.Frequency/1000);
    var start=Stopwatch.GetTimestamp();
    stats.RecordSourceMove(T(25));stats.RecordSourceMove(T(1));
    stats.RecordWrite(start,start+T(20),16,true);
    stats.RecordWrite(start+T(20),start+T(21),32,true);
    stats.RecordWrite(start+T(21),start+T(61),64,true);
    var snapshot=stats.Snapshot();
    Check(snapshot.CompletedFrames==3&&snapshot.PayloadBytesWritten==112&&snapshot.BurstFrameGaps==1&&
        snapshot.SourceMove.Count==2&&snapshot.SourceMove.SlowCount==1&&snapshot.PacketWrite.Count==3&&
        snapshot.PacketWrite.SlowCount==2&&snapshot.FrameSendGap.Count==2&&snapshot.FrameSendGap.SlowCount==1&&
        Math.Abs(snapshot.FrameSendGap.MaxMs-40)<0.1&&snapshot.PacketWrite.Histogram.Sum()==3,
        "cumulative pipeline metrics failed to distinguish source stalls, send stalls and bursts");
    snapshot.PacketWrite.Histogram[0]=long.MaxValue;
    Check(stats.Snapshot().PacketWrite.Histogram.Sum()==3,"snapshot exposed mutable internal histogram");
    var oldId=snapshot.ConnectionId;stats.Reset(false);var empty=stats.Snapshot();
    Check(empty.ConnectionId==Guid.Empty&&empty.StartedUtc is null&&empty.ElapsedMs==0&&empty.CompletedFrames==0&&
        empty.SourceMove.Count==0&&empty.PacketWrite.Count==0&&empty.FrameSendGap.Count==0,
        "disconnected pipeline metrics retained connection evidence");
    stats.Reset(true);Check(stats.Snapshot().ConnectionId!=oldId,"new connection reused metric identity");
}
Console.WriteLine("PASS bounded cumulative pipeline snapshots distinguish source/send stalls and bursts, copy histograms, and reset connection identity");

// The decoder refresh gate is private to one authenticated connection. It
// coalesces an outstanding request, suppresses only dependent frames and uses
// a small token bucket once each fresh-IDR request has completed.
{
    long clock=0;
    var first=new DecoderRefreshGate(()=>clock,1000);
    var second=new DecoderRefreshGate(()=>clock,1000);
    var config=new VideoPacket(0x20,RefreshConfiguration(),false);
    Check(first.TryPrepare(config,out var firstConfig)&&ReferenceEquals(firstConfig,config),"refresh gate changed the ordinary codec configuration");
    Check(second.TryPrepare(config,out _),"second refresh gate rejected codec configuration");
    Check(first.Request(1)==DecoderRefreshRequestResult.Accepted&&first.IsPending,"first decoder generation was not accepted");
    Check(first.Request(1)==DecoderRefreshRequestResult.Duplicate,"duplicate decoder generation was not idempotent");
    var predicted=new VideoPacket(0x21,AccessUnit(1,0x41),true);
    Check(!first.TryPrepare(predicted,out _),"pending refresh leaked a dependent frame");
    Check(second.TryPrepare(predicted,out var independent)&&independent.Payload.SequenceEqual(predicted.Payload),"one connection gated another connection's frame");
    var idr=new VideoPacket(0x21,AccessUnit(2,0x65),true);
    Check(first.TryPrepare(idr,out var refreshed)&&!first.IsPending&&first.CompletedGeneration==1,
        "fresh IDR did not complete the requested generation");
    Check(DecoderRefreshGate.ContainsNal(refreshed.Payload.AsSpan(8),7)&&
        DecoderRefreshGate.ContainsNal(refreshed.Payload.AsSpan(8),8)&&
        DecoderRefreshGate.ContainsNal(refreshed.Payload.AsSpan(8),5),"fresh IDR did not carry SPS, PPS and IDR together");
    Check(first.Request(2)==DecoderRefreshRequestResult.Accepted,"bounded burst did not admit a second decoder generation");
    Check(first.Request(3)==DecoderRefreshRequestResult.Coalesced,"newer outstanding generation was not coalesced");
    Check(first.TryPrepare(idr with {Payload=AccessUnit(3,0x65,true,true)},out var alreadyComplete)&&
        alreadyComplete.Payload.Length==AccessUnit(3,0x65,true,true).Length&&first.CompletedGeneration==3,
        "existing SPS/PPS were duplicated or coalesced generation was not completed");
    Check(first.Request(4)==DecoderRefreshRequestResult.RateLimited,"decoder refresh token bucket did not limit a third immediate request");
    Check(!first.IsPending&&first.CompletedGeneration==3,"rate-limited request altered completed or pending refresh state");
    clock+=1000;
    Check(first.Request(4)==DecoderRefreshRequestResult.Accepted,"decoder refresh token did not refill after one second");
}
Console.WriteLine("PASS decoder refresh gate is connection-local, idempotent, coalesced and rate-limited; only a fresh IDR carries missing SPS/PPS");

var options=new NetworkSessionOptions(IPAddress.Loopback,FreePort(),TimeSpan.FromMilliseconds(900),TimeSpan.FromSeconds(3));
var prepared=0;var videos=0;var inputs=0;var releases=0;var profiles=0;
var allowPrepare=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var logs=new ConcurrentQueue<string>();
Guid measuredConnectionId=Guid.Empty;
async IAsyncEnumerable<VideoPacket> Video([EnumeratorCancellation]CancellationToken token)
{
    Interlocked.Increment(ref videos);
    yield return new(0x20,[1],false);
    yield return new(0x21,[0,0,0,0,0,0,0,0,0,0,0,1,0x65],true);
    await Task.Delay(Timeout.Infinite,token);
}
await using(var server=new FrameServer(options,async(profile,token)=>
{
    Check(profile.Width==1200&&profile.RequestedRefreshRate==90,"prepare received wrong profile");
    Interlocked.Increment(ref prepared);await allowPrepare.Task.WaitAsync(token);
},Video,_=>Interlocked.Increment(ref inputs),()=>Interlocked.Increment(ref releases),()=>new(InputDesktopState.Available,"test")))
{
    server.Status+=logs.Enqueue;
    server.DisplayProfileChanged+=_=>Interlocked.Increment(ref profiles);
    var connectionUri=server.NetworkConnectionUri;
    var uri=new Uri(connectionUri!);
    var query=uri.Query[1..].Split('&').Select(item=>item.Split('=',2)).ToDictionary(pair=>pair[0],pair=>pair[1]);
    Check(uri.Scheme=="tablink"&&uri.Host=="connect"&&query["host"]=="127.0.0.1"&&query["port"]==options.Port.ToString()&&query["token"]==server.Token&&
        query["cert"]==Convert.ToHexString(SHA256.HashData(options.Certificate.RawData)).ToLowerInvariant()&&query["cert"].Length==64,"pin URI mismatch");
    Check(options.ConnectionUri(server.Token)==server.NetworkConnectionUri,"URI APIs disagree");
    using(var other=new NetworkSessionOptions(IPAddress.Loopback,FreePort()))Check(other.CertificateFingerprint!=options.CertificateFingerprint,"certificate reused across sessions");
    server.Start();
    try {var wrong=await Connect(options,new string('0',64),ct);wrong.Stream.Dispose();wrong.Client.Dispose();throw new Exception("wrong pin accepted");}
    catch(AuthenticationException){}
    Console.WriteLine("PASS session URI contains exact DER SHA-256 pin; per-session ephemeral certificate; wrong pin rejected");

    using(var idle=new TcpClient())
    {
        await idle.ConnectAsync(IPAddress.Loopback,options.Port,ct);var clock=Stopwatch.StartNew();
        await Closed(idle.GetStream(),ct);Check(clock.Elapsed<TimeSpan.FromSeconds(3),"idle TLS not bounded");
    }
    using(var plain=new TcpClient())
    {
        await plain.ConnectAsync(IPAddress.Loopback,options.Port,ct);await Hello(plain.GetStream(),server.Token,ct);await Closed(plain.GetStream(),ct);
    }
    Check(prepared==0&&videos==0&&inputs==0&&releases==0,"unauthenticated TLS altered host state");
    Console.WriteLine("PASS plaintext and idle TLS clients close without display preparation, capture, input or input release");

    foreach(var scenario in new[]{"wrong-token","missing-profile","invalid-profile","input-before-profile","ack-before-profile"})
    {
        var connection=await Connect(options,options.CertificateFingerprint,ct);
        using var client=connection.Client;using var stream=connection.Stream;
        await Hello(stream,scenario=="wrong-token"?new string('0',64):server.Token,ct);
        if(scenario=="invalid-profile")await FrameServer.WritePacketAsync(stream,0x13,"{}"u8.ToArray(),ct);
        if(scenario=="input-before-profile")await FrameServer.WritePacketAsync(stream,0x11,"{}"u8.ToArray(),ct);
        if(scenario=="ack-before-profile")await FrameServer.WritePacketAsync(stream,0x12,"{}"u8.ToArray(),ct);
        await Closed(stream,ct);
        Check(prepared==0&&videos==0&&inputs==0&&releases==0&&!server.ClientConnected&&!server.HasAuthenticatedClient&&server.LastPresentedUtc is null,"invalid client altered trusted state: "+scenario);
    }
    Console.WriteLine("PASS token and validated initial profile are required before prepare/capture/input/ACK; missing profile times out");

    var good=await Connect(options,options.CertificateFingerprint,ct);
    using(var client=good.Client)
    using(var stream=good.Stream)
    {
        Check(stream.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13,"unexpected TLS version");
        await Hello(stream,server.Token,ct);await FrameServer.WritePacketAsync(stream,0x13,Profile(),ct);
        var legacyStatus=await FrameServer.ReadPacketAsync(stream,8192,ct);
        Check(CheckNegotiationStatus(legacyStatus,"legacy HELLO").Length==0,"legacy HELLO negotiated an unsolicited feature");
        await WaitFor(()=>prepared==1,ct);
        Check(videos==0&&inputs==0&&!server.ClientConnected&&profiles==0,"media started before prepare completed");
        allowPrepare.SetResult();await ReadUntil(stream,0x20,ct);await ReadUntil(stream,0x21,ct);
        Check(server.ClientConnected&&server.HasAuthenticatedClient&&server.ClientDisplayProfile?.Width==1200&&profiles==0,"initial profile state/event incorrect");
        await WaitFor(()=>server.SendPerformance.CompletedFrames==1,ct);
        var performance=server.SendPerformance;measuredConnectionId=performance.ConnectionId;
        Check(measuredConnectionId!=Guid.Empty&&performance.SourceMove.Count==2&&performance.PacketWrite.Count==2&&
            performance.PayloadBytesWritten==14&&performance.FrameSendGap.Count==0,
            "real video loop did not separate source movement, successful video writes and frame gaps");
        await FrameServer.WritePacketAsync(stream,0x11,JsonSerializer.SerializeToUtf8Bytes(new{kind="move",x=0.2,y=0.2}),ct);
        await WaitFor(()=>inputs==1,ct);
        await FrameServer.WritePacketAsync(stream,0x12,JsonSerializer.SerializeToUtf8Bytes(new{kind="frame-presented",sequence=1,width=1200,height=1920,fps=59.5}),ct);
        await FrameServer.WritePacketAsync(stream,0x11,JsonSerializer.SerializeToUtf8Bytes(new{kind="move",x=0.5,y=0.5}),ct);
        await WaitFor(()=>inputs==2&&server.PresentedFrames==1,ct);
        Check(server.SubmittedFrames==0&&server.LastSubmittedUtc is null&&server.ClientSubmittedFps==0&&server.ClientPresentedFps==59.5,
            "legacy session invented submission telemetry or lost presentation telemetry");
        await FrameServer.WritePacketAsync(stream,0x13,Profile(),ct);await WaitFor(()=>profiles==1,ct);
        var unnegotiatedSubmission=JsonSerializer.SerializeToUtf8Bytes(new{evidence="render-submitted",frames=1,ptsUs=0,width=1200,height=1920,fps=60,decoder="test AVCodec"});
        await FrameServer.WritePacketAsync(stream,0x14,unnegotiatedSubmission,ct);
        await Closed(stream,ct);await WaitFor(()=>!server.ClientConnected,ct);
        Check(server.HasAuthenticatedClient,"trusted connection evidence remains sticky after disconnect for bounded host recovery");
        Check(server.LastSubmittedUtc is null&&server.SubmittedFrames==0,"unnegotiated 0x14 altered trusted submission state");
        Console.WriteLine("PASS legacy HELLO rejects and closes on unnegotiated 0x14 without accepting submission evidence");
    }
    Check(videos==1&&prepared==1&&releases>=1,"reconnect did not clean old session");
    Check(server.SendPerformance.ConnectionId==Guid.Empty&&server.SendPerformance.PacketWrite.Count==0,"disconnect kept old runtime performance evidence");
    var reconnected=await Connect(options,query["cert"],ct);
    using(var client=reconnected.Client)
    using(var stream=reconnected.Stream)
    {
        await Hello(stream,query["token"],ct,[FrameServer.RenderSubmittedFeature,"future-unknown-v9"]);
        await FrameServer.WritePacketAsync(stream,0x13,Profile(),ct);
        var featureStatus=await FrameServer.ReadPacketAsync(stream,8192,ct);
        Check(CheckNegotiationStatus(featureStatus,"feature HELLO").SequenceEqual([FrameServer.RenderSubmittedFeature]),
            "supported feature intersection omitted the known feature or echoed an unknown feature");
        await ReadUntil(stream,0x21,ct);
        Check(prepared==2&&videos==2&&server.PresentedFrames==0&&server.LastPresentedUtc is null&&server.SubmittedFrames==0&&server.LastSubmittedUtc is null&&server.NetworkConnectionUri==connectionUri,"reconnect reused ACK evidence or changed session capability");
        await WaitFor(()=>server.SendPerformance.CompletedFrames==1,ct);
        Check(server.SendPerformance.ConnectionId!=measuredConnectionId&&server.SendPerformance.PacketWrite.Count==2,
            "reconnect accumulated previous connection performance");
        await FrameServer.WritePacketAsync(stream,0x12,JsonSerializer.SerializeToUtf8Bytes(new{kind="frame-presented",sequence=1,width=1200,height=1920,fps=59.5}),ct);
        await WaitFor(()=>server.PresentedFrames==1,ct);
        Check(server.HasRecentPresentation&&server.ClientPresentedFps==59.5&&server.ClientReportedFps==59.5,
            "fresh presentation FPS was not projected");
        await Task.Delay(FrameServer.TelemetryFreshnessWindow+TimeSpan.FromMilliseconds(250),ct);
        Check(!server.HasRecentPresentation&&server.ClientPresentedFps==0&&server.ClientReportedFps==0&&server.PresentedFrames==1,
            "expired presentation FPS remained visible");

        var submitted=JsonSerializer.SerializeToUtf8Bytes(new{evidence="render-submitted",frames=1,ptsUs=0,width=1200,height=1920,fps=60,decoder="test AVCodec"});
        await FrameServer.WritePacketAsync(stream,0x14,submitted,ct);
        await WaitFor(()=>server.SubmittedFrames==1,ct);
        var submittedAt=server.LastSubmittedUtc;
        Check(submittedAt is not null&&server.PresentedFrames==1&&server.SubmissionEvidenceOnly&&server.HasRecentSubmission&&!server.HasRecentPresentation&&
            server.ClientSubmittedFps==60&&server.ClientPresentedFps==0&&server.ClientReportedFps==60,
            "fresh decode submission fabricated presentation or exposed stale presentation FPS");
        await FrameServer.WritePacketAsync(stream,0x14,submitted,ct);
        await FrameServer.WritePacketAsync(stream,0x11,JsonSerializer.SerializeToUtf8Bytes(new{kind="move",x=0.7,y=0.7}),ct);
        await WaitFor(()=>inputs==3,ct);
        Check(server.LastSubmittedUtc==submittedAt,"duplicate submission refreshed watchdog");
        await Task.Delay(FrameServer.TelemetryFreshnessWindow+TimeSpan.FromMilliseconds(250),ct);
        Check(!server.HasRecentSubmission&&!server.SubmissionEvidenceOnly&&server.ClientSubmittedFps==0&&server.ClientPresentedFps==0&&server.ClientReportedFps==0,
            "expired submission FPS remained visible");
        Console.WriteLine("PASS negotiated 0x14 remains separate from presentation; stale submitted/presented FPS projects as zero");
        var clock=Stopwatch.StartNew();await server.DisposeAsync();await Closed(stream,ct);
        Check(clock.Elapsed<TimeSpan.FromSeconds(2),"dispose failed to cancel blocked video");
    }
    Check(logs.All(message=>!message.Contains(server.Token)&&!message.Contains(options.CertificateFingerprint)),"status leaked pairing capability");
    Console.WriteLine("PASS legacy and feature HELLO negotiate protocol 1 before video; only render-submitted-v1 is echoed and unknown features are omitted");
    Console.WriteLine("PASS TLS profile/prepare/config/video/ACK/input ordering, profile change event, reconnect preserving listener/token/pin and resetting ACK evidence");
    Console.WriteLine("PASS disposal cancels a blocked encoder and closes its client; status logs contain no token/pin");
}

var refreshPackets=Channel.CreateUnbounded<VideoPacket>();
var refreshVideos=0;var refreshInputs=0;
async IAsyncEnumerable<VideoPacket> RefreshVideo([EnumeratorCancellation]CancellationToken token)
{
    Interlocked.Increment(ref refreshVideos);
    yield return new(0x20,RefreshConfiguration(),false);
    await foreach(var packet in refreshPackets.Reader.ReadAllAsync(token))yield return packet;
}
await using(var refreshServer=new FrameServer(RefreshVideo,_=>Interlocked.Increment(ref refreshInputs),()=>{},listenPort:0))
{
    refreshServer.Start();
    using(var client=new TcpClient())
    {
        await client.ConnectAsync(IPAddress.Loopback,refreshServer.ListeningPort,ct);
        var stream=client.GetStream();
        var endpoint=client.Client.LocalEndPoint;
        await Hello(stream,refreshServer.Token,ct,[FrameServer.RenderSubmittedFeature,FrameServer.DecoderRefreshFeature,
            FrameServer.ReceiverFeedbackFeature,FrameServer.AdaptiveVideoFeature,"unknown-refresh-v9"]);
        var status=await FrameServer.ReadPacketAsync(stream,8192,ct);
        Check(CheckNegotiationStatus(status,"decoder refresh HELLO").SequenceEqual(
            [FrameServer.RenderSubmittedFeature,FrameServer.DecoderRefreshFeature,
             FrameServer.ReceiverFeedbackFeature,FrameServer.AdaptiveVideoFeature]),
            "receiver feedback/adaptive video feature intersection or order changed");
        await FrameServer.WritePacketAsync(stream,0x13,Profile(),ct);
        var config=await FrameServer.ReadPacketAsync(stream,8192,ct);
        Check(config.Type==0x20,"initial decoder configuration missing");
        await refreshPackets.Writer.WriteAsync(new(0x21,AccessUnit(0,0x65,true,true),true),ct);
        var initial=await FrameServer.ReadPacketAsync(stream,FrameServer.MaxPacket,ct);
        Check(initial.Type==0x21,"initial IDR missing");
        await WaitFor(()=>refreshServer.FramesSent==1,ct);
        await FrameServer.WritePacketAsync(stream,0x16,Feedback(sequence:1,decoderEpoch:1,recoveryEpoch:0,
            receivedVideoFrames:1,receivedVideoBytes:initial.Payload.Length,queueDepth:1,
            queueHighWaterMark:2,inputDroppedFrames:3,backpressureTimeouts:1),ct);
        await WaitFor(()=>refreshServer.ReceiverFeedback?.Sequence==1,ct);
        var feedback=refreshServer.ReceiverFeedback;
        Check(feedback is {Sequence:1,DecoderEpoch:1,ReceivedVideoFrames:1,SubmittedFrames:0,PresentedFrames:0,
                  QueueDepth:1,QueueCapacity:12,QueueHighWaterMark:2,InputDroppedFrames:3,
                  BackpressureTimeouts:1}&&refreshServer.HasRecentReceiverFeedback&&
              refreshServer.PresentedFrames==0&&refreshServer.LastPresentedUtc is null&&
              refreshServer.SubmittedFrames==0&&refreshServer.LastSubmittedUtc is null,
            "legal receiver feedback was not retained separately from submitted/presented health");
        await FrameServer.WritePacketAsync(stream,0x12,JsonSerializer.SerializeToUtf8Bytes(new{kind="frame-presented",sequence=1,width=1200,height=1920}),ct);
        await WaitFor(()=>refreshServer.PresentedFrames==1,ct);
        var presentedAt=refreshServer.LastPresentedUtc;
        var connectionId=refreshServer.SendPerformance.ConnectionId;

        await FrameServer.WritePacketAsync(stream,0x15,Generation(1),ct);
        await FrameServer.WritePacketAsync(stream,0x11,JsonSerializer.SerializeToUtf8Bytes(new{kind="move",x=0.5,y=0.5}),ct);
        await WaitFor(()=>refreshInputs==1,ct); // Ordered receive-stream barrier after 0x15.
        Check(refreshServer.PresentedFrames==1&&refreshServer.LastPresentedUtc==presentedAt,
            "decoder refresh request fabricated presentation progress");
        await refreshPackets.Writer.WriteAsync(new(0x21,AccessUnit(1,0x41),true),ct);
        await refreshPackets.Writer.WriteAsync(new(0x21,AccessUnit(2,0x65),true),ct);
        var recovered=await FrameServer.ReadPacketAsync(stream,FrameServer.MaxPacket,ct);
        Check(recovered.Type==0x21&&BinaryPrimitives.ReadInt64BigEndian(recovered.Payload)==2,
            "refresh emitted a configuration or dependent frame before the fresh IDR");
        Check(DecoderRefreshGate.ContainsNal(recovered.Payload.AsSpan(8),7)&&
            DecoderRefreshGate.ContainsNal(recovered.Payload.AsSpan(8),8)&&
            DecoderRefreshGate.ContainsNal(recovered.Payload.AsSpan(8),5),"wire refresh did not combine SPS/PPS with the fresh IDR");
        await WaitFor(()=>refreshServer.FramesSent==2,ct);
        Check(refreshVideos==1&&refreshServer.SendPerformance.ConnectionId==connectionId&&client.Client.LocalEndPoint!.Equals(endpoint)&&
            refreshServer.PresentedFrames==1&&refreshServer.SubmittedFrames==0,
            "decoder refresh restarted media/TCP or fabricated client progress");
        await FrameServer.WritePacketAsync(stream,0x12,JsonSerializer.SerializeToUtf8Bytes(new{kind="frame-presented",sequence=2,width=1200,height=1920}),ct);
        await FrameServer.WritePacketAsync(stream,0x14,JsonSerializer.SerializeToUtf8Bytes(new{evidence="render-submitted",frames=2,ptsUs=2,width=1200,height=1920,fps=60,decoder="fallback-decoder"}),ct);
        await WaitFor(()=>refreshServer.PresentedFrames==2&&refreshServer.SubmittedFrames==2,ct);

        await FrameServer.WritePacketAsync(stream,0x15,Generation(1),ct);
        await FrameServer.WritePacketAsync(stream,0x11,JsonSerializer.SerializeToUtf8Bytes(new{kind="move",x=0.6,y=0.6}),ct);
        await WaitFor(()=>refreshInputs==2,ct);
        await refreshPackets.Writer.WriteAsync(new(0x21,AccessUnit(3,0x41),true),ct);
        var afterDuplicate=await FrameServer.ReadPacketAsync(stream,FrameServer.MaxPacket,ct);
        Check(afterDuplicate.Type==0x21&&BinaryPrimitives.ReadInt64BigEndian(afterDuplicate.Payload)==3,
            "duplicate generation reopened the keyframe gate");
        await WaitFor(()=>refreshServer.FramesSent==3,ct);
        await FrameServer.WritePacketAsync(stream,0x14,JsonSerializer.SerializeToUtf8Bytes(new{evidence="render-submitted",frames=3,ptsUs=1,width=1200,height=1920,fps=60,decoder="fabricated-skipped-p-frame"}),ct);
        await Closed(stream,ct);
    }
    await WaitFor(()=>!refreshServer.ClientConnected,ct);

    // Old clients remain compatible, but an unnegotiated new control message
    // fails closed instead of changing the media stream.
    using(var legacy=new TcpClient())
    {
        await legacy.ConnectAsync(IPAddress.Loopback,refreshServer.ListeningPort,ct);
        var stream=legacy.GetStream();await Hello(stream,refreshServer.Token,ct);
        Check(CheckNegotiationStatus(await FrameServer.ReadPacketAsync(stream,8192,ct),"legacy refresh HELLO").Length==0,
            "legacy peer received an unsolicited decoder feature");
        await ReadUntil(stream,0x20,ct);
        await FrameServer.WritePacketAsync(stream,0x15,Generation(1),ct);
        await Closed(stream,ct);
    }
    await WaitFor(()=>!refreshServer.ClientConnected,ct);

    using(var legacyFeedback=new TcpClient())
    {
        await legacyFeedback.ConnectAsync(IPAddress.Loopback,refreshServer.ListeningPort,ct);
        var stream=legacyFeedback.GetStream();await Hello(stream,refreshServer.Token,ct);
        Check(CheckNegotiationStatus(await FrameServer.ReadPacketAsync(stream,8192,ct),"legacy feedback HELLO").Length==0,
            "legacy peer received unsolicited feedback/adaptive features");
        await ReadUntil(stream,0x20,ct);
        await FrameServer.WritePacketAsync(stream,0x16,Feedback(),ct);
        await Closed(stream,ct);
    }
    await WaitFor(()=>!refreshServer.ClientConnected,ct);

    using(var structurallyInvalid=new TcpClient())
    {
        await structurallyInvalid.ConnectAsync(IPAddress.Loopback,refreshServer.ListeningPort,ct);
        var stream=structurallyInvalid.GetStream();
        await Hello(stream,refreshServer.Token,ct,[FrameServer.ReceiverFeedbackFeature,FrameServer.AdaptiveVideoFeature]);
        Check(CheckNegotiationStatus(await FrameServer.ReadPacketAsync(stream,8192,ct),"invalid feedback HELLO")
            .SequenceEqual([FrameServer.ReceiverFeedbackFeature,FrameServer.AdaptiveVideoFeature]),
            "feedback/adaptive-only feature negotiation changed");
        await ReadUntil(stream,0x20,ct);
        await FrameServer.WritePacketAsync(stream,0x16,Feedback(queueHighWaterMark:13,queueCapacity:12),ct);
        await Closed(stream,ct);
    }
    await WaitFor(()=>!refreshServer.ClientConnected,ct);

    using(var regressingFeedback=new TcpClient())
    {
        await regressingFeedback.ConnectAsync(IPAddress.Loopback,refreshServer.ListeningPort,ct);
        var stream=regressingFeedback.GetStream();
        await Hello(stream,refreshServer.Token,ct,[FrameServer.ReceiverFeedbackFeature,FrameServer.AdaptiveVideoFeature]);
        Check(CheckNegotiationStatus(await FrameServer.ReadPacketAsync(stream,8192,ct),"regressing feedback HELLO")
            .SequenceEqual([FrameServer.ReceiverFeedbackFeature,FrameServer.AdaptiveVideoFeature]),
            "regression test did not negotiate feedback/adaptive features");
        await ReadUntil(stream,0x20,ct);
        await FrameServer.WritePacketAsync(stream,0x16,Feedback(sequence:1,decoderEpoch:2,recoveryEpoch:1,
            queueHighWaterMark:6,inputDroppedFrames:5,backpressureTimeouts:3,
            overflowEvents:5,overflowDrops:5),ct);
        await WaitFor(()=>refreshServer.ReceiverFeedback?.Sequence==1,ct);
        await FrameServer.WritePacketAsync(stream,0x16,Feedback(sequence:2,decoderEpoch:2,recoveryEpoch:1,
            queueHighWaterMark:5,inputDroppedFrames:4,backpressureTimeouts:2,
            overflowEvents:5,overflowDrops:5),ct);
        await Closed(stream,ct);
    }
    await WaitFor(()=>!refreshServer.ClientConnected,ct);

    foreach(var invalidPayload in new[]{new byte[7],Generation(0),Generation(-1)})
    {
        using var invalid=new TcpClient();
        await invalid.ConnectAsync(IPAddress.Loopback,refreshServer.ListeningPort,ct);
        var stream=invalid.GetStream();await Hello(stream,refreshServer.Token,ct,[FrameServer.DecoderRefreshFeature]);
        Check(CheckNegotiationStatus(await FrameServer.ReadPacketAsync(stream,8192,ct),"invalid refresh HELLO")
            .SequenceEqual([FrameServer.DecoderRefreshFeature]),"decoder-only feature was not negotiated");
        await ReadUntil(stream,0x20,ct);
        await FrameServer.WritePacketAsync(stream,0x15,invalidPayload,ct);
        await Closed(stream,ct);
        await WaitFor(()=>!refreshServer.ClientConnected,ct);
    }
}
Console.WriteLine("PASS negotiated 0x15 keeps one authenticated TCP/media source, suppresses P frames and sends SPS/PPS+fresh IDR without synthetic ACK evidence");
Console.WriteLine("PASS skipped P frames do not enter frame counters or recent-PTS evidence");
Console.WriteLine("PASS legacy clients negotiate no decoder feature; unnegotiated, non-8-byte, zero and negative decoder refresh requests fail closed");
Console.WriteLine("PASS receiver-feedback-v1 and adaptive-video-v1 negotiate explicitly; legal 0x16 is retained without advancing presentation health");
Console.WriteLine("PASS unnegotiated, structurally invalid and regressing queue/drop/backpressure 0x16 feedback fail closed");

var harmonyOptions=new NetworkSessionOptions(IPAddress.Loopback,FreePort());
async IAsyncEnumerable<VideoPacket> HarmonyVideo([EnumeratorCancellation]CancellationToken token)
{
    yield return new(0x20,[1],false);
    yield return new(0x21,[0,0,0,0,0,0,0,0,0,0,0,1,0x65],true);
    await Task.Delay(Timeout.Infinite,token);
}
await using(var harmonyServer=new FrameServer(harmonyOptions,(_,_)=>Task.CompletedTask,HarmonyVideo,null,()=>{}))
{
    harmonyServer.Start();var connection=await Connect(harmonyOptions,harmonyOptions.CertificateFingerprint,ct);
    using var client=connection.Client;using var stream=connection.Stream;
    await Hello(stream,harmonyServer.Token,ct,[FrameServer.RenderSubmittedFeature]);await FrameServer.WritePacketAsync(stream,0x13,HarmonyProfile(),ct);
    var status=await FrameServer.ReadPacketAsync(stream,8192,ct);
    Check(CheckNegotiationStatus(status,"Harmony source profile").SequenceEqual([FrameServer.RenderSubmittedFeature]),"Harmony feature was not negotiated");
    await ReadUntil(stream,0x21,ct);await WaitFor(()=>harmonyServer.SendPerformance.CompletedFrames==1,ct);
    await FrameServer.WritePacketAsync(stream,0x14,JsonSerializer.SerializeToUtf8Bytes(new{evidence="render-submitted",frames=1,ptsUs=0,width=1200,height=1920,fps=60,decoder="Harmony AVCodec"}),ct);
    await WaitFor(()=>harmonyServer.SubmittedFrames==1,ct);
    Check(harmonyServer.PresentedFrames==0&&harmonyServer.LastPresentedUtc is null&&harmonyServer.HasRecentSubmission,
        "negotiated Harmony submission fabricated physical presentation evidence");
}
Console.WriteLine("PASS Harmony source negotiates 0x14 without fabricating presentation");

var timedOptions=new NetworkSessionOptions(IPAddress.Loopback,FreePort(),TimeSpan.FromSeconds(2),TimeSpan.FromMilliseconds(250));
var cancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var forbiddenVideo=0;
async IAsyncEnumerable<VideoPacket> Forbidden([EnumeratorCancellation]CancellationToken token)
{Interlocked.Increment(ref forbiddenVideo);await Task.Yield();yield return new(0x21,[1],true);}
await using(var timed=new FrameServer(timedOptions,async(_,token)=>
{try{await Task.Delay(Timeout.Infinite,token);}finally{cancelled.TrySetResult();}},Forbidden,null,()=>{},()=>new(InputDesktopState.Available,"test")))
{
    timed.Start();var connection=await Connect(timedOptions,timedOptions.CertificateFingerprint,ct);
    using var client=connection.Client;using var stream=connection.Stream;
    await Hello(stream,timed.Token,ct);await FrameServer.WritePacketAsync(stream,0x13,Profile(),ct);
    var clock=Stopwatch.StartNew();await Closed(stream,ct);await cancelled.Task.WaitAsync(ct);
    Check(clock.Elapsed<TimeSpan.FromSeconds(2)&&forbiddenVideo==0&&!timed.ClientConnected,"prepare timeout did not cancel or started video");
    using var idle=new TcpClient();await idle.ConnectAsync(IPAddress.Loopback,timedOptions.Port,ct);
    await timed.DisposeAsync();await Closed(idle.GetStream(),ct);
}
Console.WriteLine("PASS bounded preparation cancels its callback without capture; dispose also closes unauthenticated idle TLS");

var failureOptions=new NetworkSessionOptions(IPAddress.Loopback,FreePort());
await using(var failed=new FrameServer(failureOptions,(_,_)=>throw new IOException("private-exception-details"),Forbidden,null,()=>{}))
{
    failed.Start();var connection=await Connect(failureOptions,failureOptions.CertificateFingerprint,ct);
    using var client=connection.Client;using var stream=connection.Stream;
    await Hello(stream,failed.Token,ct);await FrameServer.WritePacketAsync(stream,0x13,Profile(),ct);
    (byte Type,byte[] Payload) packet;
    do {packet=await FrameServer.ReadPacketAsync(stream,8192,ct);}while(packet.Type==0x02);
    Check(packet.Type==0x03&&!System.Text.Encoding.UTF8.GetString(packet.Payload).Contains("private-exception-details")&&forbiddenVideo==0,"prepare failure leaked details or captured");
    await Closed(stream,ct);
}
Console.WriteLine("PASS permanent prepare failure returns sanitized 0x03 only after authenticated valid profile; no capture");

// Two simultaneous authenticated receivers own independent TLS and media state.
var multiA=new NetworkSessionOptions(IPAddress.Loopback,FreePort());
var multiB=new NetworkSessionOptions(IPAddress.Loopback,FreePort());
await using(var a=new FrameServer(multiA,(_,_)=>Task.CompletedTask,Video,null,()=>{}))
await using(var b=new FrameServer(multiB,(_,_)=>Task.CompletedTask,Video,null,()=>{}))
{
    a.Start();b.Start();var ca=await Connect(multiA,multiA.CertificateFingerprint,ct);var cb=await Connect(multiB,multiB.CertificateFingerprint,ct);
    using var clientA=ca.Client;using var sa=ca.Stream;using var clientB=cb.Client;using var sb=cb.Stream;
    await Hello(sa,a.Token,ct);await Hello(sb,b.Token,ct,[FrameServer.RenderSubmittedFeature]);
    await FrameServer.WritePacketAsync(sa,0x13,Profile(),ct);await FrameServer.WritePacketAsync(sb,0x13,Profile(),ct);
    await ReadUntil(sa,0x21,ct);await ReadUntil(sb,0x21,ct);
    await a.DisposeAsync();await Closed(sa,ct);
    Check(b.ClientConnected&&b.LastPresentedUtc is null,"stopping one device stopped or changed another");
    await FrameServer.WritePacketAsync(sb,0x12,JsonSerializer.SerializeToUtf8Bytes(new{kind="frame-presented",sequence=1,width=1200,height=1920}),ct);
    await WaitFor(()=>b.PresentedFrames==1,ct);
    // A fabricated PTS must not pass as submitted-frame progress.
    await FrameServer.WritePacketAsync(sb,0x14,JsonSerializer.SerializeToUtf8Bytes(new{evidence="render-submitted",frames=1,ptsUs=999,width=1200,height=1920}),ct);
    await Closed(sb,ct);Check(b.LastSubmittedUtc is null,"untransmitted PTS accepted");
}
Console.WriteLine("PASS simultaneous native sessions remain isolated; unknown PTS submission closes only its receiver");

for(var iteration=0;iteration<64;iteration++)
{
    IPEndPoint? wakeEndpoint=null;
    async Task ConnectWake(IPEndPoint endpoint,CancellationToken token)
    {
        wakeEndpoint=new IPEndPoint(endpoint.Address,endpoint.Port);
        using var wake=new TcpClient(endpoint.AddressFamily);
        await wake.ConnectAsync(endpoint.Address,endpoint.Port,token);
    }
    await using var ephemeral=new FrameServer(()=>[1],null,()=>{},listenPort:0,wakeListener:ConnectWake);
    ephemeral.Start();
    var ephemeralPort=ephemeral.ListeningPort;
    Check(ephemeralPort>0,"listenPort:0 did not publish its exact temporary port");
    await ephemeral.FirstAcceptStarted.WaitAsync(TimeSpan.FromSeconds(2),ct);
    Check(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
        .Any(x=>x.Address.Equals(IPAddress.Loopback)&&x.Port==ephemeralPort),
        "temporary listener was not active before disposal");
    await ephemeral.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2),ct);
    Check(wakeEndpoint is not null&&wakeEndpoint.Address.Equals(IPAddress.Loopback)&&wakeEndpoint.Port==ephemeralPort,
        "disposal did not wake the exact bound temporary endpoint");
    Check(!System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
        .Any(x=>x.Address.Equals(IPAddress.Loopback)&&x.Port==ephemeralPort),
        "temporary listener remained active after disposal");
}
Console.WriteLine("PASS 64 blocked ephemeral accepts wake their exact bound port and leave no listener");

for(var iteration=0;iteration<60;iteration++)
{
    var raceOptions=new NetworkSessionOptions(IPAddress.Loopback,FreePort());
    await using var race=new FrameServer(raceOptions,(_,_)=>Task.CompletedTask,Forbidden,null,()=>{});
    race.Start();using var idle=new TcpClient();await idle.ConnectAsync(IPAddress.Loopback,raceOptions.Port,ct);
    if(iteration%2==0)await Task.Delay(5,ct);
    var dispose=race.DisposeAsync().AsTask();
    await dispose.WaitAsync(TimeSpan.FromSeconds(2),ct);
    using var closeDeadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
    closeDeadline.CancelAfter(TimeSpan.FromSeconds(2));
    try {await Closed(idle.GetStream(),closeDeadline.Token);}
    catch(OperationCanceledException)
    {
        Console.WriteLine($"TRACE race iteration={iteration}, port={raceOptions.Port}");
        foreach(var state in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
            .Where(x=>x.LocalEndPoint.Port==raceOptions.Port||x.RemoteEndPoint.Port==raceOptions.Port))Console.WriteLine($"TRACE {state.LocalEndPoint} -> {state.RemoteEndPoint} {state.State}");
        throw;
    }
}
Console.WriteLine("PASS 60 accept/authentication/dispose races terminate within 2s without retaining listener or client");
