# SIPSorcery 10.0.16 used by TabLink browser receivers

- Package: https://www.nuget.org/packages/SIPSorcery/10.0.16
- Exact package download: https://api.nuget.org/v3-flatcontainer/sipsorcery/10.0.16/sipsorcery.10.0.16.nupkg
- Package SHA-256: `FD16F595614F7577B39F443E3A84FEA54295B2D393ED3105A7A4E33F3EF602A8`
- Repository: https://github.com/sipsorcery-org/sipsorcery
- Source commit in the package metadata: `4ca86773993a875706d8a7a4c1e0108e3a885ebf`
- TabLink uses the unmodified managed library for ICE, DTLS-SRTP, H.264 RTP packetisation and SCTP data channels. No SIP account, external signalling, STUN or TURN service is used.
- The complete `LICENSE.md` and original package `sipsorcery.nuspec` in this directory were extracted directly from this fixed NuGet package. They must accompany redistribution.

The package license is **BSD 3-Clause plus an additional geographic/use restriction** in section 2. It must not be represented as plain, unrestricted BSD. Section 2 prohibits use, modification or distribution inside Israel and the Occupied Territories on its stated terms. Outside those regions it explicitly imposes no additional commercial-use restriction and no mandatory derivative-work license. Read the full exact license before redistribution. TabLink does not remove or replace those conditions.

Section 3 of the upstream full license contains LGPL 2.1 text for SIPSorceryMedia.FFmpeg. TabLink does **not** reference the SIPSorceryMedia.FFmpeg package. TabLink's separate FFmpeg executable, patches, corresponding source and license material are supplied in `third_party/ffmpeg-tablink`.

Direct dependencies specified for .NET 10 by this fixed package:

| Package | Version |
|---|---|
| SIPSorceryMedia.Abstractions | 10.0.16 |
| BouncyCastle.Cryptography | 2.7.0 |
| Concentus | 2.2.2 |
| DnsClient | 1.8.0 |
| Makaretu.Dns.Multicast | 0.27.0 |
| SIPSorcery.WebSocketSharp | 0.0.1 |
| Microsoft.Extensions.Logging.Abstractions | 10.0.11 |

The restored project's `project.assets.json` and distributed `.deps.json` record the complete resolved dependency graph. This notice does not replace each dependency's own license.
