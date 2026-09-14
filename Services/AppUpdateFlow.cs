using System.Windows;

namespace TarkovTracker.Services;

public enum AppUpdateFlowOutcome
{
    /// <summary>The release had no installable asset.</summary>
    NoDownload,

    /// <summary>The download or install step failed.</summary>
    Failed,

    /// <summary>The update was downloaded and a restart was scheduled.</summary>
    Installing
}

/// <summary>
/// Shared download/install/restart flow so the Settings check button and the startup
/// prompt behave identically, including their GitHub release-page fallbacks.
/// </summary>
public static class AppUpdateFlow
{
    public static async Task<AppUpdateFlowOutcome> DownloadAndRestartAsync(
        Window owner,
        AppUpdateCheckResult check,
        IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(check.DownloadUrl))
        {
            OfferReleasePage(owner, "No downloadable release file was found.", "Update Download", check.ReleaseUrl);
            return AppUpdateFlowOutcome.NoDownload;
        }

        AppUpdateDownloadResult download =
            await AppUpdateService.DownloadAndInstallAsync(check, progress);

        if (!download.Succeeded)
        {
            OfferReleasePage(owner, download.Message, "Update Download Failed", check.ReleaseUrl);
            return AppUpdateFlowOutcome.Failed;
        }

        MessageBox.Show(
            owner,
            download.Message,
            "Update Ready",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        if (download.RestartScheduled)
            Application.Current.Shutdown();

        return AppUpdateFlowOutcome.Installing;
    }

    private static void OfferReleasePage(Window owner, string message, string caption, string releaseUrl)
    {
        MessageBoxResult openPage = MessageBox.Show(
            owner,
            $"{message}\n\nOpen the GitHub release page instead?",
            caption,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (openPage == MessageBoxResult.Yes)
            AppUpdateService.OpenReleasePage(releaseUrl);
    }
}
