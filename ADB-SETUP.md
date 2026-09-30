# ADB compatibility setup

The public TabLink download does not redistribute Google Android SDK
Platform-Tools. Android native Wi-Fi and USB-network connections do not need
ADB. ADB is only needed for the legacy USB-debugging compatibility mode and
for installing the APK from the Windows UI.

If you need that mode:

1. Download or update **SDK Platform-Tools for Windows** from the official
   Android page: <https://developer.android.com/tools/releases/platform-tools>.
2. Extract the official archive to a location you control, or install it with
   Android Studio's SDK Manager.
3. In TabLink, open the Android/USB compatibility page and select the complete
   `platform-tools\adb.exe` installation. `AdbWinApi.dll` and
   `AdbWinUsbApi.dll` must remain next to `adb.exe`.
4. Enable USB debugging only on the Android device you intend to use and
   approve this computer on that device.

TabLink also discovers complete installations from `ANDROID_SDK_ROOT`,
`ANDROID_HOME`, `%LOCALAPPDATA%\Android\Sdk\platform-tools`, and `PATH`.
An explicitly configured but missing or incomplete installation fails closed
so that TabLink does not silently run a different ADB executable.

Google's SDK terms apply to Platform-Tools. TabLink does not download it in
the background or imply that Google endorses TabLink.
