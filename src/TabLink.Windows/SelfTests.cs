using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TabLink.Core;

namespace TabLink.Windows;

internal static class SelfTests
{
    public static async Task RunAsync()
    {
        var log = new List<string>();
        var nic=new NetworkInterfaceChoice(IPAddress.Parse("192.168.8.20"),"Test Wi-Fi",24,NetworkInterfaceKind.WiFi,null,"test",1);
        var ruleA=NetworkFirewall.GetRuleName(nic,@"C:\TabLink\TabLink.exe",27184);
        var ruleB=NetworkFirewall.GetRuleName(nic,@"C:\TabLink\TabLink.exe",27186);
        var ruleUdp=NetworkFirewall.GetRuleName(nic,@"C:\TabLink\TabLink.exe",27200,"UDP");
        var discoveryRule=NetworkFirewall.GetRuleName(nic,@"C:\TabLink\TabLink.exe",27193,"UDP",acceptLocalBroadcast:true);
        Check(new[]{ruleA,ruleB,ruleUdp,discoveryRule}.Distinct().Count()==4,"different device endpoints share a firewall identity");
        var script=NetworkFirewall.BuildCreateScript(nic,@"C:\TabLink\TabLink.exe",ruleUdp,27200,"UDP");
        Check(script.Contains("-Protocol UDP -LocalPort 27200")&&script.Contains("-LocalAddress '192.168.8.20'")&&script.Contains("-RemoteAddress '192.168.8.0/24'")&&script.Contains("-InterfaceAlias 'Test Wi-Fi'"),"media firewall scope is not exact");
        var discoveryScript=NetworkFirewall.BuildCreateScript(nic,@"C:\TabLink\TabLink.exe",discoveryRule,27193,"UDP",acceptLocalBroadcast:true);
        Check(discoveryScript.Contains("-Protocol UDP -LocalPort 27193")&&discoveryScript.Contains("-LocalAddress 'Any'")&&
            discoveryScript.Contains("-RemoteAddress '192.168.8.0/24'")&&discoveryScript.Contains("-InterfaceAlias 'Test Wi-Fi'"),
            "discovery firewall does not admit local broadcast destinations within the selected interface/subnet scope");
        log.Add("PASS independent native/media/discovery firewall identities and selected-interface/subnet scope (script only)");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        // Only synthetic bytes and fake input callbacks are used. No desktop
        // capture, input injection, ADB, driver or display change is performed.
        var captures = 0;
        var releases = 0;
        var acknowledgements = 0;
        var inputs = new ConcurrentQueue<InputMessage>();
        await using (var server = new FrameServer(
            () => { Interlocked.Increment(ref captures); return [0xff, 0xd8, 0xff, 0xd9]; },
            inputs.Enqueue, () => Interlocked.Increment(ref releases), 10,()=>new(InputDesktopState.Available,"synthetic-test"), listenPort: 0))
        {
            server.FramePresented += () => Interlocked.Increment(ref acknowledgements);
            server.Start();
            using (var unauthorized = new TcpClient())
            {
                await unauthorized.ConnectAsync(IPAddress.Loopback, server.ListeningPort, ct);
                await SendHelloAsync(unauthorized, "wrong", ct);
                await ExpectClosedAsync(unauthorized.GetStream(), ct);
                Check(captures == 0 && !server.ClientConnected, "unauthorized client triggered a capture");
                log.Add("PASS unauthorized token: no capture / no frame");
            }

            using (var valid = await ConnectAsync(server, ct))
            {
                var stream = valid.GetStream();
                var frame = await FrameServer.ReadPacketAsync(stream, 8192, ct);
                Check(frame.Type == 1 && frame.Payload.SequenceEqual(new byte[] { 0xff, 0xd8, 0xff, 0xd9 }), "image framing");
                Check(server.LastPresentedUtc is null && server.PresentedFrames == 0,
                    "sent frames were mistaken for rendered frames");
                log.Add("PASS authorized handshake and framed image without fabricated render evidence");

                await SendAckAsync(stream, 1, ct);
                await WaitForAsync(() => server.PresentedFrames == 1, ct);
                var firstAck = server.LastPresentedUtc;
                Check(firstAck is not null && server.PresentedWidth == 1280 && server.PresentedHeight == 800,
                    "first presentation ACK was not recorded");
                await SendAckAsync(stream, 1, ct);
                await FrameServer.WritePacketAsync(stream, 0x11,
                    Encoding.UTF8.GetBytes("{\"kind\":\"move\",\"x\":0.25,\"y\":0.75}"), ct);
                await WaitForAsync(() => inputs.Count == 1, ct);
                Check(server.LastPresentedUtc == firstAck && acknowledgements == 1,
                    "duplicate presentation ACK refreshed liveness");
                log.Add("PASS repeated ACK does not refresh rendering health; valid input follows it");

                await FrameServer.ReadPacketAsync(stream, 8192, ct);
                await SendAckAsync(stream, 2, ct);
                await WaitForAsync(() => server.PresentedFrames == 2 && acknowledgements == 2, ct);
                log.Add("PASS advancing ACK updates the rendered frame count");
            }
            await WaitForAsync(() => !server.ClientConnected, ct);
            Check(server.LastPresentedUtc is null && server.PresentedFrames == 0 &&
                server.PresentedWidth == 0 && server.PresentedHeight == 0 && releases > 0,
                "disconnect retained stale rendering evidence or failed to release input");
            log.Add("PASS disconnect clears rendering evidence and releases input");

            using (var reconnected = await ConnectAsync(server, ct))
            {
                var stream = reconnected.GetStream();
                await FrameServer.ReadPacketAsync(stream, 8192, ct);
                await SendAckAsync(stream, 1, ct);
                await WaitForAsync(() => server.PresentedFrames == 1 && acknowledgements == 3, ct);
                log.Add("PASS authenticated reconnect starts a fresh ACK sequence");
                var priorReleases = releases;
                await SendAckAsync(stream, long.MaxValue, ct);
                await ExpectClosedAsync(stream, ct);
                await WaitForAsync(() => !server.ClientConnected && releases > priorReleases, ct);
                Check(server.LastPresentedUtc is null, "impossible ACK left a healthy session");
                log.Add("PASS ACK beyond this connection's sent frames fails closed");
            }

            using (var malformedInput = await ConnectAsync(server, ct))
            {
                var stream = malformedInput.GetStream();
                await FrameServer.WritePacketAsync(stream, 0x11,
                    Encoding.UTF8.GetBytes("{\"kind\":\"down\",\"x\":2,\"y\":0.5}"), ct);
                await ExpectClosedAsync(stream, ct);
                await WaitForAsync(() => !server.ClientConnected, ct);
                Check(inputs.Count == 1, "out-of-bounds input reached the input callback");
                log.Add("PASS invalid input is rejected before dispatch");
            }

            using (var connectedAtShutdown = await ConnectAsync(server, ct))
            {
                var previousReleases = releases;
                var firstDispose = server.DisposeAsync().AsTask();
                var secondDispose = server.DisposeAsync().AsTask();
                await Task.WhenAll(firstDispose, secondDispose).WaitAsync(ct);
                Check(!server.ClientConnected && server.LastPresentedUtc is null && releases > previousReleases,
                    "shutdown did not invalidate the session and release input");
                await ExpectClosedAsync(connectedAtShutdown.GetStream(), ct);
                log.Add("PASS concurrent repeated disposal closes an active session safely");
            }
        }

        await TestVideoProtocolAsync(log, ct);

        foreach (var size in new[] { 0u, uint.MaxValue })
        {
            var header = new byte[5];
            header[0] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), size);
            using var malformed = new MemoryStream(header);
            try
            {
                await FrameServer.ReadPacketAsync(malformed, 8192, ct);
                throw new Exception("invalid packet size accepted");
            }
            catch (InvalidDataException) { log.Add($"PASS packet size {size} rejected before allocation"); }
        }
        using (var truncated = new MemoryStream(new byte[] { 1, 0, 0, 0, 10, 1 }))
        {
            try
            {
                await FrameServer.ReadPacketAsync(truncated, 8192, ct);
                throw new Exception("truncated packet accepted");
            }
            catch (EndOfStreamException) { log.Add("PASS truncated packet fails closed"); }
        }
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "selftest-result.txt"), log);
    }

    public static async Task<IReadOnlyList<string>> RunCaptureRecoveryProtocolAsync()
    {
        var log=new List<string>();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct=deadline.Token;
        var available=1;var attempts=0;var disposed=0;var releases=0;var inputCount=0;var profileCount=0;var acks=0;
        InputDesktopStatus Desktop()=>Volatile.Read(ref available)==1
            ?new(InputDesktopState.Available,"simulated-normal"):new(InputDesktopState.Unavailable,"simulated-secure-switch");
        async IAsyncEnumerable<VideoPacket> Source([EnumeratorCancellation]CancellationToken token)
        {
            Interlocked.Increment(ref attempts);
            try
            {
                yield return new(0x20,VideoConfiguration,false);
                yield return new(0x21,[0,0,0,0,0,0,0,0,0,0,0,1,0x65],true);
                await Task.Delay(Timeout.Infinite,token);
            }
            finally {Interlocked.Increment(ref disposed);}
        }
        void Validate(){Check(Desktop().IsAvailable,"protected topology was queried during simulated secure desktop");}
        await using var server=new FrameServer(token=>CaptureRecovery.StreamAsync(Source,Validate,Desktop,token),
            _=>Interlocked.Increment(ref inputCount),()=>Interlocked.Increment(ref releases),Desktop,listenPort:0);
        server.DisplayProfileChanged+=_=>Interlocked.Increment(ref profileCount);
        server.FramePresented+=()=>Interlocked.Increment(ref acks);
        server.Start();
        using var client=await ConnectAsync(server,ct);
        var stream=client.GetStream();
        var endpoint=client.Client.LocalEndPoint;
        await ReadUntilAsync(stream,0x20,ct);
        await ReadUntilAsync(stream,0x21,ct);
        await SendVideoAckAsync(stream,1,90,"simulated-decoder",ct);
        await WaitForAsync(()=>server.PresentedFrames==1&&server.FramesSent==1,ct);
        var firstAck=server.LastPresentedUtc;var firstFrame=server.LastFrameUtc;
        Volatile.Write(ref available,0);
        await ReadUntilAsync(stream,0x02,ct);
        await WaitForAsync(()=>server.CapturePaused,ct);
        var pauseStarted=server.CaptureRecoveryStartedUtc;
        Check(pauseStarted is not null&&disposed==1&&attempts==1&&releases>0,"pause did not stop source/release input");
        var pauseClock=System.Diagnostics.Stopwatch.StartNew();
        await FrameServer.WritePacketAsync(stream,0x11,Encoding.UTF8.GetBytes("{\"kind\":\"down\",\"x\":0.5,\"y\":0.5}"),ct);
        await FrameServer.WritePacketAsync(stream,0x12,Encoding.UTF8.GetBytes("{\"kind\":\"frame-presented\",\"sequence\":1,\"width\":1200,\"height\":1920,\"capturePaused\":false}"),ct);
        await FrameServer.WritePacketAsync(stream,0x13,DisplayProfileBytes(),ct);
        await WaitForAsync(()=>profileCount==1,ct);
        Check(inputCount==0&&server.CapturePaused&&acks==1,"remote input/ACK changed trusted pause state");
        var heartbeatCount=0;
        while(pauseClock.Elapsed<TimeSpan.FromSeconds(22))
        {
            var packet=await FrameServer.ReadPacketAsync(stream,8192,ct);
            Check(packet.Type==0x02&&!packet.Payload.AsSpan().IsEmpty,"pause emitted a fake frame/config");
            using var json=JsonDocument.Parse(packet.Payload);
            Check(json.RootElement.GetProperty("capturePaused").GetBoolean(),"pause heartbeat missing state");
            heartbeatCount++;
            Check(server.ClientConnected&&server.FramesSent==1&&server.PresentedFrames==1&&
                server.LastFrameUtc==firstFrame&&server.LastPresentedUtc==firstAck&&acks==1&&
                server.CaptureRecoveryStartedUtc==pauseStarted&&attempts==1,
                "pause heartbeat fabricated progress, restarted capture, or reset recovery timer");
        }
        Check(heartbeatCount>=20,"pause heartbeat cadence too slow");
        log.Add("PASS same authenticated TCP stays open for >22s of simulated secure desktop; host-only 0x02 heartbeats do not change frame/ACK health");
        log.Add("PASS paused touch is dropped/released and a remote ACK capturePaused field cannot clear trusted pause");
        Volatile.Write(ref available,1);
        await ReadUntilAsync(stream,0x20,ct);
        var resume=await ReadUntilAsync(stream,0x02,ct);
        using(var json=JsonDocument.Parse(resume))Check(!json.RootElement.GetProperty("capturePaused").GetBoolean(),"missing resume state");
        await ReadUntilAsync(stream,0x21,ct);
        await SendVideoAckAsync(stream,2,90,"simulated-decoder",ct);
        await WaitForAsync(()=>server.PresentedFrames==2&&server.FramesSent==2,ct);
        Check(client.Client.LocalEndPoint!.Equals(endpoint)&&server.ClientConnected&&!server.CapturePaused&&
            server.CaptureRecoveryStartedUtc is null&&attempts==2&&server.LastPresentedUtc>firstAck&&acks==2,
            "resume reset connection/frame sequence or omitted new codec config");
        var resumedAck=server.LastPresentedUtc;
        await FrameServer.WritePacketAsync(stream,0x12,Encoding.UTF8.GetBytes("{\"kind\":\"frame-presented\",\"sequence\":2,\"width\":1200,\"height\":1920,\"capturePaused\":true}"),ct);
        await FrameServer.WritePacketAsync(stream,0x11,Encoding.UTF8.GetBytes("{\"kind\":\"move\",\"x\":0.5,\"y\":0.5}"),ct);
        await WaitForAsync(()=>inputCount==1,ct);
        Check(!server.CapturePaused&&server.LastPresentedUtc==resumedAck&&acks==2,"remote duplicate ACK set pause or refreshed health");
        log.Add("PASS same TCP resumes with fresh 0x20, capturePaused:false, next frame ACK sequence 2 and normal input; remote ACK cannot set pause");
        return log;
    }

    static async Task TestVideoProtocolAsync(List<string> log, CancellationToken ct)
    {
        var packets = Channel.CreateUnbounded<VideoPacket>();
        var profiles = new ConcurrentQueue<TabletDisplayProfile>();
        var inputs = new ConcurrentQueue<InputMessage>();
        var acknowledgements = 0;
        await using var server = new FrameServer(token => ControlledVideoAsync(packets.Reader, token), inputs.Enqueue, () => { },()=>new(InputDesktopState.Available,"synthetic-test"), listenPort: 0);
        server.DisplayProfileChanged += profiles.Enqueue;
        server.FramePresented += () => Interlocked.Increment(ref acknowledgements);
        server.Start();
        using (var connected = await ConnectAsync(server, ct))
        {
            var stream = connected.GetStream();
            var config = await ReadUntilAsync(stream, 0x20, ct);
            Check(JsonDocument.Parse(config).RootElement.GetProperty("codec").GetString() == "video/avc", "video configuration framing");
            Check(server.FramesSent == 0 && server.PresentedFrames == 0 && server.LastFrameUtc is null && server.LastPresentedUtc is null,
                "codec configuration was counted as a video or rendered frame");
            log.Add("PASS H.264 codec configuration 0x20 is not counted as a video or rendered frame");

            await FrameServer.WritePacketAsync(stream, 0x13, DisplayProfileBytes(), ct);
            await WaitForAsync(() => profiles.Count == 1, ct);
            Check(server.ClientDisplayProfile is { Width: 1200, Height: 1920, RequestedRefreshRate: 90 }, "portrait display profile not published");
            await FrameServer.WritePacketAsync(stream, 0x13, DisplayProfileBytes(true), ct);
            await WaitForAsync(() => profiles.Count == 2, ct);
            Check(server.ClientDisplayProfile is { Width: 1920, Height: 1200, Rotation: 1, RequestedRefreshRate: 90 }, "rotation display profile not published");
            Check(server.LastPresentedUtc is null && acknowledgements == 0, "display profile fabricated render evidence");
            log.Add("PASS display profile 0x13 parses portrait and landscape updates without refreshing frame health");

            await packets.Writer.WriteAsync(new(0x21, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0x65], true), ct);
            var accessUnit = await ReadUntilAsync(stream, 0x21, ct);
            Check(accessUnit.Length == 13, "H.264 access unit framing changed");
            await WaitForAsync(() => server.FramesSent == 1, ct);
            Check(server.PresentedFrames == 0 && server.LastPresentedUtc is null, "sent access unit counted as rendered before ACK");
            await SendVideoAckAsync(stream, 1, 89.8, "c2.unisoc.avc.decoder", ct);
            await WaitForAsync(() => acknowledgements == 1, ct);
            Check(server.PresentedFrames == 1 && server.PresentedWidth == 1200 && server.PresentedHeight == 1920 &&
                Math.Abs(server.ClientPresentedFps - 89.8) < 0.001 && server.ClientSubmittedFps == 0 && server.ClientDecoder == "c2.unisoc.avc.decoder",
                "video presentation ACK metadata was not recorded");
            var firstPresented = server.LastPresentedUtc;
            log.Add("PASS access unit 0x21 counts once; presentation metadata is recorded only after a valid ACK");

            await SendVideoAckAsync(stream, 1, 1, "duplicate-must-not-replace", ct);
            await FrameServer.WritePacketAsync(stream, 0x11, Encoding.UTF8.GetBytes("{\"kind\":\"move\",\"x\":0.5,\"y\":0.5}"), ct);
            await WaitForAsync(() => inputs.Count == 1, ct); // Ordered input stream is a processing barrier.
            Check(acknowledgements == 1 && server.LastPresentedUtc == firstPresented &&
                server.ClientPresentedFps == 89.8 && server.ClientSubmittedFps == 0 && server.ClientDecoder == "c2.unisoc.avc.decoder", "duplicate ACK replaced liveness or decoder telemetry");
            log.Add("PASS repeated video ACK cannot rewrite liveness or decoder/FPS telemetry");

            await packets.Writer.WriteAsync(new(0x21, [0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0x41], true), ct);
            await ReadUntilAsync(stream, 0x21, ct);
            await SendVideoAckAsync(stream, 2, 1000, new string('d', 161), ct);
            await WaitForAsync(() => acknowledgements == 2, ct);
            Check(server.PresentedFrames == 2 && server.ClientPresentedFps == 300 && server.ClientSubmittedFps == 0 && server.ClientDecoder is null,
                "FPS/decoder metadata was not bounded");
            log.Add("PASS valid advancing ACK bounds FPS and ignores overlong decoder metadata");

            await SendVideoAckAsync(stream, 3, 90, "future-frame", ct);
            await ExpectClosedAsync(stream, ct);
            await WaitForAsync(() => !server.ClientConnected, ct);
            Check(acknowledgements == 2 && server.LastPresentedUtc is null && server.PresentedFrames == 0,
                "ACK beyond the two sent access units was accepted");
            log.Add("PASS video ACK cannot claim a frame beyond this session's sent access units");
        }

        Check(server.ClientDisplayProfile is null && server.ClientSubmittedFps == 0 && server.ClientPresentedFps == 0 && server.ClientDecoder is null,
            "disconnect retained another connection's display/FPS/decoder metadata");
        using (var fresh = await ConnectAsync(server, ct))
        {
            await ReadUntilAsync(fresh.GetStream(), 0x20, ct);
            Check(server.ClientDisplayProfile is null && server.ClientSubmittedFps == 0 && server.ClientPresentedFps == 0 && server.ClientDecoder is null,
                "fresh connection inherited previous client metadata");
            await SendVideoAckAsync(fresh.GetStream(), 1, 90, "no-frame", ct);
            await ExpectClosedAsync(fresh.GetStream(), ct);
            await WaitForAsync(() => !server.ClientConnected, ct);
            Check(acknowledgements == 2, "configuration-only session accepted a rendered-frame ACK");
            log.Add("PASS reconnect resets client telemetry; codec configuration alone cannot authorize an ACK");
        }

        foreach (var invalid in new[] { Encoding.UTF8.GetBytes("{"), Encoding.UTF8.GetBytes("{}"),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(DisplayProfileBytes()).Replace("\"rotation\":0", "\"rotation\":9")) })
        {
            using var badProfile = await ConnectAsync(server, ct);
            var before = profiles.Count;
            await FrameServer.WritePacketAsync(badProfile.GetStream(), 0x13, invalid, ct);
            await ExpectClosedAsync(badProfile.GetStream(), ct);
            await WaitForAsync(() => !server.ClientConnected, ct);
            Check(profiles.Count == before && server.ClientDisplayProfile is null, "invalid display profile reached adaptation callback");
        }
        log.Add("PASS malformed, incomplete and out-of-range display profiles close the session before adaptation");
    }

    static readonly byte[] VideoConfiguration = Encoding.UTF8.GetBytes(
        "{\"codec\":\"video/avc\",\"width\":1200,\"height\":1920,\"fps\":90,\"csd0\":\"Zw==\",\"csd1\":\"aA==\"}");

    static async IAsyncEnumerable<VideoPacket> ControlledVideoAsync(ChannelReader<VideoPacket> packets,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // The test controls all access units. Configuration-only heartbeats let
        // the writer observe a malformed receive packet while no frame is due.
        yield return new(0x20, VideoConfiguration, false);
        while (!ct.IsCancellationRequested)
        {
            var next = packets.ReadAsync(ct).AsTask();
            while (!next.IsCompleted)
            {
                await Task.WhenAny(next, Task.Delay(40, ct));
                ct.ThrowIfCancellationRequested();
                if (!next.IsCompleted) yield return new(0x20, VideoConfiguration, false);
            }
            yield return await next;
        }
    }

    static byte[] DisplayProfileBytes(bool landscape = false) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        width = landscape ? 1920 : 1200, height = landscape ? 1200 : 1920, rotation = landscape ? 1 : 0,
        activeModeId = 1, refreshRate = 60, nativeWidth = 1200, nativeHeight = 1920,
        supportedModes = new[] { new { width = 1200, height = 1920, refreshRate = 60, modeId = 1 },
            new { width = 1200, height = 1920, refreshRate = 90, modeId = 2 } }
    });

    static Task SendVideoAckAsync(NetworkStream stream, long sequence, double fps, string decoder, CancellationToken ct) =>
        FrameServer.WritePacketAsync(stream, 0x12, JsonSerializer.SerializeToUtf8Bytes(new
        { kind = "frame-presented", sequence, width = 1200, height = 1920, fps, codec = "video/avc", decoder, droppedFrames = 0 }), ct);

    static async Task<byte[]> ReadUntilAsync(NetworkStream stream, byte expectedType, CancellationToken ct)
    {
        while (true)
        {
            var packet = await FrameServer.ReadPacketAsync(stream, 8192, ct);
            if (packet.Type == expectedType) return packet.Payload;
        }
    }

    static async Task<TcpClient> ConnectAsync(FrameServer server, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, server.ListeningPort, ct);
            await SendHelloAsync(client, server.Token, ct);
            var status = await FrameServer.ReadPacketAsync(client.GetStream(), 8192, ct);
            Check(status.Type == 2, "missing authenticated connection status");
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    static Task SendHelloAsync(TcpClient client, string token, CancellationToken ct) =>
        FrameServer.WritePacketAsync(client.GetStream(), 0x10,
            Encoding.UTF8.GetBytes("{\"protocol\":1,\"token\":\"" + token + "\"}"), ct);

    static Task SendAckAsync(NetworkStream stream, long sequence, CancellationToken ct) =>
        FrameServer.WritePacketAsync(stream, 0x12,
            Encoding.UTF8.GetBytes("{\"kind\":\"frame-presented\",\"sequence\":" + sequence +
                ",\"width\":1280,\"height\":800}"), ct);

    static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(10, ct);
    }

    static async Task ExpectClosedAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            while (true) await FrameServer.ReadPacketAsync(stream, 8192, ct);
        }
        catch (IOException) { }
    }

    static void Check(bool result, string description)
    {
        if (!result) throw new InvalidOperationException(description);
    }
}
