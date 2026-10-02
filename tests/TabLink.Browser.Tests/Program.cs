using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using TabLink.Core;
using TabLink.Windows;

if(args.Length is 2 or 5&&args[0]=="--chromium-smoke")
{await ChromiumSmoke.Run(args[1],args.Length==5?int.Parse(args[2]):1280,args.Length==5?int.Parse(args[3]):720,args.Length==5?int.Parse(args[4]):30);return;}

var results=new List<string>();
void Check(bool value,string message){if(!value)throw new Exception(message);var line="PASS "+message;results.Add(line);Console.WriteLine(line);}
var missingAdb=WindowsUiText.TranslateRuntime("尚未配置 Android 平台工具，请先选择 adb.exe。",ProductLanguage.English);
var exclusionSaved=WindowsUiText.TranslateRuntime("排除规则已添加并保存。",ProductLanguage.English);
Check(missingAdb.Contains("Platform-Tools",StringComparison.Ordinal)&&
    exclusionSaved.Contains("exclusion rule",StringComparison.OrdinalIgnoreCase)&&missingAdb!=exclusionSaved&&
    !WindowsUiText.ContainsHan(missingAdb)&&!WindowsUiText.ContainsHan(exclusionSaved),
    "Distinct owned log events retain meaningful English translations");
var prefixedFailure=WindowsUiText.TranslateRuntime(
    "Network-display preparation failed: ADB 错误：AdbExecutionException。",ProductLanguage.English);
var nativeFailure=WindowsUiText.TranslateNativeSessionLog(
    "设备 27186 副屏准备失败：内部失败。",ProductLanguage.English);
Check(prefixedFailure.StartsWith("Network-display preparation failed:",StringComparison.Ordinal)&&
    nativeFailure.StartsWith("Device 27186 display preparation failed:",StringComparison.Ordinal)&&
    !WindowsUiText.ContainsHan(prefixedFailure)&&!WindowsUiText.ContainsHan(nativeFailure),
    "English diagnostic prefixes survive redacted Chinese error suffixes");
var encoderEvent=WindowsUiText.TranslateRuntime("画质计划已更新：12.5 Mbps；保持同一连接与唯一副屏，重建本次编码器。",ProductLanguage.English);
var updaterEvent=WindowsUiText.TranslateRuntime("正式版 0.9.0 已准备完成；TabLink 退出后将自动安装并验证启动。",ProductLanguage.English);
Check(encoderEvent.Contains("12.5 Mbps",StringComparison.Ordinal)&&encoderEvent.Contains("encoder",StringComparison.OrdinalIgnoreCase)&&
    updaterEvent.Contains("0.9.0",StringComparison.Ordinal)&&updaterEvent.Contains("Stable release",StringComparison.Ordinal)&&
    encoderEvent!=updaterEvent&&!WindowsUiText.ContainsHan(encoderEvent)&&!WindowsUiText.ContainsHan(updaterEvent),
    "Dynamic encoder and updater logs retain distinct English meaning and values");
var sentHealth=WindowsUiText.TranslateHealthDetail("本次连接已发送 1,234 帧",ProductLanguage.English);
var presentedHealth=WindowsUiText.TranslateHealthDetail("收到 Surface 呈现回调 987 帧",ProductLanguage.English);
var pausedHealth=WindowsUiText.TranslateHealthDetail("安全桌面或采集恢复期间保留连接",ProductLanguage.English);
Check(sentHealth.Contains("1,234",StringComparison.Ordinal)&&sentHealth.Contains("sent",StringComparison.OrdinalIgnoreCase)&&
    presentedHealth.Contains("987",StringComparison.Ordinal)&&presentedHealth.Contains("Surface",StringComparison.Ordinal)&&
    pausedHealth.Contains("connection is retained",StringComparison.OrdinalIgnoreCase)&&
    sentHealth!=presentedHealth&&presentedHealth!=pausedHealth&&
    !WindowsUiText.ContainsHan(sentHealth)&&!WindowsUiText.ContainsHan(presentedHealth)&&!WindowsUiText.ContainsHan(pausedHealth),
    "Health evidence keeps distinct English stages and numeric values");
