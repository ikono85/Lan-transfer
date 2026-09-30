using System.Security.Cryptography;
using System.Text.Json;
using LanLink.Core.Security;

namespace LanLink.Core.Storage;

public static class AppPaths
{
    public static string DefaultRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanLink");
}

public sealed class AppSettings
{
    public string DeviceName { get; set; } = Environment.MachineName;
    public string DownloadDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public int Port { get; set; } = 45870;

    /// <summary>Autorise les autres PC a demander la vue ou le controle de cet ecran (chaque demande reste soumise a accord).</summary>
    public bool AllowScreenSharing { get; set; } = true;

    /// <summary>"system", "dark" ou "light".</summary>
    public string Theme { get; set; } = "system";
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _file;

    public SettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            return File.Exists(_file)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_file)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings) => File.WriteAllText(_file, JsonSerializer.Serialize(settings, Options));
}

/// <summary>
/// Certificat de l'appareil et vérificateur du mot de passe, chiffrés au repos avec DPAPI
/// (compte Windows courant) dans <c>identity.bin</c>.
/// </summary>
public sealed class IdentityStore : IDisposable
{
    private sealed record Blob(string Pfx, string? Salt, string? Key, int MemoryKiB, int Iterations, int Parallelism);

    private readonly string _file;
    private readonly bool _protect;

    public DeviceIdentity Identity { get; private set; }
    public PasswordVerifier? Verifier { get; private set; }

    public IdentityStore(string directory, bool protect = true)
    {
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "identity.bin");
        _protect = protect;

        if (TryLoad(out var identity, out var verifier))
        {
            Identity = identity!;
            Verifier = verifier;
        }
        else
        {
            Identity = DeviceIdentity.Create();
            Save();
        }
    }

    public void SetPassword(string password, Argon2Params? p = null)
    {
        Verifier = PasswordVerifier.Create(password, p);
        Save();
    }

    private void Save()
    {
        var v = Verifier;
        var blob = new Blob(
            Convert.ToBase64String(Identity.ExportPfx()),
            v is null ? null : Convert.ToBase64String(v.Salt),
            v is null ? null : Convert.ToBase64String(v.Key),
            v?.Params.MemoryKiB ?? 0, v?.Params.Iterations ?? 0, v?.Params.Parallelism ?? 0);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(blob);
        if (_protect && OperatingSystem.IsWindows())
            bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        var tmp = _file + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, _file, overwrite: true);
    }

    private bool TryLoad(out DeviceIdentity? identity, out PasswordVerifier? verifier)
    {
        identity = null;
        verifier = null;
        if (!File.Exists(_file)) return false;
        try
        {
            var bytes = File.ReadAllBytes(_file);
            if (_protect && OperatingSystem.IsWindows())
                bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            var blob = JsonSerializer.Deserialize<Blob>(bytes)!;
            identity = DeviceIdentity.FromPfx(Convert.FromBase64String(blob.Pfx));
            if (blob.Salt is not null && blob.Key is not null)
                verifier = new PasswordVerifier(Convert.FromBase64String(blob.Salt),
                    Convert.FromBase64String(blob.Key),
                    new Argon2Params(blob.MemoryKiB, blob.Iterations, blob.Parallelism));
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or FormatException)
        {
            return false; // fichier illisible : on repart d'une nouvelle identité
        }
    }

    public void Dispose() => Identity.Dispose();
}

/// <summary>Historique des transferts, un enregistrement JSON par ligne (<c>history.jsonl</c>).</summary>
public sealed class TransferHistory
{
    private readonly string _file;
    private readonly object _lock = new();

    public TransferHistory(string directory)
    {
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "history.jsonl");
    }

    public void Add(TransferRecord record)
    {
        lock (_lock)
            File.AppendAllText(_file, JsonSerializer.Serialize(record) + Environment.NewLine);
    }

    /// <summary>Les <paramref name="max"/> derniers transferts, du plus récent au plus ancien.</summary>
    public IReadOnlyList<TransferRecord> Load(int max = 500)
    {
        lock (_lock)
        {
            if (!File.Exists(_file)) return Array.Empty<TransferRecord>();
            var result = new List<TransferRecord>();
            foreach (var line in File.ReadLines(_file).Reverse().Take(max))
            {
                try
                {
                    if (JsonSerializer.Deserialize<TransferRecord>(line) is { } r) result.Add(r);
                }
                catch (JsonException) { /* ligne corrompue ignorée */ }
            }
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
            if (File.Exists(_file)) File.Delete(_file);
    }
}
