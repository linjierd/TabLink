using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TabLink.Windows;

// One certificate per listening session. Ownership transfers to FrameServer.
internal sealed class NetworkSessionOptions : IDisposable
{
    public const int DefaultPort = 27184;
    public IPAddress LocalAddress { get; }
    public int Port { get; }
    public string CertificateFingerprint { get; }
    internal X509Certificate2 Certificate { get; }
    internal TimeSpan AuthenticationTimeout { get; }
    internal TimeSpan PreparationTimeout { get; }

    public NetworkSessionOptions(IPAddress localAddress, int port = DefaultPort)
        : this(localAddress, port, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(120)) { }

    internal NetworkSessionOptions(IPAddress localAddress, int port, TimeSpan authenticationTimeout, TimeSpan preparationTimeout)
    {
        ArgumentNullException.ThrowIfNull(localAddress);
        if (localAddress.AddressFamily != AddressFamily.InterNetwork || localAddress.Equals(IPAddress.Any) ||
            localAddress.Equals(IPAddress.Broadcast) || localAddress.GetAddressBytes()[0] >= 224 ||
            (!localAddress.Equals(IPAddress.Loopback) && !NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Any(address => address.Address.Equals(localAddress))))
            throw new ArgumentException("请选择此电脑当前使用的明确 IPv4 地址。", nameof(localAddress));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (authenticationTimeout < TimeSpan.FromMilliseconds(100) || authenticationTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(authenticationTimeout));
        if (preparationTimeout < TimeSpan.FromMilliseconds(100) || preparationTimeout > TimeSpan.FromSeconds(180))
            throw new ArgumentOutOfRangeException(nameof(preparationTimeout));
        LocalAddress = localAddress; Port = port;
        AuthenticationTimeout = authenticationTimeout; PreparationTimeout = preparationTimeout;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=TabLink ephemeral session", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(localAddress);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
        // Schannel cannot use an ephemeral RSA key (SEC_E_NO_CREDENTIALS).
        // The PFX exists only in memory. UserKeySet without PersistKeySet creates
        // a temporary user key deleted by certificate disposal; no certificate
        // store/trust registration or reusable on-disk PFX is created.
        var pfx = generated.Export(X509ContentType.Pfx);
        try { Certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        CertificateFingerprint = Convert.ToHexString(SHA256.HashData(Certificate.RawData)).ToLowerInvariant();
    }

    public string ConnectionUri(string token)
    {
        if (token.Length != 64 || token.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("无效的会话令牌。", nameof(token));
        return $"tablink://connect?host={LocalAddress}&port={Port}&token={token}&cert={CertificateFingerprint}";
    }

    public void Dispose() => Certificate.Dispose();
}