var interrupted=WindowsUiText.TranslateRuntime("连接中断，等待平板重连：内部网络失败",ProductLanguage.English);
var encryptedEnded=WindowsUiText.TranslateRuntime("加密连接已结束，等待平板重新连接（IOException, 0x80004005）。",ProductLanguage.English);
Check(interrupted.Contains("waiting for the tablet to reconnect",StringComparison.OrdinalIgnoreCase)&&
    encryptedEnded.Contains("waiting for the tablet to reconnect",StringComparison.OrdinalIgnoreCase)&&
    encryptedEnded.Contains("IOException",StringComparison.Ordinal)&&encryptedEnded.Contains("0x80004005",StringComparison.Ordinal)&&
    !WindowsUiText.ContainsHan(interrupted)&&!WindowsUiText.ContainsHan(encryptedEnded),
    "Primary connection status keeps safe reconnect meaning and error codes in English");
var selectedEncoder=WindowsUiText.TranslateRuntime("已选择 NVIDIA NVENC：1920 × 1200 @ 60 fps（驱动降级原因）。 本次连接内的画质与捕获恢复继续使用此后端。",ProductLanguage.English);
var selectedSoftware=WindowsUiText.TranslateRuntime("已选择 软件 x264：1920 × 1200 @ 30 fps（软件兼容模式最高 30 fps）。 本次连接内的画质与捕获恢复继续使用此后端。",ProductLanguage.English);
var encoderBackend=WindowsUiText.TranslateRuntime("H.264 捕获后端：ddagrab；编码器：h264_nvenc；回退原因",ProductLanguage.English);
var encoderPriority=WindowsUiText.TranslateRuntime("软件编码器优先级无法降低：访问被拒绝",ProductLanguage.English);
var softwareLimit=WindowsUiText.TranslateEncoderDowngradeReason("设备请求 90 fps，软件兼容模式最高 30 fps",ProductLanguage.English);
var pathLimit=WindowsUiText.TranslateEncoderDowngradeReason("设备请求 90 fps，当前编码路径安全上限为 60 fps",ProductLanguage.English);
Check(selectedEncoder.Contains("NVIDIA NVENC",StringComparison.Ordinal)&&selectedEncoder.Contains("1920 × 1200 @ 60 fps",StringComparison.Ordinal)&&
    selectedSoftware.Contains("Software x264",StringComparison.Ordinal)&&selectedSoftware.Contains("30 fps",StringComparison.Ordinal)&&
    encoderBackend.Contains("ddagrab",StringComparison.Ordinal)&&encoderBackend.Contains("h264_nvenc",StringComparison.Ordinal)&&
    encoderPriority.Contains("priority",StringComparison.OrdinalIgnoreCase)&&
    softwareLimit.Contains("90 fps",StringComparison.Ordinal)&&softwareLimit.Contains("30 fps",StringComparison.Ordinal)&&
    pathLimit.Contains("90 fps",StringComparison.Ordinal)&&pathLimit.Contains("60 fps",StringComparison.Ordinal)&&
    WindowsUiText.TranslateEncoderBackendName("软件 x264",ProductLanguage.English)=="Software x264"&&
    !WindowsUiText.ContainsHan(selectedEncoder)&&!WindowsUiText.ContainsHan(selectedSoftware)&&
    !WindowsUiText.ContainsHan(encoderBackend)&&!WindowsUiText.ContainsHan(encoderPriority)&&
    !WindowsUiText.ContainsHan(softwareLimit)&&!WindowsUiText.ContainsHan(pathLimit),
    "Encoder status templates preserve backend and frame-rate evidence in English");
async Task Eventually(Func<bool> value,string description,int ms=6000)
{var until=DateTime.UtcNow.AddMilliseconds(ms);while(!value()&&DateTime.UtcNow<until)await Task.Delay(25);Check(value(),description);}
var storage=Path.Combine(Path.GetTempPath(),"TabLink-Browser-Test-"+Guid.NewGuid().ToString("N"));
using(var ca=new BrowserCertificateAuthority(storage))
{
    using var publicCa=X509CertificateLoader.LoadCertificateFromFile(ca.PublicCertificatePath);
    Check(!publicCa.HasPrivateKey,"Exported CA contains no private key");
    using var leaf=ca.CreateServerCertificate(IPAddress.Loopback);
    Check(TestClient.Trusted(leaf,publicCa,SslPolicyErrors.RemoteCertificateChainErrors),"Leaf validates against exact install CA");
    using var again=new BrowserCertificateAuthority(storage);
    Check(again.Fingerprint==ca.Fingerprint,"CA persists across host restarts");
    var protectedBytes=File.ReadAllBytes(Path.Combine(storage,"ca.dpapi"));
    Check(protectedBytes.Length>0&&protectedBytes[0]!=0x30,"Persisted CA private material is DPAPI, not PFX");
}
int prepared=0,disposed=0,inputs=0,presentationEvents=0;
bool pause=false;
TaskCompletionSource? prepareEntered=null,allowPrepare=null;
var host=new BrowserHost(IPAddress.Loopback,async(id,profile,ct)=>
{
    Interlocked.Increment(ref prepared);
    prepareEntered?.TrySetResult();
    if(allowPrepare is not null)await allowPrepare.Task; // deliberately ignores cancellation, exercising late cleanup.
    return new BrowserDisplaySession(token=>Video(token),_=>Interlocked.Increment(ref inputs),()=>{},()=>{Interlocked.Increment(ref disposed);return ValueTask.CompletedTask;});
    },1,FreePort(),storage);
