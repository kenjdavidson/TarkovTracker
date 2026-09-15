using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TarkovTracker.Models;

namespace TarkovTracker.Services;

internal static class TarkovDevJsonClient
{
    private const string ApiRoot = "https://json.tarkov.dev/";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(ApiRoot),
            Timeout = TimeSpan.FromMinutes(2)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("SayserTarkovTracker", AppInfo.InterfaceVersion));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public static async Task<JsonDocument> GetDocumentAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (!IsAllowedRelativePath(relativePath))
            throw new InvalidOperationException($"Blocked json.tarkov.dev path: {relativePath}");

        using HttpResponseMessage response = await Http.GetAsync(relativePath, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"json.tarkov.dev returned {(int)response.StatusCode} for {relativePath}.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static bool IsAllowedRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        if (relativePath.Contains("..", StringComparison.Ordinal) ||
            relativePath.Contains('\\', StringComparison.Ordinal) ||
            relativePath.StartsWith('/'))
        {
            return false;
        }

        foreach (char c in relativePath)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '/')
                continue;
            return false;
        }

        return true;
    }
}
