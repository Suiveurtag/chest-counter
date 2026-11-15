using System.Text.RegularExpressions;

namespace ChestCounter.Engine;

/// <summary>
/// The treasure hunt data in use: the copy built into the engine, replaced by a newer revision downloaded from the
/// reference project (kept in the data folder), so that new weeks, prize ids and Treasure Hours arrive without a new
/// version of the app. Also fetches the prize pictures it does not ship, once, into the data folder.
///
/// These are the only requests the engine makes besides the squad server: public files of the reference project
/// (content.json and its copies of the pictures) and the pictures of fortnite.bot. Nothing about the player is sent.
/// </summary>
public sealed class HuntContent : IDisposable
{
    public const string RemoteUrl = "https://raw.githubusercontent.com/LED-N/Fortnite-ChestCounter/main/content/content.json";
    private const string ImageHost = "cdn.fortnite.bot";
    private static readonly Regex SafeId = new(@"^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.Compiled);

    private readonly string _dataDir;
    private readonly bool _online;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _imageGate = new(2, 2);
    private volatile HuntCatalog _catalog;

    public HuntContent(string dataDir, bool online)
    {
        _dataDir = dataDir;
        _online = online;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ChestCounterSquad/{ChestEngine.AppVersion}");
        _catalog = HuntCatalog.Embedded();
        try
        {
            var cached = Path.Combine(dataDir, "content.json");
            if (File.Exists(cached) && HuntCatalog.Parse(File.ReadAllText(cached), out _) is { } c && c.Revision > _catalog.Revision)
            {
                _catalog = c;
            }
        }
        catch
        {
            // unreadable copy: the built-in data stays
        }
    }

    public HuntCatalog Catalog => _catalog;

    /// <summary>A newer revision was accepted.</summary>
    public event Action? Changed;

    /// <summary>Downloads content.json; a valid copy with a higher revision is kept and used at once.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (!_online) return false;
        try
        {
            var json = await _http.GetStringAsync(RemoteUrl, ct);
            if (json.Length > 512 * 1024 || HuntCatalog.Parse(json, out _) is not { } fresh || fresh.Revision <= _catalog.Revision)
            {
                return false;
            }
            Directory.CreateDirectory(_dataDir);
            var path = Path.Combine(_dataDir, "content.json");
            await File.WriteAllTextAsync(path + ".tmp", json, ct);
            File.Move(path + ".tmp", path, overwrite: true);
            _catalog = fresh;
            Changed?.Invoke();
            return true;
        }
        catch
        {
            return false; // offline: the current data stays
        }
    }

    /// <summary>
    /// The local file of a prize picture, downloaded on first use from its https address (fortnite.bot only, an
    /// image, at most 2 MB). Null when there is none or it cannot be fetched.
    /// </summary>
    public async Task<(string Path, string ContentType)?> ImageAsync(string prizeId, CancellationToken ct = default)
    {
        if (!SafeId.IsMatch(prizeId) || _catalog.Prize(prizeId) is not { Image: { } url }) return null;
        var folder = Path.Combine(_dataDir, "treasure-images");
        // The picture can change for the same prize (w2-loading.webp?v=3): the file name follows the address.
        var name = prizeId + "-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url)), 0, 4).ToLowerInvariant();
        var existing = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, name + ".*").FirstOrDefault() : null;
        if (existing != null) return (existing, TypeOf(existing));
        if (!_online || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != ImageHost) return null;
        lock (_failed)
        {
            // A picture not published yet is asked again an hour later, not on every page load.
            if (_failed.TryGetValue(name, out var when) && DateTime.UtcNow - when < TimeSpan.FromHours(1)) return null;
        }

        await _imageGate.WaitAsync(ct);
        try
        {
            existing = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, name + ".*").FirstOrDefault() : null;
            if (existing != null) return (existing, TypeOf(existing));
            // The reference project keeps a copy of each picture (often before fortnite.bot has it), then fortnite.bot.
            var query = new string(uri.Query.TrimStart('?').Where(char.IsAsciiLetterOrDigit).ToArray());
            var file = Path.GetFileNameWithoutExtension(uri.AbsolutePath) + (query.Length > 0 ? "-" + query : "") + Path.GetExtension(uri.AbsolutePath);
            foreach (var source in new[] { new Uri(RepoImages + Uri.EscapeDataString(file)), uri })
            {
                using var response = await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct);
                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                var ext = type switch { "image/webp" => ".webp", "image/png" => ".png", "image/jpeg" => ".jpg", _ => null };
                if (!response.IsSuccessStatusCode || ext == null || response.Content.Headers.ContentLength > 2_000_000) continue;
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > 2_000_000) continue;
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, name + ext);
                await File.WriteAllBytesAsync(path + ".tmp", bytes, ct);
                File.Move(path + ".tmp", path, overwrite: true);
                return (path, type);
            }
            lock (_failed) _failed[name] = DateTime.UtcNow;
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _imageGate.Release();
        }
    }

    private const string RepoImages = "https://raw.githubusercontent.com/LED-N/Fortnite-ChestCounter/main/assets/treasures/";
    private readonly Dictionary<string, DateTime> _failed = new();

    private static string TypeOf(string path) => Path.GetExtension(path) switch { ".png" => "image/png", ".jpg" => "image/jpeg", _ => "image/webp" };

    public void Dispose() => _http.Dispose();
}
