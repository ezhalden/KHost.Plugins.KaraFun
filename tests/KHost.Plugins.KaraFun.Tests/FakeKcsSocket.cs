using System.Threading.Channels;
using Google.Protobuf;
using KHost.Plugins.KaraFun.Client.Kcs;

namespace KHost.Plugins.KaraFun.Tests;

/// <summary>A socket the test drives both ends of: <see cref="Deliver"/> puts a frame in front of
/// the channel, and <see cref="Sent"/> is what the channel wrote back.</summary>
internal sealed class FakeKcsSocket : IKcsSocket
{
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
    private readonly List<Message> _sent = [];
    private readonly Lock _gate = new();

    public bool IsOpen { get; private set; }

    public Uri? ConnectedTo { get; private set; }

    /// <summary>Sends <c>socket.authenticatedEvent</c> on connect, the way the server does, so a
    /// test needing a live room does not race delivering the event by hand after connecting.</summary>
    public bool AuthenticatesOnConnect { get; init; }

    public IReadOnlyList<Message> Sent
    {
        get { lock (_gate) return [.. _sent]; }
    }

    public Task ConnectAsync(Uri url, CancellationToken cancellationToken)
    {
        ConnectedTo = url;
        IsOpen = true;

        if (AuthenticatesOnConnect)
            Deliver(new Message
            {
                Event = new Message.Types.Event
                {
                    Socket = new SocketEvent { AuthenticatedEvent = new ModelsEmpty() },
                },
            });

        return Task.CompletedTask;
    }

    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        lock (_gate)
            _sent.Add(Message.Parser.ParseFrom(payload.ToArray()));

        return Task.CompletedTask;
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            IsOpen = false;
            return null;
        }
    }

    public void Deliver(Message message) => _inbound.Writer.TryWrite(message.ToByteArray());

    /// <summary>Hangs up, the way a dropped connection does.</summary>
    public void Close() => _inbound.Writer.TryComplete();

    /// <summary>The pump is a real background task, so a test that asserts straight after
    /// delivering a frame races it.</summary>
    public async Task<IReadOnlyList<Message>> WaitForSentAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            var sent = Sent;

            if (sent.Count >= count)
                return sent;

            await Task.Delay(5);
        }

        throw new TimeoutException($"Expected {count} message(s); the channel sent {Sent.Count}.");
    }

    public bool Disposed { get; private set; }

    public void Dispose()
    {
        Disposed = true;
        Close();
    }
}
