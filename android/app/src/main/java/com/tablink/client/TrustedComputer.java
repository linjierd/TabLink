package com.tablink.client;

/** Public trust metadata only; no bearer token or private key is persisted here. */
public final class TrustedComputer {
    public final String hostId;
    public final String certificateSha256;
    public final String lastHost;
    public final int port;

    public TrustedComputer(String hostId, String certificateSha256, String lastHost, int port) {
        TrustedDeviceProtocol.validateSha256(hostId, "电脑身份");
        TrustedDeviceProtocol.validateSha256(certificateSha256, "电脑证书");
        if (!hostId.equals(certificateSha256))
            throw new IllegalArgumentException("电脑身份与固定证书不匹配");
        if (!PairingLink.isUnicastIpv4(lastHost)) throw new IllegalArgumentException("电脑地址无效");
        if (!PairingLink.isNativePort(port)) throw new IllegalArgumentException("电脑端口无效");
        this.hostId = hostId;
        this.certificateSha256 = certificateSha256;
        this.lastHost = lastHost;
        this.port = port;
    }

    public TrustedComputer withEndpoint(String host, int nextPort) {
        return new TrustedComputer(hostId, certificateSha256, host, nextPort);
    }

    /** A consumed pairing link may be reused only as a route hint for this already trusted host. */
    public boolean matchesCertificate(PairingLink pairing) {
        return pairing != null && certificateSha256.equals(pairing.certificateSha256);
    }
}
