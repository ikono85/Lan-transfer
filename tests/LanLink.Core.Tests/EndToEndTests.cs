using System.Collections.Concurrent;
using LanLink.Core.Net;
using LanLink.Core.Protocol;
using LanLink.Core.Remote;
using LanLink.Core.Sync;
using LanLink.Core.Security;
using LanLink.Core.Transfer;
using Xunit;

namespace LanLink.Core.Tests;

internal sealed class TestHost : ILinkHost
{
    public string DownloadDirectory { get; }
    public bool Accept { get; set; } = true;
    public ConcurrentQueue<TransferRecord> Finished { get; } = new();
    public ConcurrentQueue<string> Chats { get; } = new();
    public ConcurrentQueue<string> Logs { get; } = new();

    public TestHost() => DownloadDirectory = Directory.CreateTempSubdirectory("lanlink-recv").FullName;

    public Task<bool> ConfirmIncomingAsync(RemotePeer peer, TransferOffer offer, CancellationToken ct) => Task.FromResult(Accept);
    public void OnTransferProgress(TransferProgress progress) { }
    public void OnTransferFinished(TransferRecord record) => Finished.Enqueue(record);
    public void OnChatMessage(RemotePeer peer, string text) => Chats.Enqueue(text);
    public void Log(string message) => Logs.Enqueue(message);

    // Partage d ecran (configure par les tests)
    public FakeEnvironment? Env { get; set; }
    public RemoteGrant? Grant { get; set; }
    public IRemoteHostEnvironment? RemoteEnvironment => Env;
    public Task<RemoteGrant?> ConfirmRemoteAsync(RemotePeer peer, RemoteRequest request, CancellationToken ct) => Task.FromResult(Grant);

    // Synchronisation (configuree par les tests)
    public SyncPairStore? SyncStore { get; set; }
    public string? PairFolder { get; set; }

    public Task<SyncPair?> ConfirmSyncPairAsync(RemotePeer peer, SyncPairRequest request, CancellationToken ct)
    {
        if (PairFolder is null || SyncStore is null) return Task.FromResult<SyncPair?>(null);
        var pair = new SyncPair
        {
            Id = request.PairId, Name = request.Name, LocalPath = PairFolder, PeerName = peer.DeviceName,
            PeerAddress = peer.Address, PeerFingerprint = peer.CertFingerprint, IsInitiator = false,
        };
        SyncStore.Add(pair);
        return Task.FromResult<SyncPair?>(pair);
    }

    public SyncPair? FindSyncPair(Guid id) => SyncStore?.Get(id);
}

public sealed class EndToEndFixture : IAsyncLifetime
{
    public const string Password = "une phrase de passe solide";

    internal TestHost Host { get; private set; } = null!;
    internal LinkServer Server { get; private set; } = null!;
    internal DeviceIdentity ServerIdentity { get; private set; } = null!;
    internal DeviceIdentity ClientIdentity { get; private set; } = null!;
    internal LinkClientOptions ClientOptions { get; } = new() { MinArgon2 = Argon2Params.Fast };

    public Task InitializeAsync()
    {
        Host = new TestHost();
        ServerIdentity = DeviceIdentity.Create();
        ClientIdentity = DeviceIdentity.Create();
        Server = new LinkServer(ServerIdentity, "PC-Serveur",
            PasswordVerifier.Create(Password, Argon2Params.Fast), Host,
            new AuthRateLimiter(maxFailures: 100));
        Server.Start(0);
        return Task.CompletedTask;
    }

    internal Task<LinkSession> ConnectAsync(string password = Password) =>
        LinkClient.ConnectAsync("127.0.0.1", Server.Port, password, ClientIdentity, "PC-Client", ClientOptions);

    public async Task DisposeAsync()
    {
        await Server.DisposeAsync();
        ServerIdentity.Dispose();
        ClientIdentity.Dispose();
    }
}

public class EndToEndTests : IClassFixture<EndToEndFixture>
{
    private readonly EndToEndFixture _f;

    public EndToEndTests(EndToEndFixture fixture) => _f = fixture;

