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

internal readonly record struct RegistrationLifetimeTransfer(TimeSpan RemainingLifetime);

internal sealed class FrameServer : IAsyncDisposable
{
    internal const int ProtocolVersion=1;
    internal const string RenderSubmittedFeature="render-submitted-v1";
    internal const string DecoderRefreshFeature="decoder-refresh-v1";
    internal const string ReceiverFeedbackFeature="receiver-feedback-v1";
    internal const string AdaptiveVideoFeature="adaptive-video-v1";
    internal static readonly TimeSpan TelemetryFreshnessWindow=TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan RegistrationLifetime=TimeSpan.FromMinutes(5);
    public const int Port = 27183;
    public const int MaxPacket = 8 * 1024 * 1024;
    readonly Func<byte[]>? capture;
    readonly Func<CancellationToken,IAsyncEnumerable<VideoPacket>>? video;
    readonly Action<InputMessage>? input;
    readonly Action releaseInput;
    readonly Func<InputDesktopStatus> queryDesktop;
    readonly Func<IPEndPoint,CancellationToken,Task> wakeListener;
    readonly NetworkSessionOptions? network;
    readonly Func<TabletDisplayProfile,CancellationToken,Task>? prepare;
    AdaptiveVideoSession? quality;
    readonly int fps;
    readonly CancellationTokenSource cts = new();
    readonly TcpListener listener = new(IPAddress.Loopback, Port);
    readonly object lifecycleLock = new();
    readonly object statisticsLock = new();
    readonly object registrationLock = new();
    readonly Func<long> monotonicMilliseconds;
    Func<TimeSpan?>? registrationRemainingLimit;
    readonly FrameSendPerformance sendPerformance = new();
    readonly TaskCompletionSource firstAcceptStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task? run;
    Task? disposeTask;
    IPEndPoint? boundEndpoint;
    TcpClient? currentClient;
    CancellationTokenSource? currentSession;
    long framesSent, bytesSent, presentedFrames, sessionFramesStarted;
    int presentedWidth, presentedHeight;
    bool clientConnected,hasAuthenticatedClient;
    bool registrationTokenConsumed;
    string registrationToken;
    long registrationDeadlineMilliseconds;
    string? authenticatedDeviceId;
    bool capturePaused;
    DateTime? captureRecoveryStartedUtc;
    TabletDisplayProfile? clientDisplayProfile;
    double clientSubmittedFps,clientPresentedFps;
    string? clientDecoder;
    DateTime? lastFrameUtc, lastPresentedUtc;
    DateTime? lastSubmittedUtc;
    long submittedFrames;
    ReceiverFeedbackSnapshot? receiverFeedback;
    DateTime? lastReceiverFeedbackUtc;
    readonly Queue<long> recentVideoPts=new();
    public string Token { get { lock(registrationLock)return registrationToken; } }
    public int ListeningPort {get{lock(lifecycleLock)return boundEndpoint?.Port??0;}}
    internal Task FirstAcceptStarted=>firstAcceptStarted.Task;
    public string? NetworkConnectionUri
    {
        get
        {
            if(network is null)return null;
            lock(registrationLock)
            {
                if(network.TrustedDevices is not null&&!RegistrationAvailableLocked())return null;
                return network.ConnectionUri(registrationToken);
            }
        }
    }
    public bool RegistrationAvailable
    {
        get{lock(registrationLock)return network?.TrustedDevices is not null&&RegistrationAvailableLocked();}
    }
    public bool CanRefreshRegistration
    {
        get{lock(lifecycleLock)return network?.TrustedDevices is not null&&currentClient is null&&disposeTask is null;}
    }
    public event Action<string>? Status;
    public long FramesSent { get { lock (statisticsLock) return framesSent; } }
    public long BytesSent { get { lock (statisticsLock) return bytesSent; } }
    public bool ClientConnected { get { lock (statisticsLock) return clientConnected; } }
    public bool HasAuthenticatedClient { get { lock (statisticsLock) return hasAuthenticatedClient; } }
    public string? AuthenticatedDeviceId { get { lock (statisticsLock) return authenticatedDeviceId; } }
    public DateTime? LastFrameUtc { get { lock (statisticsLock) return lastFrameUtc; } }
    public DateTime? LastPresentedUtc { get { lock (statisticsLock) return lastPresentedUtc; } }
    public DateTime? LastSubmittedUtc { get { lock(statisticsLock)return lastSubmittedUtc; } }
    public long SubmittedFrames { get { lock(statisticsLock)return submittedFrames; } }
    public bool HasRecentSubmission {get{lock(statisticsLock)return IsFresh(lastSubmittedUtc,DateTime.UtcNow);}}
    public bool HasRecentPresentation {get{lock(statisticsLock)return IsFresh(lastPresentedUtc,DateTime.UtcNow);}}
    public bool SubmissionEvidenceOnly {get{lock(statisticsLock){var now=DateTime.UtcNow;return IsFresh(lastSubmittedUtc,now)&&!IsFresh(lastPresentedUtc,now);}}}
    public DateTime? LastClientProgressUtc {get{lock(statisticsLock)return lastPresentedUtc is {} p && (lastSubmittedUtc is null||p>=lastSubmittedUtc)?p:lastSubmittedUtc;}}
    public long PresentedFrames { get { lock (statisticsLock) return presentedFrames; } }
    public int PresentedWidth { get { lock (statisticsLock) return presentedWidth; } }
    public int PresentedHeight { get { lock (statisticsLock) return presentedHeight; } }
    public event Action? FramePresented;
    public event Action<TabletDisplayProfile>? DisplayProfileChanged;
    public TabletDisplayProfile? ClientDisplayProfile {get{lock(statisticsLock)return clientDisplayProfile;}}
    public double ClientSubmittedFps {get{lock(statisticsLock)return IsFresh(lastSubmittedUtc,DateTime.UtcNow)?clientSubmittedFps:0;}}
    public double ClientPresentedFps {get{lock(statisticsLock)return IsFresh(lastPresentedUtc,DateTime.UtcNow)?clientPresentedFps:0;}}
    // Compatibility projection for older status callers. New UI must name the
    // two measurements explicitly rather than treating decoder submission as display.
    public double ClientReportedFps {get{lock(statisticsLock){var now=DateTime.UtcNow;return IsFresh(lastPresentedUtc,now)?clientPresentedFps:IsFresh(lastSubmittedUtc,now)?clientSubmittedFps:0;}}}
    public string? ClientDecoder {get{lock(statisticsLock)return clientDecoder;}}
    public ReceiverFeedbackSnapshot? ReceiverFeedback {get{lock(statisticsLock)return receiverFeedback;}}
    public bool HasRecentReceiverFeedback {get{lock(statisticsLock)return IsFresh(lastReceiverFeedbackUtc,DateTime.UtcNow);}}
    public bool CapturePaused { get { lock(statisticsLock) return capturePaused; } }
    public DateTime? CaptureRecoveryStartedUtc { get { lock(statisticsLock) return captureRecoveryStartedUtc; } }
    // Fixed cumulative counters permit interval deltas without per-frame logs.
    // A new ConnectionId invalidates deltas across reconnects.
    public FrameSendPerformanceSnapshot SendPerformance { get { lock(statisticsLock) return sendPerformance.Snapshot(); } }

