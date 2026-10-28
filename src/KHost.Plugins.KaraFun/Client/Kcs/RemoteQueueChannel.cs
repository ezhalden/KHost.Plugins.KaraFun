using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.KaraFun.Client.Kcs;

/// <summary>Turns a guest's phone pick into a <see cref="RemoteSongRequest"/> the host can act on.
/// The permissions this host answers with are cosmetic; the code on the screen is the real limit.</summary>
internal sealed class RemoteQueueChannel : IAsyncDisposable
{
    private readonly Func<RemoteSongRequest, CancellationToken, Task> _onSongRequested;
    private readonly Func<CancellationToken, Task<bool>>? _showQueueToGuests;
    private readonly Func<IReadOnlyList<RemoteUser>, CancellationToken, Task>? _onGuestsPresent;
    private readonly Func<CancellationToken, Task>? _onAuthenticated;
    private readonly ILogger _logger;
    private readonly KcsConnection _connection;

    // Keyed by RemoteUser.Id, the id space userLeft's user_id names (as setRole's user_id does).
    // A pick names its remoteId instead and is looked up by value.
    private readonly ConcurrentDictionary<string, RemoteUser> _users = new();

    private long _lastRequestId;

    public RemoteQueueChannel(
        Func<CancellationToken, Task<string?>> resolveUrl,
        Func<RemoteSongRequest, CancellationToken, Task> onSongRequested,
        ILogger logger,
        Func<IKcsSocket>? socketFactory = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<IReadOnlyList<RemoteUser>, CancellationToken, Task>? onGuestsPresent = null,
        Func<CancellationToken, Task>? onAuthenticated = null,
        Func<Task>? onNotConnected = null,
        Func<CancellationToken, Task<bool>>? showQueueToGuests = null)
    {
        _showQueueToGuests = showQueueToGuests;
        _onSongRequested = onSongRequested;
        _onGuestsPresent = onGuestsPresent;
        _onAuthenticated = onAuthenticated;
        _logger = logger;
        _connection = new KcsConnection(resolveUrl, HandleAsync, logger, socketFactory, delay, onNotConnected);
    }

    public bool IsConnected => _connection.IsConnected;

    /// <summary>The guests in the room right now, as the room last described them.</summary>
    public IReadOnlyList<RemoteUser> Guests => [.. _users.Values];

    public void Start() => _connection.Start();

    internal async Task HandleAsync(Message message, CancellationToken cancellationToken)
    {
        switch (message.TypeCase)
        {
            case Message.TypeOneofCase.Event:
                await HandleEventAsync(message.Event, cancellationToken);
                break;

            case Message.TypeOneofCase.Response:
                HandleResponse(message.Response);
                break;

            case Message.TypeOneofCase.Request:
                await HandleRequestAsync(message.Request, cancellationToken);
                break;
        }
    }

