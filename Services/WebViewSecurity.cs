using Microsoft.Web.WebView2.Core;
using System.Threading.Tasks;

namespace TarkovTracker.Services;

internal static class WebViewSecurity
{
    public static void ApplyOnce(CoreWebView2? webView, string mapAssetHostName, ref bool alreadyApplied)
    {
        if (alreadyApplied || webView is null)
            return;

        CoreWebView2Settings settings = webView.Settings;
        settings.AreHostObjectsAllowed = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsWebMessageEnabled = true;

        webView.NavigationStarting += (_, e) =>
        {
            if (!IsAllowedNavigation(e.Uri, mapAssetHostName))
                e.Cancel = true;
        };

        webView.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
        };

        alreadyApplied = true;
    }

    public static async Task NavigateToStringAsync(CoreWebView2 webView, string html)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            webView.NavigationCompleted -= Handler;
            tcs.TrySetResult(e.IsSuccess);
        }

        webView.NavigationCompleted += Handler;
        try
        {
            webView.NavigateToString(html);
            await tcs.Task;
        }
        catch
        {
            webView.NavigationCompleted -= Handler;
            throw;
        }
    }

    public static bool IsAllowedHttpsUrl(string? url, params string[] allowedHosts)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (allowedHosts == null || allowedHosts.Length == 0)
            return true;

        foreach (string host in allowedHosts)
        {
            if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool IsTrustedGitHubDownloadUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;

        string host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedNavigation(string? uri, string mapAssetHostName)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return true;

        if (uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed))
            return false;

        if (parsed.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase) ||
            parsed.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase) ||
            parsed.Scheme.Equals("blob", StringComparison.OrdinalIgnoreCase))
            return true;

        return parsed.Host.Equals(mapAssetHostName, StringComparison.OrdinalIgnoreCase);
    }
}