    public FrameServer(Func<byte[]> capture, Action<InputMessage>? input, Action releaseInput, int fps = 20,
        Func<InputDesktopStatus>? queryDesktop=null, int listenPort=Port,AdaptiveVideoSession? quality=null,
        Func<IPEndPoint,CancellationToken,Task>? wakeListener=null,Func<long>? monotonicMilliseconds=null)
    {
        this.capture = capture; this.input = input; this.releaseInput = releaseInput;
        this.fps = Math.Clamp(fps, 1, 30); this.queryDesktop=queryDesktop??InputDesktopAvailability.Query;
        this.quality=quality;this.wakeListener=wakeListener??ConnectWakeAsync;
        this.monotonicMilliseconds=monotonicMilliseconds??(()=>Environment.TickCount64);
        registrationToken=NewRegistrationToken();
        registrationDeadlineMilliseconds=RegistrationDeadline(this.monotonicMilliseconds());
        listener = new TcpListener(IPAddress.Loopback, listenPort);
    }

    public FrameServer(Func<CancellationToken,IAsyncEnumerable<VideoPacket>> video,Action<InputMessage>? input,Action releaseInput,
        Func<InputDesktopStatus>? queryDesktop=null, int listenPort=Port,AdaptiveVideoSession? quality=null,
        Func<IPEndPoint,CancellationToken,Task>? wakeListener=null,Func<long>? monotonicMilliseconds=null)
    {
        this.video=video;this.input=input;this.releaseInput=releaseInput;
        this.queryDesktop=queryDesktop??InputDesktopAvailability.Query;this.quality=quality;
        this.wakeListener=wakeListener??ConnectWakeAsync;
        this.monotonicMilliseconds=monotonicMilliseconds??(()=>Environment.TickCount64);
        registrationToken=NewRegistrationToken();
        registrationDeadlineMilliseconds=RegistrationDeadline(this.monotonicMilliseconds());
        listener = new TcpListener(IPAddress.Loopback, listenPort);
    }

