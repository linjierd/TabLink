using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TabLink.Windows;

internal sealed record UpdateStartupHealthSignal(string Path, string Nonce)
{
    public static UpdateStartupHealthSignal Parse(string path, string nonce)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var transactionsRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TabLink", "Transactions"));
        var transaction = System.IO.Path.GetDirectoryName(fullPath) ?? "";
        var transactionId = System.IO.Path.GetFileName(transaction);
        if (!System.IO.Path.GetDirectoryName(transaction)!.Equals(transactionsRoot, StringComparison.OrdinalIgnoreCase) ||
            transactionId.Length != 32 || !transactionId.All(Uri.IsHexDigit) ||
            System.IO.Path.GetFileName(fullPath) != "startup-health.json")
            throw new ArgumentException("更新启动确认路径无效。");
        ValidateProtectedParent(transaction);
        return new(fullPath, UpdateSignalSecurity.NormalizeNonce(nonce));
    }

    public void MarkReady()
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        ValidateProtectedParent(directory);
        if (File.Exists(Path) || Directory.Exists(Path)) throw new IOException("更新启动确认路径已被占用。");
        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var process = Process.GetCurrentProcess();
            var executable = System.IO.Path.GetFullPath(process.MainModule?.FileName ?? Environment.ProcessPath ?? "");
            using (var stream = ProtectedUpdaterStager.CreateProtectedFile(temporary, directory))
            {
                JsonSerializer.Serialize(stream, new
                {
                    nonce = Nonce,
                    executablePath = executable,
                    pid = Environment.ProcessId,
                    processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    startedAtUtc = DateTimeOffset.UtcNow
                });
                stream.Flush(true);
            }
            ValidateProtectedParent(directory);
            ProtectedUpdaterStager.VerifyProtectedFile(temporary);
            File.Move(temporary, Path, false);
            ProtectedUpdaterStager.VerifyProtectedFile(Path);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    static void ValidateProtectedParent(string directory)
    {
        WindowsUpdatePathPolicy.VerifyExistingDirectoryChainWithoutReparse(directory);
        var transactions = Directory.GetParent(directory)?.FullName ?? "";
        var tabLink = Directory.GetParent(transactions)?.FullName ?? "";
        ProtectedUpdaterStager.VerifyProtectedDirectory(tabLink);
        ProtectedUpdaterStager.VerifyProtectedDirectory(transactions);
        ProtectedUpdaterStager.VerifyProtectedDirectory(directory);
    }
}
