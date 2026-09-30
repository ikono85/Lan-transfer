namespace LanLink.Core.Security;

/// <summary>
/// Assainit les noms et chemins reçus du réseau (traversée de répertoires,
/// noms réservés Windows, flux NTFS alternatifs, caractères de contrôle).
/// </summary>
public static class SafePath
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const string ForbiddenChars = "<>:\"|?*";
    private const int MaxNameLength = 200;

    /// <summary>Nom de fichier sûr (dernier segment, sans séparateur). Lève <see cref="ArgumentException"/> si invalide.</summary>
    public static string FileName(string? raw)
    {
        var name = (raw ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var cleaned = new string(name.Where(c => ForbiddenChars.IndexOf(c) < 0 && c >= 32).ToArray())
            .Trim(' ', '.');
        if (cleaned.Length == 0 || cleaned.Length > MaxNameLength
            || Reserved.Contains(cleaned.Split('.')[0]))
            throw new ArgumentException($"Nom de fichier invalide : {raw}");
        return cleaned;
    }

    /// <summary>Chemin relatif sûr (jamais absolu, jamais de « .. »), séparateur '/'.</summary>
    public static string RelativePath(string? raw)
    {
        var parts = (raw ?? "").Replace('\\', '/').Split('/')
            .Where(p => p.Length > 0 && p != ".").ToArray();
        if (parts.Length == 0 || parts.Length > 64)
            throw new ArgumentException($"Chemin invalide : {raw}");
        return string.Join('/', parts.Select(FileName));
    }

    /// <summary>Combine <paramref name="root"/> et un chemin relatif sûr en vérifiant qu'on reste dans <paramref name="root"/>.</summary>
    public static string Combine(string root, string safeRelative)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootFull, safeRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Chemin hors du dossier de destination : {safeRelative}");
        return full;
    }

    /// <summary>Chemin libre : « nom (1).ext », « nom (2).ext »… si le fichier ou dossier existe déjà.</summary>
    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}