    private static string TempFile(string name, int size)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("lanlink-src").FullName, name);
        var data = new byte[size];
        new Random(size).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private async Task<TransferRecord> WaitFinishedAsync(int previousCount)
    {
        for (var i = 0; i < 200 && _f.Host.Finished.Count <= previousCount; i++) await Task.Delay(25);
        Assert.True(_f.Host.Finished.Count > previousCount, "Le récepteur n'a pas terminé.");
        return _f.Host.Finished.Last();
    }

    [Fact]
    public async Task WrongPassword_IsRejected()
    {
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => _f.ConnectAsync("mauvais mot de passe"));
    }

    [Fact]
    public async Task Chat_IsDelivered()
    {
        await using var session = await _f.ConnectAsync();
        Assert.Equal("PC-Serveur", session.Server.DeviceName);
        await session.SendChatAsync("bonjour");
        Assert.Contains("bonjour", _f.Host.Chats);
    }

    [Fact]
    public async Task File_IsTransferredIntact()
    {
        var source = TempFile("données.bin", 1_000_003); // pas un multiple de la taille de bloc
        var before = _f.Host.Finished.Count;
        await using (var session = await _f.ConnectAsync())
            await session.SendPathAsync(source);

        var record = await WaitFinishedAsync(before);
        Assert.Equal(TransferStatus.Completed, record.Status);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(record.Path!));
    }

    [Fact]
    public async Task EmptyFile_IsTransferred()
    {
        var source = TempFile("vide.txt", 0);
        var before = _f.Host.Finished.Count;
        await using (var session = await _f.ConnectAsync())
            await session.SendPathAsync(source);
        var record = await WaitFinishedAsync(before);
        Assert.Equal(TransferStatus.Completed, record.Status);
        Assert.Empty(File.ReadAllBytes(record.Path!));
    }

    [Fact]
    public async Task Folder_KeepsStructure()
    {
        var root = Directory.CreateTempSubdirectory("lanlink-dir").FullName;
        var folder = Path.Combine(root, "projet");
        Directory.CreateDirectory(Path.Combine(folder, "sous", "dossier"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "alpha");
        File.WriteAllBytes(Path.Combine(folder, "sous", "dossier", "b.bin"), new byte[300_000]);

        var before = _f.Host.Finished.Count;
        await using (var session = await _f.ConnectAsync())
            await session.SendPathAsync(folder);

        var record = await WaitFinishedAsync(before);
        Assert.Equal(TransferStatus.Completed, record.Status);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(record.Path!, "a.txt")));
        Assert.Equal(300_000, new FileInfo(Path.Combine(record.Path!, "sous", "dossier", "b.bin")).Length);
    }

    [Fact]
    public async Task Rejected_TransferThrows()
    {
        var source = TempFile("refuse.txt", 10);
        _f.Host.Accept = false;
        try
        {
            await using var session = await _f.ConnectAsync();
            await Assert.ThrowsAsync<TransferRejectedException>(() => session.SendPathAsync(source));
        }
        finally
        {
            _f.Host.Accept = true;
        }
    }

    [Fact]
    public async Task InterruptedTransfer_ResumesFromPartialData()
    {
        const int size = 3 * 1024 * 1024;
        var source = TempFile("gros.bin", size);
        var before = _f.Host.Finished.Count;

        // 1re tentative : on coupe la connexion après ~1,5 Mo.
        using (var cts = new CancellationTokenSource())
        {
            await using var session = await _f.ConnectAsync();
            var progress = new Progress<TransferProgress>(p => { if (p.BytesDone > size / 2) cts.Cancel(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendPathAsync(source, progress, cts.Token));
        }
        var interrupted = await WaitFinishedAsync(before);
        Assert.Equal(TransferStatus.Interrupted, interrupted.Status);

        // 2e tentative : le point de reprise doit être > 0.
        long firstReported = -1;
        var progress2 = new Progress<TransferProgress>(p => { if (firstReported < 0 && p.BytesDone > 0) firstReported = p.BytesDone; });
        await using (var session = await _f.ConnectAsync())
            await session.SendPathAsync(source, progress2);

        var record = await WaitFinishedAsync(before + 1);
        Assert.Equal(TransferStatus.Completed, record.Status);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(record.Path!));
        Assert.True(firstReported >= 256 * 1024, $"La reprise n'a pas eu lieu (premier octet reporté : {firstReported}).");
    }

    [Fact]
    public async Task MaliciousOffer_WithTraversal_IsRefused()
    {
        await using var session = await _f.ConnectAsync();
        var field = typeof(LinkSession).GetField("_channel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var channel = (FrameChannel)field.GetValue(session)!;

        var evil = new TransferOffer("folder", "piege", 1, new List<FileEntry> { new("../../evil.txt", 1, 0) });
        await channel.WriteJsonAsync(FrameType.Offer, evil);
        channel.ReadTimeout = TimeSpan.FromSeconds(5);
        await Assert.ThrowsAnyAsync<Exception>(async () => await channel.ExpectAsync(FrameType.OfferReply));
        Assert.False(File.Exists(Path.Combine(_f.Host.DownloadDirectory, "..", "..", "evil.txt")));
    }
}

public class AuthLockoutTests
{
    [Fact]
    public async Task TooManyFailures_BlockAddress()
    {
        var host = new TestHost();
        using var serverId = DeviceIdentity.Create();
        using var clientId = DeviceIdentity.Create();
        await using var server = new LinkServer(serverId, "S", PasswordVerifier.Create("une phrase de passe solide", Argon2Params.Fast),
            host, new AuthRateLimiter(maxFailures: 2));
        server.Start(0);
        var options = new LinkClientOptions { MinArgon2 = Argon2Params.Fast };

        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
                LinkClient.ConnectAsync("127.0.0.1", server.Port, "mauvais", clientId, "C", options));

        // Même avec le bon mot de passe, l'adresse est maintenant bloquée.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            LinkClient.ConnectAsync("127.0.0.1", server.Port, "une phrase de passe solide", clientId, "C", options));
    }

    [Fact]
    public async Task Client_RefusesWeakKdfParameters()
    {
        var host = new TestHost();
        using var serverId = DeviceIdentity.Create();
        using var clientId = DeviceIdentity.Create();
        await using var server = new LinkServer(serverId, "S", PasswordVerifier.Create("une phrase de passe solide", Argon2Params.Fast), host);
        server.Start(0);
        // Options par défaut : plancher de production, que Argon2Params.Fast ne respecte pas.
        await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            LinkClient.ConnectAsync("127.0.0.1", server.Port, "une phrase de passe solide", clientId, "C"));
    }
}
