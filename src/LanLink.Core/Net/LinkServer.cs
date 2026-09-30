using System.Net;
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

/// <summary>
/// Serveur : accepte les connexions TLS 1.3 (certificats auto-signés, authentification mutuelle du
/// certificat), authentifie le correspondant par mot de passe, puis traite ses requêtes
/// (transfert de fichiers, messages).
/// </summary>
public sealed class LinkServer : IAsyncDisposable
{
    public const int ProtocolVersion = 1;

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    private readonly DeviceIdentity _identity;
    private readonly string _deviceName;
    private readonly ILinkHost _host;
    private readonly AuthRateLimiter _limiter;
    private readonly SemaphoreSlim _pendingHandshakes = new(16);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connections = new();
    private readonly object _lock = new();

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private volatile PasswordVerifier? _verifier;
    private int _remoteBusy;

    public LinkServer(DeviceIdentity identity, string deviceName, PasswordVerifier? verifier, ILinkHost host,
        AuthRateLimiter? limiter = null)
    {
        _identity = identity;
        _deviceName = deviceName;
        _verifier = verifier;
        _host = host;
        _limiter = limiter ?? new AuthRateLimiter();
    }

    /// <summary>Port réellement utilisé (utile quand on démarre avec le port 0).</summary>
    public int Port => ((IPEndPoint)_listener!.LocalEndpoint).Port;

    /// <summary>Change le mot de passe accepté ; sans mot de passe, toute connexion est refusée.</summary>
    public void UpdateVerifier(PasswordVerifier? verifier) => _verifier = verifier;

    public void Start(int port)
    {
        _listener = new TcpListener(IPAddress.IPv6Any, port) { Server = { DualMode = true } };
        _listener.Start(backlog: 32);
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            var task = Task.Run(() => HandleAsync(client));
            lock (_lock)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(task);
            }
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4().ToString() ?? "?";
        using var _ = client;
        try
        {
            client.NoDelay = true;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            if (_limiter.IsBlocked(address))
            {
                _host.Log($"⚠ Connexion refusée (trop d'échecs d'authentification) : {address}");
                return;
            }

            SslStream? ssl = null;
            RemotePeer peer;
            FrameChannel channel;
            if (!await _pendingHandshakes.WaitAsync(0).ConfigureAwait(false)) return; // trop de handshakes en parallèle
            try
            {
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                handshakeCts.CancelAfter(HandshakeTimeout);
                ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _identity.Certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    // Le certificat est auto-signé : la confiance vient de la preuve par mot de passe,
                    // liée aux empreintes des deux certificats.
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                }, handshakeCts.Token).ConfigureAwait(false);

