using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChestCounter.Engine;

/// <summary>A treasure of the hunt (gizmos are left out: they are not one item and can be found again).</summary>
/// <param name="Id">Stable key ("w3-jam"), also the name of its picture.</param>
/// <param name="PrizeIds">Ids a golden chest carries for it ("DelilahPrize:dpid_item21"): the replay names it for sure.</param>
public sealed record HuntPrize(string Id, string Kind, string Name, string? Image, IReadOnlyList<string> PrizeIds);

/// <summary>One week of the hunt: its own outfit, pickaxe or back bling, vehicle decal, jam track and loading screen.</summary>
public sealed record HuntWeek(int Number, DateTime From, DateTime To, string Title, IReadOnlyList<HuntPrize> Prizes);

/// <summary>Two hours where some kinds are much more likely (multiplier per kind). Confirmed false = expected, not announced.</summary>
public sealed record HuntHour(DateTime Start, DateTime End, IReadOnlyDictionary<string, double> Boosts, bool Confirmed);

/// <summary>Odds of one kind of treasure for each chest opened.</summary>
public sealed record HuntOdds(string Kind, int OneIn, bool Weekly);

/// <summary>
/// The treasure hunt of the season: odds published by Epic, weeks and prizes, Treasure Hours. Same data and format as
/// LED-N/Fortnite-ChestCounter's content/content.json (schema 1), so a revision published there can be used as is.
/// Every value is checked: anything out of range and the whole file is refused.
/// </summary>
public sealed class HuntCatalog
{
    public const string GoldOutfit = "outfit", PirateShop = "pirate-shop", VehicleDecal = "car", PickaxeOrBackBling = "pickaxe",
        JamTrack = "jam", LoadingScreen = "loading", Gizmo = "gizmo";

    public static readonly string[] Kinds = { GoldOutfit, PirateShop, VehicleDecal, PickaxeOrBackBling, JamTrack, LoadingScreen, Gizmo };

