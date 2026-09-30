package com.tablink.client;

import java.io.IOException;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

/** The QR-supplied exact certificate, not system CAs or a hostname, establishes identity. */
public final class PinnedTls {
    private PinnedTls() {}

    public static SSLSocket createSocket(String certificateSha256) throws IOException {
        try {
            SSLContext context = SSLContext.getInstance("TLS");
            context.init(null, new TrustManager[] { new CertificatePin(certificateSha256) }, null);
            SSLSocket socket = (SSLSocket) context.getSocketFactory().createSocket();
            ArrayList<String> protocols = new ArrayList<>();
            for (String protocol : socket.getSupportedProtocols())
                if (protocol.equals("TLSv1.2") || protocol.equals("TLSv1.3")) protocols.add(protocol);
            if (protocols.isEmpty()) { socket.close(); throw new IOException("此设备不支持 TLS 1.2"); }
            socket.setEnabledProtocols(protocols.toArray(new String[0]));
            return socket;
        } catch (GeneralSecurityException error) {
            throw new IOException("无法初始化加密连接", error);
        }
    }

    public static final class CertificatePin implements X509TrustManager {
        private final byte[] expected;

        public CertificatePin(String fingerprint) {
            if (fingerprint == null || !fingerprint.matches("[0-9A-Fa-f]{64}"))
                throw new IllegalArgumentException("证书指纹无效");
            expected = new byte[32];
            for (int i = 0; i < expected.length; i++)
                expected[i] = (byte) Integer.parseInt(fingerprint.substring(i * 2, i * 2 + 2), 16);
        }

        @Override public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            if (chain == null || chain.length == 0 || chain[0] == null)
                throw new CertificateException("电脑未提供证书");
            if (authType == null || authType.isEmpty()) throw new CertificateException("证书认证类型无效");
            try {
                byte[] actual = MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded());
                if (!MessageDigest.isEqual(expected, actual)) throw new CertificateException("电脑证书与二维码不匹配");
                chain[0].checkValidity();
            } catch (GeneralSecurityException failure) {
                if (failure instanceof CertificateException) throw (CertificateException) failure;
                throw new CertificateException("无法校验证书", failure);
            }
        }

        @Override public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            throw new CertificateException("此客户端不接受客户端证书认证");
        }

        @Override public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
    }
}
