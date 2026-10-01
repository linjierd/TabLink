package com.tablink.client;

import org.json.JSONObject;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.net.SocketException;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Enumeration;
import java.util.HashSet;
import java.util.Iterator;
import java.util.List;
import java.util.Set;

/** Broadcast discovery returns an untrusted route hint for an already pinned host. */
public final class TrustedDiscovery {
    private TrustedDiscovery() {}

    public static List<Endpoint> discover(String hostId, int timeoutMillis) throws IOException {
        TrustedDeviceProtocol.validateSha256(hostId, "电脑身份");
        if (timeoutMillis < 250 || timeoutMillis > 5000) throw new IllegalArgumentException("发现超时无效");
        byte[] random = new byte[16];
        new SecureRandom().nextBytes(random);
        StringBuilder nonceBuilder = new StringBuilder(32);
        for (byte value : random) nonceBuilder.append(String.format(java.util.Locale.ROOT, "%02x", value & 0xff));
        String nonce = nonceBuilder.toString();
        DiscoveryCandidateSet candidates = new DiscoveryCandidateSet(nonce, DiscoveryCandidateSet.DEFAULT_LIMIT);
        try (DatagramSocket socket = new DatagramSocket()) {
            socket.setBroadcast(true);
            socket.setSoTimeout(Math.min(350, timeoutMillis));
            JSONObject request = new JSONObject();
            request.put("v", 1);request.put("type", "discover");request.put("hostId", hostId);request.put("nonce", nonce);
            byte[] payload = request.toString().getBytes(StandardCharsets.UTF_8);
            Set<InetAddress> broadcasts = new HashSet<>();
            broadcasts.add(InetAddress.getByName("255.255.255.255"));
            try {
                Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
                if (interfaces != null) {
                    while (interfaces.hasMoreElements()) {
                        NetworkInterface network = interfaces.nextElement();
                        try {
                            if (!network.isUp() || network.isLoopback()) continue;
                            for (InterfaceAddress address : network.getInterfaceAddresses())
                                if (address.getAddress() instanceof Inet4Address && address.getBroadcast() != null)
                                    broadcasts.add(address.getBroadcast());
                        } catch (SocketException ignored) {
                            // A disappearing interface must not suppress the global broadcast fallback.
                        }
                    }
                }
            } catch (SocketException ignored) {
                // Some Android builds return no interface enumeration while the network is changing.
                // The limited broadcast address above remains a valid discovery attempt.
            }
            for (InetAddress address : broadcasts)
                try { socket.send(new DatagramPacket(payload, payload.length, address, TrustedDeviceProtocol.DISCOVERY_PORT)); }
                catch (IOException ignored) { }
            long deadline = android.os.SystemClock.elapsedRealtime() + timeoutMillis;
            byte[] buffer = new byte[512];
            while (android.os.SystemClock.elapsedRealtime() < deadline) {
                DatagramPacket response = new DatagramPacket(buffer, buffer.length);
                try { socket.receive(response); }
                catch (SocketTimeoutException timeout) { continue; }
                if (!(response.getAddress() instanceof Inet4Address) || response.getLength() < 32) continue;
                try {
                    JSONObject offer = new JSONObject(new String(response.getData(), response.getOffset(),
                            response.getLength(), StandardCharsets.UTF_8));
                    Set<String> keys = new HashSet<>();
                    for (Iterator<String> iterator = offer.keys(); iterator.hasNext();) keys.add(iterator.next());
                    if (!keys.equals(new HashSet<>(java.util.Arrays.asList("v", "type", "hostId", "nonce", "port"))) ||
                            offer.getInt("v") != 1 || !"offer".equals(offer.getString("type")) ||
                            !hostId.equals(offer.getString("hostId")) || !nonce.equals(offer.getString("nonce"))) continue;
                    int port = offer.getInt("port");
                    if (!PairingLink.isNativePort(port)) continue;
                    candidates.add(response.getAddress().getHostAddress(), port);
                } catch (Exception ignored) { }
            }
        } catch (org.json.JSONException error) {
            throw new IOException("无法生成可信电脑发现请求", error);
        }
        List<Endpoint> result = new ArrayList<>();
        for (DiscoveryCandidateSet.Candidate candidate : candidates.snapshot())
            result.add(new Endpoint(candidate.host, candidate.port));
        return Collections.unmodifiableList(result);
    }

    public static final class Endpoint {
        public final String host;
        public final int port;
        Endpoint(String host, int port) { this.host = host; this.port = port; }
    }
}
