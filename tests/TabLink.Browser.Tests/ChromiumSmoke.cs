using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using TabLink.Windows;
using Microsoft.Extensions.Logging;

// Isolated headless Chromium QA against a synthetic source. Certificate bypass
// is scoped to the exact generated leaf SPKI in this disposable browser profile.
// Nothing is installed in an OS trust store and no desktop is captured.
internal static class ChromiumSmoke
{
    internal static async Task Run(string fixture,int width=1280,int height=720,int fps=30)
    {
        using var logging=LoggerFactory.Create(builder=>builder.AddProvider(new DiagnosticLogger()).SetMinimumLevel(LogLevel.Debug));
        SIPSorcery.LogFactory.Set(logging);
        var parser=new AnnexBParser();
        var frames=parser.Append(await File.ReadAllBytesAsync(fixture)).ToList();
        if(parser.Complete() is {} final)frames.Add(final);
        if(frames.Count<10)throw new Exception("Need an encoded synthetic fixture.");
        var sps=frames[0].Sps;
        var offset=Array.IndexOf(sps,(byte)0x67);
        if(offset<0||sps[offset+1]!=66)throw new Exception("Synthetic fixture is not Baseline H.264.");
        var directory=Path.Combine(Path.GetTempPath(),"TabLink-Chromium-QA-"+Guid.NewGuid().ToString("N"));
        long presentationEvents=0;int prepared=0,disposed=0;
        string lastState="",lastMessage="";
        await using var host=new BrowserHost(IPAddress.Loopback,(_,_,_)=>
        {Interlocked.Increment(ref prepared);return Task.FromResult(new BrowserDisplaySession(Video,null,()=>{},()=>{Interlocked.Increment(ref disposed);return ValueTask.CompletedTask;}));},1,Port(),Path.Combine(directory,"ca"));
        host.FramePresented+=_=>Interlocked.Increment(ref presentationEvents);
        host.SessionChanged+=s=>{lastState=s.State;lastMessage=s.Message;};
        host.Diagnostic+=(_,message)=>Console.WriteLine(message);
        await host.StartAsync();
        using var ca=X509CertificateLoader.LoadCertificateFromFile(host.CaCertificatePath);
        string pin="";
        using(var handler=new HttpClientHandler{ServerCertificateCustomValidationCallback=(_,cert,_,errors)=>
        {using var key=cert!.GetRSAPublicKey();pin=Convert.ToBase64String(SHA256.HashData(key!.ExportSubjectPublicKeyInfo()));return TestClient.Trusted(cert!,ca,errors);}})
        using(var client=new HttpClient(handler))await client.GetStringAsync(host.BaseUri);
        int debugging=Port();
        var start=new ProcessStartInfo(@"C:\Program Files\Google\Chrome\Application\chrome.exe"){UseShellExecute=false,CreateNoWindow=true};
        foreach(var arg in new[]{"--headless=new","--no-first-run","--no-default-browser-check","--window-size=1280,800","--user-data-dir="+Path.Combine(directory,"chrome"),
            "--remote-debugging-address=127.0.0.1","--remote-debugging-port="+debugging,"--ignore-certificate-errors-spki-list="+pin,"about:blank"})start.ArgumentList.Add(arg);
        using var browser=Process.Start(start)??throw new Exception("Could not start isolated Chromium.");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            using var http=new HttpClient();string? target=null;
            for(int i=0;i<60&&target is null;i++)
            {
                try{using var json=JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{debugging}/json/list",timeout.Token));
                    target=json.RootElement.EnumerateArray().First(x=>x.GetProperty("type").GetString()=="page").GetProperty("webSocketDebuggerUrl").GetString();}
                catch{await Task.Delay(100,timeout.Token);}
            }
            if(target is null)throw new Exception("Isolated Chromium CDP did not start.");
            using var cdp=new ClientWebSocket();await cdp.ConnectAsync(new Uri(target),timeout.Token);
            int messageId=0;
            async Task<JsonElement> Command(string method,object parameters)
            {
                int id=++messageId;
                await cdp.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{id,method,@params=parameters})),WebSocketMessageType.Text,true,timeout.Token);
                while(true)
                {
                    using var doc=await BrowserRtcSession.ReadJsonAsync(cdp,1048576,timeout.Token);
                    if(doc.RootElement.TryGetProperty("id",out var found)&&found.GetInt32()==id)return doc.RootElement.Clone();
                }
            }
            await Command("Page.addScriptToEvaluateOnNewDocument",new{source="const Pc=window.RTCPeerConnection;window.RTCPeerConnection=class extends Pc {constructor(...a){super(...a);window.qaPeer=this;}};"});
            await Command("Page.navigate",new{url=host.CreatePairing().Uri});
            for(int i=0;i<40;i++)
            {
                var state=await Command("Runtime.evaluate",new{expression="document.readyState",returnByValue=true});
                if(state.GetProperty("result").GetProperty("result").GetProperty("value").GetString()=="complete")break;
                await Task.Delay(100,timeout.Token);
            }
            var selections=$"document.getElementById('quality').value='{Math.Min(width,height)}';document.getElementById('fps').value='{fps}';document.getElementById('orientation').value='{(width>height?"landscape":"portrait")}';";
            await Command("Runtime.evaluate",new{expression=selections+"window.qaTrace=[];window.RTCPeerConnection=class extends window.RTCPeerConnection {constructor(...a){super(...a);window.qaPeer=this;this.addEventListener('iceconnectionstatechange',()=>qaTrace.push('ice:'+this.iceConnectionState));this.addEventListener('connectionstatechange',()=>qaTrace.push('dtls:'+this.connectionState));}close(){qaTrace.push(new Error('closed').stack);super.close();}};document.getElementById('start').click()",userGesture=true,returnByValue=true});
            await Task.Delay(1500,timeout.Token);
            var rtc=await Command("Runtime.evaluate",new{expression="(async()=>{const p=window.qaPeer,s=await p.getStats();return JSON.stringify({connection:p.connectionState,ice:p.iceConnectionState,signaling:p.signalingState,transports:[...s.values()].filter(v=>v.type==='transport').map(v=>({dtls:v.dtlsState,bytesSent:v.bytesSent,bytesReceived:v.bytesReceived})),pairs:[...s.values()].filter(v=>v.type==='candidate-pair').map(v=>({state:v.state,nominated:v.nominated,requestsSent:v.requestsSent,responsesReceived:v.responsesReceived}))});})()",awaitPromise=true,returnByValue=true});
            Console.WriteLine(rtc.GetRawText());
            var trace=await Command("Runtime.evaluate",new{expression="JSON.stringify(window.qaTrace)",returnByValue=true});Console.WriteLine(trace.GetRawText());
            for(int i=0;i<160&&Interlocked.Read(ref presentationEvents)<8;i++)await Task.Delay(100,timeout.Token);
            var metrics=await Command("Runtime.evaluate",new{expression="JSON.stringify({width:document.getElementById('screen').videoWidth,height:document.getElementById('screen').videoHeight,decoded:document.getElementById('screen').getVideoPlaybackQuality().totalVideoFrames,status:document.getElementById('status').textContent,secure:isSecureContext})",returnByValue=true});
            var result=metrics.GetProperty("result").GetProperty("result").GetProperty("value").GetString();
            Console.WriteLine(result);
            if(presentationEvents<8)throw new Exception($"Chromium did not present eight progress reports; host state={lastState}; message={lastMessage}; prepared={prepared}");
            await Command("Runtime.evaluate",new{expression="document.getElementById('disconnect').click()",userGesture=true,returnByValue=true});
            for(int i=0;i<40&&disposed!=1;i++)await Task.Delay(50,timeout.Token);
            if(disposed!=1)throw new Exception("Browser disconnect did not dispose its session.");
            Console.WriteLine($"PASS isolated Chromium HTTPS/WSS + DTLS-SRTP + NVENC Baseline decode + requestVideoFrameCallback ({presentationEvents} progress reports); no physical-device claim");
        }
        finally {try{if(!browser.HasExited)browser.Kill(entireProcessTree:true);}catch{} }
        async IAsyncEnumerable<VideoPacket> Video([EnumeratorCancellation]CancellationToken ct)
        {
            yield return new(0x20,JsonSerializer.SerializeToUtf8Bytes(new{width,height,fps,csd0=Convert.ToBase64String(frames[0].Sps),csd1=Convert.ToBase64String(frames[0].Pps)}),false);
            long n=0;
            while(true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.0/fps),ct);
                var frame=frames[(int)(n++%frames.Count)];
                yield return new(0x21,[0,0,0,0,0,0,0,0,..frame.Data],true);
            }
        }
    }
    static int Port(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
    sealed class DiagnosticLogger:ILoggerProvider
    {
        public ILogger CreateLogger(string name)=>new Sink(name);
        public void Dispose(){}
        sealed class Sink(string name):ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
            public bool IsEnabled(LogLevel level)=>true;
            public void Log<TState>(LogLevel level,EventId id,TState state,Exception? error,Func<TState,Exception?,string> format)
            {var text=format(state,error);if((text.Contains("handshake",StringComparison.OrdinalIgnoreCase)||text.Contains("DTLS",StringComparison.OrdinalIgnoreCase)||level>=LogLevel.Warning)&&!text.Contains("fingerprint",StringComparison.OrdinalIgnoreCase))Console.WriteLine(name+": "+text.Split('\n')[0]);}
        }
    }
}
