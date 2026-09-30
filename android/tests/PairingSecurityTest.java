package com.tablink.client;

import com.google.zxing.BarcodeFormat;
import com.google.zxing.common.BitMatrix;
import com.google.zxing.qrcode.QRCodeWriter;
import java.io.FileInputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.concurrent.FutureTask;
import java.util.concurrent.TimeUnit;
import javax.net.ssl.KeyManagerFactory;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLServerSocket;
import javax.net.ssl.SSLSocket;

/** Real loopback TLS handshakes and QR decode; no Android device or network service required. */
public final class PairingSecurityTest {
    private static final String TOKEN = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static final String CERT = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private static final String LINK = "tablink://connect?host=192.168.42.1&port=27184&token=" + TOKEN + "&cert=" + CERT;
    private static int checks;

    public static void main(String[] args) throws Exception {
        PairingLink paired = PairingLink.parse(LINK);
        check(paired.host.equals("192.168.42.1") && paired.port == 27184, "USB tether IPv4 accepted");
        check(paired.token().equals(TOKEN) && paired.certificateSha256.equals(CERT), "credentials retained exactly");
        check(paired.toPrivateUri().equals(LINK), "canonical round trip");
        check(!paired.toString().contains(TOKEN) && !paired.toString().contains(CERT), "diagnostic text redacted");
        check(PairingLink.parse(LINK.replace(CERT, CERT.toUpperCase())).certificateSha256.equals(CERT), "uppercase cert normalized");
        check(PairingLink.parse(LINK.replace("192.168.42.1", "169.254.2.3")).host.equals("169.254.2.3"), "link-local unicast accepted");
        for (int port : new int[] {27184, 27186, 27187, 27188, 27189, 27190, 27191, 27192}) {
            String sessionLink = LINK.replace("port=27184", "port=" + port);
            PairingLink multiSession = PairingLink.parse(sessionLink);
            check(multiSession.port == port, "native multi-session port retained");
            check(multiSession.toPrivateUri().equals(sessionLink), "multi-session URI round trip retains port");
            check(multiSession.token().equals(TOKEN) && multiSession.certificateSha256.equals(CERT), "port does not alter authentication");
            rejectLink(sessionLink.replace(TOKEN, TOKEN.toUpperCase()), "token rules still apply on every native port");
            rejectLink(sessionLink.replace(CERT, "invalid"), "certificate rules still apply on every native port");
        }
        for (String rejectedPort : new String[] {"27183", "27185", "27193", "0", "-1", "80", "65535", "65536",
                "2147483648", "999999999999999999999", "027184", "+27184", "27184.0", "2.7184e4", "%3227184", ""}) {
            rejectLink(LINK.replace("port=27184", "port=" + rejectedPort), "browser, boundary or noncanonical port rejected");
        }
        String[] invalidLinks = {
            null, "", LINK + " ", LINK + "#fragment", LINK.replace("tablink:", "https:"),
            LINK.replace("connect?", "connect/?"), LINK.replace("connect?", "user@connect?"),
            LINK.replace("connect?", "connect:5?"), LINK + "&width=1200", LINK + "&host=10.0.0.1",
            LINK.replace("&port=27184", ""), LINK.replace("27184", "27183"), LINK.replace("27184", "65536"),
            LINK.replace(TOKEN, TOKEN.toUpperCase()), LINK.replace(TOKEN, "deadbeef"), LINK.replace(CERT, "xyz"),
            LINK.replace("192.168.42.1", "computer.local"), LINK.replace("192.168.42.1", "127.0.0.1"),
            LINK.replace("192.168.42.1", "0.0.0.0"), LINK.replace("192.168.42.1", "224.0.0.1"),
            LINK.replace("192.168.42.1", "255.255.255.255"), LINK.replace("192.168.42.1", "256.1.1.1"),
            LINK.replace("192.168.42.1", "192.168.042.1"), LINK.replace("192.168.42.1", "[::1]"),
            LINK.replace("192.168.42.1", "192%2e168.42.1"), LINK.replace("192.168.42.1", "192.168.42.1:123"),
            LINK.replace("&port=27184", "&port=27184&port=27184"), LINK + "&", LINK.replace("connect?", "connect?=")
        };
        for (String invalid : invalidLinks) {
            try { PairingLink.parse(invalid); throw new AssertionError("malformed pairing accepted"); }
            catch (IllegalArgumentException expected) {
                check(!expected.getMessage().contains(TOKEN), "URI rejection never reveals token");
            }
        }

        BitMatrix qr = new QRCodeWriter().encode(LINK, BarcodeFormat.QR_CODE, 480, 480);
        byte[] luminance = new byte[480 * 480];
        byte[] rotated = new byte[luminance.length];
        for (int y = 0; y < 480; y++) for (int x = 0; x < 480; x++) {
            byte pixel = (byte) (qr.get(x, y) ? 0 : 255);
            luminance[y * 480 + x] = pixel;
            rotated[x * 480 + 479 - y] = pixel;
        }
        check(QrCodeDecoder.decode(luminance, 480, 480).equals(LINK), "real QR luminance decode");
        check(QrCodeDecoder.decode(rotated, 480, 480).equals(LINK), "rotated QR decode");

        KeyStore keys = KeyStore.getInstance("PKCS12");
        try (FileInputStream file = new FileInputStream(args[0])) { keys.load(file, "android".toCharArray()); }
        X509Certificate certificate = (X509Certificate) keys.getCertificate("androiddebugkey");
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(certificate.getEncoded());
        StringBuilder correctPin = new StringBuilder();
        for (byte value : digest) correctPin.append(String.format(java.util.Locale.ROOT, "%02x", value & 255));
        PinnedTls.CertificatePin trust = new PinnedTls.CertificatePin(correctPin.toString());
        trust.checkServerTrusted(new X509Certificate[] {certificate}, "RSA");
        check(trust.getAcceptedIssuers().length == 0, "no implicit system CA trust");
        rejectCertificate(() -> new PinnedTls.CertificatePin(CERT).checkServerTrusted(new X509Certificate[] {certificate}, "RSA"));
        rejectCertificate(() -> trust.checkServerTrusted(new X509Certificate[0], "RSA"));
        rejectCertificate(() -> trust.checkServerTrusted(null, "RSA"));
        rejectCertificate(() -> trust.checkServerTrusted(new X509Certificate[] {certificate}, ""));
        rejectCertificate(() -> trust.checkClientTrusted(new X509Certificate[] {certificate}, "RSA"));
        KeyManagerFactory keyManager = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
        keyManager.init(keys, "android".toCharArray());
        SSLContext serverContext = SSLContext.getInstance("TLS");
        serverContext.init(keyManager.getKeyManagers(), null, null);
        handshake(serverContext, correctPin.toString(), true);
        handshake(serverContext, CERT, false);
        System.out.println("PASS: " + checks + " pairing, QR and pinned-TLS assertions");
    }

