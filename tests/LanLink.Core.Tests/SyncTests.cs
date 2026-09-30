using LanLink.Core.Net;
using LanLink.Core.Sync;
using Xunit;

namespace LanLink.Core.Tests;

public class SyncPlannerTests
{
    private static readonly long T0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly long Later = T0 + TimeSpan.TicksPerMinute * 10;

    private static Dictionary<string, SyncEntry> Map(params (string Path, long Size, long Ticks)[] items) =>
        items.ToDictionary(i => i.Path, i => new SyncEntry(i.Size, i.Ticks), StringComparer.OrdinalIgnoreCase);

    private static SyncPlan Plan(Dictionary<string, SyncEntry> l, Dictionary<string, SyncEntry> r, Dictionary<string, SyncEntry>? b = null) =>
        SyncPlanner.Plan(l, r, b ?? Map());

    [Fact]
    public void NewFileOnEitherSide_IsCopied()
    {
        var plan = Plan(Map(("a", 1, T0)), Map(("b", 2, T0)));
        Assert.Contains(plan.Actions, a => a.Path == "a" && a.Kind == SyncActionKind.Push);
        Assert.Contains(plan.Actions, a => a.Path == "b" && a.Kind == SyncActionKind.Pull);
    }

    [Fact]
    public void IdenticalFiles_NeedNothing_AndEnterBaseline()
    {
        var plan = Plan(Map(("a", 5, T0)), Map(("a", 5, T0 + TimeSpan.TicksPerSecond)));
        Assert.Empty(plan.Actions);
        Assert.True(plan.Baseline.ContainsKey("a"));
    }

    [Fact]
    public void ChangedOnlyLocally_IsPushed_ChangedOnlyRemotely_IsPulled()
    {
        var baseline = Map(("a", 5, T0), ("b", 5, T0));
        var plan = Plan(Map(("a", 6, Later), ("b", 5, T0)), Map(("a", 5, T0), ("b", 9, Later)), baseline);
        Assert.Equal(SyncActionKind.Push, plan.Actions.Single(a => a.Path == "a").Kind);
        Assert.Equal(SyncActionKind.Pull, plan.Actions.Single(a => a.Path == "b").Kind);
        Assert.All(plan.Actions, a => Assert.False(a.Conflict));
    }

    [Fact]
    public void ChangedOnBothSides_NewestWins_AndIsFlaggedConflict()
    {
        var baseline = Map(("a", 5, T0));
        var localNewer = Plan(Map(("a", 6, Later + 5)), Map(("a", 7, Later)), baseline).Actions.Single();
        Assert.Equal(SyncActionKind.Push, localNewer.Kind);
        Assert.True(localNewer.Conflict);

        var remoteNewer = Plan(Map(("a", 6, Later)), Map(("a", 7, Later + TimeSpan.TicksPerMinute)), baseline).Actions.Single();
        Assert.Equal(SyncActionKind.Pull, remoteNewer.Kind);
        Assert.True(remoteNewer.Conflict);
    }

    [Fact]
    public void DeletionPropagates_OnlyIfOtherSideIsUnchanged()
    {
        var baseline = Map(("a", 5, T0), ("b", 5, T0));

        // supprimé à distance, inchangé ici -> suppression locale ; modifié ici -> on garde et on renvoie
        var plan = Plan(Map(("a", 5, T0), ("b", 8, Later)), Map(), baseline);
        Assert.Equal(SyncActionKind.DeleteLocal, plan.Actions.Single(x => x.Path == "a").Kind);
        var kept = plan.Actions.Single(x => x.Path == "b");
        Assert.Equal(SyncActionKind.Push, kept.Kind);
        Assert.True(kept.Conflict);

        // supprimé ici, inchangé là-bas -> suppression distante ; modifié là-bas -> on le récupère
        var plan2 = Plan(Map(), Map(("a", 5, T0), ("b", 8, Later)), baseline);
        Assert.Equal(SyncActionKind.DeleteRemote, plan2.Actions.Single(x => x.Path == "a").Kind);
        Assert.Equal(SyncActionKind.Pull, plan2.Actions.Single(x => x.Path == "b").Kind);
    }

    [Fact]
    public void DeletedEverywhere_DisappearsFromBaseline()
    {
        var plan = Plan(Map(), Map(), Map(("a", 5, T0)));
        Assert.Empty(plan.Actions);
        Assert.Empty(plan.Baseline);
    }

