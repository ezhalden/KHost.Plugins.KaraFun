using KHost.Plugins.KaraFun.Client.Kcs;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.KaraFun.Tests;

public class RemoteQueueChannelTests : IAsyncLifetime
{
    private const string Url = "wss://kcs.example.com/ws?t=token";

    private readonly FakeKcsSocket _socket = new();
    private readonly List<RemoteSongRequest> _picks = [];
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RemoteQueueChannel _channel;

    private readonly List<RemoteUser> _present = [];

    private int _settlements;

    public RemoteQueueChannelTests()
        => _channel = new RemoteQueueChannel(
            _ => Task.FromResult<string?>(Url),
            (pick, _) => { lock (_picks) _picks.Add(pick); return Task.CompletedTask; },
            NullLogger.Instance,
            () => _socket,
            delay: (_, _) => Task.CompletedTask,
            onGuestsPresent: (guests, _) => { lock (_present) _present.AddRange(guests); return Task.CompletedTask; },
            onAuthenticated: _ =>
            {
                Interlocked.Increment(ref _settlements);
                _settled.TrySetResult();
                return Task.CompletedTask;
            });

    public Task InitializeAsync()
    {
        _channel.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _socket.Close();
        await _channel.DisposeAsync();
    }

    private static Message Authenticated() => new()
    {
        Event = new Message.Types.Event
        {
            Socket = new SocketEvent { AuthenticatedEvent = new ModelsEmpty() },
        },
    };

    private static Message Joined(string userId, string remoteId, string name) => new()
    {
        Event = new Message.Types.Event
        {
            Remote = new RemoteEvent
            {
                UserJoined = new RemoteUserJoinedEvent
                {
                    User = new RemoteUser { Id = userId, RemoteId = remoteId, Name = name },
                },
            },
        },
    };

    private static Message Left(string userId) => new()
    {
        Event = new Message.Types.Event
        {
            Remote = new RemoteEvent { UserLeft = new RemoteUserLeftEvent { UserId = userId } },
        },
    };

    private static Message Pick(
        ulong id,
        uint songId = 42,
        string singer = "",
        string remoteId = "",
        PlayerSongIdentifier.Types.TypeEnum type = PlayerSongIdentifier.Types.TypeEnum.Catalog)
        => new()
        {
            Request = new Message.Types.Request
            {
                Id = id,
                Player = new PlayerRequest
                {
                    AddToQueue = new PlayerAddToQueueRequest
                    {
                        Song = new PlayerSongIdentifier { Id = songId, Type = type },
                        RemoteId = remoteId,
                        Options = new PlayerSongOptions { Singer = singer },
                    },
                },
            },
        };

    private async Task<RemoteSongRequest> WaitForPickAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            lock (_picks)
                if (_picks.Count > 0)
                    return _picks[0];

