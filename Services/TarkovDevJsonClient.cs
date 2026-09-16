using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TarkovTracker.Models;

namespace TarkovTracker.Services;

internal sealed class CachedJsonDocument : IDisposable
{
    private JsonDocument? _document;

    public CachedJsonDocument(string? cacheFile, bool notModified)
    {
        CacheFile = cacheFile;
        NotModified = notModified;
    }

    public CachedJsonDocument(JsonDocument document)
    {
        _document = document;
        NotModified = false;
    }

    public bool NotModified { get; }
    public string? CacheFile { get; }

    public JsonDocument Document =>
        _document ?? throw new InvalidOperationException("JSON cache was not parsed.");

    public async Task EnsureParsedAsync(CancellationToken cancellationToken)
    {
        if (_document != null)
            return;

        if (string.IsNullOrWhiteSpace(CacheFile) || !File.Exists(CacheFile))
            throw new FileNotFoundException("Missing json.tarkov.dev cache file.", CacheFile);

        await using FileStream stream = new(
            CacheFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        _document?.Dispose();
        _document = null;
    }
}

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

    public static async Task<CachedJsonDocument> GetDocumentAsync(
        string relativePath,
        string? cacheDirectory,
        CancellationToken cancellationToken)
    {
        if (!IsAllowedRelativePath(relativePath))
            throw new InvalidOperationException($"Blocked json.tarkov.dev path: {relativePath}");

        string? cacheFile = null;
        string? etagFile = null;
        string? cachedEtag = null;
        if (!string.IsNullOrWhiteSpace(cacheDirectory))
        {
            string cacheDir = Path.Combine(cacheDirectory, "http-cache");
            Directory.CreateDirectory(cacheDir);
            string safeName = relativePath.Replace('/', '_');
            cacheFile = Path.Combine(cacheDir, safeName + ".json");
            etagFile = Path.Combine(cacheDir, safeName + ".etag");
            if (File.Exists(etagFile) && File.Exists(cacheFile))
                cachedEtag = (await File.ReadAllTextAsync(etagFile, cancellationToken).ConfigureAwait(false)).Trim();
        }

        using HttpResponseMessage response = await SendAsync(
            relativePath,
            cachedEtag,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified &&
            !string.IsNullOrWhiteSpace(cacheFile) &&
            File.Exists(cacheFile))
        {
            return new CachedJsonDocument(cacheFile, notModified: true);
        }

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            using HttpResponseMessage retry = await SendAsync(relativePath, cachedEtag: null, cancellationToken)
                .ConfigureAwait(false);
            return await StoreResponseAsync(retry, relativePath, cacheFile, etagFile, cancellationToken)
                .ConfigureAwait(false);
        }

        return await StoreResponseAsync(response, relativePath, cacheFile, etagFile, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        string relativePath,
        string? cachedEtag,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        if (!string.IsNullOrWhiteSpace(cachedEtag) &&
            EntityTagHeaderValue.TryParse(cachedEtag, out EntityTagHeaderValue? etag))
        {
            request.Headers.IfNoneMatch.Add(etag);
        }

        return await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CachedJsonDocument> StoreResponseAsync(
        HttpResponseMessage response,
        string relativePath,
        string? cacheFile,
        string? etagFile,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"json.tarkov.dev returned {(int)response.StatusCode} for {relativePath}.");
        }

        if (string.IsNullOrWhiteSpace(cacheFile) || string.IsNullOrWhiteSpace(etagFile))
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new CachedJsonDocument(document);
        }

        string tempPath = cacheFile + ".tmp";
        try
        {
            await using (FileStream fileStream = new(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await response.Content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, cacheFile, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (IOException)
            {
            }

            throw;
        }

        string? newEtag = response.Headers.ETag?.ToString();
        if (!string.IsNullOrWhiteSpace(newEtag))
            await File.WriteAllTextAsync(etagFile, newEtag, cancellationToken).ConfigureAwait(false);

        return new CachedJsonDocument(cacheFile, notModified: false);
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
