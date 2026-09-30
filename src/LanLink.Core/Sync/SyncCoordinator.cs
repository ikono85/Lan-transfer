using LanLink.Core.Net;
using LanLink.Core.Security;

namespace LanLink.Core.Sync;

public enum SyncState { Idle, Syncing, Error, Paused }

/// <param name="Result">Résultat de la dernière passe réussie, sinon null.</param>
public sealed record SyncStatus(Guid PairId, SyncState State, string Message, DateTime? LastSyncUtc, SyncResult? Result = null);

/// <summary>
/// Pilote les synchronisations dont ce PC est l'initiateur : passe périodique, déclenchement quelques secondes après
/// une modification locale, reprise avec attente croissante en cas d'erreur.
/// </summary>
public sealed class SyncCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

    private sealed class Runtime : IDisposable
    {
        public required Guid Id { get; init; }
        public CancellationTokenSource Cts { get; } = new();
        public SemaphoreSlim Trigger { get; } = new(0, 1);
        public FileSystemWatcher? Watcher { get; set; }
        public Task? Loop { get; set; }

        public void Signal()
        {
            try { Trigger.Release(); }
            catch (SemaphoreFullException) { /* déjà demandé */ }
        }

        public void Dispose()
        {
            Watcher?.Dispose();
            Cts.Dispose();
        }
    }

    private readonly SyncPairStore _pairs;
    private readonly SyncStateStore _states;
    private readonly DeviceIdentity _identity;
    private readonly Func<string> _deviceName;
    private readonly LinkClientOptions _options;
    private readonly Func<SyncPair, string?>? _resolveAddress;
    private readonly Dictionary<Guid, Runtime> _runtimes = new();
    private readonly object _lock = new();

    public event Action<SyncStatus>? StatusChanged;

    /// <param name="resolveAddress">Retrouve la nouvelle adresse d'un PC (par son nom) quand l'ancienne ne répond plus.</param>
    public SyncCoordinator(SyncPairStore pairs, SyncStateStore states, DeviceIdentity identity, Func<string> deviceName,
        LinkClientOptions? options = null, Func<SyncPair, string?>? resolveAddress = null)
    {
        _pairs = pairs;
        _states = states;
        _identity = identity;
        _deviceName = deviceName;
        _options = options ?? new LinkClientOptions();
        _resolveAddress = resolveAddress;
    }

    /// <summary>Démarre la surveillance de toutes les synchronisations initiées par ce PC.</summary>
    public void Start()
    {
        foreach (var pair in _pairs.All().Where(p => p.IsInitiator)) Watch(pair);
    }

    /// <summary>Commence à suivre une nouvelle paire (après sa création).</summary>
    public void Watch(SyncPair pair)
    {
        if (!pair.IsInitiator) return;
        lock (_lock)
        {
            if (_runtimes.ContainsKey(pair.Id)) return;
            var runtime = new Runtime { Id = pair.Id };
            runtime.Watcher = CreateWatcher(pair, runtime);
            runtime.Loop = Task.Run(() => LoopAsync(runtime));
            _runtimes[pair.Id] = runtime;
        }
    }

    public async Task UnwatchAsync(Guid id)
    {
        Runtime? runtime;
        lock (_lock)
        {
            if (!_runtimes.Remove(id, out runtime)) return;
        }
        runtime.Cts.Cancel();
        try { await (runtime.Loop ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        runtime.Dispose();
    }

    /// <summary>Lance une passe tout de suite (bouton « Synchroniser maintenant »).</summary>
    public void SyncNow(Guid id)
    {
        lock (_lock)
            if (_runtimes.TryGetValue(id, out var runtime)) runtime.Signal();
    }

    private FileSystemWatcher? CreateWatcher(SyncPair pair, Runtime runtime)
    {
        try
        {
            if (!Directory.Exists(pair.LocalPath)) return null;
            var watcher = new FileSystemWatcher(pair.LocalPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            void Changed(object _, FileSystemEventArgs e)
            {
                // Les écritures internes (temporaires, corbeille) ne doivent pas relancer une passe en boucle.
                if (e.FullPath.Contains(SyncFileSystem.InternalDir, StringComparison.OrdinalIgnoreCase)) return;
                runtime.Signal();
            }
            watcher.Changed += Changed;
            watcher.Created += Changed;
            watcher.Deleted += Changed;
            watcher.Renamed += (s, e) => Changed(s, e);
            watcher.Error += (_, _) => runtime.Signal(); // tampon saturé : on refait une passe complète
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
        {
            return null; // la passe périodique suffit
        }
    }

    private async Task LoopAsync(Runtime runtime)
    {
        var ct = runtime.Cts.Token;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var pair = _pairs.Get(runtime.Id);
            if (pair is null) return;

            if (pair.Paused)
            {
                Publish(pair, SyncState.Paused, "En pause.");
            }
            else
            {
                var ok = await RunOnceAsync(pair, ct).ConfigureAwait(false);
                failures = ok ? 0 : Math.Min(failures + 1, 8);
            }

            var wait = TimeSpan.FromSeconds(Math.Max(pair.IntervalSeconds, 10));
            if (failures > 0) wait = TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds * Math.Pow(2, failures), MaxBackoff.TotalSeconds));
            try
            {
                if (await runtime.Trigger.WaitAsync(wait, ct).ConfigureAwait(false))
                {
                    await Task.Delay(Debounce, ct).ConfigureAwait(false); // laisse finir une copie en cours
                    while (runtime.Trigger.CurrentCount > 0) await runtime.Trigger.WaitAsync(0, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Une passe de synchronisation. Renvoie vrai si elle s'est terminée sans erreur de connexion.</summary>
    public async Task<bool> RunOnceAsync(SyncPair pair, CancellationToken ct)
    {
        Publish(pair, SyncState.Syncing, "Connexion…");
        try
        {
            await using var link = await ConnectAsync(pair, ct).ConfigureAwait(false);
            if (!string.Equals(link.Server.CertFingerprint, pair.PeerFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new SyncAbortedException("Le certificat de l'autre PC a changé : par sécurité, la synchronisation est bloquée. Supprime-la et recrée-la si c'est normal.");

            var progress = new Progress<SyncProgress>(p =>
                Publish(pair, SyncState.Syncing, p.CurrentPath is null ? p.Phase : $"{p.Phase} : {p.CurrentPath} ({p.Done}/{p.Total})"));
            var result = await link.SyncAsync(pair, _states, progress, ct).ConfigureAwait(false);

            pair.LastSyncUtc = DateTime.UtcNow;
            pair.LastError = result.Errors.Count > 0 ? $"{result.Errors.Count} fichier(s) en échec : {result.Errors[0]}" : null;
            _pairs.Update(pair);
            Publish(pair, result.Errors.Count > 0 ? SyncState.Error : SyncState.Idle, Summary(result), result);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Toute autre erreur (réseau, disque, protocole…) est affichée et la boucle continue avec attente croissante.
            pair.LastError = ex.Message;
            _pairs.Update(pair);
            Publish(pair, SyncState.Error, ex.Message);
            return false;
        }
    }

    private async Task<LinkSession> ConnectAsync(SyncPair pair, CancellationToken ct)
    {
        if (pair.Password is null) throw new SyncAbortedException("Mot de passe de l'autre PC introuvable.");
        try
        {
            return await LinkClient.ConnectAsync(pair.PeerAddress, pair.PeerPort, pair.Password, _identity, _deviceName(), _options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or TimeoutException && _resolveAddress?.Invoke(pair) is { } address
                                       && address != pair.PeerAddress)
        {
            // Le PC a changé d'adresse (DHCP) : on le retrouve par son nom.
            var link = await LinkClient.ConnectAsync(address, pair.PeerPort, pair.Password, _identity, _deviceName(), _options, ct).ConfigureAwait(false);
            pair.PeerAddress = address;
            _pairs.Update(pair);
            return link;
        }
    }

    private static string Summary(SyncResult r) => r.Total == 0 && r.Errors.Count == 0
        ? "À jour."
        : $"↑ {r.Pushed} envoyé(s), ↓ {r.Pulled} reçu(s), {r.DeletedLocal + r.DeletedRemote} supprimé(s)"
          + (r.Conflicts > 0 ? $", {r.Conflicts} conflit(s) (ancienne version en corbeille)" : "")
          + (r.Errors.Count > 0 ? $", {r.Errors.Count} erreur(s)" : "");

    private void Publish(SyncPair pair, SyncState state, string message, SyncResult? result = null) =>
        StatusChanged?.Invoke(new SyncStatus(pair.Id, state, message, pair.LastSyncUtc, result));

    public async ValueTask DisposeAsync()
    {
        Guid[] ids;
        lock (_lock) ids = _runtimes.Keys.ToArray();
        foreach (var id in ids) await UnwatchAsync(id).ConfigureAwait(false);
    }
}
