using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace TabLink.Windows;

/// <summary>
/// Publishes only an unauthenticated route hint for an already-known host ID.
/// A response never grants access; the client must still pin the persistent TLS
/// certificate and complete a fresh signed challenge.
/// </summary>
internal sealed class NativeDiscoveryService : IAsyncDisposable
{
    internal const int Port = 27193;
    internal const int ProtocolVersion = 1;
    const int MaximumPacketBytes = 512;
    internal static readonly TimeSpan ReceiveFailureBackoff = TimeSpan.FromMilliseconds(100);
    readonly NetworkInterfaceChoice network;
    readonly string hostId;
    readonly int servicePort;
    readonly CancellationTokenSource lifetime = new();
    readonly UdpClient socket;
    readonly Task loop;
    readonly DiscoveryResponseRateLimiter rateLimiter = new();
    bool disposed;

    internal NativeDiscoveryService(NetworkInterfaceChoice network, string hostId,
        int servicePort = NetworkSessionOptions.DefaultPort)
    {
        ArgumentNullException.ThrowIfNull(network);
        TrustedPairingProtocol.ValidateHostId(hostId);
        if (servicePort is < 1024 or > 65535 || servicePort == Port)
            throw new ArgumentOutOfRangeException(nameof(servicePort));
        this.network = network;
        this.hostId = hostId;
        this.servicePort = servicePort;
        socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.ExclusiveAddressUse = true;
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        loop = Task.Run(() => RunAsync(lifetime.Token));
    }

    async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await socket.ReceiveAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException error)
            {
                try { await WaitForReceiveRecoveryAsync(error, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                continue;
            }
            if (received.Buffer.Length is < 32 or > MaximumPacketBytes ||
                received.RemoteEndPoint.AddressFamily != AddressFamily.InterNetwork ||
                !IsSameSubnet(received.RemoteEndPoint.Address, network.LocalAddress, network.PrefixLength) ||
                !TryAuthorizeRequest(received.Buffer, hostId, received.RemoteEndPoint.Address, rateLimiter, out var request)) continue;
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                v = ProtocolVersion,
                type = "offer",
                hostId,
                nonce = request.Nonce,
                port = servicePort
            });
            try { await socket.SendAsync(payload, received.RemoteEndPoint, cancellationToken); }
            catch (Exception error) when (error is SocketException or OperationCanceledException) { }
        }
    }

    internal static Task WaitForReceiveRecoveryAsync(SocketException error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        // Windows can surface an ICMP response to a previous UDP reply as
        // WSAECONNRESET on the next receive. An oversized datagram can likewise
        // report WSAEMSGSIZE. Neither invalidates this bound listener.
        return error.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize
            ? Task.CompletedTask
            : Task.Delay(ReceiveFailureBackoff, cancellationToken);
    }

    internal static bool TryAuthorizeRequest(ReadOnlySpan<byte> payload, string expectedHostId,
        IPAddress remoteAddress, DiscoveryResponseRateLimiter rateLimiter, out DiscoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        request = null!;
        DiscoveryRequest parsed;
        try { parsed = ParseRequest(payload); }
        catch (InvalidDataException) { return false; }
        if (!string.Equals(parsed.HostId, expectedHostId, StringComparison.Ordinal) ||
            !rateLimiter.TryAcquire(remoteAddress)) return false;
        request = parsed;
        return true;
    }

    internal static DiscoveryRequest ParseRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload.Length > MaximumPacketBytes) throw new InvalidDataException("发现请求长度无效。");
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 2
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("发现请求必须是对象。");
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is not ("v" or "type" or "hostId" or "nonce") || !values.TryAdd(property.Name, property.Value))
                    throw new InvalidDataException("发现请求包含未知或重复字段。");
            }
            if (values.Count != 4 || values["v"].ValueKind != JsonValueKind.Number ||
                !values["v"].TryGetInt32(out var version) || version != ProtocolVersion ||
                values["type"].ValueKind != JsonValueKind.String || values["type"].GetString() != "discover" ||
                values["hostId"].ValueKind != JsonValueKind.String || values["nonce"].ValueKind != JsonValueKind.String)
                throw new InvalidDataException("发现请求字段无效。");
            var requestedHost = values["hostId"].GetString()!;
            var nonce = values["nonce"].GetString()!;
            TrustedPairingProtocol.ValidateHostId(requestedHost);
            if (nonce.Length != 32 || !nonce.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new InvalidDataException("发现请求随机数无效。");
            return new(requestedHost, nonce);
        }
        catch (JsonException error) { throw new InvalidDataException("发现请求 JSON 无效。", error); }
    }

    internal static bool IsSameSubnet(IPAddress candidate, IPAddress local, int prefixLength)
    {
        if (candidate.AddressFamily != AddressFamily.InterNetwork || local.AddressFamily != AddressFamily.InterNetwork ||
            prefixLength is < 1 or > 32 || IPAddress.IsLoopback(candidate) || candidate.GetAddressBytes()[0] >= 224) return false;
        var candidateValue = ToUInt32(candidate);
        var localValue = ToUInt32(local);
        var mask = prefixLength == 32 ? uint.MaxValue : uint.MaxValue << (32 - prefixLength);
        return (candidateValue & mask) == (localValue & mask) && candidateValue != localValue;
    }

    static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        socket.Dispose();
        try { await loop; }
        catch (OperationCanceledException) { }
        lifetime.Dispose();
    }
}

internal sealed class DiscoveryResponseRateLimiter
{
    internal const int MaximumTrackedAddresses = 256;
    internal const int MaximumResponsesPerWindow = 8;
    const long ResponseWindowMilliseconds = 10000;
    const long TrackingLifetimeMilliseconds = 60000;
    readonly Func<long> clock;
    readonly object gate = new();
    readonly Dictionary<IPAddress, Queue<long>> recent = [];

    internal DiscoveryResponseRateLimiter(Func<long>? clock = null) => this.clock = clock ?? (() => Environment.TickCount64);

    internal int TrackedAddressCount
    {
        get { lock (gate) return recent.Count; }
    }

    internal bool TryAcquire(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var now = clock();
        lock (gate)
        {
            foreach (var stale in recent.Where(item => item.Value.Count == 0 || now - item.Value.Peek() > TrackingLifetimeMilliseconds)
                         .Select(item => item.Key).ToArray()) recent.Remove(stale);
            if (!recent.TryGetValue(address, out var queue))
            {
                if (recent.Count >= MaximumTrackedAddresses) return false;
                recent.Add(address, queue = new Queue<long>());
            }
            while (queue.Count != 0 && now - queue.Peek() > ResponseWindowMilliseconds) queue.Dequeue();
            if (queue.Count >= MaximumResponsesPerWindow) return false;
            queue.Enqueue(now);
            return true;
        }
    }
}

internal sealed record DiscoveryRequest(string HostId, string Nonce);