    [Fact]
    public void BusyFile_IsSkipped_AndKeepsItsBaseline()
    {
        var local = new Dictionary<string, SyncEntry>(StringComparer.OrdinalIgnoreCase) { ["a"] = new SyncEntry(10, Later, Busy: true) };
        var plan = SyncPlanner.Plan(local, Map(("a", 5, T0)), Map(("a", 5, T0)));
        Assert.Empty(plan.Actions);
        Assert.Equal(T0, plan.Baseline["a"].Ticks);
    }

    [Fact]
    public void PathsAreCaseInsensitive()
    {
        var plan = Plan(Map(("Doc.txt", 5, T0)), Map(("doc.TXT", 5, T0)));
        Assert.Empty(plan.Actions);
    }
}

public class SyncFilesTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("lanlink-sync").FullName;

    [Fact]
    public void Scanner_SkipsInternalIgnoredAndBusyMarksRecent()
    {
        var root = TempDir();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, ".lanlink-sync", "trash"));
        File.WriteAllText(Path.Combine(root, "keep.txt"), "x");
        File.WriteAllText(Path.Combine(root, "sub", "deep.txt"), "y");
        File.WriteAllText(Path.Combine(root, ".lanlink-sync", "trash", "old.txt"), "z");
        File.WriteAllText(Path.Combine(root, "Thumbs.db"), "t");
        File.WriteAllText(Path.Combine(root, "~$temp.docx"), "t");
        File.WriteAllText(Path.Combine(root, "partial.part"), "t");
        File.SetLastWriteTimeUtc(Path.Combine(root, "keep.txt"), DateTime.UtcNow.AddHours(-1));

        var entries = FolderScanner.Scan(root);
        Assert.Equal(new[] { "keep.txt", "sub/deep.txt" }, entries.Keys.Order().ToArray());
        Assert.False(entries["keep.txt"].Busy);
        Assert.True(entries["sub/deep.txt"].Busy); // écrit à l'instant
    }

    [Fact]
    public void Replace_And_Delete_KeepOldVersionsInTrash()
    {
        var root = TempDir();
        var fs = new SyncFileSystem(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "ancien");

        var temp = fs.NewTempPath();
        File.WriteAllText(temp, "nouveau");
        var ticks = DateTime.UtcNow.AddDays(-2).Ticks;
        fs.Replace("a.txt", temp, ticks);

        Assert.Equal("nouveau", File.ReadAllText(Path.Combine(root, "a.txt")));
        Assert.Equal(ticks, File.GetLastWriteTimeUtc(Path.Combine(root, "a.txt")).Ticks);
        var trash = Path.Combine(root, ".lanlink-sync", "trash");
        Assert.Equal("ancien", File.ReadAllText(Directory.GetFiles(trash, "a.txt", SearchOption.AllDirectories).Single()));

        Directory.CreateDirectory(Path.Combine(root, "d", "e"));
        File.WriteAllText(Path.Combine(root, "d", "e", "f.txt"), "f");
        fs.Delete("d/e/f.txt");
        Assert.False(Directory.Exists(Path.Combine(root, "d"))); // dossiers vides retirés
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void FileSystem_RejectsPathsOutsideRoot()
    {
        var fs = new SyncFileSystem(TempDir());
        Assert.Throws<ArgumentException>(() => fs.Resolve("../evil.txt"));
        Assert.Throws<ArgumentException>(() => fs.Resolve("a/../../evil.txt"));
    }

    [Fact]
    public void PairStore_RoundTrips_AndProtectsPassword()
    {
        var dir = TempDir();
        var store = new SyncPairStore(dir);
        var pair = new SyncPair { Name = "Docs", LocalPath = @"C:\Docs", IsInitiator = true, Password = "secret très secret", PeerFingerprint = "AB" };
        store.Add(pair);

        Assert.DoesNotContain("secret très secret", File.ReadAllText(Path.Combine(dir, "sync-pairs.json")));
        var reloaded = new SyncPairStore(dir).Get(pair.Id)!;
        Assert.Equal("secret très secret", reloaded.Password);
        Assert.Equal("Docs", reloaded.Name);
    }

    [Fact]
    public void StateStore_RoundTrips()
    {
        var states = new SyncStateStore(TempDir());
        var id = Guid.NewGuid();
        Assert.Empty(states.Load(id));
        states.Save(id, new Dictionary<string, SyncEntry> { ["a/b.txt"] = new(3, 99) });
        Assert.Equal(new SyncEntry(3, 99), states.Load(id)["A/B.TXT"]);
    }
}

