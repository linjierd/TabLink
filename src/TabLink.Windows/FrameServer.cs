using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TabLink.Core;

namespace TabLink.Windows;

internal sealed class FrameServer : IAsyncDisposable
{
    public const int Port = 27183;
    public const int MaxPacket = 8 * 1024 * 1024;
    readonly Func<byte[]>? capture;
    readonly Func<CancellationToken,IAsyncEnumerable<VideoPacket>>? video;
    readonly Action<InputMessage>? input;
    readonly Action releaseInput;
    readonly Func<InputDesktopStatus> queryDesktop;
    readonly NetworkSessionOptions? network;
    readonly Func<TabletDisplayProfile,CancellationToken,Task>? prepare;
    readonly int fps;
    readonly CancellationTokenSource cts = new();
    readonly TcpListener listener = new(IPAddress.Loopback, Port);
    readonly object lifecycleLock = new();
    readonly object statisticsLock = new();
    readonly FrameSendPerformance sendPerformance = new();
    Task? run;
    Task? disposeTask;
    TcpClient? currentClient;
    CancellationTokenSource? currentSession;
    long framesSent, bytesSent, presentedFrames, sessionFramesStarted;
    int presentedWidth, presentedHeight;
    bool clientConnected;
    bool capturePaused;
    DateTime? captureRecoveryStartedUtc;
    TabletDisplayProfile? clientDisplayProfile;
    double clientReportedFps;
    string? clientDecoder;
    DateTime? lastFrameUtc, lastPresentedUtc;
    DateTime? lastSubmittedUtc;
    long submittedFrames;
    readonly Queue<long> recentVideoPts=new();
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public int ListeningPort => listener.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : 0;
    public string? NetworkConnectionUri => network?.ConnectionUri(Token);
    public event Action<string>? Status;
    public long FramesSent { get { lock (statisticsLock) return framesSent; } }
    public long BytesSent { get { lock (statisticsLock) return bytesSent; } }
    public bool ClientConnected { get { lock (statisticsLock) return clientConnected; } }
    public DateTime? LastFrameUtc { get { lock (statisticsLock) return lastFrameUtc; } }
    public DateTime? LastPresentedUtc { get { lock (statisticsLock) return lastPresentedUtc; } }
    public DateTime? LastSubmittedUtc { get { lock(statisticsLock)return lastSubmittedUtc; } }
    public long SubmittedFrames { get { lock(statisticsLock)return submittedFrames; } }
    public bool SubmissionEvidenceOnly {get{lock(statisticsLock)return presentedFrames==0&&submittedFrames>0;}}
    public DateTime? LastClientProgressUtc {get{lock(statisticsLock)return lastPresentedUtc is {} p && (lastSubmittedUtc is null||p>=lastSubmittedUtc)?p:lastSubmittedUtc;}}
    public long PresentedFrames { get { lock (statisticsLock) return presentedFrames; } }
    public int PresentedWidth { get { lock (statisticsLock) return presentedWidth; } }
    public int PresentedHeight { get { lock (statisticsLock) return presentedHeight; } }
    public event Action? FramePresented;
    public event Action<TabletDisplayProfile>? DisplayProfileChanged;
    public TabletDisplayProfile? ClientDisplayProfile {get{lock(statisticsLock)return clientDisplayProfile;}}
    public double ClientReportedFps {get{lock(statisticsLock)return clientReportedFps;}}
    public string? ClientDecoder {get{lock(statisticsLock)return clientDecoder;}}
    public bool CapturePaused { get { lock(statisticsLock) return capturePaused; } }
    public DateTime? CaptureRecoveryStartedUtc { get { lock(statisticsLock) return captureRecoveryStartedUtc; } }
    // Fixed cumulative counters permit interval deltas without per-frame logs.
    // A new ConnectionId invalidates deltas across reconnects.
    public FrameSendPerformanceSnapshot SendPerformance { get { lock(statisticsLock) return sendPerformance.Snapshot(); } }

    public FrameServer(Func<byte[]> capture, Action<InputMessage>? input, Action releaseInput, int fps = 20,
        Func<InputDesktopStatus>? queryDesktop=null, int listenPort=Port)
    {
        this.capture = capture; this.input = input; this.releaseInput = releaseInput;
        this.fps = Math.Clamp(fps, 1, 30); this.queryDesktop=queryDesktop??InputDesktopAvailability.Query;
        listener = new TcpListener(IPAddress.Loopback, listenPort);
    }