            await Task.Delay(5);
        }

        throw new TimeoutException("No pick reached the host.");
    }

    [Fact]
    public async Task Connect_UsesTheUrlItWasGiven()
    {
        _socket.Deliver(Authenticated());
        await _socket.WaitForSentAsync(1);

        Assert.Equal(new Uri(Url), _socket.ConnectedTo);
    }

    /// <summary>Asking for the user list is the first thing said once authenticated: a guest who
    /// joined before the host connected is announced no other way, joins never being replayed.</summary>
    [Fact]
    public async Task Authenticated_AsksWhoIsInTheRoom()
    {
        Assert.Empty(_socket.Sent);

        _socket.Deliver(Authenticated());
        var sent = await _socket.WaitForSentAsync(1);

        var request = Assert.Single(sent).Request;
        Assert.NotNull(request);
        Assert.Equal(RemoteRequest.TypeOneofCase.UserList, request.Remote.TypeCase);
    }

    [Fact]
    public async Task Pick_NamingItsSinger_ReachesTheHostUnderThatName()
    {
        _socket.Deliver(Pick(id: 7, songId: 1234, singer: "Ada"));

        var pick = await WaitForPickAsync();

        Assert.Equal("Ada", pick.SingerName);
        Assert.Equal(1234, pick.SongId);
        Assert.False(pick.Community);
    }

    [Fact]
    public async Task Pick_IsAcknowledgedOnTheIdItArrivedOn()
    {
        _socket.Deliver(Pick(id: 99, singer: "Ada"));

        var response = Assert.Single(await _socket.WaitForSentAsync(1)).Response;

        Assert.NotNull(response);
        Assert.Equal(99ul, response.Id);
        Assert.Equal(PlayerResponse.TypeOneofCase.AddToQueue, response.Player.TypeCase);
    }

    /// <summary>A room set up not to ask for a singer sends none, and the guest is the singer.</summary>
    [Fact]
    public async Task Pick_WithNoSingerOfItsOwn_UsesTheNameTheGuestJoinedUnder()
    {
        _socket.Deliver(Joined("u1", "r1", "Grace"));
        _socket.Deliver(Pick(id: 1, remoteId: "r1"));

        Assert.Equal("Grace", (await WaitForPickAsync()).SingerName);
    }

    [Fact]
    public async Task Pick_FromAGuestWhoHasLeft_IsRefusedRatherThanQueuedToNobody()
    {
        _socket.Deliver(Joined("u1", "r1", "Grace"));
        _socket.Deliver(Left("u1"));
        _socket.Deliver(Pick(id: 1, remoteId: "r1"));

        var response = Assert.Single(await _socket.WaitForSentAsync(1)).Response;

        Assert.Equal(ModelsError.Types.TypeEnum.BadContent, response.Error.Type);
        Assert.Empty(_picks);
    }

    /// <summary>userLeft names a user id. One guest's user id equal to another guest's remoteId
    /// must take out only the guest it names.</summary>
    [Fact]
    public async Task GuestLeaving_IsMatchedOnTheUserIdOnly_SoAGuestWhoseRemoteIdIsEqualStays()
    {
        _socket.Deliver(Joined("x", "r1", "Grace"));
        _socket.Deliver(Joined("u2", "x", "Bob"));
        _socket.Deliver(Left("x"));
        _socket.Deliver(Pick(id: 1, remoteId: "x"));

        Assert.Equal("Bob", (await WaitForPickAsync()).SingerName);
    }

    private async Task<List<RemoteUser>> WaitForPresentAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            lock (_present)
                if (_present.Count >= count)
                    return [.. _present];

            await Task.Delay(5);
        }

        throw new TimeoutException($"{count} guest(s) never reached the host.");
    }

    /// <summary>A guest joining is the cue to tie their phone back to their singer, before any pick.</summary>
    [Fact]
    public async Task Joined_TellsTheHostWhoIsHere()
    {
        _socket.Deliver(Joined("u1", "r1", "Grace"));

        var guest = Assert.Single(await WaitForPresentAsync(1));

        Assert.Equal("r1", guest.RemoteId);
        Assert.Equal("Grace", guest.Name);
    }

    /// <summary>Guests already in the room on a reconnect are never announced by a join, so the
    /// list is the only way the host hears of them.</summary>
    [Fact]
    public async Task UserList_TellsTheHostEveryoneAlreadyInTheRoom()
    {
        var list = new Message
        {
            Response = new Message.Types.Response
            {
                Remote = new RemoteResponse
                {
                    UserList = new RemoteUserListResponse
                    {
                        Users =
                        {
                            new RemoteUser { Id = "u1", RemoteId = "r1", Name = "Grace" },
                            new RemoteUser { Id = "u2", RemoteId = "r2", Name = "Bob" },
                        },
                    },
                },
            },
        };

        _socket.Deliver(list);

        var guests = await WaitForPresentAsync(2);

        Assert.Equal(["Bob", "Grace"], guests.Select(guest => guest.Name).Order());
    }

    [Fact]
    public async Task Pick_OfACommunitySong_SaysSo()
    {
        _socket.Deliver(Pick(id: 1, songId: 6229, singer: "Ada",
            type: PlayerSongIdentifier.Types.TypeEnum.Community));

        var pick = await WaitForPickAsync();

        Assert.True(pick.Community);
        Assert.Equal(6229, pick.SongId);
    }

    [Fact]
    public async Task Quiz_IsRefused()
    {
        _socket.Deliver(new Message
        {
            Request = new Message.Types.Request
            {
                Id = 3,
                Player = new PlayerRequest
                {
                    AddToQueue = new PlayerAddToQueueRequest
                    {
                        Quiz = new PlayerQuizIdentifier { Id = 5 },
                        Options = new PlayerSongOptions { Singer = "Ada" },
                    },
                },
            },
        });

        var response = Assert.Single(await _socket.WaitForSentAsync(1)).Response;

        Assert.Equal(ModelsError.Types.TypeEnum.NotSupported, response.Error.Type);
        Assert.Empty(_picks);
    }

    /// <summary>Answered as the desktop answers it: every capacity off.</summary>
    [Fact]
    public async Task Capacities_AreReportedOff()
    {
        _socket.Deliver(new Message
        {
            Request = new Message.Types.Request
            {
                Id = 4,
                Socket = new SocketRequest { GetCapacities = new ModelsEmpty() },
            },
        });

        var response = Assert.Single(await _socket.WaitForSentAsync(1)).Response;
        var capacities = response.Socket.GetCapacities.Capacities;

        Assert.Equal(4ul, response.Id);
        Assert.Equal(ModelsActivableEnum.Disabled, capacities.CanHandleCommunityAddToQueue);
        Assert.Equal(ModelsActivableEnum.Disabled, capacities.CanHandleQuizAddToQueue);
        Assert.Equal(ModelsActivableEnum.Disabled, capacities.AllSingerRotationGroupsQueueForAdmins);
    }

    /// <summary>A refusal rather than silence: a phone waiting on an answer that never comes reads
    /// as the room being broken.</summary>
    [Fact]
    public async Task ARequestThisHostDoesNotAnswer_IsRefusedRatherThanIgnored()
    {
        _socket.Deliver(Ask(5, new PlayerRequest { Play = new ModelsEmpty() }));

        var response = Assert.Single(await _socket.WaitForSentAsync(1)).Response;

        Assert.Equal(5ul, response.Id);
        Assert.Equal(ModelsError.Types.TypeEnum.NotSupported, response.Error.Type);
    }

    /// <summary>Refusing this leaves the room reading as having no player to add to, a guest who
    /// cannot queue a song however many permissions they were granted.</summary>
    [Fact]
    public async Task Status_SaysAPlayerIsHereSoTheRoomCanBeAddedTo()
    {
        _socket.Deliver(Ask(11, new PlayerRequest { Status = new ModelsEmpty() }));

        var response = Assert.Single(await _socket.WaitForSentAsync(1)).Response;

        Assert.Equal(11ul, response.Id);
        Assert.Equal(PlayerStatus.Types.State.Idle, response.Player.Status.Status.State);
    }

    [Fact]
    public async Task Queue_IsAnsweredRatherThanRefused()
    {
        _socket.Deliver(Ask(12, new PlayerRequest { Queue = new ModelsEmpty() }));

        var response = Assert.Single(await _socket.WaitForSentAsync(1)).Response;

        Assert.Equal(12ul, response.Id);
        Assert.Equal(PlayerResponse.TypeOneofCase.Queue, response.Player.TypeCase);
    }

    /// <summary>Adding is the one thing a guest is for, and the phone hides the control without it.</summary>
    [Fact]
    public async Task GetPermissions_GrantsAddingToTheQueue()
    {
        _socket.Deliver(Ask(13, new RemoteRequest { GetPermissions = new ModelsEmpty() }));

        var permissions = Assert.Single(await _socket.WaitForSentAsync(1))
            .Response.Remote.GetPermissions.Permissions;

        Assert.True(permissions.AddToQueue);
        Assert.True(permissions.ShownTypes.Karaoke);
    }

    /// <summary>Advertising a permission whose request is still refused paints a control on the
    /// phone that answers an error when pressed.</summary>
    [Fact]
    public async Task GetPermissions_GrantsNothingThisHostStillRefuses()
    {
        _socket.Deliver(Ask(14, new RemoteRequest { GetPermissions = new ModelsEmpty() }));

        var permissions = Assert.Single(await _socket.WaitForSentAsync(1))
            .Response.Remote.GetPermissions.Permissions;

        Assert.False(permissions.ManageQueue);
        Assert.False(permissions.ManagePlayback);
        Assert.False(permissions.ManageVolumes);
        Assert.False(permissions.SendPhotos);
        Assert.False(permissions.ShownTypes.Community);
        Assert.False(permissions.ShownTypes.Quiz);
    }

    /// <summary>A venue that hides the queue is answered on the permission, so the page never
    /// draws the list. Read per join rather than held, since that is when it is asked.</summary>
    [Fact]
    public async Task GetPermissions_WithTheVenueHidingTheQueue_DoesNotGrantViewQueue()
    {
        var socket = new FakeKcsSocket { AuthenticatesOnConnect = true };
        await using var channel = new RemoteQueueChannel(
            _ => Task.FromResult<string?>(Url),
            (_, _) => Task.CompletedTask,
            NullLogger.Instance,
            () => socket,
            delay: (_, _) => Task.CompletedTask,
            showQueueToGuests: _ => Task.FromResult(false));

        channel.Start();
        socket.Deliver(Ask(21, new RemoteRequest { GetPermissions = new ModelsEmpty() }));

        var sent = await socket.WaitForSentAsync(1);
        var permissions = Assert.Single(sent, message =>
                message.TypeCase == Message.TypeOneofCase.Response
                && message.Response.DomainCase == Message.Types.Response.DomainOneofCase.Remote)
            .Response.Remote.GetPermissions.Permissions;

        Assert.False(permissions.ViewQueue);

        // The rest is unchanged: hiding the queue is not closing the room.
        Assert.True(permissions.AddToQueue);
    }

    /// <summary>Granted because the list it paints is read only: dragging is disabled behind
    /// ManageQueue and the transport behind ManagePlayback, both of which this host refuses. So
    /// it advertises no control that answers an error, which is the rule the sibling test keeps.
    /// </summary>
    [Fact]
    public async Task GetPermissions_LetsTheRoomSeeTheQueue()
    {
        _socket.Deliver(Ask(14, new RemoteRequest { GetPermissions = new ModelsEmpty() }));

        var permissions = Assert.Single(await _socket.WaitForSentAsync(1))
            .Response.Remote.GetPermissions.Permissions;

        Assert.True(permissions.ViewQueue);
    }

    /// <summary>State reaches a phone unsolicited or not at all: the page registers no response
    /// handler and asks for neither the queue nor the status, so both go out as events.</summary>
    [Fact]
    public async Task PublishQueue_GoesOutAsAnEventRatherThanAnAnswer()
    {
        _socket.Deliver(Authenticated());
        await _settled.Task;

        var queue = new PlayerQueue
        {
            Items =
            {
                new PlayerQueueItem
                {
                    Id = "turn-1",
                    Song = new PlayerQueueSong
                    {
                        Id = new PlayerSongIdentifier { Type = PlayerSongIdentifier.Types.TypeEnum.Unknown, Id = 0 },
                        Title = "Today",
                        Artist = "The Smashing Pumpkins",
                    },
                },
            },
        };

        await _channel.PublishQueueAsync(queue);

        var sent = await _socket.WaitForSentAsync(2);
        var published = Assert.Single(sent, message => message.TypeCase == Message.TypeOneofCase.Event
            && message.Event.DomainCase == Message.Types.Event.DomainOneofCase.Player
            && message.Event.Player.TypeCase == PlayerEvent.TypeOneofCase.Queue);

        var item = Assert.Single(published.Event.Player.Queue.Queue.Items);
        Assert.Equal("Today", item.Song.Title);
        Assert.Equal(0u, item.Song.Id.Id);
    }

    [Fact]
    public async Task PublishStatus_GoesOutAsAnEventRatherThanAnAnswer()
    {
        _socket.Deliver(Authenticated());
        await _settled.Task;

        await _channel.PublishStatusAsync(new PlayerStatus { State = PlayerStatus.Types.State.Playing });

        var sent = await _socket.WaitForSentAsync(2);
        var published = Assert.Single(sent, message => message.TypeCase == Message.TypeOneofCase.Event
            && message.Event.DomainCase == Message.Types.Event.DomainOneofCase.Player
            && message.Event.Player.TypeCase == PlayerEvent.TypeOneofCase.Status);

        Assert.Equal(PlayerStatus.Types.State.Playing, published.Event.Player.Status.Status.State);
    }

    /// <summary>A pick arrives under the join nickname, not one asked for per song.</summary>    /// <summary>A pick arrives under the join nickname, not one asked for per song.</summary>
    [Fact]
    public async Task GetPreferences_LeavesTheOptionsPromptOff()
    {
        _socket.Deliver(Ask(15, new RemoteRequest { GetPreferences = new ModelsEmpty() }));

        var preferences = Assert.Single(await _socket.WaitForSentAsync(1))
            .Response.Remote.GetPreferences.Preferences;

        Assert.False(preferences.AskOptions);
    }

    /// <summary>Nothing in KHost carries a per-performance pitch or tempo, so neither is offered.</summary>
    [Fact]
    public async Task GetConfiguration_OffersNoPitchOrTempoRange()
    {
        _socket.Deliver(Ask(16, new RemoteRequest { GetConfiguration = new ModelsEmpty() }));

        var configuration = Assert.Single(await _socket.WaitForSentAsync(1))
            .Response.Remote.GetConfiguration.Configuration;

        Assert.Equal(configuration.PitchMin, configuration.PitchMax);
        Assert.Equal(configuration.TempoMin, configuration.TempoMax);

        // A zero step is a divisor on the phone's side.
        Assert.NotEqual(0u, configuration.PitchStep);
        Assert.NotEqual(0u, configuration.TempoStep);
    }

    /// <summary>The only proof this host gets that KaraFun is reachable again, and what a code
    /// that failed to mint during an outage waits on.</summary>
    [Fact]
    public async Task Authenticated_SettlesTheRoomSoAnOutagesLeftoversAreRetried()
    {
        _socket.Deliver(Authenticated());

        await _settled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, Volatile.Read(ref _settlements));
    }

    /// <summary>KaraFun being down throws out of the REST call for the socket's address, so the
    /// wait after must be the long one: the short refused-socket delay would hammer an outage.</summary>
    [Fact]
    public async Task ResolvingTheAddress_ThrowingBecauseKaraFunIsDown_WaitsASessionRetryNotARefusedOne()
    {
        var delays = new List<TimeSpan>();
        var waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var channel = new RemoteQueueChannel(
            _ => throw new HttpRequestException("KaraFun is down"),
            (_, _) => Task.CompletedTask,
            NullLogger.Instance,
            () => new FakeKcsSocket(),
            delay: (span, ct) =>
            {
                lock (delays) delays.Add(span);
                waited.TrySetResult();

                // Parks the pump here rather than spinning the retry loop. It must honour the
                // token, or disposing the channel waits on a delay that never ends.
                return Task.Delay(Timeout.Infinite, ct);
            });

        channel.Start();

        await waited.Task.WaitAsync(TimeSpan.FromSeconds(5));

        TimeSpan chosen;
        lock (delays) chosen = delays[0];

        Assert.InRange(chosen, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(35));
    }

    private static Message Ask(ulong id, PlayerRequest player)
        => new() { Request = new Message.Types.Request { Id = id, Player = player } };

    private static Message Ask(ulong id, RemoteRequest remote)
        => new() { Request = new Message.Types.Request { Id = id, Remote = remote } };

    [Fact]
    public async Task Authenticated_ForgetsTheGuestsOfTheConnectionThatEnded()
    {
        _socket.Deliver(Joined("u1", "r1", "Grace"));
        _socket.Deliver(Pick(id: 1, remoteId: "r1"));
        await WaitForPickAsync();

        // A reconnect re-authenticates, and whoever was in the room belonged to the old socket.
        _socket.Deliver(Authenticated());
        await _socket.WaitForSentAsync(2);

        _socket.Deliver(Pick(id: 2, remoteId: "r1"));
        var refusal = (await _socket.WaitForSentAsync(3))[^1].Response;

        Assert.Equal(ModelsError.Types.TypeEnum.BadContent, refusal.Error.Type);
    }
}
