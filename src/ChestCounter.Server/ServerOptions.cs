using System.Security.Cryptography;

namespace ChestCounter.Server;

/// <summary>Everything is set with environment variables (see .env.example). Every one has a safe default.</summary>
public sealed record ServerOptions
{
    /// <summary>CC_DATABASE: path of the SQLite file (or a full "Data Source=..." connection string).</summary>
    public string Database { get; init; } = "data/squads.db";
    /// <summary>CC_LISTEN: address(es) to listen on, e.g. http://0.0.0.0:8080.</summary>
    public string Listen { get; init; } = "http://0.0.0.0:8080";
    /// <summary>CC_PUBLIC_URL: how clients reach the server (used in invite links). Empty = taken from each request.</summary>
    public string PublicUrl { get; init; } = "";
    /// <summary>CC_AUTH_SECRET: key the device tokens are hashed with. Changing it signs every device out.</summary>
    public string AuthSecret { get; init; } = "";
    /// <summary>CC_ADMIN_TOKEN: enables /api/v1/admin/* when set.</summary>
    public string AdminToken { get; init; } = "";
    /// <summary>CC_REGISTRATION_KEY: when set, new devices must present it (keeps strangers off a public server).</summary>
    public string RegistrationKey { get; init; } = "";
    /// <summary>CC_MAX_SQUAD_SIZE: members per squad (4 = a Fortnite squad).</summary>
    public int MaxSquadSize { get; init; } = 4;
    /// <summary>CC_INVITE_TTL_HOURS: default lifetime of an invite.</summary>
    public int InviteTtlHours { get; init; } = 168;

    public static ServerOptions FromConfiguration(IConfiguration c)
    {
        var defaults = new ServerOptions();
        string S(string key, string fallback) => c[key] is { Length: > 0 } v ? v : fallback;
        int I(string key, int fallback, int min, int max) => int.TryParse(c[key], out var v) ? Math.Clamp(v, min, max) : fallback;
        return new ServerOptions
        {
            Database = S("CC_DATABASE", defaults.Database),
            Listen = S("CC_LISTEN", defaults.Listen),
            PublicUrl = S("CC_PUBLIC_URL", "").TrimEnd('/'),
            AuthSecret = S("CC_AUTH_SECRET", ""),
            AdminToken = S("CC_ADMIN_TOKEN", ""),
            RegistrationKey = S("CC_REGISTRATION_KEY", ""),
            MaxSquadSize = I("CC_MAX_SQUAD_SIZE", defaults.MaxSquadSize, 2, 16),
            InviteTtlHours = I("CC_INVITE_TTL_HOURS", defaults.InviteTtlHours, 1, 24 * 90),
        };
    }

    public string ConnectionString => Database.Contains('=') ? Database : $"Data Source={Database}";

    public string? DatabaseFile => Database.Contains('=')
        ? new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(Database).DataSource
        : Database;

    /// <summary>
    /// The secret from the environment, or one generated once and kept next to the database: the server works out of
    /// the box, and tokens survive restarts either way.
    /// </summary>
    public byte[] ResolveSecret(ILogger log)
    {
        if (AuthSecret.Length > 0)
        {
            if (AuthSecret.Length < 16)
            {
                log.LogWarning("CC_AUTH_SECRET is shorter than 16 characters: use a long random value.");
            }
            return System.Text.Encoding.UTF8.GetBytes(AuthSecret);
        }
        var file = DatabaseFile;
        if (string.IsNullOrEmpty(file) || file == ":memory:")
        {
            return RandomNumberGenerator.GetBytes(32);
        }
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "auth-secret.key");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            log.LogWarning("CC_AUTH_SECRET is not set: a secret was generated in {Path}. Keep that file with the database.", path);
        }
        return System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim());
    }
}