    public FrameServer(NetworkSessionOptions options, Func<TabletDisplayProfile,CancellationToken,Task> prepare,
        Func<CancellationToken,IAsyncEnumerable<VideoPacket>> video, Action<InputMessage>? input, Action releaseInput,
        Func<InputDesktopStatus>? queryDesktop=null,AdaptiveVideoSession? quality=null,Func<long>? monotonicMilliseconds=null,
        TimeSpan? registrationLifetime=null,Func<TimeSpan?>? registrationRemainingLimit=null)
        : this(video,input,releaseInput,queryDesktop,quality:quality,monotonicMilliseconds:monotonicMilliseconds)
    {
        network=options??throw new ArgumentNullException(nameof(options));
        this.prepare=prepare??throw new ArgumentNullException(nameof(prepare));
        this.registrationRemainingLimit=registrationRemainingLimit;
        if(registrationLifetime is {} cap)
        {
            if(cap<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(registrationLifetime));
            lock(registrationLock)
                registrationDeadlineMilliseconds=RegistrationDeadline(this.monotonicMilliseconds(),cap);
        }
        listener=new TcpListener(options.LocalAddress,options.Port);
    }

    public string RefreshRegistrationUri()
    {
        if(network?.TrustedDevices is null)throw new InvalidOperationException("当前监听不支持可信设备注册。");
        lock(lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(disposeTask is not null,this);
            if(currentClient is not null)throw new InvalidOperationException("设备正在认证或传输，暂时不能生成新的配对二维码。");
            lock(registrationLock)
            {
                registrationToken=NewRegistrationToken();
                registrationTokenConsumed=false;
                registrationRemainingLimit=null;
                registrationDeadlineMilliseconds=RegistrationDeadline(monotonicMilliseconds());
                return network.ConnectionUri(registrationToken);
            }
        }
    }

    /// <summary>
    /// Atomically retires the current one-time registration bearer before a
    /// listener is moved to another local binding. If a client consumed or the
    /// clock expired the bearer first, the caller must not publish a replacement
    /// without another explicit user request.
    /// </summary>
    public bool TryRetireRegistration()=>TryRetireRegistration(out _);

