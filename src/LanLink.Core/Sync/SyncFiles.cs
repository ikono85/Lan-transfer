using System.Text.Json;
using LanLink.Core.Security;

namespace LanLink.Core.Sync;

/// <summary>Parcourt un dossier synchronisé et décrit ses fichiers.</summary>
public static class FolderScanner
{
    /// <summary>Dossier interne (corbeille, fichiers temporaires) : jamais synchronisé.</summary>
    public const string InternalPrefix = ".lanlink";

    /// <summary>Un fichier modifié depuis moins de ce délai est considéré comme « en cours d'écriture ».</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Thumbs.db", "desktop.ini", ".DS_Store",
    };

    public static Dictionary<string, SyncEntry> Scan(string root, DateTime? utcNow = null, List<string>? skipped = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var result = new Dictionary<string, SyncEntry>(StringComparer.OrdinalIgnoreCase);
        Walk(new DirectoryInfo(root), "", now, result, skipped);
        return result;
    }

    private static void Walk(DirectoryInfo dir, string prefix, DateTime now, Dictionary<string, SyncEntry> result, List<string>? skipped)
    {
        IEnumerable<FileSystemInfo> children;
        try { children = dir.EnumerateFileSystemInfos().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var child in children)
        {
            if (child.Name.StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // liens et jonctions : jamais suivis

            var relative = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
            if (child is DirectoryInfo sub)
            {
                Walk(sub, relative, now, result, skipped);
                continue;
            }

            if (IgnoredNames.Contains(child.Name) || child.Name.StartsWith("~$", StringComparison.Ordinal)
                || child.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                // Un nom que SafePath modifierait (espace final, caractère interdit…) serait refusé ou renommé par l'autre PC.
                if (SafePath.RelativePath(relative) != relative) throw new ArgumentException(relative);
            }
            catch (ArgumentException)
            {
                skipped?.Add(relative);
                continue;
            }

            var file = (FileInfo)child;
            var modified = file.LastWriteTimeUtc;
            result[relative] = new SyncEntry(file.Length, modified.Ticks, now - modified < SettleTime);
        }
    }
}

/// <summary>
/// Applique les changements dans un dossier synchronisé. Tout fichier écrasé ou supprimé est d'abord
/// déplacé dans <c>.lanlink-sync/trash</c> : rien n'est perdu.
/// </summary>
public sealed class SyncFileSystem
{
    public const string InternalDir = ".lanlink-sync";

    private readonly string _root;

    public SyncFileSystem(string root) => _root = Path.GetFullPath(root);

    private string TempDir => Path.Combine(_root, InternalDir, "tmp");
    private string TrashDir => Path.Combine(_root, InternalDir, "trash");

    public string NewTempPath()
    {
        Directory.CreateDirectory(TempDir);
        return Path.Combine(TempDir, Guid.NewGuid().ToString("N") + ".part");
    }