    public FrameServer(Func<CancellationToken,IAsyncEnumerable<VideoPacket>> video,Action<InputMessage>? input,Action releaseInput,
        Func<InputDesktopStatus>? queryDesktop=null, int listenPort=Port)
    {
        this.video=video;this.input=input;this.releaseInput=releaseInput;
        this.queryDesktop=queryDesktop??InputDesktopAvailability.Query;
        listener = new TcpListener(IPAddress.Loopback, listenPort);
    }

    public FrameServer(NetworkSessionOptions options, Func<TabletDisplayProfile,CancellationToken,Task> prepare,
        Func<CancellationToken,IAsyncEnumerable<VideoPacket>> video, Action<InputMessage>? input, Action releaseInput,
        Func<InputDesktopStatus>? queryDesktop=null)
        : this(video,input,releaseInput,queryDesktop)
    {
        network=options??throw new ArgumentNullException(nameof(options));
        this.prepare=prepare??throw new ArgumentNullException(nameof(prepare));
        listener=new TcpListener(options.LocalAddress,options.Port);
    }

    public void RequestReconnect()
    {
        CancellationTokenSource? session;
        TcpClient? client;
        lock(lifecycleLock){session=currentSession;client=currentClient;}
        // Cancellation/Schannel can synchronously invoke continuations which
        // acquire lifecycleLock. Never cancel or close under that lock.
        try { session?.Cancel(); } catch(ObjectDisposedException) { }
        client?.Close();
    }

    public void Start()
    {
        lock (lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(disposeTask is not null, this);
            if (run is not null) throw new InvalidOperationException("画面服务已经启动");
            listener.Start(1);
            run = Task.Run(() => RunAsync(cts.Token));
        }
    }

    void SetConnected(bool connected)
    {
        lock (statisticsLock)
        {
            clientConnected = connected;
            capturePaused=false;captureRecoveryStartedUtc=null;
            // Rendering evidence is scoped to one authenticated connection. A
            // disconnected or newly reconnected client must never inherit it.
            sessionFramesStarted = presentedFrames = 0;
            presentedWidth = presentedHeight = 0;
            lastPresentedUtc = null;
            lastSubmittedUtc=null;submittedFrames=0;recentVideoPts.Clear();
            clientDisplayProfile=null;clientReportedFps=0;clientDecoder=null;
            sendPerformance.Reset(connected);
        }
    }