    public bool TryRetireRegistration(out RegistrationLifetimeTransfer transfer)
    {
        transfer=default;
        if(network?.TrustedDevices is null)return false;
        lock(registrationLock)
        {
            if(!TryGetRegistrationRemainingLocked(out var remainingMilliseconds))return false;
            transfer=new RegistrationLifetimeTransfer(TimeSpan.FromMilliseconds(remainingMilliseconds));
            registrationTokenConsumed=true;
            return true;
        }
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

    internal void AttachQualitySession(AdaptiveVideoSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock(statisticsLock)
        {
            if(clientConnected)
                throw new InvalidOperationException("画质控制器必须在媒体连接开始前绑定。");
            quality=session;
        }
    }

    public void Start()
    {
        lock (lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(disposeTask is not null, this);
            if (run is not null) throw new InvalidOperationException("画面服务已经启动");
            listener.Start(1);
            if(listener.LocalEndpoint is not IPEndPoint endpoint)
            {
                listener.Stop();
                throw new InvalidOperationException("无法确认画面服务的本地端点");
            }
            boundEndpoint=new IPEndPoint(endpoint.Address,endpoint.Port);
            run = Task.Run(() => RunAsync(cts.Token));
        }
    }

    void SetConnected(bool connected,string? deviceId=null)
    {
        lock (statisticsLock)
        {
            clientConnected = connected;
            if(connected)hasAuthenticatedClient=true;
            // Keep the last authenticated durable identity for this listener so
            // revocation can also collect a VDD retained during a bounded
            // reconnect gap. A later trusted authentication publishes its own
            // identity before any display preparation.
            if(connected&&deviceId is not null)authenticatedDeviceId=deviceId;
            capturePaused=false;captureRecoveryStartedUtc=null;
            // Rendering evidence is scoped to one authenticated connection. A
            // disconnected or newly reconnected client must never inherit it.
            sessionFramesStarted = presentedFrames = 0;
            presentedWidth = presentedHeight = 0;
            lastPresentedUtc = null;
            lastSubmittedUtc=null;submittedFrames=0;recentVideoPts.Clear();
            receiverFeedback=null;lastReceiverFeedbackUtc=null;
            clientDisplayProfile=null;clientSubmittedFps=clientPresentedFps=0;clientDecoder=null;
            sendPerformance.Reset(connected);
        }
    }

    void PublishAuthenticatedDevice(string deviceId)
    {
        lock(statisticsLock)authenticatedDeviceId=deviceId;
    }

    async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var trustedSession=false;
            AdaptiveVideoSession? sessionQuality=null;
            try
            {
                // Disposal wakes this accept before closing the listener, to
                // avoid orphaning a socket in Windows' AcceptEx/close race.
                var pendingAccept=listener.AcceptTcpClientAsync();
                firstAcceptStarted.TrySetResult();
                using var client = await pendingAccept;
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
                string[] negotiatedFeatures;
                string? trustedDeviceId=null;
                if(helloPacket.Type==0x10)
                {
                    var hello=TrustedPairingProtocol.ParseInitialHello(helloPacket.Payload);
                    var requestsTrust=hello.Features.Contains(TrustedPairingProtocol.Feature,StringComparer.Ordinal);
                    if(hello.Protocol!=ProtocolVersion||!TryConsumeRegistrationToken(hello.Token,requestsTrust))
                        throw new InvalidDataException("连接令牌无效、已经使用或已经过期，请在电脑端重新生成。");
                    // A persistent native listener can remain alive indefinitely
                    // for signed reconnects. Its QR bearer must therefore be
                    // single-use even when an older client does not request
                    // durable trust. Ephemeral legacy/test listeners retain the
                    // historical reconnect behavior.
                    if(requestsTrust)
                    {
                        var registry=network?.TrustedDevices??throw new InvalidDataException("电脑端不支持可信设备注册。");
                        if(hello.DeviceId is null||hello.DevicePublicKey is null)
                            throw new InvalidDataException("可信设备注册信息不完整。");
                        var enrolled=registry.Register(hello.DeviceId,hello.DevicePublicKey,hello.DeviceName);
                        trustedDeviceId=enrolled.DeviceId;
                        PublishAuthenticatedDevice(trustedDeviceId);
                        await WritePacketAsync(stream,TrustedPairingProtocol.TrustEstablishedPacket,
                            JsonSerializer.SerializeToUtf8Bytes(new{protocol=ProtocolVersion,feature=TrustedPairingProtocol.Feature,
                                hostId=registry.HostId,deviceId=enrolled.DeviceId}),authTimeout.Token);
                    }
                    else if(hello.DeviceId is not null||hello.DevicePublicKey is not null||hello.DeviceName is not null)
                        throw new InvalidDataException("客户端未协商可信设备注册。");
                    negotiatedFeatures=NegotiateFeatures(hello.Features,requestsTrust);
                }
                else if(helloPacket.Type==TrustedPairingProtocol.TrustedHelloPacket)
                {
                    var registry=network?.TrustedDevices??throw new InvalidDataException("电脑端不支持可信设备重连。");
                    var hello=TrustedPairingProtocol.ParseTrustedHello(helloPacket.Payload);
                    if(hello.Protocol!=ProtocolVersion||!hello.Features.Contains(TrustedPairingProtocol.Feature,StringComparer.Ordinal)||
                        !registry.Contains(hello.DeviceId))throw new InvalidDataException("可信设备未登记或已撤销。");
                    var challenge=TrustedPairingProtocol.CreateChallenge();
                    await WritePacketAsync(stream,TrustedPairingProtocol.TrustedChallengePacket,
                        JsonSerializer.SerializeToUtf8Bytes(new{protocol=ProtocolVersion,feature=TrustedPairingProtocol.Feature,
                            hostId=registry.HostId,deviceId=hello.DeviceId,challenge=Convert.ToBase64String(challenge)}),authTimeout.Token);
                    var proofPacket=await ReadPacketAsync(stream,4096,authTimeout.Token);
                    if(proofPacket.Type!=TrustedPairingProtocol.TrustedProofPacket)throw new InvalidDataException("缺少可信设备签名。");
                    var proof=TrustedPairingProtocol.ParseTrustedProof(proofPacket.Payload);
                    if(proof.DeviceId!=hello.DeviceId||!registry.Verify(hello.DeviceId,challenge,proof.Signature))
                        throw new InvalidDataException("可信设备签名无效。");
                    trustedDeviceId=hello.DeviceId;
                    PublishAuthenticatedDevice(trustedDeviceId);
                    negotiatedFeatures=NegotiateFeatures(hello.Features,true);
                }
                else throw new InvalidDataException("缺少握手");

                TabletDisplayProfile? initialProfile=null;
                if(network is not null)
                {
                    // Revocation can race the end of signature verification.
                    // Publish the authenticated identity first so the UI can
                    // cancel this exact session, then recheck the registry before
                    // accepting a display profile or invoking any VDD mutation.
                    if(trustedDeviceId is not null&&network.TrustedDevices is {} registry&&
                        !registry.Contains(trustedDeviceId))
                        throw new InvalidDataException("可信设备已撤销。");
                    var profilePacket=await ReadPacketAsync(stream,8192,authTimeout.Token);
                    if(profilePacket.Type!=0x13)throw new InvalidDataException("缺少平板显示参数");
                    initialProfile=TabletDisplayProfile.Parse(Encoding.UTF8.GetString(profilePacket.Payload));
                    if(trustedDeviceId is not null&&network.TrustedDevices is {} currentRegistry&&
                        !currentRegistry.Contains(trustedDeviceId))
                        throw new InvalidDataException("可信设备已撤销。");
                    try { await PrepareAsync(initialProfile,stream,negotiatedFeatures,sessionCts.Token); }
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
                lock(statisticsLock)sessionQuality=quality;
                SetConnected(true,trustedDeviceId);
                sessionQuality?.BeginConnection(negotiatedFeatures.Contains(ReceiverFeedbackFeature,StringComparer.Ordinal)&&
                    negotiatedFeatures.Contains(AdaptiveVideoFeature,StringComparer.Ordinal));
                trustedSession=true;
                if(initialProfile is not null)lock(statisticsLock)clientDisplayProfile=initialProfile;
                Status?.Invoke("平板已连接，正在传输副屏");
                await WritePacketAsync(stream,0x02,StatusPayload(network is null?
                    "USB 副屏已连接":"加密副屏已连接",negotiatedFeatures),sessionCts.Token);
                var decoderRefresh=negotiatedFeatures.Contains(DecoderRefreshFeature,StringComparer.Ordinal)
                    ?new DecoderRefreshGate():null;
                var receive = ReadInputsAsync(stream,
                    negotiatedFeatures.Contains(RenderSubmittedFeature,StringComparer.Ordinal),decoderRefresh,
                    negotiatedFeatures.Contains(ReceiverFeedbackFeature,StringComparer.Ordinal),sessionCts.Token);
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
                            if(decoderRefresh is not null)
                            {
                                if(!decoderRefresh.TryPrepare(packet,out var preparedPacket))continue;
                                packet=preparedPacket;
                            }
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
                sessionQuality?.EndConnection();
                if(trustedSession)releaseInput();
            }
        }
    }

