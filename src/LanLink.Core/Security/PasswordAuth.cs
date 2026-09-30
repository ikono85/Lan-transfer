using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace LanLink.Core.Security;

public sealed record Argon2Params(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>Paramètres de production (~0,3 s sur un PC récent).</summary>
    public static Argon2Params Default { get; } = new(64 * 1024, 3, 4);

    /// <summary>Paramètres très légers, réservés aux tests.</summary>
    public static Argon2Params Fast { get; } = new(256, 1, 1);

    /// <summary>Plancher accepté par un client : évite qu'un faux serveur impose une dérivation triviale.</summary>
    public static Argon2Params ClientMinimum { get; } = new(16 * 1024, 2, 1);

    /// <summary>Plafond accepté par un client : évite qu'un faux serveur n'épuise la mémoire.</summary>
    public static Argon2Params ClientMaximum { get; } = new(512 * 1024, 16, 16);

    public bool IsWithin(Argon2Params min, Argon2Params max) =>
        MemoryKiB >= min.MemoryKiB && MemoryKiB <= max.MemoryKiB
        && Iterations >= min.Iterations && Iterations <= max.Iterations
        && Parallelism >= min.Parallelism && Parallelism <= max.Parallelism;
}

/// <summary>Mot de passe permanent d'un PC : seul le résultat de la dérivation Argon2id est conservé.</summary>
public sealed class PasswordVerifier
{
    public byte[] Salt { get; }
    public byte[] Key { get; }
    public Argon2Params Params { get; }

    public PasswordVerifier(byte[] salt, byte[] key, Argon2Params p)
    {
        Salt = salt;
        Key = key;
        Params = p;
    }

    public static PasswordVerifier Create(string password, Argon2Params? p = null)
    {
        p ??= Argon2Params.Default;
        var salt = RandomNumberGenerator.GetBytes(16);
        return new PasswordVerifier(salt, DeriveKey(password, salt, p), p);
    }

    public static byte[] DeriveKey(string password, byte[] salt, Argon2Params p)
    {
        var pw = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        try
        {
            using var argon = new Argon2id(pw)
            {
                Salt = salt,
                MemorySize = p.MemoryKiB,
                Iterations = p.Iterations,
                DegreeOfParallelism = p.Parallelism,
            };
            return argon.GetBytes(32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pw);
        }
    }
}

public static class PasswordPolicy
{
    public const int MinLength = 12;

    /// <summary>Renvoie un message d'erreur, ou null si le mot de passe est acceptable.</summary>
    public static string? Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return $"Le mot de passe doit contenir au moins {MinLength} caractères.";
        if (password.Distinct().Count() < 5)
            return "Le mot de passe est trop répétitif.";
        return null;
    }
}

/// <summary>
/// Preuves mutuelles HMAC liées aux deux certificats TLS : un intermédiaire qui
/// relaierait la session présente des certificats différents sur chaque tronçon,
/// donc ses preuves ne concordent jamais.
/// </summary>
public static class HandshakeProof
{
    public static byte[] Compute(byte[] key, string role, byte[] serverNonce, byte[] clientNonce,
        byte[] serverCertHash, byte[] clientCertHash)
    {
        using var hmac = new HMACSHA256(key);
        var label = Encoding.ASCII.GetBytes("LanLink/1 " + role);
        var data = label.Concat(serverNonce).Concat(clientNonce)
            .Concat(serverCertHash).Concat(clientCertHash).ToArray();
        return hmac.ComputeHash(data);
    }

    public static bool Verify(byte[] expected, byte[] actual) =>
        CryptographicOperations.FixedTimeEquals(expected, actual);
}

/// <summary>Bloque temporairement une adresse après trop d'échecs d'authentification.</summary>
public sealed class AuthRateLimiter
{
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, List<DateTime>> _failures = new();
    private readonly object _lock = new();

    public AuthRateLimiter(int maxFailures = 5, TimeSpan? window = null, Func<DateTime>? now = null)
    {
        _maxFailures = maxFailures;
        _window = window ?? TimeSpan.FromMinutes(5);
        _now = now ?? (() => DateTime.UtcNow);
    }

    public bool IsBlocked(string address)
    {
        lock (_lock) return Recent(address).Count >= _maxFailures;
    }

    public void RecordFailure(string address)
    {
        lock (_lock) Recent(address).Add(_now());
    }

    public void RecordSuccess(string address)
    {
        lock (_lock) _failures.Remove(address);
    }

    private List<DateTime> Recent(string address)
    {
        if (!_failures.TryGetValue(address, out var list))
            _failures[address] = list = new List<DateTime>();
        var limit = _now() - _window;
        list.RemoveAll(t => t < limit);
        return list;
    }
}