public class SyncEndToEndTests : IClassFixture<EndToEndFixture>
{
    private readonly EndToEndFixture _f;
    private readonly DateTime _t0 = DateTime.UtcNow.AddHours(-1);

    public SyncEndToEndTests(EndToEndFixture fixture) => _f = fixture;

    private static string TempDir(string prefix) => Directory.CreateTempSubdirectory(prefix).FullName;

    private static void Write(string root, string rel, string content, DateTime modifiedUtc)
    {
        var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }

    private static string Read(string root, string rel) => File.ReadAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

    private static bool Exists(string root, string rel) => File.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Crée la paire côté initiateur (dossier a) et côté passif (dossier b), comme le fait l'interface.</summary>
    private async Task<(SyncPair Pair, string A, string B, SyncStateStore States)> CreatePairAsync()
    {
        var a = TempDir("lanlink-A");
        var b = TempDir("lanlink-B");
        _f.Host.SyncStore = new SyncPairStore(TempDir("lanlink-pairs"), protect: false);
        _f.Host.PairFolder = b;

        var pair = new SyncPair
        {
            Id = Guid.NewGuid(), Name = "Docs", LocalPath = a, PeerName = "PC-Serveur", PeerAddress = "127.0.0.1",
            PeerPort = _f.Server.Port, IsInitiator = true, Password = EndToEndFixture.Password,
        };
        await using var link = await _f.ConnectAsync();
        var reply = await link.RequestSyncPairAsync(new SyncPairRequest(pair.Id, pair.Name));
        Assert.True(reply.Accepted);
        pair.PeerFingerprint = link.Server.CertFingerprint;
        return (pair, a, b, new SyncStateStore(TempDir("lanlink-state")));
    }

    private async Task<SyncResult> SyncAsync(SyncPair pair, SyncStateStore states)
    {
        await using var link = await _f.ConnectAsync();
        return await link.SyncAsync(pair, states);
    }