    private static void handshake(SSLContext context, String pin, boolean shouldConnect) throws Exception {
        try (SSLServerSocket server = (SSLServerSocket) context.getServerSocketFactory().createServerSocket(
                0, 1, InetAddress.getByName("127.0.0.1"))) {
            server.setSoTimeout(5000);
            FutureTask<Boolean> result = new FutureTask<>(() -> {
                try (SSLSocket accepted = (SSLSocket) server.accept()) {
                    accepted.setSoTimeout(5000);
                    accepted.startHandshake();
                    return accepted.getInputStream().read() == 42;
                } catch (java.io.IOException failure) { return false; }
            });
            Thread worker = new Thread(result, "TabLink-test-TLS-server");
            worker.setDaemon(true);
            worker.start();
            boolean connected = false;
            try (SSLSocket client = PinnedTls.createSocket(pin)) {
                for (String protocol : client.getEnabledProtocols())
                    check(protocol.equals("TLSv1.2") || protocol.equals("TLSv1.3"), "legacy TLS disabled");
                client.connect(new InetSocketAddress("127.0.0.1", server.getLocalPort()), 5000);
                client.setSoTimeout(5000);
                client.startHandshake();
                connected = true;
                client.getOutputStream().write(42);
                client.getOutputStream().flush();
            } catch (javax.net.ssl.SSLHandshakeException expectedMismatch) {
                check(!shouldConnect, "certificate mismatch fails during handshake");
            }
            check(connected == shouldConnect, "real TLS connection obeys exact certificate pin");
            check(result.get(6, TimeUnit.SECONDS) == shouldConnect, "application bytes only after verified TLS");
        }
    }

    private interface CertificateAction { void run() throws CertificateException; }
    private static void rejectLink(String link, String description) {
        try { PairingLink.parse(link); throw new AssertionError(description); }
        catch (IllegalArgumentException expected) {
            check(!expected.getMessage().contains(TOKEN), description + "; credential remains redacted");
        }
    }
    private static void rejectCertificate(CertificateAction action) throws Exception {
        try { action.run(); throw new AssertionError("untrusted certificate accepted"); }
        catch (CertificateException expected) { checks++; }
    }
    private static void check(boolean condition, String description) {
        if (!condition) throw new AssertionError(description);
        checks++;
    }
}