var statuses=new List<BrowserSessionStatus>();
host.SessionChanged+=status=>{lock(statuses)statuses.Add(status);};
host.FramePresented+=_=>Interlocked.Increment(ref presentationEvents);
await host.StartAsync();
using var caPublic=X509CertificateLoader.LoadCertificateFromFile(host.CaCertificatePath);
try
{
    var offer=host.CreatePairing();
    Check(offer.Uri.StartsWith(host.BaseUri+"/#token=")&&offer.ExpiresUtc>DateTimeOffset.UtcNow.AddMinutes(4),"Pairing uses fragment and five-minute lifetime");
    using var handler=new HttpClientHandler{ServerCertificateCustomValidationCallback=(_,cert,_,error)=>TestClient.Trusted(cert!,caPublic,error)};
    using var http=new HttpClient(handler);
    var page=await http.GetStringAsync(host.BaseUri+"/");
    Check(page.Contains("Browser display")&&page.Contains("id=\"language\"")&&page.Contains("id=\"viewerLanguage\"")&&
        page.Contains("value=\"system\"")&&page.Contains("value=\"zh-CN\"")&&page.Contains("value=\"en\""),
        "HTTPS serves the English-first offline client with system, Chinese and English choices");
    using var response=await http.GetAsync(host.BaseUri+"/client.js");
    var clientScript=await response.Content.ReadAsStringAsync();
    Check(response.Headers.Contains("Content-Security-Policy")&&response.Headers.CacheControl?.NoStore==true,"No-store and CSP protect pairing page");
    Check(clientScript.Contains("navigator.language")&&clientScript.Contains("localStorage.getItem(languageStorageKey)")&&
        clientScript.Contains("value===\"system\"||value===\"zh-CN\"||value===\"en\"")&&
        clientScript.Contains("[\"language\",\"viewerLanguage\"]")&&
        clientScript.Contains("statusKey(hostStatusKey(message))")&&!clientScript.Contains("status(message.message)"),
        "Browser language follows navigator by default, persists all three modes and maps host states locally");
    using(var ordinary=new HttpClient())
    {
        bool rejected=false;try{await ordinary.GetStringAsync(host.BaseUri);}catch(HttpRequestException){rejected=true;}
        Check(rejected,"An untrusted installation CA is rejected by ordinary TLS validation");
    }
    using(var idle=new ClientWebSocket())
    {
        idle.Options.RemoteCertificateValidationCallback=(_,cert,_,error)=>TestClient.Trusted((X509Certificate2)cert!,caPublic,error);
        idle.Options.SetRequestHeader("Origin",host.BaseUri);
        await idle.ConnectAsync(new Uri(host.BaseUri.Replace("https:","wss:")+"/signal"),CancellationToken.None);
        bool ended=false;using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try{await idle.ReceiveAsync(new byte[256],timeout.Token);ended=true;}catch(WebSocketException){ended=true;}
        Check(ended&&prepared==0,"Idle authenticated-origin WSS times out without allocating a display");
    }
    using(var wrongOrigin=new ClientWebSocket())
    {
        wrongOrigin.Options.RemoteCertificateValidationCallback=(_,cert,_,error)=>TestClient.Trusted((X509Certificate2)cert!,caPublic,error);
        wrongOrigin.Options.SetRequestHeader("Origin","https://untrusted.invalid");
        bool failed=false;try{await wrongOrigin.ConnectAsync(new Uri(host.BaseUri.Replace("https:","wss:")+"/signal"),CancellationToken.None);}catch(WebSocketException){failed=true;}
        Check(failed&&prepared==0,"Wrong origin cannot prepare a display");
    }
    var invalid=await TestClient.Open(host,caPublic,new string('0',64));
    await Task.Delay(250);Check(prepared==0,"Wrong token cannot prepare a display");
    await invalid.DisposeAsync();
    var profileRejected=false;
    try
    {
        BrowserProfile.Parse(JsonSerializer.Serialize(new{width=240,height=720,rotation=1,activeModeId=1,
            refreshRate=30,nativeWidth=240,nativeHeight=720,
            supportedModes=new[]{new{width=240,height=720,refreshRate=30,modeId=1}}}));
    }
    catch(InvalidDataException){profileRejected=true;}
    Check(profileRejected&&prepared==0,"Invalid profile rejected before display allocation");
    var pairing=host.CreatePairing();
    await using(var client=await TestClient.Open(host,caPublic,TestClient.Token(pairing)))
    {
        await client.Negotiate();
        await Eventually(()=>client.ReceivedFrames>=3,"DTLS-SRTP carries H.264 frames after authenticated negotiation",12000);
        Check(prepared==1,"Exactly one display prepared after DTLS and data channel connect");
        client.Control!.send(JsonSerializer.Serialize(new{type="presented",frames=1,width=1280,height=720}));
        await Eventually(()=>presentationEvents==1,"Actual browser presentation message advances host event");
        client.Control.send(JsonSerializer.Serialize(new{type="presented",frames=1,width=1280,height=720}));
        await Task.Delay(200);Check(presentationEvents==1,"Duplicate presentation cannot refresh watchdog");
        DateTime? before;lock(statuses)before=statuses.Last(x=>x.PresentedFrames>0).LastPresentedUtc;
        pause=true;
        await Eventually(()=>{lock(statuses)return statuses.Any(x=>x.CapturePaused);},"Host pause propagated without closing TCP");
        var prior=inputs;client.Control.send("{\"type\":\"input\",\"kind\":\"down\",\"x\":0.5,\"y\":0.5}");
        await Task.Delay(150);Check(inputs==prior,"Input discarded during capture pause");
        pause=false;
        await Eventually(()=>{lock(statuses)return statuses.Any(x=>x.State=="streaming"&&x.LastPresentedUtc==before&&x.PresentedFrames==1&&!x.CapturePaused);},"Capture resume does not forge presentation time");
        await using(var replay=await TestClient.Open(host,caPublic,TestClient.Token(pairing)))
        {await Task.Delay(150);Check(prepared==1,"Pairing token is single-use");}
        Check(client.LocalCandidates.Count>0&&client.LocalCandidates.All(c=>c.StartsWith("candidate:")&&BrowserRtcSession.CandidateUsesAddress(c,IPAddress.Loopback)),"ICE uses W3C syntax and only publishes the selected interface");
    }
    await Eventually(()=>disposed==1&&host.ActiveSessionCount==0,"Disconnect disposes exactly the owned display");
    await using(var active=await TestClient.Open(host,caPublic,TestClient.Token(host.CreatePairing())))
    {
        await active.Negotiate();
        await Eventually(()=>prepared==2&&active.ReceivedFrames>1,"The sole encrypted browser session streams",12000);
        await using(var rejected=await TestClient.Open(host,caPublic,TestClient.Token(host.CreatePairing())))
        {
            await Task.Delay(250);
            Check(prepared==2&&host.ActiveSessionCount==1,"A second browser client is rejected before display preparation");
        }
        Check(active.LocalCandidates.Select(x=>int.Parse(x.Split(' ')[5])).Distinct().All(BrowserHost.MediaPorts.Contains),
            "The sole session uses an approved UDP media port");
    }
    await Eventually(()=>disposed==2&&host.ActiveSessionCount==0,"The sole browser session releases its display before replacement");
    prepareEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);allowPrepare=new(TaskCreationOptions.RunContinuationsAsynchronously);
    await using(var late=await TestClient.Open(host,caPublic,TestClient.Token(host.CreatePairing())))
    {
        await late.Negotiate();await prepareEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Guid id;lock(statuses)id=statuses.Last(x=>x.State=="preparing").Id;
        var stopping=host.StopSessionAsync(id);
        await Task.Delay(150);Check(!stopping.IsCompleted,"Stop keeps the sole browser slot until late preparation is settled");
        allowPrepare.TrySetResult();
        await stopping;
        await Eventually(()=>disposed==3,"Preparation finishing after Stop is disposed once");
    }
    Check(BrowserRtcSession.ContainsKeyFrameRequest(new byte[]{0x81,206,0,2,0,0,0,0,0,0,0,0}),"RTCP PLI requests a fresh keyframe");
    var reduced=new RTCPCompoundPacket(new byte[]{0x81,206,0,2,0,0,0,0,0,0,0,0});
    Check(reduced.Feedback is {} feedback&&BrowserRtcSession.ContainsKeyFrameRequest(feedback.GetBytes()),"Reduced-size browser PLI needs no SDES reserialisation");
    Check(!BrowserRtcSession.ContainsKeyFrameRequest(new byte[]{0x81,206,0xff,0xff}),"Malformed RTCP does not request frames");
    var requested60=BrowserProfile.Parse(JsonSerializer.Serialize(new{width=1080,height=1920,rotation=0,activeModeId=1,
        refreshRate=60,nativeWidth=1080,nativeHeight=1920,
        supportedModes=new[]{new{width=1080,height=1920,refreshRate=60,modeId=1}}}));
    var software30=BrowserMediaParameters.Negotiate(requested60,1080,1920,30);
    Check(software30.FramesPerSecond==30&&software30.RtpTimestampStep==3000&&requested60.RequestedRefreshRate==60,
        "60 Hz virtual display negotiates an actual 30 fps software media cadence and matching RTP timestamp step");
    var hardware60=BrowserMediaParameters.Negotiate(requested60,1080,1920,60);
    Check(hardware60.RtpTimestampStep==1500,"60 fps hardware media uses its actual RTP timestamp step");
    bool mismatchedFpsRejected=false;
    try{BrowserMediaParameters.Negotiate(requested60,1080,1920,45);}catch(InvalidDataException){mismatchedFpsRejected=true;}
    Check(mismatchedFpsRejected,"browser media negotiation rejects unadvertised encoder frame rates");
}
finally {await host.DisposeAsync();await host.DisposeAsync();}
Check(disposed==3,"Host disposal is idempotent");
int softwareDisposed=0;
await using(var softwareHost=new BrowserHost(IPAddress.Loopback,(id,p,ct)=>Task.FromResult(
    new BrowserDisplaySession(token=>Video(token,p.Width,p.Height,30),null,()=>{},()=>
    {Interlocked.Increment(ref softwareDisposed);return ValueTask.CompletedTask;})),1,FreePort(),storage))
{
    await softwareHost.StartAsync();
    await using var softwareClient=await TestClient.Open(softwareHost,caPublic,TestClient.Token(softwareHost.CreatePairing()),
        width:1080,height:1920,fps:60);
    await softwareClient.Negotiate();
    await Eventually(()=>softwareClient.ReceivedFrames>=3,
        "60 Hz browser display stays connected while actual software media runs at 30 fps",12000);
}
Check(softwareDisposed==1,"30 fps browser software-media session releases its sole display");
Console.WriteLine($"PASS total={results.Count}; real browser rendering / physical displays not claimed");

