using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ImeGameGuard;

internal sealed record CatalogRefreshResult(bool Updated, bool Failed, string Message)
{
    public static CatalogRefreshResult NotNeeded => new(false, false, string.Empty);
}

/// <summary>
/// Loads a cached process-name database and refreshes it from a pinned public JSON source.
/// Only executable names are read; the downloaded file is never executed.
/// </summary>
internal sealed class GameProcessCatalog
{
    public const string DefaultUrl = "https://raw.githubusercontent.com/nino-exe/GameProcessesDB/main/gameprocessesdb.json";
    private const int MaxDownloadBytes = 5 * 1024 * 1024;
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string _cachePath;
    private string _url;
    private bool _enabled;
    private int _refreshHours;
    private string[] _patterns = Array.Empty<string>();

    public GameProcessCatalog(AppConfig config, string baseDirectory)
    {
        _cachePath = ResolveCachePath(config, baseDirectory);
        _url = config.OnlineGameListUrl;
        _enabled = config.OnlineGameListEnabled;
        _refreshHours = config.OnlineGameListRefreshHours;
        LoadCache();
    }

    public IReadOnlyList<string> Patterns => Volatile.Read(ref _patterns);
    public int Count => Volatile.Read(ref _patterns).Length;

    public void UpdateConfig(AppConfig config)
    {
        var nextPath = ResolveCachePath(config, AppContext.BaseDirectory);
        var pathChanged = !string.Equals(_cachePath, nextPath, StringComparison.OrdinalIgnoreCase);
        _cachePath = nextPath;
        _url = config.OnlineGameListUrl;
        _enabled = config.OnlineGameListEnabled;
        _refreshHours = config.OnlineGameListRefreshHours;
        if (pathChanged)
            LoadCache();
    }

    public async Task<CatalogRefreshResult> RefreshIfNeededAsync()
    {
        if (!_enabled)
            return CatalogRefreshResult.NotNeeded;

        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(_cachePath))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_cachePath);
                if (age < TimeSpan.FromHours(Math.Clamp(_refreshHours, 1, 8760)))
                    return CatalogRefreshResult.NotNeeded;
            }

            if (!Uri.TryCreate(_url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return new CatalogRefreshResult(false, true, "The online game list URL must use HTTPS.");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
                return new CatalogRefreshResult(false, true, "The online game list is too large and was rejected.");

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(json) > MaxDownloadBytes)
                return new CatalogRefreshResult(false, true, "The online game list is too large and was rejected.");

            var patterns = ParsePatterns(json);
            if (patterns.Length == 0)
                return new CatalogRefreshResult(false, true, "The online game list is invalid or empty.");

            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var temporary = _cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), timeout.Token).ConfigureAwait(false);
            File.Move(temporary, _cachePath, true);
            Volatile.Write(ref _patterns, patterns);
            return new CatalogRefreshResult(true, false, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return new CatalogRefreshResult(false, true, "The online game list download timed out.");
        }
        catch (Exception ex)
        {
            return new CatalogRefreshResult(false, true, ex.Message);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void LoadCache()
    {
        try
        {
            if (File.Exists(_cachePath))
                Volatile.Write(ref _patterns, ParsePatterns(File.ReadAllText(_cachePath)));
        }
        catch
        {
            Volatile.Write(ref _patterns, Array.Empty<string>());
        }
    }

    private static string[] ParsePatterns(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("processName", out var value) || value.ValueKind != JsonValueKind.String)
                continue;
            var name = value.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 260 || name.IndexOfAny(new[] { '\\', '/' }) >= 0)
                continue;
            names.Add(name);
        }
        return names.ToArray();
    }

    private static string ResolveCachePath(AppConfig config, string baseDirectory)
    {
        var file = config.OnlineGameListFile.Trim();
        return Path.IsPathRooted(file) ? file : Path.Combine(baseDirectory, file);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ImeGameGuard", "1.0"));
        return client;
    }
}
