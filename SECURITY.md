# Security policy

**English (Singapore)** | [简体中文](SECURITY.zh-CN.md)

TabLink handles administrator elevation, a display driver, USB device identity,
TLS pairing, input forwarding and signed software updates. Do not file a public
issue that contains an exploit or any device serial, USB/PnP ID, IP/MAC address,
local or network path, pairing link, token, certificate, key, raw log or private
diagnostic capture.

Report vulnerabilities through the repository's **Security** tab using a
[private GitHub security advisory](https://github.com/linjierd/TabLink/security/advisories/new).
Include the affected version, a minimal reproduction and the expected security
boundary. Replace private values with obvious placeholders. If a token,
certificate or key was exposed during testing, rotate it and do not include the
original value in the report.

The in-app v1 redacted support bundle contains only the structured fields shown
in its preview. It does not read or copy raw logs. TabLink never sends, uploads
or attaches a support bundle automatically. Attach one only when you choose to,
after reviewing it; a support bundle is optional and is not required for a
public issue or security advisory.

Supported security fixes target the latest published version. Historical local
candidates and unsigned development APKs are not treated as supported stable
releases.
