using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TabLink.Windows;

// The caller may supply the installation-scoped native host certificate for
// trusted reconnects. Tests and legacy callers retain an ephemeral certificate.
// Certificate ownership always transfers to FrameServer through this object.
internal sealed class NetworkSessionOptions : IDisposable
{
    public const int DefaultPort = 27184;
    public IPAddress LocalAddress { get; }
    public int Port { get; }
    public string CertificateFingerprint { get; }
    internal X509Certificate2 Certificate { get; }
    internal ITrustedDeviceRegistry? TrustedDevices { get; }
    internal TimeSpan AuthenticationTimeout { get; }
    internal TimeSpan PreparationTimeout { get; }

    public NetworkSessionOptions(IPAddress localAddress, int port = DefaultPort)
        : this(localAddress, port, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(120), null, null) { }

    internal NetworkSessionOptions(IPAddress localAddress, int port, TimeSpan authenticationTimeout, TimeSpan preparationTimeout)
        : this(localAddress, port, authenticationTimeout, preparationTimeout, null, null) { }

    internal NetworkSessionOptions(IPAddress localAddress, int port, X509Certificate2 certificate,
        ITrustedDeviceRegistry trustedDevices)
        : this(localAddress, port, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(120), certificate, trustedDevices) { }

    internal NetworkSessionOptions(IPAddress localAddress, int port, TimeSpan authenticationTimeout,
        TimeSpan preparationTimeout, X509Certificate2? certificate, ITrustedDeviceRegistry? trustedDevices)
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
        if (certificate is not null)
        {
            if (!certificate.HasPrivateKey) { certificate.Dispose(); throw new ArgumentException("原生主机证书缺少私钥。", nameof(certificate)); }
            if (trustedDevices is null) { certificate.Dispose(); throw new ArgumentNullException(nameof(trustedDevices)); }
            var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
            try { TrustedPairingProtocol.ValidateHostId(trustedDevices.HostId); }
            catch { certificate.Dispose(); throw; }
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(fingerprint),
                    Encoding.ASCII.GetBytes(trustedDevices.HostId)))
            {
                certificate.Dispose();
                throw new ArgumentException("持久主机证书与可信设备注册表身份不匹配。", nameof(certificate));
            }
            Certificate = certificate;
            TrustedDevices = trustedDevices;
        }
        else
        {
            if (trustedDevices is not null) throw new ArgumentException("可信设备验证必须绑定持久主机证书。", nameof(trustedDevices));
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=TabLink ephemeral session", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(localAddress);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
            // Schannel cannot use an ephemeral RSA key (SEC_E_NO_CREDENTIALS).
            var pfx = generated.Export(X509ContentType.Pfx);
            try { Certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
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