                channel = new FrameChannel(ssl) { ReadTimeout = HandshakeTimeout };
                var authenticated = await AuthenticateAsync(channel, ssl, address, handshakeCts.Token).ConfigureAwait(false);
                if (authenticated is null) return;
                peer = authenticated;
            }
            finally
            {
                _pendingHandshakes.Release();
            }

            await using (ssl.ConfigureAwait(false))
            {
                _host.Log($"🔐 Connexion authentifiée : {peer.DeviceName} ({peer.Address})");
                channel.ReadTimeout = null;
                await ServeAsync(channel, peer).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or TimeoutException
                                       or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Connexion abandonnée ou handshake TLS échoué : rien à signaler à l'utilisateur.
        }
        catch (Exception ex)
        {
            _host.Log($"✖ Erreur de connexion ({address}) : {ex.Message}");
        }
    }

    private async Task<RemotePeer?> AuthenticateAsync(FrameChannel channel, SslStream ssl, string address, CancellationToken ct)
    {
        var verifier = _verifier;
        var serverNonce = RandomNumberGenerator.GetBytes(32);
        if (verifier is null)
        {
            await channel.WriteJsonAsync(FrameType.ServerAuth, new ServerAuthMessage(false, null, "Aucun mot de passe défini sur ce PC."), ct).ConfigureAwait(false);
            return null;
        }

        await channel.WriteJsonAsync(FrameType.Hello, new HelloMessage(ProtocolVersion, _deviceName,
            Convert.ToBase64String(verifier.Salt), verifier.Params.MemoryKiB, verifier.Params.Iterations,
            verifier.Params.Parallelism, Convert.ToBase64String(serverNonce)), ct).ConfigureAwait(false);

        var auth = (await channel.ExpectAsync(FrameType.ClientAuth, ct).ConfigureAwait(false)).Json<ClientAuthMessage>();
        var clientNonce = Convert.FromBase64String(auth.Nonce);
        var clientCertHash = SHA256.HashData(ssl.RemoteCertificate!.GetRawCertData());

        var expected = HandshakeProof.Compute(verifier.Key, "client", serverNonce, clientNonce, _identity.Fingerprint, clientCertHash);
        if (clientNonce.Length != 32 || !HandshakeProof.Verify(expected, Convert.FromBase64String(auth.Proof)))
        {
            _limiter.RecordFailure(address);
            _host.Log($"⚠ Mot de passe incorrect depuis {address}");
            await channel.WriteJsonAsync(FrameType.ServerAuth, new ServerAuthMessage(false, null, "Mot de passe incorrect."), ct).ConfigureAwait(false);
            return null;
        }

        _limiter.RecordSuccess(address);
        var serverProof = HandshakeProof.Compute(verifier.Key, "server", serverNonce, clientNonce, _identity.Fingerprint, clientCertHash);
        await channel.WriteJsonAsync(FrameType.ServerAuth, new ServerAuthMessage(true, Convert.ToBase64String(serverProof), null), ct).ConfigureAwait(false);

        var name = string.IsNullOrWhiteSpace(auth.DeviceName) ? address : auth.DeviceName.Trim();
        return new RemotePeer(address, name.Length > 64 ? name[..64] : name, Convert.ToHexString(clientCertHash));
    }

    private async Task ServeAsync(FrameChannel channel, RemotePeer peer)
    {
        while (!_cts.IsCancellationRequested)
        {
            Frame frame;
            try { frame = await channel.ReadAsync(_cts.Token).ConfigureAwait(false); }
            catch (EndOfStreamException) { return; }

            try
            {
                switch (frame.Type)
                {
                    case FrameType.Offer:
                        await new TransferReceiver(_host, peer)
                            .ReceiveAsync(channel, frame.Json<TransferOffer>(), _cts.Token).ConfigureAwait(false);
                        break;

                    case FrameType.Chat:
                        _host.OnChatMessage(peer, frame.Json<ChatMessage>().Text);
                        await channel.WriteAsync(FrameType.Ack, ReadOnlyMemory<byte>.Empty, _cts.Token).ConfigureAwait(false);
                        break;

                    case FrameType.RemoteRequest:
                        await ServeRemoteAsync(channel, peer, frame.Json<RemoteRequest>()).ConfigureAwait(false);
                        return; // la connexion était dédiée au partage d'écran

                    case FrameType.SyncPairRequest:
                    {
                        var request = frame.Json<SyncPairRequest>();
                        var pair = await _host.ConfirmSyncPairAsync(peer, request, _cts.Token).ConfigureAwait(false);
                        await channel.WriteJsonAsync(FrameType.SyncPairReply,
                            new SyncPairReply(pair is not null, pair is null ? "Refusé par l'utilisateur." : null), _cts.Token).ConfigureAwait(false);
                        break;
                    }

                    case FrameType.SyncOpen:
                        await ServeSyncAsync(channel, peer, frame.Json<SyncOpenMessage>()).ConfigureAwait(false);
                        return; // la connexion était dédiée à cette passe de synchronisation

                    default:
                        throw new LinkProtocolException($"Requête inattendue : {frame.Type}");
                }
            }
            catch (LinkProtocolException ex)
            {
                _host.Log($"✖ Protocole invalide de {peer.DeviceName} : {ex.Message}");
                try { await channel.WriteAsync(FrameType.Error, System.Text.Encoding.UTF8.GetBytes(ex.Message), _cts.Token).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
                return;
            }
            catch (ArgumentException ex)
            {
                _host.Log($"✖ Requête refusée de {peer.DeviceName} : {ex.Message}");
                return;
            }
        }
    }

    private async Task ServeSyncAsync(FrameChannel channel, RemotePeer peer, SyncOpenMessage open)
    {
        var pair = _host.FindSyncPair(open.PairId);
        // La paire n'est utilisable que par le PC avec lequel elle a été convenue (même certificat).
        if (pair is null || pair.IsInitiator || !string.Equals(pair.PeerFingerprint, peer.CertFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            await channel.WriteJsonAsync(FrameType.SyncOpenReply,
                new SyncOpenReply(false, "Synchronisation inconnue sur l'autre PC (supprimée ?)."), _cts.Token).ConfigureAwait(false);
            return;
        }
        await new SyncServerSession(channel, pair, _host.Log).RunAsync(_cts.Token).ConfigureAwait(false);
    }

    private async Task ServeRemoteAsync(FrameChannel channel, RemotePeer peer, RemoteRequest request)
    {
        async Task Refuse(string reason) =>
            await channel.WriteJsonAsync(FrameType.RemoteReply,
                new RemoteReply(false, reason, false, false, false, new List<MonitorInfo>(), 0), _cts.Token).ConfigureAwait(false);

        var env = _host.RemoteEnvironment;
        if (env is null) { await Refuse("Ce PC ne partage pas son écran.").ConfigureAwait(false); return; }
        if (Interlocked.CompareExchange(ref _remoteBusy, 1, 0) != 0)
        {
            await Refuse("Une session de partage est déjà en cours.").ConfigureAwait(false);
            return;
        }

        try
        {
            var granted = await _host.ConfirmRemoteAsync(peer, request, _cts.Token).ConfigureAwait(false);
            if (granted is null) { await Refuse("Refusé par l'utilisateur.").ConfigureAwait(false); return; }

            // On n'accorde jamais plus que ce que le visiteur a demandé.
            var grant = new RemoteGrant(granted.Control && request.WantControl,
                granted.Audio && request.WantAudio, granted.Clipboard && request.WantClipboard);

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _host.OnRemoteSessionStarted(peer, grant, () => stop.Cancel());
            try
            {
                await new RemoteHostSession(channel, env, request, grant).RunAsync(stop.Token).ConfigureAwait(false);
            }
            finally
            {
                _host.OnRemoteSessionEnded(peer);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _remoteBusy, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null) await _acceptLoop.ConfigureAwait(false);
        Task[] pending;
        lock (_lock) pending = _connections.ToArray();
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        _cts.Dispose();
    }
}
