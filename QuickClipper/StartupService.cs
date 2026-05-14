using System.Diagnostics;
using System.IO;

namespace QuickClipper;

public sealed class StartupService
{
    private const string ShortcutName = "QuickClipper.lnk";

    private static string StartupShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        ShortcutName);

    public bool IsEnabled() => File.Exists(StartupShortcutPath);

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            CreateShortcut();
        }
        else if (File.Exists(StartupShortcutPath))
        {
            File.Delete(StartupShortcutPath);
        }
    }

    private static void CreateShortcut()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        var script = string.Join(Environment.NewLine, new[]
        {
            "$shell = New-Object -ComObject WScript.Shell",
            $"$shortcut = $shell.CreateShortcut('{EscapePowerShell(StartupShortcutPath)}')",
            $"$shortcut.TargetPath = '{EscapePowerShell(exePath)}'",
            $"$shortcut.WorkingDirectory = '{EscapePowerShell(Path.GetDirectoryName(exePath) ?? "")}'",
            $"$shortcut.IconLocation = '{EscapePowerShell(exePath)}'",
            "$shortcut.Save()"
        });

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command {Quote(script)}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        process?.WaitForExit(5000);
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''");

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
