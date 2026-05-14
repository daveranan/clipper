using Velopack;
using Velopack.Sources;

namespace QuickClipper;

public sealed class UpdateService
{
    public async Task<UpdateCheckResult> CheckDownloadAndRestartAsync(
        string repositoryUrl,
        Func<string, Task<bool>>? confirmInstall = null,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return UpdateCheckResult.Disabled("GitHub repository URL is not configured.");
        }

        var manager = new UpdateManager(new GithubSource(repositoryUrl.Trim(), null, false, null));
        if (!manager.IsInstalled)
        {
            return UpdateCheckResult.Disabled("Updates only work from an installed Velopack release.");
        }

        if (manager.UpdatePendingRestart is not null)
        {
            if (confirmInstall is not null && !await confirmInstall("A downloaded update is ready. Restart and apply it now?"))
            {
                return UpdateCheckResult.NoUpdate("Update is ready to apply later.");
            }

            manager.ApplyUpdatesAndRestart(manager.UpdatePendingRestart);
            return UpdateCheckResult.Restarting("Applying downloaded update.");
        }

        var update = await manager.CheckForUpdatesAsync();
        if (update is null)
        {
            return UpdateCheckResult.NoUpdate("Already up to date.");
        }

        var version = update.TargetFullRelease.Version.ToString();
        if (confirmInstall is not null && !await confirmInstall($"QuickClipper {version} is available. Download and restart now?"))
        {
            return UpdateCheckResult.NoUpdate($"Update {version} skipped.");
        }

        await manager.DownloadUpdatesAsync(update, progress, cancellationToken);
        manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
        return UpdateCheckResult.Restarting($"Restarting to apply {update.TargetFullRelease.Version}.");
    }
}

public sealed record UpdateCheckResult(UpdateCheckResultKind Kind, string Message)
{
    public static UpdateCheckResult Disabled(string message) => new(UpdateCheckResultKind.Disabled, message);

    public static UpdateCheckResult NoUpdate(string message) => new(UpdateCheckResultKind.NoUpdate, message);

    public static UpdateCheckResult Restarting(string message) => new(UpdateCheckResultKind.Restarting, message);
}

public enum UpdateCheckResultKind
{
    Disabled,
    NoUpdate,
    Restarting
}
