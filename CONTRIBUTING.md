# Contributing to TabLink

**English (Singapore)** | [简体中文](CONTRIBUTING.zh-CN.md)

Contributions are welcome. Keep changes focused, include a clear description
of the behaviour change, and add tests when the change affects security,
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

## Compatibility catalogue

The public compatibility catalogue is a curated evidence index, not telemetry and
not a direct export of an Issue or support bundle. Each report describes one
specific TabLink version, host, receiver, transport and display configuration.
Maintain the distinction between requested refresh rate, decoder-submitted
frames, presentation callbacks and physical presentation measurements. Missing
evidence must remain explicitly unverified; do not generalise one successful
configuration to a whole device family.

Only maintainers should transcribe reviewed, public, non-unique facts into
`compatibility/catalog.json`. Never copy an Issue body, ZIP member, attachment
name or raw diagnostic output into the catalogue. The validator rejects unknown
fields and common identity, address, path and token patterns, but automated
checks cannot prove that a model label is public or that a test claim is true.
Human review remains required.

After editing the catalogue, regenerate the schema and Markdown view, then verify
that the committed outputs are byte-for-byte current:

```powershell
dotnet run --project .\tools\TabLink.CompatibilityCatalog\TabLink.CompatibilityCatalog.csproj -c Release -- --root . --write
dotnet run --project .\tools\TabLink.CompatibilityCatalog\TabLink.CompatibilityCatalog.csproj -c Release -- --root . --check
```

The tool is offline. It does not open Issues, unpack support bundles, inspect
devices, or access the network. Review the resulting diff before committing.

## GitHub languages

English (Singapore) is the canonical GitHub language. Keep the matching
Simplified Chinese mirror complete, and put English before Chinese in Issue and
pull request templates. When a current version, download, hash, security rule,
privacy boundary or unverified limitation changes, update both languages in the
same commit. Run the contract before submitting:

```powershell
.\tools\Test-GitHubLanguageContract.ps1
```

## Licensing

By contributing, you agree that your TabLink-authored contribution is provided
under the repository's MIT licence. Third-party material must keep its original
licence and provenance and must not be copied into the repository merely to
make a local build convenient.
