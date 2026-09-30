using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using LanLink.Core.Protocol;
using LanLink.Core.Remote;
using LanLink.Core.Sync;
using LanLink.Core.Security;
using LanLink.Core.Transfer;

namespace LanLink.Core.Net;

public sealed class AuthenticationFailedException : Exception
{
    public AuthenticationFailedException(string message) : base(message) { }
}

public sealed record LinkClientOptions
{
    public Argon2Params MinArgon2 { get; init; } = Argon2Params.ClientMinimum;
    public Argon2Params MaxArgon2 { get; init; } = Argon2Params.ClientMaximum;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

public static class LinkClient
{
    public static async Task<LinkSession> ConnectAsync(string host, int port, string password,
        DeviceIdentity identity, string deviceName, LinkClientOptions? options = null, CancellationToken ct = default)
    {
        options ??= new LinkClientOptions();
        var tcp = new TcpClient { NoDelay = true };
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        SslStream? ssl = null;
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(options.ConnectTimeout);
                try { await tcp.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Impossible de joindre {host}:{port}.");
                }

                ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "lanlink",
                    ClientCertificates = new System.Security.Cryptography.X509Certificates.X509CertificateCollection { identity.Certificate },
                    EnabledSslProtocols = SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                }, connectCts.Token).ConfigureAwait(false);
            }

            var channel = new FrameChannel(ssl) { ReadTimeout = TimeSpan.FromSeconds(30) };
            var hello = await ReadHelloAsync(channel, ct).ConfigureAwait(false);

            var p = new Argon2Params(hello.MemoryKiB, hello.Iterations, hello.Parallelism);
            if (!p.IsWithin(options.MinArgon2, options.MaxArgon2))
                throw new AuthenticationFailedException("Le serveur impose des paramètres de dérivation inacceptables.");
            var serverNonce = Convert.FromBase64String(hello.Nonce);
            if (serverNonce.Length != 32) throw new LinkProtocolException("Nonce serveur invalide.");

            var key = await Task.Run(() => PasswordVerifier.DeriveKey(password, Convert.FromBase64String(hello.Salt), p), ct).ConfigureAwait(false);
            var serverCertHash = SHA256.HashData(ssl.RemoteCertificate!.GetRawCertData());
            var clientNonce = RandomNumberGenerator.GetBytes(32);

            var proof = HandshakeProof.Compute(key, "client", serverNonce, clientNonce, serverCertHash, identity.Fingerprint);
            await channel.WriteJsonAsync(FrameType.ClientAuth,
                new ClientAuthMessage(deviceName, Convert.ToBase64String(clientNonce), Convert.ToBase64String(proof)), ct).ConfigureAwait(false);

            var result = (await channel.ExpectAsync(FrameType.ServerAuth, ct).ConfigureAwait(false)).Json<ServerAuthMessage>();
            if (!result.Ok)
                throw new AuthenticationFailedException(result.Error ?? "Authentification refusée.");
            var expected = HandshakeProof.Compute(key, "server", serverNonce, clientNonce, serverCertHash, identity.Fingerprint);
            if (result.Proof is null || !HandshakeProof.Verify(expected, Convert.FromBase64String(result.Proof)))
                throw new AuthenticationFailedException("Le serveur n'a pas prouvé qu'il connaît le mot de passe.");

            channel.ReadTimeout = null;
            var address = host;
            var peer = new RemotePeer(address, hello.DeviceName, Convert.ToHexString(serverCertHash));
            return new LinkSession(tcp, ssl, channel, peer);
        }
        catch
        {
            if (ssl is not null) await ssl.DisposeAsync().ConfigureAwait(false);
            tcp.Dispose();
            throw;
        }
    }

    private static async Task<HelloMessage> ReadHelloAsync(FrameChannel channel, CancellationToken ct)
    {
        var frame = await channel.ReadAsync(ct).ConfigureAwait(false);
        if (frame.Type == FrameType.ServerAuth)
            throw new AuthenticationFailedException(frame.Json<ServerAuthMessage>().Error ?? "Connexion refusée.");
        if (frame.Type != FrameType.Hello) throw new LinkProtocolException("Réponse inattendue du serveur.");
        var hello = frame.Json<HelloMessage>();
        if (hello.Version != LinkServer.ProtocolVersion)
            throw new LinkProtocolException($"Version de protocole non prise en charge : {hello.Version}.");
        return hello;
    }
}

/// <summary>Connexion authentifiée vers un autre PC.</summary>
public sealed class LinkSession : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly SslStream _ssl;
    private readonly FrameChannel _channel;

    public RemotePeer Server { get; }

    internal LinkSession(TcpClient tcp, SslStream ssl, FrameChannel channel, RemotePeer server)
    {
        _tcp = tcp;
        _ssl = ssl;
        _channel = channel;
        Server = server;
    }

    public async Task SendChatAsync(string text, CancellationToken ct = default)
    {
        await _channel.WriteJsonAsync(FrameType.Chat, new ChatMessage(text), ct).ConfigureAwait(false);
        _channel.ReadTimeout = TimeSpan.FromSeconds(30);
        await _channel.ExpectAsync(FrameType.Ack, ct).ConfigureAwait(false);
    }

    /// <summary>Envoie un fichier ou un dossier. Annuler ou couper la connexion laisse le transfert reprenable.</summary>
    public Task SendPathAsync(string path, IProgress<TransferProgress>? progress = null, CancellationToken ct = default) =>
        TransferSender.SendAsync(_channel, path, progress, ct);

    /// <summary>Propose à l'autre PC de synchroniser un dossier ; il choisit de son côté le dossier correspondant.</summary>
    public async Task<SyncPairReply> RequestSyncPairAsync(SyncPairRequest request, CancellationToken ct = default)
    {
        await _channel.WriteJsonAsync(FrameType.SyncPairRequest, request, ct).ConfigureAwait(false);
        _channel.ReadTimeout = TimeSpan.FromMinutes(5); // l'utilisateur distant doit répondre
        var reply = (await _channel.ExpectAsync(FrameType.SyncPairReply, ct).ConfigureAwait(false)).Json<SyncPairReply>();
        _channel.ReadTimeout = null;
        return reply;
    }

    /// <summary>Lance une passe de synchronisation ; la connexion devient dédiée et se termine avec elle.</summary>
    public Task<SyncResult> SyncAsync(SyncPair pair, SyncStateStore states, IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default) => SyncEngine.RunAsync(_channel, pair, states, progress, ct);

    /// <summary>
    /// Demande à voir (et éventuellement contrôler) l'écran de l'autre PC. La connexion devient dédiée à cette
    /// session : elle est fermée quand la session est libérée. Lève <see cref="RemoteRefusedException"/> si refusé.
    /// </summary>
    public async Task<RemoteViewerSession> StartRemoteAsync(RemoteRequest request, IRemoteViewerSink sink, CancellationToken ct = default)
    {
        await _channel.WriteJsonAsync(FrameType.RemoteRequest, request, ct).ConfigureAwait(false);
        _channel.ReadTimeout = TimeSpan.FromMinutes(5); // l'utilisateur distant doit répondre
        var reply = (await _channel.ExpectAsync(FrameType.RemoteReply, ct).ConfigureAwait(false)).Json<RemoteReply>();
        if (!reply.Accepted) throw new RemoteRefusedException(reply.Reason);
        _channel.ReadTimeout = null;

        var viewer = new RemoteViewerSession(this, _channel, reply, sink);
        viewer.Start();
        return viewer;
    }

    public async ValueTask DisposeAsync()
    {
        await _ssl.DisposeAsync().ConfigureAwait(false);
        _tcp.Dispose();
    }
}