    async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var trustedSession=false;
            try
            {
                // Disposal wakes this accept before closing the listener, to
                // avoid orphaning a socket in Windows' AcceptEx/close race.
                using var client = await listener.AcceptTcpClientAsync();
                ct.ThrowIfCancellationRequested();
                client.NoDelay = true;
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                using var stream = network is null ? (Stream)client.GetStream() : new SslStream(client.GetStream(),false);
                // Publish only after stream ownership is established: closing a
                // TcpClient concurrently with GetStream can lose its stream.
                ct.ThrowIfCancellationRequested();
                lock(lifecycleLock){currentClient=client;currentSession=sessionCts;}
                using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                authTimeout.CancelAfter(network?.AuthenticationTimeout??TimeSpan.FromSeconds(4));
                if(stream is SslStream tls)
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate=network!.Certificate,
                        ClientCertificateRequired=false,
                        EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,
                        CertificateRevocationCheckMode=System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                        AllowRenegotiation=false
                    },authTimeout.Token);
                var helloPacket = await ReadPacketAsync(stream, 8192, authTimeout.Token);
                if (helloPacket.Type != 0x10) throw new InvalidDataException("缺少握手");
                var hello = JsonSerializer.Deserialize<Hello>(helloPacket.Payload, JsonOptions);
                if (hello?.Protocol != 1 || hello.Token is null ||
                    !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello.Token), Encoding.UTF8.GetBytes(Token)))
                    throw new InvalidDataException("连接令牌无效");

                TabletDisplayProfile? initialProfile=null;
                if(network is not null)
                {
                    var profilePacket=await ReadPacketAsync(stream,8192,authTimeout.Token);
                    if(profilePacket.Type!=0x13)throw new InvalidDataException("缺少平板显示参数");
                    initialProfile=TabletDisplayProfile.Parse(Encoding.UTF8.GetString(profilePacket.Payload));
                    try { await PrepareAsync(initialProfile,stream,sessionCts.Token); }
                    catch(Exception error) when(error is not (OperationCanceledException or TimeoutException))
                    {
                        // Only an authenticated, validated-profile client receives
                        // preparation errors. Never serialize exception details.
                        using var errorDeadline=CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                        errorDeadline.CancelAfter(TimeSpan.FromSeconds(1));
                        try { await WritePacketAsync(stream,0x03,Encoding.UTF8.GetBytes("{\"message\":\"电脑未能准备副屏，请查看电脑连接记录。\"}"),errorDeadline.Token); }
                        catch(Exception sendError) when(sendError is IOException or OperationCanceledException or SocketException) { }
                        throw new IOException("电脑未能准备副屏。",error);
                    }
                }
                SetConnected(true);
                trustedSession=true;
                if(initialProfile is not null)lock(statisticsLock)clientDisplayProfile=initialProfile;
                Status?.Invoke("平板已连接，正在传输副屏");
                await WritePacketAsync(stream, 2, Encoding.UTF8.GetBytes(network is null?
                    "{\"message\":\"USB 副屏已连接\"}":"{\"message\":\"加密副屏已连接\"}"), sessionCts.Token);
                var receive = ReadInputsAsync(stream, sessionCts.Token);
                // A peer disconnect/reconnect must also interrupt a blocked encoder.
                var cancelOnReceiveEnd=receive.ContinueWith(_=>sessionCts.Cancel(),CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
                try
                {
                    if(video is not null)
                    {
                        await using var packets=video(sessionCts.Token).GetAsyncEnumerator(sessionCts.Token);
                        while(true)
                        {
                            var moveStarted=Stopwatch.GetTimestamp();
                            bool hasPacket;
                            try { hasPacket=await packets.MoveNextAsync(); }
                            finally
                            {
                                var movedAt=Stopwatch.GetTimestamp();
                                lock(statisticsLock)sendPerformance.RecordSourceMove(movedAt-moveStarted);
                            }
                            if(!hasPacket)break;
                            var packet=packets.Current;
                            if(receive.IsCompleted)break;
                            if(packet.Payload.Length is <1 or >MaxPacket)throw new InvalidDataException("无效视频包");
                            if(packet.CapturePaused is bool paused)
                            {
                                if(packet.Type!=0x02||packet.IsFrame)throw new InvalidDataException("无效的本机采集状态包");
                                SetCapturePaused(paused);
                            }
                            using var sendTimeout=CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                            sendTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                            if(packet.IsFrame)lock(statisticsLock)
                            {
                                sessionFramesStarted++;
                                if(packet.Type==0x21&&packet.Payload.Length>=9)
                                {
                                    recentVideoPts.Enqueue(BinaryPrimitives.ReadInt64BigEndian(packet.Payload));
                                    while(recentVideoPts.Count>512)recentVideoPts.Dequeue();
                                }
                            }
                            var writeStarted=Stopwatch.GetTimestamp();
                            await WritePacketAsync(stream,packet.Type,packet.Payload,sendTimeout.Token);
                            var writtenAt=Stopwatch.GetTimestamp();
                            lock(statisticsLock)
                            {
                                sendPerformance.RecordWrite(writeStarted,writtenAt,packet.Payload.Length,packet.IsFrame);
                                if(packet.IsFrame){framesSent++;lastFrameUtc=DateTime.UtcNow;}
                                bytesSent+=packet.Payload.Length;
                            }
                        }
                        if(!receive.IsCompleted)throw new IOException("视频编码流已停止。");
                    }
                    else while (!sessionCts.IsCancellationRequested && !receive.IsCompleted)
                    {
                        var began = Environment.TickCount64;
                        var moveStarted=Stopwatch.GetTimestamp();
                        var frame = capture!();
                        var movedAt=Stopwatch.GetTimestamp();
                        lock(statisticsLock)sendPerformance.RecordSourceMove(movedAt-moveStarted);
                        if (frame.Length == 0 || frame.Length > MaxPacket) throw new InvalidDataException("图像帧大小无效，请降低分辨率");
                        using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                        sendTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                        // A fast loopback receiver can acknowledge before the
                        // write continuation runs, so include the in-flight frame.
                        lock (statisticsLock) sessionFramesStarted++;
                        var writeStarted=Stopwatch.GetTimestamp();
                        await WritePacketAsync(stream, 1, frame, sendTimeout.Token);
                        var writtenAt=Stopwatch.GetTimestamp();
                        lock (statisticsLock)
                        {
                            sendPerformance.RecordWrite(writeStarted,writtenAt,frame.Length,true);
                            framesSent++;
                            bytesSent += frame.Length;
                            lastFrameUtc = DateTime.UtcNow;
                        }
                        var delay = 1000 / fps - (int)(Environment.TickCount64 - began);
                        if (delay > 0) await Task.Delay(delay, sessionCts.Token);
                    }
                    // Surface protocol and input failures to the normal session
                    // error path rather than silently waiting for a reconnect.
                    await receive;
                }
                finally
                {
                    sessionCts.Cancel(); client.Close();
                    try { await receive; } catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or InvalidDataException or JsonException) { }
                    await cancelOnReceiveEnd;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or SocketException or InvalidDataException or JsonException or OperationCanceledException or System.Runtime.InteropServices.ExternalException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException or NotSupportedException or AuthenticationException or ArgumentException)
            { if (!ct.IsCancellationRequested) Status?.Invoke(network is null?"连接中断，等待平板重连：" + e.Message:$"加密连接已结束，等待平板重新连接（{e.GetType().Name}, 0x{(e.GetBaseException() is System.ComponentModel.Win32Exception native?native.NativeErrorCode:e.HResult):X8}）。"); }
            finally
            {
                lock(lifecycleLock){currentClient=null;currentSession=null;}
                SetConnected(false);
                if(trustedSession)releaseInput();
            }
        }
    }

    async Task PrepareAsync(TabletDisplayProfile profile,Stream stream,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(network!.PreparationTimeout);
        var task=Task.Run(()=>prepare!(profile,timeout.Token),timeout.Token);
        // UI preparation must honor cancellation before display mutations. Never
        // wait indefinitely for a non-cooperative callback after timeout/disposal.
        _=task.ContinueWith(t=>{_ = t.Exception;},CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        try
        {
            while(!task.IsCompleted)
            {
                await WritePacketAsync(stream,0x02,Encoding.UTF8.GetBytes("{\"message\":\"正在准备副屏画面\"}"),timeout.Token);
                await Task.WhenAny(task,Task.Delay(TimeSpan.FromSeconds(1),timeout.Token));
                timeout.Token.ThrowIfCancellationRequested();
            }
            await task.WaitAsync(timeout.Token);
        }
        finally {timeout.Cancel();}
    }

    async Task ReadInputsAsync(Stream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var packet = await ReadPacketAsync(stream, 8192, ct);
            if(packet.Type==0x13)
            {
                var profile=TabletDisplayProfile.Parse(Encoding.UTF8.GetString(packet.Payload));
                lock(statisticsLock)clientDisplayProfile=profile;
                DisplayProfileChanged?.Invoke(profile);continue;
            }
            if(packet.Type==0x14)
            {
                var progress=JsonSerializer.Deserialize<SubmittedAck>(packet.Payload,JsonOptions);
                if(progress is null||progress.Evidence!="render-submitted"||progress.Frames<1||progress.PtsUs<0||!double.IsFinite(progress.Fps)||progress.Fps is <0 or >300)
                    throw new InvalidDataException("无效的解码提交进度");
                lock(statisticsLock)
                {
                    if(clientDisplayProfile is null||progress.Width!=clientDisplayProfile.Width||progress.Height!=clientDisplayProfile.Height||progress.Frames>sessionFramesStarted||!recentVideoPts.Contains(progress.PtsUs))
                        throw new InvalidDataException("解码进度不属于本次连接发送的画面");
                    if(progress.Frames>submittedFrames)
                    {
                        submittedFrames=progress.Frames;lastSubmittedUtc=DateTime.UtcNow;
                        clientReportedFps=progress.Fps;clientDecoder=progress.Decoder is {Length:<=160}?progress.Decoder:null;
                    }
                }
                // This weaker evidence is deliberately separate from physical
                // presentation; never fire FramePresented or increment its count.
                continue;
            }
            if (packet.Type == 0x12)
            {
                var ack = JsonSerializer.Deserialize<PresentedAck>(packet.Payload, JsonOptions);
                if (ack is null || ack.Kind != "frame-presented" || ack.Sequence < 1 ||
                    ack.Width < 1 || ack.Height < 1 || ack.Width > 8192 || ack.Height > 8192)
                    throw new InvalidDataException("无效的画面确认");
                var advanced = false;
                lock (statisticsLock)
                {
                    if (ack.Sequence > sessionFramesStarted)
                        throw new InvalidDataException("画面确认超过本次连接发送的帧数");
                    if (ack.Sequence > presentedFrames)
                    {
                        presentedFrames = ack.Sequence;
                        presentedWidth = ack.Width;
                        presentedHeight = ack.Height;
                        lastPresentedUtc = DateTime.UtcNow;
                        clientReportedFps=double.IsFinite(ack.Fps)?Math.Clamp(ack.Fps,0,300):0;
                        clientDecoder=ack.Decoder is {Length:<=160}?ack.Decoder:null;
                        advanced = true;
                    }
                }
                // Repeated or reordered ACKs do not refresh the watchdog.
                if (advanced) FramePresented?.Invoke();
                continue;
            }
            if (packet.Type != 0x11) throw new InvalidDataException("不支持的输入包");
            var message = JsonSerializer.Deserialize<InputMessage>(packet.Payload, JsonOptions);
            if (message is null || !double.IsFinite(message.X) || !double.IsFinite(message.Y) ||
                message.X < 0 || message.X > 1 || message.Y < 0 || message.Y > 1 ||
                !double.IsFinite(message.Delta)) throw new InvalidDataException("输入坐标无效");
            if (message.Kind is not ("down" or "move" or "up" or "scroll")) throw new InvalidDataException("输入类型无效");
            if(CapturePaused||!queryDesktop().IsAvailable) { releaseInput();continue; }
            input?.Invoke(message);
        }
    }

    void SetCapturePaused(bool paused)
    {
        bool changed;
        lock(statisticsLock)
        {
            changed=capturePaused!=paused;
            if(paused&&!capturePaused)captureRecoveryStartedUtc=DateTime.UtcNow;
            if(!paused)captureRecoveryStartedUtc=null;
            capturePaused=paused;
        }
        if(paused&&changed)releaseInput();
    }

    internal static async Task<(byte Type, byte[] Payload)> ReadPacketAsync(Stream stream, int limit, CancellationToken ct)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, ct);
        var size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
        if (size == 0 || size > limit) throw new InvalidDataException("无效的数据包长度");
        var payload = new byte[(int)size];
        await stream.ReadExactlyAsync(payload, ct);
        return (header[0], payload);
    }

    internal static async Task WritePacketAsync(Stream stream, byte type, byte[] data, CancellationToken ct)
    {
        var header = new byte[5]; header[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)data.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(data, ct);
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycleLock) return new ValueTask(disposeTask ??= Task.Run(DisposeCoreAsync));
    }

    async Task DisposeCoreAsync()
    {
        cts.Cancel();
        RequestReconnect();
        try
        {
            if (run is not null)
            {
                if(!run.IsCompleted)
                {
                    // AcceptEx can complete at the same instant Stop closes its
                    // listening socket, leaving an accepted socket orphaned.
                    // Wake our own listener so the normal using scope owns and
                    // closes that socket before Stop. No authentication occurs.
                    using var wake=new TcpClient();
                    using var wakeDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    try {await wake.ConnectAsync(network?.LocalAddress??IPAddress.Loopback,network?.Port??Port,wakeDeadline.Token);}
                    catch(Exception ex) when(ex is SocketException or IOException or OperationCanceledException)
                    {listener.Stop();}
                }
                try { await run.ConfigureAwait(false); }
                catch (Exception e) when (e is OperationCanceledException or SocketException) { }
            }
        }
        finally
        {
            listener.Stop();
            SetConnected(false);
            try { releaseInput(); }
            finally { network?.Dispose(); cts.Dispose(); }
        }
    }
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    record Hello(int Protocol, string Token);
    record SubmittedAck(string Evidence,long Frames,long PtsUs,int Width,int Height,double Fps=0,string? Decoder=null);
    record PresentedAck(string Kind, long Sequence, int Width, int Height,double Fps=0,string? Codec=null,string? Decoder=null,long DroppedFrames=0);
}

