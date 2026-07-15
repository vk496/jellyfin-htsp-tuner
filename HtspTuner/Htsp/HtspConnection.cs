using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HtspTuner.Htsp;

/// <summary>
/// Transport-independent settings for an <see cref="HtspConnection"/> and <c>HtspClient</c>.
/// </summary>
/// <remarks>
/// Kept free of any Jellyfin type so the HTSP layer can be unit-tested and driven from a console
/// harness. The Jellyfin layer constructs this from <c>PluginConfiguration</c>.
/// </remarks>
internal sealed record HtspClientOptions
{
    /// <summary>Gets the Tvheadend host name or IP address.</summary>
    public required string Host { get; init; }

    /// <summary>Gets the HTSP port. Tvheadend's default is 9982.</summary>
    public int Port { get; init; } = 9982;

    /// <summary>Gets the HTTP port, used for channel icons, the mux-map seed and the HTTP fallback.</summary>
    public int HttpPort { get; init; } = 9981;

    /// <summary>Gets a value indicating whether HTTP requests use TLS.</summary>
    public bool UseHttps { get; init; }

    /// <summary>Gets the Tvheadend webroot, when it sits behind a reverse proxy.</summary>
    public string WebRoot { get; init; } = string.Empty;

    /// <summary>Gets the user name. Empty is a valid, supported (anonymous) configuration.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Gets the password. Empty is valid.</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>Gets the streaming profile passed to <c>subscribe</c>.</summary>
    public string Profile { get; init; } = "htsp";

    /// <summary>Gets the subscription weight. Higher wins when tuners are contended.</summary>
    public int SubscriptionWeight { get; init; } = 100;

    /// <summary>Gets how many days of EPG to stream in via the async metadata channel.</summary>
    public int EpgDays { get; init; } = 14;

    /// <summary>Gets the per-request response timeout.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Thrown when Tvheadend denies access during authentication.</summary>
internal sealed class HtspAuthenticationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="HtspAuthenticationException"/> class.</summary>
    /// <param name="message">The message.</param>
    public HtspAuthenticationException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown when Tvheadend returns an <c>error</c> or <c>noaccess</c> for a request.</summary>
internal sealed class HtspServerException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="HtspServerException"/> class.</summary>
    /// <param name="message">Tvheadend's error message.</param>
    public HtspServerException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown when a request receives no response within its timeout.</summary>
internal sealed class HtspTimeoutException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="HtspTimeoutException"/> class.</summary>
    /// <param name="message">The message.</param>
    public HtspTimeoutException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A single HTSP TCP connection: async framing, request/response correlation, async event fan-out
/// and bounded auto-reconnect. Owns the socket and one read-loop task; spins up no OS threads.
/// </summary>
internal sealed class HtspConnection : IAsyncDisposable
{
    private const int OurHtspVersion = 43;

    private readonly HtspClientOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<HtspMessage>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Random _jitter = new();

    private long _seq;
    private Socket? _socket;
    private Stream? _stream;
    private Task? _superviseTask;
    private Task? _keepAliveTask;
    private Task? _readLoopTask;
    private volatile bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="HtspConnection"/> class.</summary>
    /// <param name="options">The connection options.</param>
    /// <param name="logger">The logger.</param>
    public HtspConnection(HtspClientOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Raised for every server-pushed async event (a message with a <c>method</c> and no <c>seq</c>).</summary>
    public event Action<HtspMessage>? EventReceived;

    /// <summary>Raised after a successful <em>re</em>connect, so callers can re-arm async metadata.</summary>
    public event Action? Reconnected;

    /// <summary>Gets a value indicating whether the socket is currently connected and authenticated.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Gets the HTSP protocol version negotiated with the server (min of ours and theirs).</summary>
    public int NegotiatedVersion { get; private set; } = OurHtspVersion;

    /// <summary>
    /// Performs the initial connect, hello and authenticate, then starts the background read loop and
    /// reconnect supervisor. Throws immediately on a bad configuration so the caller sees it at once.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token for the initial attempt.</param>
    /// <returns>A task that completes once the connection is authenticated.</returns>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await EstablishAsync(cancellationToken).ConfigureAwait(false);
        IsConnected = true;
        _superviseTask = Task.Run(SuperviseAsync);
        _keepAliveTask = Task.Run(KeepAliveAsync);
    }