    async Task PrepareAsync(TabletDisplayProfile profile,Stream stream,IReadOnlyList<string> negotiatedFeatures,CancellationToken ct)
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
            // Always acknowledge the negotiated protocol before media begins,
            // even when preparation completes synchronously. Protocol v1 peers
            // ignore the additional fields and continue to read the message.
            await WritePacketAsync(stream,0x02,StatusPayload("正在准备副屏画面",negotiatedFeatures),timeout.Token);
            while(!task.IsCompleted)
            {
                await Task.WhenAny(task,Task.Delay(TimeSpan.FromSeconds(1),timeout.Token));
                timeout.Token.ThrowIfCancellationRequested();
                if(!task.IsCompleted)
                    await WritePacketAsync(stream,0x02,StatusPayload("正在准备副屏画面",negotiatedFeatures),timeout.Token);
            }
            await task.WaitAsync(timeout.Token);
        }
        finally {timeout.Cancel();}
    }

    static string[] NegotiateFeatures(IEnumerable<string>? requestedFeatures,bool trustedDeviceAuthenticated=false)
    {
        if(requestedFeatures is null)return [];
        var requested=requestedFeatures.ToHashSet(StringComparer.Ordinal);
        var negotiated=new List<string>(5);
        if(requested.Contains(RenderSubmittedFeature))negotiated.Add(RenderSubmittedFeature);
        if(requested.Contains(DecoderRefreshFeature))negotiated.Add(DecoderRefreshFeature);
        if(requested.Contains(ReceiverFeedbackFeature))negotiated.Add(ReceiverFeedbackFeature);
        if(requested.Contains(AdaptiveVideoFeature))negotiated.Add(AdaptiveVideoFeature);
        if(trustedDeviceAuthenticated&&requested.Contains(TrustedPairingProtocol.Feature))negotiated.Add(TrustedPairingProtocol.Feature);
        return [.. negotiated];
    }

    static byte[] StatusPayload(string message,IReadOnlyList<string> negotiatedFeatures)=>
        JsonSerializer.SerializeToUtf8Bytes(new{message,protocol=ProtocolVersion,features=negotiatedFeatures});

    static bool IsFresh(DateTime? observedUtc,DateTime nowUtc)=>
        observedUtc is {} observed&&observed>nowUtc-TelemetryFreshnessWindow;

    static bool IsSessionToken(string value)=>value.Length==64&&
        value.All(character=>character is >= '0' and <= '9' or >= 'a' and <= 'f');

    bool TryConsumeRegistrationToken(string candidate,bool requestsTrust)
    {
        if(!IsSessionToken(candidate))return false;
        lock(registrationLock)
        {
            var persistent=network?.TrustedDevices is not null;
            if(persistent&&!RegistrationAvailableLocked())return false;
            if(!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate),
                    Encoding.ASCII.GetBytes(registrationToken)))return false;
            if(persistent||requestsTrust)registrationTokenConsumed=true;
            return true;
        }
    }

    bool RegistrationAvailableLocked()=>TryGetRegistrationRemainingLocked(out _);

    bool TryGetRegistrationRemainingLocked(out long remainingMilliseconds)
    {
        remainingMilliseconds=0;
        var now=monotonicMilliseconds();
        if(registrationTokenConsumed||now>=registrationDeadlineMilliseconds)return false;
        remainingMilliseconds=registrationDeadlineMilliseconds-now;
        if(registrationRemainingLimit is not {} limit)return true;
        TimeSpan? external;
        try{external=limit();}
        catch{return false;}
        if(external is not {} cap)return false;
        var capMilliseconds=(long)Math.Floor(cap.TotalMilliseconds);
        if(capMilliseconds<=0)return false;
        remainingMilliseconds=Math.Min(remainingMilliseconds,capMilliseconds);
        return remainingMilliseconds>0;
    }

    static string NewRegistrationToken()=>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    static long RegistrationDeadline(long now)=>RegistrationDeadline(now,RegistrationLifetime);

    static long RegistrationDeadline(long now,TimeSpan requestedLifetime)
    {
        var lifetime=(long)Math.Floor(Math.Min(RegistrationLifetime.TotalMilliseconds,
            requestedLifetime.TotalMilliseconds));
        return now>long.MaxValue-lifetime?long.MaxValue:now+lifetime;
    }

    async Task ReadInputsAsync(Stream stream,bool renderSubmittedNegotiated,DecoderRefreshGate? decoderRefresh,
        bool receiverFeedbackNegotiated,CancellationToken ct)
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
            if(packet.Type==0x15)
            {
                if(decoderRefresh is null)throw new InvalidDataException("客户端未协商解码器刷新功能");
                if(packet.Payload.Length!=sizeof(long))throw new InvalidDataException("无效的解码器刷新请求");
                var generation=BinaryPrimitives.ReadInt64BigEndian(packet.Payload);
                if(generation<=0)throw new InvalidDataException("无效的解码器刷新代次");
                decoderRefresh.Request(generation);continue;
            }
            if(packet.Type==0x14)
            {
                if(!renderSubmittedNegotiated)
                    throw new InvalidDataException("客户端未协商解码提交进度功能");
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
                        clientSubmittedFps=progress.Fps;clientDecoder=progress.Decoder is {Length:<=160}?progress.Decoder:null;
                    }
                }
                // This weaker evidence is deliberately separate from physical
                // presentation; never fire FramePresented or increment its count.
                continue;
            }
            if(packet.Type==0x16)
            {
                if(!receiverFeedbackNegotiated)
                    throw new InvalidDataException("客户端未协商接收端反馈功能");
                var report=JsonSerializer.Deserialize<ReceiverFeedbackAck>(packet.Payload,JsonOptions);
                if(report is null||report.Kind!="receiver-feedback"||report.Sequence<1||report.DecoderEpoch<0||
                    report.RecoveryEpoch<0||report.ReceivedVideoFrames<0||report.ReceivedVideoBytes<0||
                    report.SubmittedFrames<0||report.PresentedFrames<0||report.PresentedFrames>report.SubmittedFrames||
                    report.SubmittedFrames>report.ReceivedVideoFrames||report.QueueCapacity is <1 or >64||
                    report.QueueDepth<0||report.QueueDepth>report.QueueCapacity||report.QueueHighWaterMark<0||
                    report.QueueHighWaterMark>report.QueueCapacity||report.InputDroppedFrames<0||
                    report.BackpressureTimeouts<0||report.OverflowEvents<0||
                    report.OverflowDrops<0||report.ExpiredDrops<0||report.AwaitingKeyFrameDrops<0||
                    report.RenderDrops<0||report.DecoderFallbacks<0)
                    throw new InvalidDataException("无效的接收端反馈");
                lock(statisticsLock)
                {
                    if(report.ReceivedVideoFrames>sessionFramesStarted)
                        throw new InvalidDataException("接收端反馈超过本次连接发送的画面");
                    if(receiverFeedback is {} prior)
                    {
                        if(report.Sequence<=prior.Sequence||report.DecoderEpoch<prior.DecoderEpoch||
                            report.RecoveryEpoch<prior.RecoveryEpoch||report.ReceivedVideoFrames<prior.ReceivedVideoFrames||
                            report.ReceivedVideoBytes<prior.ReceivedVideoBytes||report.SubmittedFrames<prior.SubmittedFrames||
                            report.PresentedFrames<prior.PresentedFrames||report.DecoderFallbacks<prior.DecoderFallbacks||
                            report.QueueHighWaterMark<prior.QueueHighWaterMark||
                            report.InputDroppedFrames<prior.InputDroppedFrames||
                            report.BackpressureTimeouts<prior.BackpressureTimeouts)
                            throw new InvalidDataException("接收端反馈计数发生回退");
                        if(report.DecoderEpoch==prior.DecoderEpoch&&
                            (report.OverflowEvents<prior.OverflowEvents||report.OverflowDrops<prior.OverflowDrops||
                             report.ExpiredDrops<prior.ExpiredDrops||
                             report.AwaitingKeyFrameDrops<prior.AwaitingKeyFrameDrops||report.RenderDrops<prior.RenderDrops))
                            throw new InvalidDataException("当前解码代次的接收端反馈计数发生回退");
                    }
                    var now=DateTime.UtcNow;
                    receiverFeedback=new(report.Sequence,report.DecoderEpoch,report.RecoveryEpoch,
                        report.ReceivedVideoFrames,report.ReceivedVideoBytes,report.SubmittedFrames,
                        report.PresentedFrames,report.QueueDepth,report.QueueCapacity,report.QueueHighWaterMark,
                        report.InputDroppedFrames,report.BackpressureTimeouts,report.OverflowEvents,
                        report.OverflowDrops,report.ExpiredDrops,report.AwaitingKeyFrameDrops,report.RenderDrops,
                        report.DecoderFallbacks,report.AwaitingKeyFrame,now);
                    lastReceiverFeedbackUtc=now;
                }
                // Receiver feedback is an adaptive-control input only. It does
                // not refresh submitted/presented evidence or the display lease.
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
                        clientPresentedFps=double.IsFinite(ack.Fps)?Math.Clamp(ack.Fps,0,300):0;
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
                    using var wakeDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    IPEndPoint? wakeEndpoint;
                    lock(lifecycleLock)wakeEndpoint=boundEndpoint;
                    if(wakeEndpoint is null)listener.Stop();
                    else try
                    {
                        await wakeListener(wakeEndpoint,wakeDeadline.Token);
                    }
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

    static async Task ConnectWakeAsync(IPEndPoint endpoint,CancellationToken ct)
    {
        using var wake=new TcpClient(endpoint.AddressFamily);
        await wake.ConnectAsync(endpoint.Address,endpoint.Port,ct);
    }

    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    record SubmittedAck(string Evidence,long Frames,long PtsUs,int Width,int Height,double Fps=0,string? Decoder=null);
    record PresentedAck(string Kind, long Sequence, int Width, int Height,double Fps=0,string? Codec=null,string? Decoder=null,long DroppedFrames=0);
    record ReceiverFeedbackAck(string Kind,long Sequence,long DecoderEpoch,long RecoveryEpoch,
        long ReceivedVideoFrames,long ReceivedVideoBytes,long SubmittedFrames,long PresentedFrames,
        int QueueDepth,int QueueCapacity,int QueueHighWaterMark,long InputDroppedFrames,
        long BackpressureTimeouts,long OverflowEvents,long OverflowDrops,long ExpiredDrops,
        long AwaitingKeyFrameDrops,long RenderDrops,long DecoderFallbacks,bool AwaitingKeyFrame);
}

