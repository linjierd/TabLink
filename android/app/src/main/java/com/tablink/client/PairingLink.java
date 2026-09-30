package com.tablink.client;

import java.net.URI;
import java.net.URISyntaxException;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;

/** Strict, dependency-free parser. Its errors and toString never contain credentials. */
public final class PairingLink {
    public static final int NETWORK_PORT = 27184;
    public final String host;
    public final int port;
    public final String certificateSha256;
    private final String token;

    private PairingLink(String host, int port, String token, String certificateSha256) {
        this.host = host;
        this.port = port;
        this.token = token;
        this.certificateSha256 = certificateSha256.toLowerCase(Locale.ROOT);
    }

    public static PairingLink parse(String text) {
        if (text == null || text.length() > 512 || !text.equals(text.trim()))
            throw new IllegalArgumentException("连接链接为空、过长或包含多余空白");
        final URI uri;
        try { uri = new URI(text); }
        catch (URISyntaxException invalid) { throw new IllegalArgumentException("连接链接格式无效"); }
        if (!"tablink".equals(uri.getScheme()) || !"connect".equals(uri.getRawAuthority())
                || uri.getRawFragment() != null || uri.getRawPath() == null || !uri.getRawPath().isEmpty()
                || uri.getRawQuery() == null)
            throw new IllegalArgumentException("请使用电脑端 TabLink 生成的连接链接");
        Map<String, String> values = new HashMap<>();
        for (String item : uri.getRawQuery().split("&", -1)) {
            int equals = item.indexOf('=');
            if (equals <= 0 || equals != item.lastIndexOf('='))
                throw new IllegalArgumentException("连接链接参数无效");
            String key = item.substring(0, equals);
            String value = item.substring(equals + 1);
            if (!key.equals("host") && !key.equals("port") && !key.equals("token") && !key.equals("cert"))
                throw new IllegalArgumentException("连接链接包含未知参数");
            if (values.put(key, value) != null)
                throw new IllegalArgumentException("连接链接参数重复");
        }
        if (values.size() != 4) throw new IllegalArgumentException("连接链接缺少参数");
        String host = values.get("host");
        if (!isUnicastIpv4(host)) throw new IllegalArgumentException("电脑地址必须是有效的局域网 IPv4 地址");
        int port = parseNativePort(values.get("port"));
        String token = values.get("token");
        if (token == null || !token.matches("[0-9a-f]{64}"))
            throw new IllegalArgumentException("配对凭证无效，请重新扫描电脑二维码");
        String certificate = values.get("cert");
        if (certificate == null || !certificate.matches("[0-9A-Fa-f]{64}"))
            throw new IllegalArgumentException("电脑证书指纹无效，请重新扫描电脑二维码");
        return new PairingLink(host, port, token, certificate);
    }

    private static int parseNativePort(String value) {
        // Exact strings reject noncanonical numeric forms and integer overflow.
        // 27185 belongs exclusively to the browser HTTPS endpoint, not native TLS.
        if (value != null) {
            switch (value) {
                case "27184": case "27186": case "27187": case "27188":
                case "27189": case "27190": case "27191": case "27192":
                    return Integer.parseInt(value);
                default:
                    break;
            }
        }
        throw new IllegalArgumentException("连接端口无效，请使用电脑端的原生客户端二维码；27185 仅用于浏览器");
    }

    private static boolean isUnicastIpv4(String value) {
        if (value == null || !value.matches("[0-9.]{7,15}")) return false;
        String[] octets = value.split("\\.", -1);
        if (octets.length != 4) return false;
        int first = 0;
        for (int i = 0; i < octets.length; i++) {
            String octet = octets[i];
            if (octet.isEmpty() || octet.length() > 3 || octet.length() > 1 && octet.charAt(0) == '0') return false;
            int number = Integer.parseInt(octet);
            if (number > 255) return false;
            if (i == 0) first = number;
        }
        return first > 0 && first != 127 && first < 224;
    }

    public String token() { return token; }

    /** Only for private preferences / explicit sharing, never diagnostic output. */
    public String toPrivateUri() {
        return "tablink://connect?host=" + host + "&port=" + port + "&token=" + token + "&cert=" + certificateSha256;
    }

    @Override public String toString() { return "TabLink " + host + ":" + port + " (TLS paired)"; }
}
