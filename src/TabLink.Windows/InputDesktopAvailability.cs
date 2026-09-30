using System.Runtime.InteropServices;
using System.Text;

namespace TabLink.Windows;

public enum InputDesktopState { Unknown, Available, Unavailable }

public readonly record struct InputDesktopStatus(InputDesktopState State, string Reason, int NativeError = 0)
{
    public bool IsAvailable => State == InputDesktopState.Available;
    public bool IsUnavailable => State == InputDesktopState.Unavailable;
}

/// <summary>Reads desktop metadata only; never opens, switches to, or captures a secure desktop.</summary>
public static class InputDesktopAvailability
{
    public static InputDesktopStatus Query()
    {
        if (!OperatingSystem.IsWindows()) return new(InputDesktopState.Unknown, "not-windows");
        // This is a borrowed handle. GetThreadDesktop documentation expressly says not to close it.
        var desktop = GetThreadDesktop(GetCurrentThreadId());
        if (desktop == IntPtr.Zero) return Unknown("thread-desktop-query-failed");
        var name = new StringBuilder(256);
        if (!GetUserObjectName(desktop, 2, name, name.Capacity * sizeof(char), out _))
            return Unknown("desktop-name-query-failed");
        // TabLink and its watcher are launched on the normal interactive desktop.
        // Refuse capture if either were ever launched on another desktop.
        if (!string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase))
            return new(InputDesktopState.Unavailable, "not-normal-desktop");
        // UOI_IO reports whether this exact desktop receives input, without asking for access to
        // the desktop currently shown by UAC, Winlogon, or another user's session.
        if (!GetUserObjectInput(desktop, 6, out var receivesInput, sizeof(int), out _))
            return Unknown("desktop-input-query-failed");
        return receivesInput != 0
            ? new(InputDesktopState.Available, "normal-desktop-active")
            : new(InputDesktopState.Unavailable, "normal-desktop-not-input");
    }

    static InputDesktopStatus Unknown(string reason) =>
        new(InputDesktopState.Unknown, reason, Marshal.GetLastWin32Error());

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetUserObjectName(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetUserObjectInput(IntPtr handle, int index, out int value, int length, out int needed);
}
