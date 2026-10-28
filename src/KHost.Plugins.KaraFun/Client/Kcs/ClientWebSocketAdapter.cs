using System.Net.WebSockets;

namespace KHost.Plugins.KaraFun.Client.Kcs;

/// <summary>The real socket, adding only the <c>kcpp</c> subprotocol: no extra headers or rebuilt
/// URL, since the server's <c>kcs_url</c> signs the connection's identity into its <c>t=</c> token.</summary>
internal sealed class ClientWebSocketAdapter : IKcsSocket
{
    private readonly ClientWebSocket _socket = new();

    public ClientWebSocketAdapter()
    {
        _socket.Options.AddSubProtocol(KcsConnection.Subprotocol);
        ConfigureKeepAlive(_socket.Options);
    }

    internal static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Without a timeout the pings go out but an unanswered one is never noticed, so a
    /// network that vanished with no close frame leaves ReceiveAsync blocked until the OS gives up
    /// on the TCP connection, with the room's code still on screen.</summary>
    internal static void ConfigureKeepAlive(ClientWebSocketOptions options)
    {
        options.KeepAliveInterval = KeepAliveInterval;
        options.KeepAliveTimeout = KeepAliveTimeout;
    }

    public bool IsOpen => _socket.State == WebSocketState.Open;

    public Task ConnectAsync(Uri url, CancellationToken cancellationToken)
        => _socket.ConnectAsync(url, cancellationToken);

    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        => _socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).AsTask();

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var frame = new MemoryStream();

        ValueWebSocketReceiveResult result;

        // A message is a frame, but a frame may still arrive in several reads: no length prefix
        // means EndOfMessage is the only boundary there is.
        do
        {
            result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            frame.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        // Text is not this protocol, only an edge's error page or a proxy talking. Empty rather
        // than null so a stray one is skipped instead of read as the peer hanging up.
        return result.MessageType == WebSocketMessageType.Binary ? frame.ToArray() : [];
    }

    public void Dispose() => _socket.Dispose();
}