internal record InputMessage(string Kind, double X, double Y, double Delta = 0);
internal record VideoPacket(byte Type,byte[] Payload,bool IsFrame,bool? CapturePaused=null);

internal sealed record PipelineTimingSnapshot(long Count,double TotalMs,double MaxMs,long SlowCount,long[] Histogram);
internal sealed record FrameSendPerformanceSnapshot(Guid ConnectionId,DateTime? StartedUtc,double ElapsedMs,
    long CompletedFrames,long PayloadBytesWritten,long BurstFrameGaps,double BurstThresholdMs,double SlowThresholdMs,
    double?[] HistogramUpperBoundsMs,PipelineTimingSnapshot SourceMove,PipelineTimingSnapshot PacketWrite,
    PipelineTimingSnapshot FrameSendGap);

// Caller owns statisticsLock. No timers, logging, array growth or allocations
// occur on the packet path; arrays are copied only when a snapshot is requested.
internal sealed class FrameSendPerformance
{
    const double BurstThresholdMs=2,SlowThresholdMs=16.667;
    static readonly double[] BoundsMs=[0.25,1,2,5,10,16.667,33.334,100];
    readonly Timing sourceMove=new(),packetWrite=new(),frameGap=new();
    Guid connectionId;
    DateTime? startedUtc;
    long startedAt,lastFrameAt,completedFrames,payloadBytesWritten,burstFrameGaps;

