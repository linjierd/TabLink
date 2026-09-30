# Contributing to TabLink

Contributions are welcome. Keep changes focused, include a clear description
of the behavior change, and add tests when the change affects security,
device identity, display ownership, transport framing or update validation.

## Development setup

- Windows 11 x64 and the .NET 10 SDK for the Windows host and tests.
- JDK 17 plus Android SDK 35 for the Android client.
- A signed Virtual Display Driver package is retained for local packaging, but
  unit tests do not install it or change display topology.
- ADB and the patched FFmpeg executable are local build dependencies. They are
  deliberately excluded from Git. See `third_party/adb/README.md` and
  `third_party/ffmpeg-tablink/README.md` for pinned versions and hashes.

Run the managed test projects from PowerShell:

```powershell
$projects = Get-ChildItem .\tests -Filter '*.csproj' -Recurse
foreach ($project in $projects) {
    dotnet run --project $project.FullName -c Release
    if ($LASTEXITCODE -ne 0) { throw "Test failed: $($project.FullName)" }
}
```

Build the host without installing a driver:

```powershell
dotnet build .\src\TabLink.Windows\TabLink.Windows.csproj -c Release
dotnet build .\src\TabLink.DriverSetup\TabLink.DriverSetup.csproj -c Release
```

## Privacy and hardware evidence

Never commit real device serial numbers, USB/PnP instance IDs, pairing tokens,
private IP addresses, certificates with private keys, screenshots containing
personal data, or absolute paths from a contributor's computer. Use obvious
test values such as `TEST-SERIAL-001` in code and tests. Raw device diagnostics
belong outside the repository.

## Licensing

By contributing, you agree that your TabLink-authored contribution is provided
under the repository's MIT license. Third-party material must keep its original
license and provenance and must not be copied into the repository merely to
make a local build convenient.
