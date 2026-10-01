# Diagnostics and redacted support-bundle regression checks

Run from the repository root:

```powershell
dotnet run --project tests/TabLink.Diagnostics.Tests/TabLink.Diagnostics.Tests.csproj -c Release
```

The executable compile-links the production `DiagnosticStore.cs` and runs only
inside a unique `TabLink-Diagnostics-*` directory under the operating system's
temporary directory. Cleanup verifies the resolved directory stays under that
temporary root. It does not build or launch the Windows app, access live
diagnostics, change ACLs, connect devices or change displays.

The 18 scenarios contain 164 assertions covering:

- Initial save, complete JSON replacement and no temporary-file leftovers.
- A real Windows file lock on the destination, preserving the last good JSON.
- A file blocking creation of the output directory, followed by recovery.
- A failing snapshot delegate, cyclic JSON and a failing serialized getter.
- A throwing warning callback and per-file warning suppression until recovery.
- Partial staged writing followed by a simulated disk-full `IOException`,
  followed by recovery. This is fault injection, not filling a real disk.
- Filename traversal, alternate stream syntax, reserved Windows device names,
  invalid characters and relative output directories.
- Eight concurrent writers on the same store, with all 160 writes succeeding.
- Concurrent readers with one and four store instances. Transient Windows file
  access or replacement conflicts may reject best-effort saves; every successful
  read must contain a complete JSON snapshot, and temporary files must be cleaned.
- A monitor-like loop continuing after failed saves. This proves the store does
  not throw into that simulated stop branch, not that the real `MainForm` monitor
  or an active secondary-display connection was exercised.
- A support ZIP with an exact five-entry allow-list whose extracted bytes match
  the text shown in the in-app preview. Its manifest records the size and SHA-256
  of every non-manifest entry.
- Rejection of free-form path, serial-like and token-like metadata, duplicate
  or reordered health stages, unsafe destination names, unknown runtime values,
  and any mismatch between the reviewed preview, manifest and saved bytes.
- An injected partial archive failure preserving the previous target byte for
  byte, plus a real Windows destination lock, staging cleanup, and a later
  complete retry.
- Selection of display data only from a currently connected native or streaming
  browser session, excluding pending and last-known profiles.

The full application should additionally be built and its real monitoring call
sites reviewed to verify snapshot collection occurs inside the best-effort
boundary. Live connection validation is a separate check.