internal record InputMessage(string Kind, double X, double Y, double Delta = 0);
internal record VideoPacket(byte Type,byte[] Payload,bool IsFrame,bool? CapturePaused=null,int? SourceFps=null);

internal sealed record ReceiverFeedbackSnapshot(long Sequence,long DecoderEpoch,long RecoveryEpoch,
    long ReceivedVideoFrames,long ReceivedVideoBytes,long SubmittedFrames,long PresentedFrames,
    int QueueDepth,int QueueCapacity,int QueueHighWaterMark,long InputDroppedFrames,
    long BackpressureTimeouts,long OverflowEvents,long OverflowDrops,long ExpiredDrops,
    long AwaitingKeyFrameDrops,long RenderDrops,long DecoderFallbacks,bool AwaitingKeyFrame,
    DateTime ReceivedUtc);

internal enum DecoderRefreshRequestResult { Accepted, Duplicate, Coalesced, RateLimited }

// Connection-scoped PLI-style gate for a decoder that has already been rebuilt.
// It never restarts capture or replays an old picture: dependent frames are
// withheld until the running encoder produces a fresh IDR.
internal sealed class DecoderRefreshGate
{
    const int MaxConfigurationBytes=128*1024;
    const int MaxParameterSetBytes=64*1024;
    const double TokenCapacity=2;
    readonly object gate=new();
    readonly Func<long> timestamp;
    readonly long timestampFrequency;
    byte[]? configuration;
    long highestGeneration,pendingGeneration,completedGeneration;
    long tokenTimestamp;
    double tokens=TokenCapacity;
    bool pending;