    internal void Reset(bool connected)
    {
        connectionId=connected?Guid.NewGuid():Guid.Empty;
        startedUtc=connected?DateTime.UtcNow:null;
        startedAt=connected?Stopwatch.GetTimestamp():0;
        lastFrameAt=completedFrames=payloadBytesWritten=burstFrameGaps=0;
        sourceMove.Reset();packetWrite.Reset();frameGap.Reset();
    }

    internal void RecordSourceMove(long elapsedTicks)=>sourceMove.Add(elapsedTicks);

    internal void RecordWrite(long began,long ended,int bytes,bool isFrame)
    {
        packetWrite.Add(ended-began);
        payloadBytesWritten+=bytes;
        if(!isFrame)return;
        completedFrames++;
        if(lastFrameAt!=0)
        {
            var gap=ended-lastFrameAt;
            frameGap.Add(gap);
            if(ToMs(gap)<BurstThresholdMs)burstFrameGaps++;
        }
        lastFrameAt=ended;
    }

    internal FrameSendPerformanceSnapshot Snapshot()=>new(connectionId,startedUtc,
        startedAt==0?0:ToMs(Stopwatch.GetTimestamp()-startedAt),completedFrames,payloadBytesWritten,burstFrameGaps,
        BurstThresholdMs,SlowThresholdMs,[..BoundsMs.Select(value=>(double?)value),null],
        sourceMove.Snapshot(),packetWrite.Snapshot(),frameGap.Snapshot());

    static double ToMs(long ticks)=>(double)ticks*1000/Stopwatch.Frequency;

    sealed class Timing
    {
        readonly long[] histogram=new long[BoundsMs.Length+1];
        long count,totalTicks,maxTicks,slowCount;
        internal void Add(long ticks)
        {
            ticks=Math.Max(0,ticks);count++;totalTicks+=ticks;maxTicks=Math.Max(maxTicks,ticks);
            var ms=ToMs(ticks);
            if(ms>=SlowThresholdMs)slowCount++;
            var bucket=0;
            while(bucket<BoundsMs.Length&&ms>=BoundsMs[bucket])bucket++;
            histogram[bucket]++;
        }
        internal void Reset(){count=totalTicks=maxTicks=slowCount=0;Array.Clear(histogram);}
        internal PipelineTimingSnapshot Snapshot()=>new(count,ToMs(totalTicks),ToMs(maxTicks),slowCount,(long[])histogram.Clone());
    }
}