    [Fact]
    public async Task TwoWaySync_CopiesMergesAndResolvesConflicts()
    {
        var (pair, a, b, states) = await CreatePairAsync();
        Write(a, "a.txt", "A1", _t0);
        Write(a, "sub/b.txt", "B1", _t0);
        Write(b, "c.txt", "C1", _t0);

        // 1re passe : union des deux dossiers
        var first = await SyncAsync(pair, states);
        Assert.Equal(2, first.Pushed);
        Assert.Equal(1, first.Pulled);
        Assert.Empty(first.Errors);
        Assert.Equal("A1", Read(b, "a.txt"));
        Assert.Equal("B1", Read(b, "sub/b.txt"));
        Assert.Equal("C1", Read(a, "c.txt"));
        Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(a, "a.txt")).Ticks, File.GetLastWriteTimeUtc(Path.Combine(b, "a.txt")).Ticks);

        // 2e passe sans changement : rien à faire
        var idle = await SyncAsync(pair, states);
        Assert.Equal(0, idle.Total);

        // Modifications : a.txt côté A, c.txt supprimé côté B, sub/b.txt modifié des deux côtés (B plus récent)
        Write(a, "a.txt", "A2", _t0.AddMinutes(10));
        File.Delete(Path.Combine(b, "c.txt"));
        Write(a, "sub/b.txt", "B-de-A", _t0.AddMinutes(20));
        Write(b, "sub/b.txt", "B-de-B", _t0.AddMinutes(30));

        var second = await SyncAsync(pair, states);
        Assert.Equal("A2", Read(b, "a.txt"));
        Assert.False(Exists(a, "c.txt"));
        Assert.Equal("B-de-B", Read(a, "sub/b.txt"));
        Assert.Equal("B-de-B", Read(b, "sub/b.txt"));
        Assert.Equal(1, second.Conflicts);
        Assert.Empty(second.Errors);

        // Rien n'est perdu : c.txt et la version perdante de b.txt sont à la corbeille de A
        var trash = Path.Combine(a, ".lanlink-sync", "trash");
        Assert.Equal("C1", File.ReadAllText(Directory.GetFiles(trash, "c.txt", SearchOption.AllDirectories).Single()));
        Assert.Equal("B-de-A", File.ReadAllText(Directory.GetFiles(trash, "b.txt", SearchOption.AllDirectories).Single()));
    }

    [Fact]
    public async Task DeletionOnInitiator_PropagatesToPassive()
    {
        var (pair, a, b, states) = await CreatePairAsync();
        Write(a, "x.txt", "X", _t0);
        await SyncAsync(pair, states);
        Assert.True(Exists(b, "x.txt"));

        File.Delete(Path.Combine(a, "x.txt"));
        var result = await SyncAsync(pair, states);
        Assert.Equal(1, result.DeletedRemote);
        Assert.False(Exists(b, "x.txt"));
        Assert.Single(Directory.GetFiles(Path.Combine(b, ".lanlink-sync", "trash"), "x.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FileBeingWritten_IsSkippedThenSynced()
    {
        var (pair, a, b, states) = await CreatePairAsync();
        Write(a, "fresh.txt", "encore en cours", DateTime.UtcNow);

        var first = await SyncAsync(pair, states);
        Assert.Equal(0, first.Total);
        Assert.False(Exists(b, "fresh.txt"));

        File.SetLastWriteTimeUtc(Path.Combine(a, "fresh.txt"), _t0);
        var second = await SyncAsync(pair, states);
        Assert.Equal(1, second.Pushed);
        Assert.Equal("encore en cours", Read(b, "fresh.txt"));
    }

    [Fact]
    public async Task MassDeletion_IsRefused()
    {
        var (pair, a, b, states) = await CreatePairAsync();
        for (var i = 0; i < 12; i++) Write(a, $"f{i}.txt", "x" + i, _t0);
        await SyncAsync(pair, states);
        Assert.Equal(12, Directory.GetFiles(b, "f*.txt").Length);

        foreach (var file in Directory.GetFiles(a, "f*.txt")) File.Delete(file); // dossier « vidé » (disque débranché ?)
        await Assert.ThrowsAsync<SyncAbortedException>(() => SyncAsync(pair, states));
        Assert.Equal(12, Directory.GetFiles(b, "f*.txt").Length);
    }

    [Fact]
    public async Task MissingLocalFolder_IsRefused()
    {
        var (pair, a, _, states) = await CreatePairAsync();
        Directory.Delete(a, recursive: true);
        await Assert.ThrowsAsync<SyncAbortedException>(() => SyncAsync(pair, states));
    }

    [Fact]
    public async Task UnknownPair_IsRefusedByPassiveSide()
    {
        var (pair, _, _, states) = await CreatePairAsync();
        pair.Id = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<SyncAbortedException>(() => SyncAsync(pair, states));
        Assert.Contains("inconnue", ex.Message);
    }

    [Fact]
    public async Task PairRequest_CanBeRefused()
    {
        _f.Host.SyncStore = new SyncPairStore(TempDir("lanlink-pairs"), protect: false);
        _f.Host.PairFolder = null;
        await using var link = await _f.ConnectAsync();
        var reply = await link.RequestSyncPairAsync(new SyncPairRequest(Guid.NewGuid(), "Docs"));
        Assert.False(reply.Accepted);
    }

    [Fact]
    public async Task Coordinator_SyncsOnStartAndAfterLocalChange()
    {
        var (pair, a, b, states) = await CreatePairAsync();
        Write(a, "first.txt", "1", _t0);

        var store = new SyncPairStore(TempDir("lanlink-cpairs"), protect: false);
        store.Add(pair);
        await using var coordinator = new SyncCoordinator(store, states, _f.ClientIdentity, () => "PC-Client", _f.ClientOptions);
        var idle = new TaskCompletionSource<SyncStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StatusChanged += s =>
        {
            if (s.State == SyncState.Idle && s.Result is { Total: > 0 }) idle.TrySetResult(s);
        };
        coordinator.Start();

        await idle.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("1", Read(b, "first.txt"));

        // Modification locale : détectée par l'observateur de fichiers, synchronisée après un court délai
        Write(a, "second.txt", "2", _t0);
        for (var i = 0; i < 300 && !Exists(b, "second.txt"); i++) await Task.Delay(50);
        Assert.Equal("2", Read(b, "second.txt"));
        Assert.NotNull(store.Get(pair.Id)!.LastSyncUtc);
    }
}
