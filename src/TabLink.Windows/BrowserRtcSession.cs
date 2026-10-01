#if !TABLINK_NO_BROWSER
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using TabLink.Core;

namespace TabLink.Windows;

// Signalling is authenticated by the HTTPS origin and a single-use pairing
// capability before this object exists. DTLS authenticates the SDP fingerprint;
// no unencrypted media or input is accepted on this transport.
internal sealed class BrowserRtcSession : IAsyncDisposable
{
    internal static bool IsSupported => true;
    readonly IPAddress address;
    readonly int mediaPort;
    readonly WebSocket socket;
    readonly TabletDisplayProfile profile;
    readonly Func<Guid,TabletDisplayProfile,CancellationToken,Task<BrowserDisplaySession>> prepare;
    readonly Action<BrowserSessionStatus> report;
    readonly Action presented;
    readonly Action<string> diagnostic;
    readonly CancellationTokenSource lifetime;
    readonly Channel<string> outgoing=Channel.CreateBounded<string>(new BoundedChannelOptions(32){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    readonly TaskCompletionSource connected=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource controlReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object gate=new();
    readonly SessionPresentationDeadline presentationDeadline=new();
    RTCPeerConnection? peer;
    RTCDataChannel? control;
    BrowserDisplaySession? display;
    Task? writerTask,readerTask,videoTask,watchdogTask,preparationTask,cleanupTask;
    bool capturePaused,needKeyFrame=true;
    long framesSent,framesPresented;
    DateTime? lastPresentedUtc;
    DateTime lastStatusUtc=DateTime.MinValue;
    int messagesThisSecond;
    long messageSecond;
    public Guid Id {get;}
    public int MediaPort=>mediaPort;

    internal BrowserRtcSession(Guid id,IPAddress address,int mediaPort,WebSocket socket,TabletDisplayProfile profile,
        Func<Guid,TabletDisplayProfile,CancellationToken,Task<BrowserDisplaySession>> prepare,
        Action<BrowserSessionStatus> report,Action presented,Action<string> diagnostic,CancellationToken ct)
    {
        Id=id;this.address=address;this.mediaPort=mediaPort;this.socket=socket;this.profile=profile;
        this.prepare=prepare;this.report=report;this.presented=presented;this.diagnostic=diagnostic;
        lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    internal async Task RunAsync()
    {
        var ct=lifetime.Token;
        writerTask=WriteLoop(ct);
        try
        {
            EmitStatus("connecting","正在建立加密的浏览器副屏连接。");
            using(var negotiation=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                negotiation.CancelAfter(TimeSpan.FromSeconds(15));
                using var offer=await ReadJsonAsync(socket,65536,negotiation.Token);
                if(offer.RootElement.GetProperty("type").GetString()!="offer")throw new InvalidDataException("需要 WebRTC offer。");
                var sdp=offer.RootElement.GetProperty("sdp").GetString();
                if(string.IsNullOrWhiteSpace(sdp)||sdp.Length>60000)throw new InvalidDataException("无效的会话描述。");
                lock(gate)
                {
                    ct.ThrowIfCancellationRequested();
                    peer=new RTCPeerConnection(new RTCConfiguration{X_BindAddress=address,X_ICEIncludeAllInterfaceAddresses=false,
                        X_UseRtpFeedbackProfile=true,iceServers=[]},mediaPort,videoAsPrimary:true);
                }
                peer.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.H264,96,90000,
                    "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e02a"),MediaStreamStatusEnum.SendOnly));
                peer.onicecandidate+=candidate=>
                {
                    // The socket is bound to one selected interface; additionally
                    // reject any unexpected candidate before exposing it in SDP.
                    if(candidate is not null&&CandidateUsesAddress(candidate.candidate,address))
                        Queue(new{type="ice",candidate=new{candidate="candidate:"+candidate.candidate,
                            sdpMid=candidate.sdpMid??candidate.sdpMLineIndex.ToString(),candidate.sdpMLineIndex}});
                };
                peer.onconnectionstatechange+=state=>
                {
                    diagnostic("WebRTC connection: "+state);
                    if(state==RTCPeerConnectionState.connected)connected.TrySetResult();
                    else if(state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)Cancel();
                };
                peer.oniceconnectionstatechange+=state=>diagnostic("ICE connection: "+state);
                peer.ondatachannel+=AttachControl;
                peer.OnReceiveReport+=(_,media,packet)=>
                {
                    // Reduced-size feedback deliberately has no SDES. Serialising
                    // the entire compound packet would throw for browser PLI.
                    if(media==SDPMediaTypesEnum.video&&packet.Feedback is {} feedback&&ContainsKeyFrameRequest(feedback.GetBytes()))
                        lock(gate)needKeyFrame=true;
                };
                var result=peer.setRemoteDescription(new RTCSessionDescriptionInit{type=RTCSdpType.offer,sdp=sdp});
                if(result!=SetDescriptionResultEnum.OK)throw new InvalidDataException("浏览器未协商到 H.264。");
                var answer=peer.createAnswer(null);
                // X_BindAddress prevents candidate enumeration from other NICs.
                // This assertion also fails closed if that library contract drifts.
                foreach(var line in answer.sdp.Split('\n').Where(x=>x.StartsWith("a=candidate:")))
                    if(!CandidateUsesAddress(line.Trim()[2..],address))throw new InvalidDataException("媒体绑定地址不一致。");
                await peer.setLocalDescription(answer);
                Queue(new{type="answer",sdp=answer.sdp});
                readerTask=ReadLoop(ct);
                await Task.WhenAll(connected.Task,controlReady.Task).WaitAsync(negotiation.Token);
            }
            EmitStatus("preparing","正在为此设备准备独立副屏。");
            using(var preparation=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // First use can install/reload the signed virtual display
                // device before allocation. Keep the authenticated peer alive
                // while that bounded local transaction completes.
                preparation.CancelAfter(TimeSpan.FromSeconds(120));
                // Stop/host disposal must await this whole lifecycle. A callback
                // that returns after cancellation is disposed before the sole
                // browser slot is released or another pairing can be accepted.
                Task lifecycle;
                lock(gate)preparationTask=lifecycle=PrepareDisplayLifecycleAsync(preparation.Token,ct);
                await lifecycle;
            }
            ct.ThrowIfCancellationRequested();
            lock(gate)presentationDeadline.Reset(DateTime.UtcNow);
            EmitStatus("streaming","已连接，等待浏览器呈现画面。");
            videoTask=StreamVideo(ct);
            watchdogTask=WatchPresentation(ct);
            var finished=await Task.WhenAny(videoTask,readerTask!,writerTask,watchdogTask);
            await finished;
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
        catch(Exception ex)
        {
            diagnostic("Browser session failure: "+ex.GetType().Name);
            // Never send exception text (SDP, local paths, certificates or tokens)
            // to a remote client. Root prepare is responsible for local detail.
            try {Queue(new{type="error",message="电脑未能继续提供副屏。请查看电脑连接记录，再生成新的配对二维码。"});await Task.Delay(100);}catch{}
        }
        finally {await DisposeAsync();}
    }

    async Task PrepareDisplayLifecycleAsync(CancellationToken preparationToken,CancellationToken sessionToken)
    {
        var pending=Task.Run(()=>prepare(Id,profile,preparationToken),CancellationToken.None);
        BrowserDisplaySession? prepared=null;
        try
        {
            prepared=await pending.WaitAsync(preparationToken);
            bool accepted;
            lock(gate)
            {
                accepted=cleanupTask is null&&!sessionToken.IsCancellationRequested;
                if(accepted)display=prepared;
            }
            if(!accepted)
            {
                await prepared.DisposeAsync();
                sessionToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException();
            }
        }
        catch
        {
            if(prepared is null)await DisposeLatePreparation(pending);
            throw;
        }
    }

    static async Task DisposeLatePreparation(Task<BrowserDisplaySession> pending)
    {try{await (await pending.ConfigureAwait(false)).DisposeAsync();}catch{}}

    void AttachControl(RTCDataChannel channel)
    {
        lock(gate)
        {
            if(control is not null||channel.label!="control"){channel.close();return;}
            control=channel;
            diagnostic("Control channel received: "+channel.readyState);
            channel.onopen+=()=>{diagnostic("Control channel open");controlReady.TrySetResult();};
            channel.onclose+=Cancel;
            channel.onmessage+=(_,_,data)=>HandleControl(data);
            if(channel.readyState==RTCDataChannelState.open)controlReady.TrySetResult();
        }
    }

    void HandleControl(byte[] data)
    {
        try
        {
            if(lifetime.IsCancellationRequested)return;
            if(data.Length>4096)throw new InvalidDataException();
            lock(gate)
            {
                var second=Environment.TickCount64/1000;
                if(second!=messageSecond){messageSecond=second;messagesThisSecond=0;}
                if(++messagesThisSecond>180)throw new InvalidDataException();
            }
            using var json=JsonDocument.Parse(data,new JsonDocumentOptions{MaxDepth=8});
            var root=json.RootElement;
            switch(root.GetProperty("type").GetString())
            {
                case "input":
                    var message=JsonSerializer.Deserialize<InputMessage>(data,new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
                    if(message is null||!double.IsFinite(message.X)||!double.IsFinite(message.Y)||!double.IsFinite(message.Delta)||
                        message.X is <0 or >1||message.Y is <0 or >1||Math.Abs(message.Delta)>1200||message.Kind is not ("down" or "up" or "move" or "scroll"))
                        throw new InvalidDataException();
                    lock(gate)
                    {
                        if(display is null)return;
                        if(capturePaused||!InputDesktopAvailability.Query().IsAvailable){display.ReleaseInput();return;}
                        display.Input?.Invoke(message);
                    }
                    break;
                case "presented":
                    var frames=root.GetProperty("frames").GetInt64();
                    bool advanced=false;
                    lock(gate)
                    {
                        if(display is null||capturePaused)return;
                        if(root.GetProperty("width").GetInt32()!=profile.Width||root.GetProperty("height").GetInt32()!=profile.Height||frames<0||frames>framesSent)
                            throw new InvalidDataException();
                        if(frames>framesPresented)
                        {framesPresented=frames;lastPresentedUtc=DateTime.UtcNow;advanced=true;}
                    }
                    if(advanced){presented();if(frames==1||DateTime.UtcNow-lastStatusUtc>TimeSpan.FromSeconds(1))EmitStatus("streaming","浏览器正在呈现独立副屏。");}
                    break;
                default: throw new InvalidDataException();
            }
        }
        catch {Cancel();}
    }

    async Task StreamVideo(CancellationToken ct)
    {
        byte[] sps=[],pps=[];
        BrowserMediaParameters? media=null;
        // The async source is consumed directly: no unbounded encoded-frame
        // queue. UDP sends are synchronous; FFmpeg's stdout pipe is bounded.
        await foreach(var packet in display!.VideoFactory(ct).WithCancellation(ct))
        {
            if(packet.CapturePaused is bool paused)
            {
                lock(gate)
                {
                    capturePaused=paused;
                    if(paused)display.ReleaseInput();
                    else needKeyFrame=true;
                }
                EmitStatus(paused?"paused":"streaming",paused?"Windows 暂时切换桌面，等待普通桌面恢复。":"画面正在恢复。");
            }
            if(packet.Type==0x20)
            {
                using var config=JsonDocument.Parse(packet.Payload);
                var root=config.RootElement;
                media=BrowserMediaParameters.Negotiate(profile,root.GetProperty("width").GetInt32(),
                    root.GetProperty("height").GetInt32(),root.GetProperty("fps").GetInt32());
                sps=Convert.FromBase64String(root.GetProperty("csd0").GetString()!);
                pps=Convert.FromBase64String(root.GetProperty("csd1").GetString()!);
                if(sps.Length>4096||pps.Length>4096||!ContainsNal(sps,7)||!ContainsNal(pps,8))throw new InvalidDataException("无效 H.264 配置。");
                lock(gate)needKeyFrame=true;
                Queue(new{type="media",width=media.Value.Width,height=media.Value.Height,
                    fps=media.Value.FramesPerSecond,displayFps=profile.RequestedRefreshRate});
            }
            if(packet.Type!=0x21||!packet.IsFrame)continue;
            if(packet.Payload.Length is <9 or >8388608)throw new InvalidDataException("无效 H.264 帧。");
            var bytes=packet.Payload.AsSpan(8).ToArray();
            bool key=ContainsNal(bytes,5);
            lock(gate)
            {
                if(capturePaused)continue;
                if(needKeyFrame&&!key)continue;
                if(key)needKeyFrame=false;
                framesSent++;
            }
            if(key)
            {
                if(sps.Length==0||pps.Length==0)throw new InvalidDataException("缺少 H.264 配置。");
                bytes=[..sps,..pps,..bytes];
            }
            if(media is null)throw new InvalidDataException("缺少 H.264 媒体参数。");
            peer!.SendVideo(media.Value.RtpTimestampStep,bytes);
        }
    }

    async Task WatchPresentation(CancellationToken ct)
    {
        while(true)
        {
            await Task.Delay(1000,ct);
            lock(gate)
            {
                var now=DateTime.UtcNow;
                if(now>presentationDeadline.Evaluate(now,lastPresentedUtc,capturePaused,InputDesktopAvailability.Query()).DeadlineUtc)
                    throw new IOException("浏览器未呈现画面。");
            }
        }
    }

    async Task ReadLoop(CancellationToken ct)
    {
        int count=0;long second=0;
        try { while(true)
        {
            using var json=await ReadJsonAsync(socket,16384,ct);
            var now=Environment.TickCount64/1000;
            if(now!=second){second=now;count=0;}
            if(++count>32)throw new InvalidDataException("信令过快。");
            var root=json.RootElement;
            if(root.GetProperty("type").GetString()!="ice")throw new InvalidDataException("不支持的信令。");
            var candidate=root.GetProperty("candidate");
            var text=candidate.GetProperty("candidate").GetString();
            if(text?.Length>2048)throw new InvalidDataException();
            if(!string.IsNullOrWhiteSpace(text))peer!.addIceCandidate(new RTCIceCandidateInit{candidate=text,
                sdpMid=candidate.TryGetProperty("sdpMid",out var mid)?mid.GetString():"0",
                sdpMLineIndex=candidate.TryGetProperty("sdpMLineIndex",out var index)?index.GetUInt16():(ushort)0});
        }}catch(EndOfStreamException){Cancel();}
    }

    void EmitStatus(string state,string message)
    {
        BrowserSessionStatus status;
        lock(gate){lastStatusUtc=DateTime.UtcNow;status=new(Id,state,message,capturePaused,lastPresentedUtc,framesPresented);}
        report(status);
        Queue(new{type="status",state,message,capturePaused=status.CapturePaused});
    }
    void Queue(object message)
    {if(!outgoing.Writer.TryWrite(JsonSerializer.Serialize(message)))Cancel();}
    async Task WriteLoop(CancellationToken ct)
    {
        await foreach(var message in outgoing.Reader.ReadAllAsync(ct))
        {using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.SendAsync(Encoding.UTF8.GetBytes(message),WebSocketMessageType.Text,true,deadline.Token);}
    }
    internal static async Task<JsonDocument> ReadJsonAsync(WebSocket socket,int maximum,CancellationToken ct)
    {
        var bytes=new byte[maximum];int offset=0;
        while(true)
        {
            if(offset==maximum)throw new InvalidDataException("信令过大。");
            var result=await socket.ReceiveAsync(bytes.AsMemory(offset),ct);
            if(result.MessageType==WebSocketMessageType.Close)throw new EndOfStreamException("浏览器已断开。");
            if(result.MessageType!=WebSocketMessageType.Text)throw new InvalidDataException("需要文本信令。");
            offset+=result.Count;
            if(result.EndOfMessage)return JsonDocument.Parse(bytes.AsMemory(0,offset),new JsonDocumentOptions{MaxDepth=16});
        }
    }
    internal static bool CandidateUsesAddress(string candidate,IPAddress address)
    {
        var parts=candidate.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        return parts.Length>=8&&parts[2].Equals("udp",StringComparison.OrdinalIgnoreCase)&&
            IPAddress.TryParse(parts[4],out var found)&&found.Equals(address)&&parts[6]=="typ"&&parts[7]=="host";
    }
    internal static bool ContainsKeyFrameRequest(ReadOnlySpan<byte> bytes)
    {
        for(int offset=0;offset+4<=bytes.Length;)
        {
            var length=((bytes[offset+2]<<8)|bytes[offset+3])+1;length*=4;
            if(length<4||offset+length>bytes.Length)return false;
            if(bytes[offset+1]==206&&(bytes[offset]&31) is 1 or 4)return true;
            offset+=length;
        }
        return false;
    }
    internal static bool ContainsNal(ReadOnlySpan<byte> bytes,int type)
    {
        for(int i=0;i+3<bytes.Length;i++)
            if(bytes[i]==0&&bytes[i+1]==0&&bytes[i+2]==1&&(bytes[i+3]&31)==type)return true;
        return false;
    }
    void Cancel(){try{lifetime.Cancel();}catch(ObjectDisposedException){}}
    public ValueTask DisposeAsync(){lock(gate)return new(cleanupTask??=Task.Run(Cleanup));}
    async Task Cleanup()
    {
        Cancel();outgoing.Writer.TryComplete();
        try {socket.Abort();peer?.Close("session ended");peer?.Dispose();}catch{}
        Task? preparing;lock(gate)preparing=preparationTask;
        foreach(var task in new[]{preparing,videoTask,readerTask,writerTask,watchdogTask})
            if(task is not null)try{await task.ConfigureAwait(false);}catch{}
        try {if(display is not null)await display.DisposeAsync();}
        finally {report(new(Id,"closed","浏览器副屏已断开。"));lifetime.Dispose();}
    }
}

internal readonly record struct BrowserMediaParameters(int Width,int Height,int FramesPerSecond,uint RtpTimestampStep)
{
    internal static BrowserMediaParameters Negotiate(TabletDisplayProfile display,int width,int height,int fps)
    {
        if(width!=display.Width||height!=display.Height)
            throw new InvalidDataException("编码尺寸与浏览器协商不一致。");
        // The virtual display retains the receiver-requested refresh rate. A
        // software encoder may independently cap the media cadence at 30 fps.
        var requested=display.RequestedRefreshRate;
        if(fps!=requested&&(requested!=60||fps!=30)||fps<=0||90000%fps!=0)
            throw new InvalidDataException("编码帧率与浏览器协商不一致。");
        return new(width,height,fps,(uint)(90000/fps));
    }
}
#endif
