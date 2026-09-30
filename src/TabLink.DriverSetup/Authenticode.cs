using System.Runtime.InteropServices;

namespace TabLink.DriverSetup;

internal static class Authenticode
{
    internal static void Verify(string path)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var name = Marshal.StringToCoTaskMemUni(path);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = name }, filePointer, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2, // WTD_UI_NONE
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = filePointer,
                dwStateAction = 1, // WTD_STATEACTION_VERIFY
                // Offline: use local trust and cached chains. Pinned SHA256 above
                // additionally binds these exact upstream binaries.
                dwProvFlags = 0x1000 | 0x10
            };
            int result;
            try { result = WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
            finally
            {
                data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
            if (result != 0)
                throw new InvalidDataException($"Windows 无法验证驱动数字签名：{Path.GetFileName(path)} (0x{result:X8})。未安装驱动；不会修改证书或系统签名策略。");
        }
        finally { Marshal.FreeHGlobal(filePointer); Marshal.FreeCoTaskMem(name); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct WINTRUST_FILE_INFO { public uint cbStruct; public IntPtr pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct WINTRUST_DATA
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData;
        public uint dwUIChoice; public uint fdwRevocationChecks; public uint dwUnionChoice;
        public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData;
        public IntPtr pwszURLReference; public uint dwProvFlags; public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WINTRUST_DATA data);
}
