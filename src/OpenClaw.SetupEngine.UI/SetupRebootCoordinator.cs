using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace OpenClaw.SetupEngine.UI;

internal static class SetupRebootCoordinator
{
    internal const string ContinueSetupArgument = "--continue-setup-after-reboot";

    private const string RunOnceKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceValueName = "OpenClawContinueSetupAfterReboot";

    internal static string PendingMarkerPath => Path.Combine(
        SetupContext.ResolveLocalDataDir(),
        "pending-setup-reboot.json");

    internal static void RegisterContinueAfterReboot(string? reason, string? logPath)
    {
        var markerDir = Path.GetDirectoryName(PendingMarkerPath);
        if (!string.IsNullOrWhiteSpace(markerDir))
            Directory.CreateDirectory(markerDir);

        var marker = new PendingSetupRebootMarker(
            DateTimeOffset.UtcNow,
            reason,
            logPath);
        File.WriteAllText(PendingMarkerPath, JsonSerializer.Serialize(marker, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        var exePath = ResolveCurrentExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath))
            throw new InvalidOperationException("无法定位当前 OpenClaw 可执行文件，不能注册重启后继续安装。");

        using var key = Registry.CurrentUser.CreateSubKey(RunOnceKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户 RunOnce 注册表项。");
        key.SetValue(RunOnceValueName, $"\"{exePath}\" {ContinueSetupArgument}");
    }

    internal static void ClearPendingMarker()
    {
        TryDeleteFile(PendingMarkerPath);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunOnceKeyPath, writable: true);
            key?.DeleteValue(RunOnceValueName, throwOnMissingValue: false);
        }
        catch
        {
            // RunOnce is best-effort cleanup; setup can still continue without it.
        }
    }

    internal static void RestartWindowsNow()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("/r");
        psi.ArgumentList.Add("/t");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("OpenClaw 正在重启 Windows 以完成 WSL 功能启用。");

        Process.Start(psi)?.Dispose();
    }

    private static string? ResolveCurrentExecutablePath()
    {
        try
        {
            return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch
        {
            return Environment.ProcessPath;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A stale marker is harmless; avoid failing setup resume on cleanup.
        }
    }

    private sealed record PendingSetupRebootMarker(
        DateTimeOffset CreatedAtUtc,
        string? Reason,
        string? LogPath);
}
