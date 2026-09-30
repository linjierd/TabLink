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
    Check((await http.GetStringAsync(host.BaseUri+"/")).Contains("浏览器副屏"),"HTTPS serves embedded offline client");
    using var response=await http.GetAsync(host.BaseUri+"/client.js");
    Check(response.Headers.Contains("Content-Security-Policy")&&response.Headers.CacheControl?.NoStore==true,"No-store and CSP protect pairing page");
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
        allowPrepare.TrySetResult();
        await stopping;
        await Eventually(()=>disposed==3,"Preparation finishing after Stop is disposed once");
    }
    Check(BrowserRtcSession.ContainsKeyFrameRequest(new byte[]{0x81,206,0,2,0,0,0,0,0,0,0,0}),"RTCP PLI requests a fresh keyframe");
    var reduced=new RTCPCompoundPacket(new byte[]{0x81,206,0,2,0,0,0,0,0,0,0,0});
    Check(reduced.Feedback is {} feedback&&BrowserRtcSession.ContainsKeyFrameRequest(feedback.GetBytes()),"Reduced-size browser PLI needs no SDES reserialisation");
    Check(!BrowserRtcSession.ContainsKeyFrameRequest(new byte[]{0x81,206,0xff,0xff}),"Malformed RTCP does not request frames");
}
finally {await host.DisposeAsync();await host.DisposeAsync();}
Check(disposed==3,"Host disposal is idempotent");
Console.WriteLine($"PASS total={results.Count}; real browser rendering / physical displays not claimed");

static int FreePort(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
async IAsyncEnumerable<VideoPacket> Video([EnumeratorCancellation]CancellationToken ct)
{
    byte[] sps=[0,0,0,1,0x67,0x42,0xe0,0x2a],pps=[0,0,0,1,0x68,0xce,0x06,0xe2];
    var config=JsonSerializer.SerializeToUtf8Bytes(new{width=1280,height=720,fps=30,csd0=Convert.ToBase64String(sps),csd1=Convert.ToBase64String(pps)});
    yield return new(0x20,config,false);
    bool wasPaused=false;
    while(true)
    {
        await Task.Delay(33,ct);
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
    public static async Task<TestClient> Open(BrowserHost host,X509Certificate2 ca,string token,bool invalidProfile=false)
    {
        var client=new TestClient();client.Socket.Options.SetRequestHeader("Origin",host.BaseUri);
        client.Socket.Options.RemoteCertificateValidationCallback=(_,cert,_,error)=>Trusted((X509Certificate2)cert!,ca,error);
        await client.Socket.ConnectAsync(new Uri(host.BaseUri.Replace("https:","wss:")+"/signal"),client.lifetime.Token);
        await client.Send(new{type="hello",token,profile=new{width=invalidProfile?240:1280,height=720,rotation=1,activeModeId=1,refreshRate=30,nativeWidth=1280,nativeHeight=720,supportedModes=new[]{new{width=1280,height=720,refreshRate=30,modeId=1}}}});
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
