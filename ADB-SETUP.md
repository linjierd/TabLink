# ADB compatibility setup

The public TabLink download does **not** redistribute Google Android SDK
Platform-Tools. Native Wi-Fi and ordinary USB-network sharing use TabLink's TLS
network protocol and do not require ADB, developer mode or USB debugging. ADB is
only needed for the legacy USB-debugging compatibility path and for installing
the APK from the Windows UI.

## Accepted Platform-Tools build

TabLink 0.8.7 accepts only the official **SDK Platform-Tools r37.0.0 for
Windows** triplet below. The files must come from the same official archive and
remain next to each other:

| File | Required SHA-256 |
| --- | --- |
| `adb.exe` | `957E46B8615F7AF5B7292A2DDABE98D2E61940C3FB2B0545756507F080613E71` |
| `AdbWinApi.dll` | `120BEF587119C6CB926B86B9BE90FDFBCE38937588EAE28CD91A94CE63C7B965` |
| `AdbWinUsbApi.dll` | `6CA69A2CA0E31309C087D288F058977D421AD03500E4C3E1DBD981241A069C60` |

Download Platform-Tools from the official Android page:
<https://developer.android.com/tools/releases/platform-tools>. TabLink does not
download it in the background and does not accept a different version merely
because another `adb.exe` is available from Android Studio, an environment
variable or `PATH`.

## First interactive use

1. Extract the official r37.0.0 Windows archive to a location you control.
2. Enable USB debugging only on the Android device you intend to use and approve
   this computer on that device.
3. In TabLink, open **USB debugging (compatibility)** and choose the complete
   `platform-tools\adb.exe` when prompted.
4. On the first interactive refresh, APK installation or connection, TabLink
   opens and hashes the three required regular files, rejecting a missing,
   modified or reparse-point member. Unrelated files in the user-controlled
   source directory are ignored and are never copied.
5. After validation, TabLink copies the triplet to
   `%ProgramData%\TabLink\Adb\sha256-<manifest-hash>\`. The protected copy and
   its directory are owned and writable only by the administrator boundary used
   by TabLink. The file set, hashes, owner, DACL and reparse-point boundary are
   checked again before execution.

After that first staging succeeds, subsequent elevated ADB operations re-verify
and use the protected ProgramData copy. TabLink does not execute an arbitrary
path from user settings, `ANDROID_SDK_ROOT`, `ANDROID_HOME`, `%LOCALAPPDATA%`,
`PATH` or another program's installation.

## Pending reverse cleanup

Each ADB USB session receives a random device-side reverse port. Before
`reverse --no-rebind`, TabLink writes a protected `Prepared` receipt that only
reserves the endpoint. It becomes `Owned`, and therefore eligible for exact
cleanup, only after that command clearly succeeds. Receipts are bound to the
originating Windows user SID and stored under
`%ProgramData%\TabLink\UsbReverseCleanup\`.

A `Prepared` receipt has no removal authority, so it can be permanently sealed
without contacting ADB even when it was written by another Windows user. An
`Owned` receipt from another user is instead retained and rotated for a later
matching user session; it never reaches ADB under the current user. Deferred
entries carry a persistent queue sequence and move to the tail, so one offline
device cannot permanently starve later entries and ordering does not depend on
filesystem timestamps.

Automatic cleanup uses only the fixed-hash protected r37.0.0 copy. If that copy
is unavailable, the device is offline, policy now excludes the device, or the
mapping no longer matches, cleanup is deferred or safely completed without a
delete as appropriate. TabLink never falls back to another ADB executable,
never uses an implicit device, and never issues `--remove-all`, `kill-server`,
`start-server` or `tcpip` for this recovery.

For an eligible `Owned` receipt, TabLink reads the exact reverse mapping once
during approval and a second time immediately before the single-endpoint
remove. ADB has no cross-process atomic compare-and-delete operation, so another
local administrator can still race the narrow interval after the final check;
that limitation is intentionally documented rather than hidden.

The public ZIP does not contain Platform-Tools, but a protected copy remains
available after one successful interactive selection and staging of the exact
r37.0.0 triplet. Background recovery can re-verify and reuse that copy without
consulting the original user-writable SDK directory.

Old ownership records under `%LOCALAPPDATA%\TabLink` remain useful only for
diagnosis and cannot authorize elevated ADB removal or a new elevated display
recovery. Current display leases, ownership markers and immutable bootstrap
records live under protected `%ProgramData%\TabLink\DisplayLeases\`; only the
per-user `.last.json` / `last-display.json` files from this display-state group
remain in LocalAppData as layout preferences. USB cleanup failure also cannot
prevent TabLink from reclaiming its exact virtual display and active driver
instance. TabLink does not keep an active virtual display merely because the
application or pairing page is open: it installs the single active device only
when an authenticated receiver is ready to connect, and removes that exact
device on disconnect or owner exit. The signed driver package and one-output
configuration remain staged for the next connection.

Google's SDK terms apply to Platform-Tools. Using the official archive does not
imply that Google endorses TabLink.