    // Tvheadend (and any NAT/firewall in between) drops an idle socket, and every reconnect re-syncs the
    // whole 60k-event guide. A trivial periodic request keeps the one connection alive so that never happens.
    private async Task KeepAliveAsync()
    {
        while (!_disposed)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(45), _shutdownCts.Token).ConfigureAwait(false);
                if (!IsConnected)
                {
                    continue;
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                await SendAsync(new HtspMessage().Add("method", "getSysTime"), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposed)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "HTSP keepalive ping failed; the supervisor will reconnect if needed");
            }
        }
    }

    /// <summary>
    /// Sends a request and awaits the correlated response. Adds a <c>seq</c>, enforces a real timeout
    /// that faults the task, and throws <see cref="HtspServerException"/> when Tvheadend replies with
    /// <c>error</c> or <c>noaccess</c>.
    /// </summary>
    /// <param name="message">The request message. A <c>seq</c> field is added.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The response message.</returns>
    public async Task<HtspMessage> SendAsync(HtspMessage message, CancellationToken cancellationToken)
    {
        var seq = Interlocked.Increment(ref _seq);
        message.Add("seq", seq);

        var tcs = new TaskCompletionSource<HtspMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = tcs;
        try
        {
            await SendRawAsync(message, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _shutdownCts.Token);
            timeoutCts.CancelAfter(_options.RequestTimeout);

            HtspMessage response;
            try
            {
                response = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                throw new HtspTimeoutException(
                    $"Tvheadend did not answer '{message.Method}' within {_options.RequestTimeout.TotalSeconds:0}s");
            }

            if (response.NoAccess)
            {
                throw new HtspServerException($"Access denied for '{message.Method}'");
            }

            if (response.Error is { } err)
            {
                throw new HtspServerException(err);
            }

            return response;
        }
        finally
        {
            _pending.TryRemove(seq, out _);
        }
    }

    /// <summary>
    /// Serialises and writes a message without registering a response. Used for streaming control
    /// messages and <c>enableAsyncMetadata</c>, whose result arrives as async events, not a reply.
    /// </summary>
    /// <param name="message">The message to write.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the bytes are on the wire.</returns>
    public Task SendNoReplyAsync(HtspMessage message, CancellationToken cancellationToken)
        => SendRawAsync(message, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _shutdownCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        CleanupSocket();
        FailAllPending(new ObjectDisposedException(nameof(HtspConnection)));

        if (_superviseTask is { } t)
        {
            try
            {
                await t.ConfigureAwait(false);
            }
            catch
            {
                // supervisor teardown races are expected on dispose.
            }
        }

        _shutdownCts.Dispose();
        _writeLock.Dispose();
    }

    private async Task SendRawAsync(HtspMessage message, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new HtspServerException("Not connected to Tvheadend");
        var bytes = HtsmsgCodec.Serialize(message);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task EstablishAsync(CancellationToken cancellationToken)
    {
        var socket = await ConnectSocketAsync(cancellationToken).ConfigureAwait(false);
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);

        // The read loop must be live before hello/auth, since their replies flow through it. Keep the
        // task: the supervisor awaits it to learn the socket died — polling the socket instead races with
        // the read loop draining it (Poll()==readable but Available==0), which falsely trips reconnects.
        _readLoopTask = Task.Run(() => ReadLoopAsync(_stream, _shutdownCts.Token));

        await HelloAsync(cancellationToken).ConfigureAwait(false);
        await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Socket> ConnectSocketAsync(CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(_options.Host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(_options.Host, cancellationToken).ConfigureAwait(false);
        }

        if (addresses.Length == 0)
        {
            throw new HtspServerException($"Could not resolve host '{_options.Host}'");
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.NoDelay = true;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(address, _options.Port, connectCts.Token).ConfigureAwait(false);
                _logger.LogDebug("HTSP connected to {Address}:{Port}", address, _options.Port);
                return socket;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                last = ex;
                socket.Dispose();
                _logger.LogDebug(ex, "HTSP connect to {Address} failed; trying next address", address);
            }
        }

        throw new HtspServerException(
            $"Could not connect to any address for {_options.Host}:{_options.Port}: {last?.Message}");
    }

    private async Task HelloAsync(CancellationToken cancellationToken)
    {
        var version = typeof(HtspConnection).Assembly.GetName().Version?.ToString() ?? "1.0.0.0";
        var hello = new HtspMessage()
            .Add("method", "hello")
            .Add("htspversion", (long)OurHtspVersion)
            .Add("clientname", "Jellyfin HTSP Tuner")
            .Add("clientversion", version);

        var response = await SendAsync(hello, cancellationToken).ConfigureAwait(false);

        var serverVersion = (int)response.GetInt("htspversion", OurHtspVersion);
        NegotiatedVersion = Math.Min(OurHtspVersion, serverVersion);
        _helloChallenge = response.GetBin("challenge");
        _logger.LogInformation(
            "HTSP hello: server '{Name}' v{ServerVer}, negotiated v{Negotiated}",
            response.GetString("servername"),
            serverVersion,
            NegotiatedVersion);
    }

    private byte[]? _helloChallenge;

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        var auth = new HtspMessage().Add("method", "authenticate");
        auth.AddIfPresent("username", _options.Username);

        if (_helloChallenge is { } challenge)
        {
            var pw = Encoding.UTF8.GetBytes(_options.Password);
            var buffer = new byte[pw.Length + challenge.Length];
            Buffer.BlockCopy(pw, 0, buffer, 0, pw.Length);
            Buffer.BlockCopy(challenge, 0, buffer, pw.Length, challenge.Length);
            auth.Add("digest", SHA1.HashData(buffer));
        }

        var response = await SendAsync(auth, cancellationToken).ConfigureAwait(false);
        if (response.NoAccess)
        {
            throw new HtspAuthenticationException(
                "Tvheadend denied access. Check the user name and password, or that anonymous access is permitted.");
        }

        _logger.LogInformation(
            "HTSP authenticated as '{User}'",
            string.IsNullOrEmpty(_options.Username) ? "(anonymous)" : _options.Username);
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken cancellationToken)
    {
        var reader = PipeReader.Create(stream);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;

                var before = buffer.Length;
                try
                {
                    while (HtsmsgCodec.TryReadFrame(ref buffer, out var msg))
                    {
                        if (msg is not null)
                        {
                            Dispatch(msg);
                        }
                    }
                }
                catch (HtspProtocolException ex)
                {
                    // A single malformed message must not kill the connection, but we can only
                    // resync the byte stream if the codec advanced past the bad frame.
                    if (buffer.Length == before)
                    {
                        _logger.LogWarning(ex, "Unrecoverable HTSP frame; forcing reconnect");
                        break;
                    }

                    _logger.LogWarning(ex, "Skipped malformed HTSP frame");
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    _logger.LogDebug("HTSP peer closed the connection");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown or reconnect.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HTSP read loop ended");
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private void Dispatch(HtspMessage msg)
    {
        try
        {
            // Responses carry seq and no method; async events carry method and no seq.
            if (msg.Method is null && msg.Seq is { } seq)
            {
                if (_pending.TryRemove(seq, out var tcs))
                {
                    tcs.TrySetResult(msg);
                }

                return;
            }

            if (msg.Method is not null)
            {
                EventReceived?.Invoke(msg);
            }
        }
        catch (Exception ex)
        {
            // A handler bug must never take down the connection.
            _logger.LogError(ex, "HTSP event handler threw for '{Method}'", msg.Method);
        }
    }

    private async Task SuperviseAsync()
    {
        var attempt = 0;
        while (!_disposed)
        {
            // Wait for the current connection's read loop to end (socket close / error).
            await WaitForSocketDeathAsync().ConfigureAwait(false);

            IsConnected = false;
            FailAllPending(new IOException("HTSP connection lost"));
            CleanupSocket();

            if (_disposed)
            {
                break;
            }

            // Bounded exponential backoff with jitter, capped at 60s. Never in a lock.
            var reconnected = false;
            while (!_disposed && !reconnected)
            {
                var delay = BackoffDelay(attempt++);
                try
                {
                    await Task.Delay(delay, _shutdownCts.Token).ConfigureAwait(false);
                    _logger.LogInformation("HTSP reconnecting (attempt {Attempt})", attempt);
                    await EstablishAsync(_shutdownCts.Token).ConfigureAwait(false);
                    IsConnected = true;
                    attempt = 0;
                    reconnected = true;
                    _logger.LogInformation("HTSP reconnected");
                    Reconnected?.Invoke();
                }
                catch (OperationCanceledException) when (_disposed)
                {
                    return;
                }
                catch (HtspAuthenticationException ex)
                {
                    // Credentials no longer valid: retrying would just hammer the server. Give up.
                    _logger.LogError(ex, "HTSP authentication failed on reconnect; giving up");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "HTSP reconnect attempt {Attempt} failed", attempt);
                }
            }
        }
    }

    private async Task WaitForSocketDeathAsync()
    {
        // The read loop ends exactly when the socket dies (ReadAsync completes or throws). Await it —
        // do NOT poll the socket, which races with the read loop draining it and trips false reconnects.
        var task = _readLoopTask;
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The read loop logs its own exit reason; here we only care that it ended.
        }
    }

    private TimeSpan BackoffDelay(int attempt)
    {
        var seconds = Math.Min(60, Math.Pow(2, Math.Min(attempt, 6)));
        var withJitter = seconds + _jitter.NextDouble();
        return TimeSpan.FromSeconds(Math.Min(60, withJitter));
    }

    private void CleanupSocket()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        var socket = Interlocked.Exchange(ref _socket, null);
        try
        {
            stream?.Dispose();
        }
        catch
        {
            // ignore.
        }

        try
        {
            socket?.Dispose();
        }
        catch
        {
            // ignore.
        }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetException(ex);
            }
        }
    }
}