    internal DecoderRefreshGate(Func<long>? timestamp=null,long timestampFrequency=0)
    {
        this.timestamp=timestamp??Stopwatch.GetTimestamp;
        this.timestampFrequency=timestampFrequency>0?timestampFrequency:Stopwatch.Frequency;
        tokenTimestamp=this.timestamp();
    }

    internal bool IsPending {get{lock(gate)return pending;}}
    internal long CompletedGeneration {get{lock(gate)return completedGeneration;}}

    // A two-request burst accommodates an immediate decoder fallback; one token
    // per second thereafter bounds an authenticated peer without penalising an
    // idempotent retry or a newer generation coalesced into the outstanding PLI.
    internal DecoderRefreshRequestResult Request(long generation)
    {
        if(generation<=0)throw new ArgumentOutOfRangeException(nameof(generation));
        lock(gate)
        {
            if(generation<=highestGeneration)return DecoderRefreshRequestResult.Duplicate;
            if(pending)
            {
                highestGeneration=pendingGeneration=generation;
                return DecoderRefreshRequestResult.Coalesced;
            }
            RefillTokens();
            if(tokens<1)return DecoderRefreshRequestResult.RateLimited;
            tokens-=1;
            highestGeneration=pendingGeneration=generation;
            pending=true;
            return DecoderRefreshRequestResult.Accepted;
        }
    }