static int FreePort(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
async IAsyncEnumerable<VideoPacket> Video([EnumeratorCancellation]CancellationToken ct,int width=1280,int height=720,int fps=30)
{
    byte[] sps=[0,0,0,1,0x67,0x42,0xe0,0x2a],pps=[0,0,0,1,0x68,0xce,0x06,0xe2];
    var config=JsonSerializer.SerializeToUtf8Bytes(new{width,height,fps,csd0=Convert.ToBase64String(sps),csd1=Convert.ToBase64String(pps)});
    yield return new(0x20,config,false);
    bool wasPaused=false;
    while(true)
    {
        await Task.Delay(TimeSpan.FromSeconds(1d/fps),ct);
        if(pause!=wasPaused){wasPaused=pause;yield return new(0x02,[],false,pause);}
        if(pause)continue;
        // Transport fixture only: non-decodable IDR bytes exercise packetisation,
        // SRTP and reassembly without reading any screen or allocating a VDD.
        yield return new(0x21,new byte[]{0,0,0,0,0,0,0,0,0,0,0,1,0x65,0x88,0x84,0x00},true);
    }
}

sealed class TestClient : IAsyncDisposable
{
    public readonly ClientWebSocket Socket=new();
    public RTCPeerConnection? Peer;
    public RTCDataChannel? Control;
    public long ReceivedFrames;
    public List<string> LocalCandidates=[];
    readonly SemaphoreSlim sendGate=new(1,1);
    readonly CancellationTokenSource lifetime=new(TimeSpan.FromSeconds(25));
    readonly TaskCompletionSource answer=new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task? reader;
    readonly List<object> candidates=[];
    bool offerSent;
    public static string Token(BrowserPairingOffer offer)=>new Uri(offer.Uri).Fragment[7..];
    public static bool Trusted(X509Certificate2 certificate,X509Certificate2 ca,SslPolicyErrors errors)
    {
        if((errors&SslPolicyErrors.RemoteCertificateNameMismatch)!=0)return false;
        using var chain=new X509Chain();chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }
    public static async Task<TestClient> Open(BrowserHost host,X509Certificate2 ca,string token,bool invalidProfile=false,
        int width=1280,int height=720,int fps=30)
    {
        var client=new TestClient();client.Socket.Options.SetRequestHeader("Origin",host.BaseUri);
        client.Socket.Options.RemoteCertificateValidationCallback=(_,cert,_,error)=>Trusted((X509Certificate2)cert!,ca,error);
        await client.Socket.ConnectAsync(new Uri(host.BaseUri.Replace("https:","wss:")+"/signal"),client.lifetime.Token);
        var reportedWidth=invalidProfile?240:width;
        await client.Send(new{type="hello",token,profile=new{width=reportedWidth,height,rotation=width<height?0:1,
            activeModeId=1,refreshRate=fps,nativeWidth=reportedWidth,nativeHeight=height,
            supportedModes=new[]{new{width=reportedWidth,height,refreshRate=fps,modeId=1}}}});
        return client;
    }
    public async Task Negotiate()
    {
        Peer=new RTCPeerConnection(new RTCConfiguration{X_BindAddress=IPAddress.Loopback,X_ICEIncludeAllInterfaceAddresses=false,iceServers=[]});
        Peer.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.H264,96,90000,"level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e02a"),MediaStreamStatusEnum.RecvOnly));
        Peer.OnVideoFrameReceived+=(_,_,_,_)=>Interlocked.Increment(ref ReceivedFrames);
        Control=await Peer.createDataChannel("control");
        Peer.onicecandidate+=candidate=>{if(candidate is null)return;var value=new{type="ice",candidate=new{candidate=candidate.candidate,candidate.sdpMid,candidate.sdpMLineIndex}};lock(candidates){if(!offerSent)candidates.Add(value);else _=Send(value);}};
        var offer=Peer.createOffer(null);await Peer.setLocalDescription(offer);
        await Send(new{type="offer",sdp=offer.sdp});
        lock(candidates){offerSent=true;foreach(var candidate in candidates)_=Send(candidate);candidates.Clear();}
        reader=ReadLoop();
        await answer.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    async Task ReadLoop()
    {
        try
        {
            while(true)
            {
                using var json=await BrowserRtcSession.ReadJsonAsync(Socket,65536,lifetime.Token);
                var root=json.RootElement;
                if(root.GetProperty("type").GetString()=="answer")
                {var result=Peer!.setRemoteDescription(new RTCSessionDescriptionInit{type=RTCSdpType.answer,sdp=root.GetProperty("sdp").GetString()});if(result!=SetDescriptionResultEnum.OK)throw new Exception(result.ToString());answer.TrySetResult();}
                else if(root.GetProperty("type").GetString()=="ice")
                {var value=root.GetProperty("candidate");var candidate=value.GetProperty("candidate").GetString()!;LocalCandidates.Add(candidate);Peer!.addIceCandidate(new RTCIceCandidateInit{candidate=candidate,sdpMid=value.GetProperty("sdpMid").GetString(),sdpMLineIndex=value.GetProperty("sdpMLineIndex").GetUInt16()});}
            }
        }
        catch(Exception ex){answer.TrySetException(ex);}
    }
    async Task Send(object data)
    {await sendGate.WaitAsync(lifetime.Token);try{await Socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data)),WebSocketMessageType.Text,true,lifetime.Token);}finally{sendGate.Release();}}
    public async ValueTask DisposeAsync(){lifetime.Cancel();Socket.Abort();Peer?.Close("test ended");Peer?.Dispose();if(reader is not null)try{await reader;}catch{}Socket.Dispose();}
}