    public string Resolve(string relativePath) => SafePath.Combine(_root, SafePath.RelativePath(relativePath));

    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    /// <summary>Remplace (ou crée) le fichier par <paramref name="tempFile"/> et lui donne la date de modification d'origine.</summary>
    public void Replace(string relativePath, string tempFile, long modifiedTicks)
    {
        var destination = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) MoveToTrash(destination, relativePath);
        File.Move(tempFile, destination);
        try { File.SetLastWriteTimeUtc(destination, new DateTime(modifiedTicks, DateTimeKind.Utc)); }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException) { }
    }

    public void Delete(string relativePath)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path)) return;
        MoveToTrash(path, relativePath);

        // Retire les dossiers devenus vides (jamais la racine).
        var dir = Path.GetDirectoryName(path);
        while (dir is not null && dir.Length > _root.Length && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    private void MoveToTrash(string absolutePath, string relativePath)
    {
        var target = Path.Combine(TrashDir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(absolutePath, SafePath.Unique(target));
    }

    /// <summary>Supprime définitivement ce qui est à la corbeille depuis plus longtemps que <paramref name="age"/>, et les temporaires abandonnés.</summary>
    public void Cleanup(TimeSpan age)
    {
        try
        {
            if (Directory.Exists(TrashDir))
                foreach (var dir in Directory.EnumerateDirectories(TrashDir))
                    if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > age) Directory.Delete(dir, recursive: true);
            if (Directory.Exists(TempDir))
                foreach (var file in Directory.EnumerateFiles(TempDir))
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Dernier état synchronisé de chaque synchronisation (fichier par paire).</summary>
public sealed class SyncStateStore
{
    private readonly string _directory;

    public SyncStateStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    private string FileFor(Guid id) => Path.Combine(_directory, $"sync-{id:N}.json");

    public Dictionary<string, SyncEntry> Load(Guid id)
    {
        try
        {
            var path = FileFor(id);
            var data = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, SyncEntry>>(File.ReadAllText(path))
                : null;
            return new Dictionary<string, SyncEntry>(data ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new Dictionary<string, SyncEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(Guid id, IReadOnlyDictionary<string, SyncEntry> state)
    {
        var path = FileFor(id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(Guid id)
    {
        var path = FileFor(id);
        if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>Liste persistante des synchronisations. Les mots de passe sont chiffrés avec DPAPI.</summary>
public sealed class SyncPairStore
{
    private sealed class Stored
    {
        public SyncPair Pair { get; set; } = new();
        public string? PasswordProtected { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _file;
    private readonly bool _protect;
    private readonly object _lock = new();
    private readonly List<SyncPair> _pairs = new();

    public event Action? Changed;

    public SyncPairStore(string directory, bool protect = true)
    {
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "sync-pairs.json");
        _protect = protect;
        Load();
    }

    public IReadOnlyList<SyncPair> All()
    {
        lock (_lock) return _pairs.ToList();
    }

    public SyncPair? Get(Guid id)
    {
        lock (_lock) return _pairs.FirstOrDefault(p => p.Id == id);
    }

    public void Add(SyncPair pair)
    {
        lock (_lock)
        {
            _pairs.Add(pair);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Enregistre les modifications faites sur un objet obtenu par <see cref="Get"/>.</summary>
    public void Update(SyncPair pair)
    {
        lock (_lock) Save();
        Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        lock (_lock)
        {
            _pairs.RemoveAll(p => p.Id == id);
            Save();
        }
        Changed?.Invoke();
    }

    private void Save()
    {
        var items = _pairs.Select(p => new Stored
        {
            Pair = new SyncPair
            {
                Id = p.Id, Name = p.Name, LocalPath = p.LocalPath, PeerName = p.PeerName, PeerAddress = p.PeerAddress,
                PeerPort = p.PeerPort, PeerFingerprint = p.PeerFingerprint, IsInitiator = p.IsInitiator,
                IntervalSeconds = p.IntervalSeconds, Paused = p.Paused, LastSyncUtc = p.LastSyncUtc, LastError = p.LastError,
            },
            PasswordProtected = p.Password is null ? null : Protect(p.Password),
        }).ToList();
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(items, Options));
        File.Move(temp, _file, overwrite: true);
    }

    private void Load()
    {
        if (!File.Exists(_file)) return;
        try
        {
            foreach (var item in JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(_file)) ?? new())
            {
                item.Pair.Password = item.PasswordProtected is null ? null : Unprotect(item.PasswordProtected);
                _pairs.Add(item.Pair);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            _pairs.Clear();
        }
    }

    private string Protect(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        if (_protect && OperatingSystem.IsWindows())
            bytes = System.Security.Cryptography.ProtectedData.Protect(bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private string Unprotect(string value)
    {
        var bytes = Convert.FromBase64String(value);
        if (_protect && OperatingSystem.IsWindows())
            bytes = System.Security.Cryptography.ProtectedData.Unprotect(bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