    private static readonly Regex SafeId = new(@"^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.Compiled);
    private static readonly Regex SafePrizeId = new(@"^[A-Za-z0-9:_.\-]{1,80}$", RegexOptions.Compiled);

    public int Revision { get; private init; }
    public IReadOnlyList<HuntOdds> Odds { get; private init; } = Array.Empty<HuntOdds>();
    public HuntPrize PirateShopAccess { get; private init; } = null!;
    public IReadOnlyList<HuntWeek> Weeks { get; private init; } = Array.Empty<HuntWeek>();
    public IReadOnlyList<HuntHour> Hours { get; private init; } = Array.Empty<HuntHour>();
    public string OddsSource { get; private init; } = "";
    public string HoursSource { get; private init; } = "";

    public DateTime HuntStart => Weeks.Count > 0 ? Weeks[0].From : DateTime.MinValue;
    public DateTime HuntEnd => Weeks.Count > 0 ? Weeks[^1].To : DateTime.MinValue;

    public IEnumerable<HuntPrize> AllPrizes => Weeks.SelectMany(w => w.Prizes).Prepend(PirateShopAccess);

    public HuntOdds OddsOf(string kind) => Odds.First(o => o.Kind == kind);

    /// <summary>The prize a golden chest id stands for (case does not matter), or null when not known.</summary>
    public HuntPrize? Find(string? prizeId) => prizeId == null ? null
        : AllPrizes.FirstOrDefault(p => p.PrizeIds.Any(id => string.Equals(id, prizeId, StringComparison.OrdinalIgnoreCase)));

    public HuntPrize? Prize(string id) => AllPrizes.FirstOrDefault(p => p.Id == id);

    /// <summary>The multiplier of a kind at a moment (1 outside the Treasure Hours).</summary>
    public double Boost(string kind, DateTime utc) =>
        Hours.FirstOrDefault(h => utc >= h.Start && utc < h.End)?.Boosts.GetValueOrDefault(kind, 1) is { } b and > 0 ? b : 1;

    public HuntWeek? WeekOf(DateTime utc) => Weeks.FirstOrDefault(w => utc >= w.From && utc < w.To);

    /// <summary>The week whose chances are shown: the current one, or the last one once the hunt is over.</summary>
    public HuntWeek? ShownWeek(DateTime utc) => WeekOf(utc) ?? Weeks.LastOrDefault(w => w.To <= utc);

    // ---- probabilities ----------------------------------------------------------------------------------------

    /// <summary>
    /// Natural log of the chance of never having obtained it: Σ ln(1 − p × boost), each chest with the boost of the
    /// moment it was opened, plus <paramref name="plainChests"/> at the normal odds (added by hand: no time).
    /// Kept as a log: the chance of "at least once" rounds to exactly 1 for the common kinds, this does not.
    /// </summary>
    public double LogNever(HuntOdds odds, IEnumerable<DateTime> chestTimesUtc, long plainChests)
    {
        var p = 1.0 / odds.OneIn;
        var sum = Math.Max(0, plainChests) * double.LogP1(-p);
        foreach (var t in chestTimesUtc)
        {
            sum += double.LogP1(-Math.Min(0.999, p * Boost(odds.Kind, t)));
        }
        return sum;
    }

    /// <summary>How many of them on average with these chests: Σ p × boost.</summary>
    public double Expected(HuntOdds odds, IEnumerable<DateTime> chestTimesUtc, long plainChests)
    {
        var p = 1.0 / odds.OneIn;
        var sum = Math.Max(0, plainChests) * p;
        foreach (var t in chestTimesUtc)
        {
            sum += Math.Min(0.999, p * Boost(odds.Kind, t));
        }
        return sum;
    }

    /// <summary>Chests needed to reach a chance (0.5 = one in two).</summary>
    public static long ChestsFor(int oneIn, double chance) => (long)Math.Ceiling(Math.Log(1 - chance) / double.LogP1(-1.0 / oneIn));

    // ---- reading content.json ---------------------------------------------------------------------------------

    public static HuntCatalog Embedded()
    {
        using var stream = typeof(HuntCatalog).Assembly.GetManifestResourceStream("ChestCounter.Engine.Hunt.content.json")
            ?? throw new InvalidDataException("content.json is not built into the engine");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), out var error) ?? throw new InvalidDataException("built-in content.json is not valid: " + error);
    }

    /// <summary>Reads and checks a content.json. Only data, never markup; every picture an https address.</summary>
    public static HuntCatalog? Parse(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("schema");
            var revision = root.GetProperty("revision").GetInt32();
            if (revision < 1) throw new InvalidDataException("revision");

            static string Text(JsonElement e, int max) => e.GetString() is { } t && t.Length <= max ? t : throw new InvalidDataException("text");
            static string Kind(JsonElement e) => Array.IndexOf(Kinds, e.GetString()) >= 0 ? e.GetString()! : throw new InvalidDataException("kind");
            static DateTime When(JsonElement e) => DateTime.Parse(e.GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            static string? Https(JsonElement e, int max) =>
                Text(e, max) is var url && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? url : throw new InvalidDataException("url");
            static HuntPrize Prize(JsonElement e)
            {
                var id = e.GetProperty("id").GetString() ?? "";
                if (!SafeId.IsMatch(id)) throw new InvalidDataException("prize id");
                var image = e.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.String ? Https(img, 300) : null;
                var ids = e.TryGetProperty("prizeIds", out var list) ? list.EnumerateArray().Select(x => x.GetString() ?? "").Take(6).ToArray() : Array.Empty<string>();
                if (ids.Any(x => !SafePrizeId.IsMatch(x))) throw new InvalidDataException("prizeIds");
                return new HuntPrize(id, Kind(e.GetProperty("kind")), Text(e.GetProperty("name"), 80), image, ids);
            }

            var odds = root.GetProperty("odds").EnumerateArray().Take(20).Select(o =>
            {
                var oneIn = o.GetProperty("oneIn").GetInt32();
                if (oneIn is < 1 or > 100_000_000) throw new InvalidDataException("oneIn");
                return new HuntOdds(Kind(o.GetProperty("kind")), oneIn, o.TryGetProperty("weekly", out var w) && w.GetBoolean());
            }).ToList();
            if (Kinds.Any(k => odds.All(o => o.Kind != k))) throw new InvalidDataException("odds missing");

            var weeks = root.GetProperty("weeks").EnumerateArray().Take(30).Select(w => new HuntWeek(
                w.GetProperty("number").GetInt32(), When(w.GetProperty("start")), When(w.GetProperty("end")), Text(w.GetProperty("title"), 80),
                w.GetProperty("prizes").EnumerateArray().Take(20).Select(Prize).ToList())).OrderBy(w => w.From).ToList();
            if (weeks.Count == 0 || weeks.Any(w => w.To <= w.From)) throw new InvalidDataException("weeks");

            var hours = root.TryGetProperty("treasureHours", out var th) ? th.EnumerateArray().Take(300).Select(h =>
            {
                var boosts = h.GetProperty("boosts").EnumerateObject().ToDictionary(
                    b => Array.IndexOf(Kinds, b.Name) >= 0 ? b.Name : throw new InvalidDataException("boost kind"),
                    b => b.Value.GetDouble() is var x and >= 1 and <= 100 ? x : throw new InvalidDataException("boost"));
                var hour = new HuntHour(When(h.GetProperty("start")), When(h.GetProperty("end")), boosts, h.TryGetProperty("confirmed", out var cf) && cf.GetBoolean());
                return hour.End > hour.Start && hour.End - hour.Start <= TimeSpan.FromHours(24) ? hour : throw new InvalidDataException("hour");
            }).OrderBy(h => h.Start).ToList() : new List<HuntHour>();

            var pirate = Prize(root.GetProperty("pirateShop"));
            if (pirate.Kind != PirateShop) throw new InvalidDataException("pirateShop");

            string Source(string name, string fallback) =>
                root.TryGetProperty("sources", out var src) && src.TryGetProperty(name, out var v) ? Https(v, 300)! : fallback;

            return new HuntCatalog
            {
                Revision = revision, Odds = odds, PirateShopAccess = pirate, Weeks = weeks, Hours = hours,
                OddsSource = Source("odds", "https://x.com/FortniteStatus"), HoursSource = Source("treasureHours", "https://fortnite.bot"),
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