    private async Task HandleEventAsync(Message.Types.Event @event, CancellationToken cancellationToken)
    {
        switch (@event.DomainCase)
        {
            case Message.Types.Event.DomainOneofCase.Socket
                when @event.Socket.TypeCase == SocketEvent.TypeOneofCase.AuthenticatedEvent:
                // Whoever was in the room belongs to the connection that just ended.
                _users.Clear();
                _logger.LogInformation("KaraFun remote: authenticated, asking who is in the room");
                await SendRequestAsync(new Message.Types.Request
                {
                    Remote = new RemoteRequest { UserList = new ModelsEmpty() },
                }, cancellationToken);

                // Off the pump since it reaches the network: a request arriving here must not wait
                // behind an HTTP timeout.
                if (_onAuthenticated is { } authenticated)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await authenticated(cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "KaraFun remote: could not settle the room after authenticating");
                        }
                    }, cancellationToken);

                break;

            case Message.Types.Event.DomainOneofCase.Remote:
                HandleRemoteEvent(@event.Remote);
                break;
        }
    }

    /// <summary>Drops the guest from the roster and nothing else: a phone leaves every time it locks,
    /// so leaving is not unpairing, and the host's tie to their singer stays.</summary>
    /// <remarks>Matched on <see cref="RemoteUser.Id"/> alone. Matching a remoteId too let one guest's
    /// id leaving take out another guest whose id in the other space happened to be equal.</remarks>
    private void Forget(string id)
    {
        if (string.IsNullOrEmpty(id))
            return;

        // Logged either way: an id nobody on the roster carries is the evidence for which id space
        // userLeft really uses, still unconfirmed on the wire.
        if (_users.TryRemove(id, out var leaving))
            _logger.LogInformation("KaraFun remote: '{Guest}' left the room (user {UserId}, remote {RemoteId})",
                leaving.Name, id, leaving.RemoteId);
        else
            _logger.LogInformation("KaraFun remote: user {UserId} left, but nobody in the room carries that id", id);
    }

    /// <summary>Off the pump, since tying a guest reaches the host's database.</summary>
    private void Present(IReadOnlyList<RemoteUser> guests)
    {
        if (_onGuestsPresent is not { } present || guests.Count == 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await present(guests, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KaraFun remote: could not tie the room's guests back to their singers");
            }
        });
    }

    private void HandleRemoteEvent(RemoteEvent remote)
    {
        switch (remote.TypeCase)
        {
            case RemoteEvent.TypeOneofCase.UserJoined when remote.UserJoined.User is { } joined:
                Remember(joined);
                _logger.LogInformation("KaraFun remote: '{Guest}' joined the room (user {UserId}, remote {RemoteId})",
                    joined.Name, joined.Id, joined.RemoteId);
                Present([joined]);
                break;

            case RemoteEvent.TypeOneofCase.UserUpdate when remote.UserUpdate.User is { } updated:
                Remember(updated);
                break;

            case RemoteEvent.TypeOneofCase.UserLeft:
                Forget(remote.UserLeft.UserId);
                break;
        }
    }

    private void HandleResponse(Message.Types.Response response)
    {
        if (response.DomainCase == Message.Types.Response.DomainOneofCase.Error)
        {
            _logger.LogWarning(
                "KaraFun remote: the server refused a request ({Type}) {Message}",
                response.Error.Type, response.Error.Message);
            return;
        }

        if (response is { DomainCase: Message.Types.Response.DomainOneofCase.Remote, Remote.TypeCase: RemoteResponse.TypeOneofCase.UserList })
        {
            foreach (var user in response.Remote.UserList.Users)
                Remember(user);

            // A reconnect or a restart into the same room hears no joins for guests already in it,
            // so this list is the only chance to tie them back.
            Present([.. response.Remote.UserList.Users]);
        }
    }

    private async Task HandleRequestAsync(Message.Types.Request request, CancellationToken cancellationToken)
    {
        // Answered exactly as the desktop does, all off: claiming a capability this has never seen
        // negotiate would be a guess a live room pays for.
        if (request is { DomainCase: Message.Types.Request.DomainOneofCase.Socket, Socket.TypeCase: SocketRequest.TypeOneofCase.GetCapacities })
        {
            await RespondAsync(request.Id, new Message.Types.Response
            {
                Socket = new SocketResponse
                {
                    GetCapacities = new SocketGetCapacitiesResponse { Capacities = new SocketCapacities() },
                },
            }, cancellationToken);
            return;
        }

        if (request is { DomainCase: Message.Types.Request.DomainOneofCase.Player, Player.TypeCase: PlayerRequest.TypeOneofCase.AddToQueue })
        {
            await AddToQueueAsync(request.Id, request.Player.AddToQueue, cancellationToken);
            return;
        }

        // A refusal here reads as a missing player, not a missing control: a guest with every
        // permission still could not queue a song.
        if (request.DomainCase == Message.Types.Request.DomainOneofCase.Player)
        {
            switch (request.Player.TypeCase)
            {
                case PlayerRequest.TypeOneofCase.Status:
                    await RespondAsync(request.Id, new Message.Types.Response
                    {
                        Player = new PlayerResponse
                        {
                            Status = new PlayerStatusResponse { Status = Status() },
                        },
                    }, cancellationToken);
                    return;

                case PlayerRequest.TypeOneofCase.Queue:
                    await RespondAsync(request.Id, new Message.Types.Response
                    {
                        Player = new PlayerResponse
                        {
                            Queue = new PlayerQueueResponse { Queue = new PlayerQueue() },
                        },
                    }, cancellationToken);
                    return;
            }
        }

        // A refusal here is not neutral: a guest whose permissions never arrived is shown a room
        // they may look at and not add to, the same as no remote at all.
        if (request.DomainCase == Message.Types.Request.DomainOneofCase.Remote)
        {
            switch (request.Remote.TypeCase)
            {
                case RemoteRequest.TypeOneofCase.GetPermissions:
                    await RespondAsync(request.Id, new Message.Types.Response
                    {
                        Remote = new RemoteResponse
                        {
                            GetPermissions = new RemoteGetPermissionsResponse
                            {
                                Permissions = GrantedPermissions(await ShowQueueAsync(cancellationToken)),
                            },
                        },
                    }, cancellationToken);
                    return;

                case RemoteRequest.TypeOneofCase.GetPreferences:
                    await RespondAsync(request.Id, new Message.Types.Response
                    {
                        Remote = new RemoteResponse
                        {
                            GetPreferences = new RemoteGetPreferencesResponse { Preferences = Preferences() },
                        },
                    }, cancellationToken);
                    return;

                case RemoteRequest.TypeOneofCase.GetConfiguration:
                    await RespondAsync(request.Id, new Message.Types.Response
                    {
                        Remote = new RemoteResponse
                        {
                            GetConfiguration = new RemoteGetConfigurationResponse { Configuration = Configuration() },
                        },
                    }, cancellationToken);
                    return;
            }
        }

        // Everything else is answered rather than ignored: a phone waiting on a reply that never
        // comes reads as the room being broken, not as a thing this host merely does not do.
        _logger.LogInformation(
            "KaraFun remote: refused a {Domain}.{Type} request this host does not answer",
            request.DomainCase, Describe(request));

        await RefuseAsync(request.Id, ModelsError.Types.TypeEnum.NotSupported,
            "This host does not answer that yet.", cancellationToken);
    }

    /// <summary>The venue's answer, defaulting to yes for a channel built without one.</summary>
    private async Task<bool> ShowQueueAsync(CancellationToken cancellationToken)
        => _showQueueToGuests is null || await _showQueueToGuests(cancellationToken);

    /// <summary>Grants only what this host serves; advertising more paints a phone control that
    /// errors when pressed.</summary>
    /// <remarks><c>ViewQueue</c> paints a read only list and nothing else: dragging is disabled
    /// behind <c>ManageQueue</c> and the transport behind <c>ManagePlayback</c>, both of which
    /// this host refuses. A local song carries no KaraFun id, which costs a placeholder tile and
    /// an undercount in the page's own song total, not a broken row. It is the venue's call, and
    /// read here rather than held, since permissions are answered per join.</remarks>
    private static RemotePermissions GrantedPermissions(bool viewQueue) => new()
    {
        AddToQueue = true,
        ManageQueue = false,
        ViewQueue = viewQueue,
        ManagePlayback = false,
        ManageVolumes = false,
        SendPhotos = false,
        ShownTypes = new RemoteShownTypes
        {
            Karaoke = true,
            Community = false,
            Quiz = false,
            Battle = false,
        },
    };

    /// <summary><c>AskOptions</c> off so a pick carries the join nickname KHost puts on the queue.
    /// <c>SingerRotation</c> off: the phone's rotation is KaraFun's, and this host runs its own.</summary>
    private static RemotePreferences Preferences() => new()
    {
        AskOptions = false,
        SingerRotation = false,
    };

    /// <summary>A degenerate range, honestly: nothing in KHost carries a per-performance pitch or
    /// tempo. Steps stay 1 rather than 0, since a zero step is a divisor on the phone's side.</summary>
    private static RemoteConfiguration Configuration() => new()
    {
        PitchStep = 1,
        TempoStep = 1,
        PitchMin = 0,
        PitchMax = 0,
        TempoMin = 0,
        TempoMax = 0,
    };

    /// <summary>A player that exists and plays nothing, so the room reads as addable; every
    /// transport request is still refused, so nothing here should draw a live control.</summary>
    private static PlayerStatus Status() => new()
    {
        State = PlayerStatus.Types.State.Idle,
        Tempo = 0,
        Pitch = 0,
    };

    /// <summary>Names the inner request case, so a refusal says which control the phone wanted.</summary>
    private static string Describe(Message.Types.Request request) => request.DomainCase switch
    {
        Message.Types.Request.DomainOneofCase.Remote => request.Remote.TypeCase.ToString(),
        Message.Types.Request.DomainOneofCase.Player => request.Player.TypeCase.ToString(),
        Message.Types.Request.DomainOneofCase.Socket => request.Socket.TypeCase.ToString(),
        _ => "unknown",
    };

    private async Task AddToQueueAsync(ulong requestId, PlayerAddToQueueRequest pick, CancellationToken cancellationToken)
    {
        if (pick.Quiz is not null || pick.Song is null)
        {
            await RefuseAsync(requestId, ModelsError.Types.TypeEnum.NotSupported,
                "This host plays songs only.", cancellationToken);
            return;
        }

        // A pick with no name of its own belongs to whoever sent it, which is how KaraFun's own
        // host reads it: the web page only asks for a singer when the room is set up to.
        var singer = Blank(pick.Options?.Singer) ?? Blank(GuestNamed(pick.RemoteId)?.Name);

        if (singer is null)
        {
            await RefuseAsync(requestId, ModelsError.Types.TypeEnum.BadContent,
                "That pick named no singer.", cancellationToken);
            return;
        }

        var song = new RemoteSongRequest(
            (int)pick.Song.Id,
            pick.Song.Type == PlayerSongIdentifier.Types.TypeEnum.Community,
            singer,
            pick.RemoteId ?? string.Empty);

        // Acknowledged before the work: downloading and importing runs for minutes on the socket's
        // only thread, so waiting here would stop the room.
        await RespondAsync(requestId, new Message.Types.Response
        {
            Player = new PlayerResponse { AddToQueue = new ModelsEmpty() },
        }, cancellationToken);

        _logger.LogInformation(
            "KaraFun remote: '{Singer}' asked for {Kind} song {SongId}",
            song.SingerName, song.Community ? "community" : "catalog", song.SongId);

        _ = Task.Run(async () =>
        {
            try
            {
                await _onSongRequested(song, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KaraFun remote: could not queue song {SongId}", song.SongId);
            }
        }, cancellationToken);
    }

    private RemoteUser? GuestNamed(string remoteId)
        => string.IsNullOrEmpty(remoteId)
            ? null
            : _users.Values.FirstOrDefault(user => user.RemoteId == remoteId);

    private void Remember(RemoteUser user)
    {
        if (!string.IsNullOrEmpty(user.Id))
            _users[user.Id] = user;
    }

    private Task RefuseAsync(ulong requestId, ModelsError.Types.TypeEnum type, string message, CancellationToken cancellationToken)
        => RespondAsync(requestId, new Message.Types.Response
        {
            Error = new ModelsError { Type = type, Message = message },
        }, cancellationToken);

    private Task RespondAsync(ulong requestId, Message.Types.Response response, CancellationToken cancellationToken)
    {
        // The id the request came in on, echoed back unchanged: it is the only thing tying an
        // answer to its question, since the payload of a successful one is empty.
        response.Id = requestId;

        return _connection.SendAsync(new Message { Response = response }, cancellationToken);
    }

    private Task SendRequestAsync(Message.Types.Request request, CancellationToken cancellationToken)
    {
        request.Id = (ulong)Interlocked.Increment(ref _lastRequestId);

        return _connection.SendAsync(new Message { Request = request }, cancellationToken);
    }

    /// <summary>Pushes what is playing to every phone in the room. An event, not an answer: the web
    /// remote registers no response handler and asks for nothing, so state reaches it unsolicited or
    /// not at all.</summary>
    /// <remarks>Dropped while the socket is down rather than queued, since each push carries the
    /// whole status and the next change makes a phone that missed one right again.</remarks>
    internal async Task PublishStatusAsync(PlayerStatus status, CancellationToken cancellationToken = default)
    {
        if (!_connection.IsConnected)
            return;

        try
        {
            await _connection.SendAsync(new Message
            {
                Event = new Message.Types.Event
                {
                    Player = new PlayerEvent { Status = new PlayerStatusEvent { Status = status } },
                },
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            // A room that cannot be told what is playing is not a reason to fail the song.
            _logger.LogDebug(ex, "KaraFun remote: could not publish what is playing");
        }
    }

    /// <summary>Pushes the room's queue to every phone in it, the same unsolicited way the status
    /// goes out.</summary>
    /// <remarks>Room wide by necessity rather than by choice: the event carries no recipient and
    /// the page reads no per phone response, so every guest sees one list. Dropped while the
    /// socket is down, since each push carries the whole queue and the next change makes a phone
    /// that missed one right again.</remarks>
    internal async Task PublishQueueAsync(PlayerQueue queue, CancellationToken cancellationToken = default)
    {
        if (!_connection.IsConnected)
            return;

        try
        {
            await _connection.SendAsync(new Message
            {
                Event = new Message.Types.Event
                {
                    Player = new PlayerEvent { Queue = new PlayerQueueEvent { Queue = queue } },
                },
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            // A room that cannot be shown the queue is not a reason to fail the song.
            _logger.LogDebug(ex, "KaraFun remote: could not publish the queue");
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
