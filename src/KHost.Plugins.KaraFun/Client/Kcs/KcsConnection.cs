using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.KaraFun.Client.Kcs;

/// <summary>The socket a room's picks arrive on: one serialized <see cref="Message"/> per binary
/// frame, unsigned, identified only by the <c>t=</c> token already inside <c>kcs_url</c>.</summary>
internal sealed class KcsConnection : IAsyncDisposable
{
    /// <summary>The only subprotocol the desktop offers, and the server selects it.</summary>
    public const string Subprotocol = "kcpp";

    // These pace a reconnect and are the desktop's own; the 25-35s spread keeps a chain of venues
    // from all asking for a fresh URL in lockstep.
    private static readonly TimeSpan DisconnectGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RefusedDelay = TimeSpan.FromSeconds(5);

    /// <summary>How long the same failure stays quiet before it is worth saying again.</summary>
    private static readonly TimeSpan FailureRepeatWarning = TimeSpan.FromMinutes(5);
    private const int SessionRetrySecondsMin = 25;
    private const int SessionRetrySecondsMax = 35;

    private readonly Func<CancellationToken, Task<string?>> _resolveUrl;
    private readonly Func<Message, CancellationToken, Task> _onMessage;
    private readonly Func<Task>? _onNotConnected;
    private readonly Func<IKcsSocket> _socketFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _logger;

    // A socket permits one send at a time, and replies are written from whichever thread handled
    // the request that prompted them.
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();

    private IKcsSocket? _socket;
    private Task? _pump;

    // A dead remote retries every half minute all night, so the same reason logs once and stays
    // quiet until it changes; full silence would leave no trace of a socket that never opens.
    private string? _lastFailure;
    private DateTime _lastFailureWarned;

    public KcsConnection(
        Func<CancellationToken, Task<string?>> resolveUrl,
        Func<Message, CancellationToken, Task> onMessage,
        ILogger logger,
        Func<IKcsSocket>? socketFactory = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<Task>? onNotConnected = null)
    {
        _onNotConnected = onNotConnected;
        _resolveUrl = resolveUrl;
        _onMessage = onMessage;
        _logger = logger;
        _socketFactory = socketFactory ?? (() => new ClientWebSocketAdapter());
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Whether a socket is open right now. False while reconnecting.</summary>
    public bool IsConnected => _socket?.IsOpen == true;

    /// <summary>Returns without waiting for a connection, since a venue with wifi down must still
    /// finish starting up; this reconnects on its own once the wifi is not.</summary>
    public void Start() => _pump ??= Task.Run(() => RunAsync(_stopping.Token));

    /// <summary>Does nothing while the socket is down: a guest whose phone asked mid-reconnect is
    /// better served by the next full snapshot than by an error for a stale question.</summary>
    public async Task SendAsync(Message message, CancellationToken cancellationToken = default)
    {
        if (_socket is not { IsOpen: true } socket)
            return;

        var payload = message.ToByteArray();

        await _sending.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(payload, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The pump owns reconnecting; a send that lost the race to a drop is not an error.
            _logger.LogDebug(ex, "KaraFun remote: send dropped, the socket had already gone");
        }
        finally
        {
            _sending.Release();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan delay;

            try
            {
                delay = await ConnectAndPumpAsync(cancellationToken);
            }
            // Only a real shutdown ends the pump. HttpClient reports its own timeout as a
            // TaskCanceledException, which is an OperationCanceledException with nobody having
            // cancelled anything, so catching the type alone retired the remote for the rest of
            // the session on one slow request: the room's QR code vanished and nothing said why.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Report($"the connection failed: {ex.Message}", ex);
                delay = RefusedDelay;
            }

            // Said on every pass rather than only on the transition, so a listener that somehow
            // acted on a stale connected state is put right on the next one rather than staying wrong.
            if (_onNotConnected is { } notConnected)
            {
                try
                {
                    await notConnected();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "KaraFun remote: could not settle the room after losing the socket");
                }
            }

            try
            {
                await _delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Returns how long to wait before trying again.</summary>
    private async Task<TimeSpan> ConnectAndPumpAsync(CancellationToken cancellationToken)
    {
        // Re-read every time round rather than kept: the token in the URL belongs to one session,
        // so a reconnect against yesterday's string authenticates as nobody.
        string? url;

        try
        {
            url = await _resolveUrl(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // KaraFun being down lands here, not on the connect below, since asking for the address
            // is itself a REST call; the longer session-retry wait avoids adding load to an outage.
            Report($"could not ask for the control-system address: {ex.Message}", ex);

            return RandomSessionRetry();
        }

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Report(url is null or "" 
                ? "the session carries no control-system address, so there is nothing to connect to"
                : $"the session's control-system address is not a URL: '{url}'");

            return RandomSessionRetry();
        }

        using var socket = _socketFactory();

        await socket.ConnectAsync(uri, cancellationToken);

        _socket = socket;
        _lastFailure = null;
        _logger.LogInformation("KaraFun remote: connected");

        try
        {
            await ReceiveLoopAsync(socket, cancellationToken);
        }
        finally
        {
            _socket = null;
        }

        _logger.LogInformation("KaraFun remote: disconnected");

        return DisconnectGrace;
    }

    private async Task ReceiveLoopAsync(IKcsSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await socket.ReceiveAsync(cancellationToken) is not { } frame)
                return;

            if (frame.Length == 0)
                continue;

            Message message;

            try
            {
                message = Message.Parser.ParseFrom(frame);
            }
            catch (InvalidProtocolBufferException ex)
            {
                // A frame that fails to decode may just be a message this transcribed schema never
                // described, as easily as a corrupt one; neither is worth dropping the socket over.
                _logger.LogDebug(ex, "KaraFun remote: could not decode a frame");
                continue;
            }

            try
            {
                await _onMessage(message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One guest's pick failing must not take the room's socket down with it, and a
                // handler that timed out internally is one of those failures rather than a stop.
                _logger.LogWarning(ex, "KaraFun remote: handling a message failed");
            }
        }
    }

    /// <summary>Says a reason once, then stops repeating it until it changes.</summary>
    /// <summary>Says it once, then keeps saying it on an interval while it is still true.</summary>
    /// <remarks>Deduping to Debug alone is what hid a dead remote for four hours: the first failure
    /// warned, every retry after it was invisible at the default level, and the only symptom in the
    /// room was a QR code that had quietly stopped being drawn. A repeat that is still failing is
    /// news again after a while.</remarks>
    private void Report(string reason, Exception? ex = null)
    {
        var now = DateTime.UtcNow;

        if (reason == _lastFailure && now - _lastFailureWarned < FailureRepeatWarning)
        {
            _logger.LogDebug(ex, "KaraFun remote: {Reason}", reason);
            return;
        }

        _lastFailure = reason;
        _lastFailureWarned = now;
        _logger.LogWarning(ex, "KaraFun remote: {Reason}", reason);
    }

    private static TimeSpan RandomSessionRetry()
        => TimeSpan.FromSeconds(Random.Shared.Next(SessionRetrySecondsMin, SessionRetrySecondsMax + 1));

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        if (_pump is { } pump)
        {
            try { await pump; }
            catch (OperationCanceledException) { }
        }

        _stopping.Dispose();
        _sending.Dispose();
    }
}