    // Returns false only for a dependent video frame suppressed while waiting
    // for the next fresh IDR. Status/configuration packets continue normally.
    internal bool TryPrepare(VideoPacket packet,out VideoPacket prepared)
    {
        prepared=packet;
        if(packet.Type==0x20)
        {
            lock(gate)configuration=packet.Payload.ToArray();
            return true;
        }
        if(packet.Type!=0x21||!packet.IsFrame)return true;

        byte[]? currentConfiguration;
        lock(gate)
        {
            if(!pending)return true;
            currentConfiguration=configuration;
        }
        if(packet.Payload.Length<13)throw new InvalidDataException("无效的 H.264 访问单元");
        var accessUnit=packet.Payload.AsSpan(8);
        if(!ContainsNal(accessUnit,5))return false;
        if(currentConfiguration is null)throw new InvalidDataException("解码器刷新缺少 H.264 配置");

        var (sps,pps)=ParseConfiguration(currentConfiguration);
        var addSps=!ContainsNal(accessUnit,7);
        var addPps=!ContainsNal(accessUnit,8);
        var extra=checked((addSps?sps.Length:0)+(addPps?pps.Length:0));
        if(extra>0)
        {
            var length=checked(packet.Payload.Length+extra);
            if(length>FrameServer.MaxPacket)throw new InvalidDataException("补齐参数集后的 H.264 包超过限制");
            var payload=new byte[length];
            packet.Payload.AsSpan(0,8).CopyTo(payload);
            var offset=8;
            if(addSps){sps.CopyTo(payload,offset);offset+=sps.Length;}
            if(addPps){pps.CopyTo(payload,offset);offset+=pps.Length;}
            accessUnit.CopyTo(payload.AsSpan(offset));
            prepared=packet with {Payload=payload};
        }
        lock(gate)
        {
            // Any newer generation coalesced before this point is also satisfied
            // by this still-unsent fresh IDR.
            completedGeneration=Math.Max(completedGeneration,pendingGeneration);
            pending=false;
        }
        return true;
    }

    void RefillTokens()
    {
        var now=timestamp();
        if(now<tokenTimestamp){tokenTimestamp=now;return;}
        var elapsed=(double)(now-tokenTimestamp)/timestampFrequency;
        if(elapsed>0)tokens=Math.Min(TokenCapacity,tokens+elapsed);
        tokenTimestamp=now;
    }

    static (byte[] Sps,byte[] Pps) ParseConfiguration(byte[] bytes)
    {
        if(bytes.Length is <2 or >MaxConfigurationBytes)
            throw new InvalidDataException("无效的 H.264 配置长度");
        try
        {
            using var json=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=8});
            var root=json.RootElement;
            if(root.GetProperty("codec").GetString()!="video/avc")throw new InvalidDataException("解码器刷新只支持 H.264");
            var sps=Convert.FromBase64String(root.GetProperty("csd0").GetString()??"");
            var pps=Convert.FromBase64String(root.GetProperty("csd1").GetString()??"");
            if(sps.Length is <4 or >MaxParameterSetBytes||pps.Length is <4 or >MaxParameterSetBytes||
                !ContainsNal(sps,7)||!ContainsNal(pps,8))throw new InvalidDataException("H.264 配置缺少有效 SPS/PPS");
            return(sps,pps);
        }
        catch(Exception ex) when(ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {throw new InvalidDataException("无法解析解码器刷新配置",ex);}
    }

    internal static bool ContainsNal(ReadOnlySpan<byte> bytes,int expectedType)
    {
        for(var index=0;index+3<bytes.Length;index++)
        {
            int prefix;
            if(bytes[index]!=0||bytes[index+1]!=0)continue;
            if(bytes[index+2]==1)prefix=3;
            else if(index+3<bytes.Length&&bytes[index+2]==0&&bytes[index+3]==1)prefix=4;
            else continue;
            var header=index+prefix;
            if(header<bytes.Length&&(bytes[header]&0x80)==0&&(bytes[header]&31)==expectedType)return true;
        }
        return false;
    }
}

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
