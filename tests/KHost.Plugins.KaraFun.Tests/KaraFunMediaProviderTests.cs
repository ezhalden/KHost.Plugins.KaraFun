using KHost.Abstractions.Exceptions;
using KHost.Abstractions.Messaging;
using KHost.Abstractions.Messaging.Messages;
using KHost.Abstractions.Interactions;
using KHost.Abstractions.Interactions.Requests;
using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using KHost.Plugins.KaraFun.Client;
using KHost.Plugins.KaraFun.Client.Kcs;
using KHost.Plugins.KaraFun.Kit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.KaraFun.Tests;

public class KaraFunMediaProviderTests : IDisposable
{
    private readonly IPluginContext _plugin = Substitute.For<IPluginContext>();
    private readonly IOptionsMonitor<KaraFunSettings> _settings = Substitute.For<IOptionsMonitor<KaraFunSettings>>();
    private readonly IFlashService _flash = Substitute.For<IFlashService>();
    private readonly IInteractionDispatcher _dispatcher = Substitute.For<IInteractionDispatcher>();
    private readonly IMediaAcquisitionService _library = Substitute.For<IMediaAcquisitionService>();
    private readonly ISingerQueueService _queue = Substitute.For<ISingerQueueService>();
    private readonly IUsersService _users = Substitute.For<IUsersService>();
    private readonly IPerformanceService _performances = Substitute.For<IPerformanceService>();
    private readonly InMemoryCache _cache = new();
    private readonly Guid _singerId = Guid.NewGuid();
    private readonly string _mediaDirectory = Path.Combine(Path.GetTempPath(), $"khost-karafun-tests-{Guid.NewGuid():N}");

    /// <summary>Stands in for the host's secret store: without it every write to IPluginContext
    /// vanishes, reading as a plugin that cannot remember a session rather than a forgetful double.</summary>
    private readonly Dictionary<string, string?> _secrets = [];

    private int _codesShown;
    private int _codesHidden;

    public KaraFunMediaProviderTests()
    {
        // The code reaches the screens from the socket's pump, not the call the test made, so every
        // assertion about it has to wait for that thread rather than run straight after the await.
        _plugin.When(plugin => plugin.RegisterQrCodeAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<QrCodeFeatures>(), Arg.Any<CancellationToken>()))
            .Do(_ => Interlocked.Increment(ref _codesShown));
        _plugin.When(plugin => plugin.UnregisterQrCodeAsync(Arg.Any<CancellationToken>()))
            .Do(_ => Interlocked.Increment(ref _codesHidden));

        _settings.CurrentValue.Returns(new KaraFunSettings());

        _plugin.SetSecretAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _secrets[call.ArgAt<string>(0)] = call.ArgAt<string?>(1);
                return Task.CompletedTask;
            });

        _plugin.GetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(_secrets.GetValueOrDefault(call.ArgAt<string>(0))));
        _library.MediaDirectory.Returns(_mediaDirectory);
        _queue.SelectedUserId.Returns(_singerId);
    }

    private readonly IMessageBroker _broker = Substitute.For<IMessageBroker>();

    /// <summary>Playback is resolved from here on use rather than taken in the constructor, since
    /// the provider is an IMediaPlaybackGate and PlaybackService reaches every gate.</summary>
    private readonly IPlaybackService _playback = Substitute.For<IPlaybackService>();
    private readonly IServiceProvider _services = Substitute.For<IServiceProvider>();

    public void Dispose()
    {
        if (Directory.Exists(_mediaDirectory))
            Directory.Delete(_mediaDirectory, recursive: true);
    }

    private KaraFunMediaProvider Provider(KaraFunApi? api = null)
        => new(_plugin, _settings, _dispatcher, _library, _queue, _users, _performances, _cache,
            _broker, _services, NullLogger<KaraFunMediaProvider>.Instance, _flash,
            api ?? new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(new FakeHandler(new Dictionary<string, string>()))),
            // No socket: a provider built here must not put a reconnecting connection on the
            // network. The channel has its own tests, against a fake one.
            remoteSocket: null);

    /// <summary>A provider whose remote channel runs, over a socket the test drives.</summary>
    private KaraFunMediaProvider Provider(KaraFunApi api, FakeKcsSocket socket)
        => Provider(api, () => socket);

    /// <summary>For a test that outlives one channel: signing out disposes the socket with it, and
    /// a disposed one hangs up on the next channel the moment it reads.</summary>
    private KaraFunMediaProvider Provider(KaraFunApi api, Func<IKcsSocket> sockets)
        => new(_plugin, _settings, _dispatcher, _library, _queue, _users, _performances, _cache,
            _broker, _services, NullLogger<KaraFunMediaProvider>.Instance, _flash, api, sockets);

    /// <summary>Stands in for the host filling in the dialog and pressing Continue.</summary>
    private void ArrangeCredentialPrompt(string login, string password)
        => _dispatcher.RequestAsync(Arg.Any<TextPromptRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["login"] = login, ["password"] = password });

    /// <summary>Stands in for the host closing the dialog without filling it in.</summary>
    private void ArrangeCredentialPromptCancelled()
        => _dispatcher.RequestAsync(Arg.Any<TextPromptRequest>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, string>?)null);

    private static MediaSearchEntity SignInRow(List<MediaSearchEntity> results) => Assert.Single(results);

    /// <summary>Asserts the community-search offer is last before handing back the rest.</summary>
    private static List<MediaSearchEntity> SongRows(List<MediaSearchEntity> results)
    {
        Assert.Equal("Search Community Songs", results[^1].Title);
        return [.. results[..^1]];
    }

    [Fact]
    public async Task SearchAsync_BlankQuery_ReturnsNothing()
        => Assert.Empty(await Provider().SearchAsync("   "));

    /// <summary>No session yet is not a broken search, but a host who hasn't signed in, and the
    /// row exists to fix that rather than to look like an error.</summary>
    [Fact]
    public async Task SearchAsync_NoSession_OffersTheSignInRowInstead()
    {
        var row = SignInRow(await Provider().SearchAsync("bohemian rhapsody"));

        Assert.Equal("Sign in to KaraFun", row.Title);
        Assert.Equal("Sign in", Assert.Single(row.SupportedActions).DisplayName);
    }

    [Fact]
    public async Task SignIn_Cancelled_ReportsNoWarning()
    {
        ArrangeCredentialPromptCancelled();
        var provider = Provider();
        var row = SignInRow(await provider.SearchAsync("neon moon"));

        await Assert.Single(row.SupportedActions).PerformAsync(row);

        _plugin.DidNotReceive().AddWarning(Arg.Any<string>());
    }

    [Fact]
    public async Task SignIn_Cancelled_StillOffersTheSignInRowOnTheNextSearch()
    {
        ArrangeCredentialPromptCancelled();
        var provider = Provider();
        var row = SignInRow(await provider.SearchAsync("neon moon"));
        await Assert.Single(row.SupportedActions).PerformAsync(row);

        SignInRow(await provider.SearchAsync("neon moon"));
    }

    [Fact]
    public async Task SignIn_Succeeds_ThenSearchReturnsSongsFromSearchAndInfo()
    {
        var handler = new FakeHandler(new Dictionary<string, string>
        {
            ["session/open"] = """<response status="OK"><session><key>sk-1</key></session></response>""",
            ["search/top"] = """<response status="OK"><list><song id="6229" content="a" meta="b" /></list></response>""",
            ["song/info"] = """<response status="OK"><song id="6229" len="243"><title>Neon Moon</title><artist id="1">Brooks &amp; Dunn</artist></song></response>""",
        });
        ArrangeCredentialPrompt("host@example.com", "hunter2");
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        var row = SignInRow(await provider.SearchAsync("neon moon"));

        await Assert.Single(row.SupportedActions).PerformAsync(row);
        var results = await provider.SearchAsync("neon moon");

        var song = Assert.Single(SongRows(results));
        Assert.Equal("Neon Moon", song.Title);
        Assert.Equal("Brooks & Dunn", song.Artist);
        Assert.Equal(TimeSpan.FromSeconds(243), song.Duration);
        Assert.Equal("6229", song.ForeignKey);
    }

    /// <summary>A regression guard: nothing about a KaraFun login belongs on
    /// <see cref="KaraFunSettings"/>, the type written to plugins.json.</summary>
    [Fact]
    public void KaraFunSettings_CarriesNothingThatWouldPersistCredentials()
    {
        var propertyNames = typeof(KaraFunSettings).GetProperties().Select(p => p.Name);

        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("login", StringComparison.OrdinalIgnoreCase)
            || name.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A keyring that refuses the write is not a refused password: the session works for
    /// this run, and blaming the sign-in sends a host to retype a password that was right.</summary>
    [Fact]
    public async Task SignIn_Succeeds_ButTheSessionCannotBeSaved_SaysSoRatherThanThatSignInFailed()
    {
        // Thrown from the call itself, not a faulted task: the host's store is synchronous underneath.
        _plugin.SetSecretAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new PlatformNotSupportedException("No unlocked keyring"));
        var handler = new FakeHandler(new Dictionary<string, string>
        {
            ["session/open"] = """<response status="OK"><session><key>sk-1</key></session></response>""",
            ["search/top"] = """<response status="OK"><list><song id="6229" content="a" meta="b" /></list></response>""",
            ["song/info"] = """<response status="OK"><song id="6229" len="243"><title>Neon Moon</title><artist id="1">Brooks &amp; Dunn</artist></song></response>""",
        });
        ArrangeCredentialPrompt("host@example.com", "hunter2");
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        var row = SignInRow(await provider.SearchAsync("neon moon"));

        await Assert.Single(row.SupportedActions).PerformAsync(row);

        _plugin.Received(1).AddWarning(Arg.Is<string>(m =>
            m.Contains("could not save") && m.Contains("No unlocked keyring")));
        _plugin.DidNotReceive().AddWarning(Arg.Is<string>(m => m.Contains("Could not sign in")));
        Assert.Equal("Neon Moon", Assert.Single(SongRows(await provider.SearchAsync("neon moon"))).Title);
    }

    [Fact]
    public async Task SignIn_WrongCredentials_ReportsAWarningRatherThanThrowing()
    {
        var handler = new FakeHandler(new Dictionary<string, string>
        {
            ["session/open"] = """<response status="Error"><error action="fail">WRONG_PASSWORD</error></response>""",
        });
        ArrangeCredentialPrompt("host@example.com", "wrong");
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        var row = SignInRow(await provider.SearchAsync("neon moon"));

        await Assert.Single(row.SupportedActions).PerformAsync(row);

        _plugin.Received(1).AddWarning(Arg.Is<string>(m => m.Contains("WRONG_PASSWORD")));
        SignInRow(await provider.SearchAsync("neon moon"));
    }

    /// <summary>The host just pressed Sign in and typed a password; a refusal is exactly what the
    /// flash guidance means by "why an action was refused".</summary>
    [Fact]
    public async Task SignIn_WrongCredentials_FlashesTheReasonNamingKaraFun()
    {
        var handler = new FakeHandler(new Dictionary<string, string>
        {
            ["session/open"] = """<response status="Error"><error action="fail">WRONG_PASSWORD</error></response>""",
        });
        ArrangeCredentialPrompt("host@example.com", "wrong");
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        var row = SignInRow(await provider.SearchAsync("neon moon"));

        await Assert.Single(row.SupportedActions).PerformAsync(row);

        _flash.Received(1).Show(
            Arg.Is<string>(m => m.StartsWith("KaraFun sign-in failed") && m.Contains("WRONG_PASSWORD")),
            FlashType.Warning);
    }

    /// <summary>Exactly these three: the collection is closed, so a thumbnail column coming back
    /// is a failure here rather than an empty first column in the results grid.</summary>
    [Fact]
    public void Columns_DeclaresAThumbnailThenTitleArtistDuration()
    {
        var provider = Provider();

        Assert.Collection(provider.Columns,
            c => Assert.Equal(MediaResultColumn.ThumbnailKey, c.Key),
            c => Assert.Equal(MediaResultColumn.TitleKey, c.Key),
            c => Assert.Equal(MediaResultColumn.ArtistKey, c.Key),
            c => Assert.Equal(MediaResultColumn.DurationKey, c.Key));
    }

    [Fact]
    public void Columns_ThumbnailIsTheFirstOneANarrowPanelDrops()
    {
        var thumbnail = Provider().Columns[0];

        // It helps a host recognise a track but names none of them, so the title and the artist
        // both outrank it when there is not room for everything.
        Assert.Equal(MediaResultColumnKind.Thumbnail, thumbnail.EffectiveKind);
        Assert.False(thumbnail.Essential);
    }

    // Shaped from a live session/remote capture: the payload sits on the response root, the link
    // is short_url, and qrcode is a base64 PNG of an already-drawn code rather than text.
    private const string OkRemote =
        """<response status="OK"><short_url>www.karafun.com/411854</short_url><channel>411854</channel><qrcode>iVBORw0KGgo=</qrcode><max_remotes>20</max_remotes></response>""";

    /// <summary>The host draws its own code from the string: KaraFun's qrcode_svg is a picture,
    /// and a code carrying the same text scans to the same place.</summary>
    [Fact]
    public async Task SignIn_PutsTheRemoteCodeOnTheScreens()
    {
        await RoomLiveAsync(new Dictionary<string, string> { ["session/remote"] = OkRemote });

        await WaitForCodesShownAsync(1);

        await _plugin.Received(1).RegisterQrCodeAsync(
            "www.karafun.com/411854", Arg.Any<string?>(), QrCodeFeatures.SongEnqueue, Arg.Any<CancellationToken>());
    }

    /// <summary>A code on screen means the room is live: a connected socket alone is inert until
    /// authenticated, so the code waits and a guest never scans one nobody is listening behind.</summary>
    [Fact]
    public async Task TheCode_WaitsForAuthenticationRatherThanTheConnection()
    {
        var responses = new Dictionary<string, string>
        {
            ["session/info"] = OkSessionInfoWithRemote,
            ["session/remote"] = OkRemote,
        };

        // Connects, then silence: the server has not said the socket is authenticated.
        var socket = new FakeKcsSocket();
        await SignedInAsync(responses, socket);
        await WaitForConnectAsync(socket);

        AssertNoCodeRegistered();

        socket.Deliver(new Message
        {
            Event = new Message.Types.Event
            {
                Socket = new SocketEvent { AuthenticatedEvent = new ModelsEmpty() },
            },
        });

        await WaitForCodesShownAsync(1);
    }

    /// <summary>Losing the socket takes the code down, even though it minted fine earlier.</summary>
    [Fact]
    public async Task TheSocketDropping_TakesTheCodeDownAgain()
    {
        var socket = await RoomLiveAsync(new Dictionary<string, string> { ["session/remote"] = OkRemote });
        await WaitForCodesShownAsync(1);

        socket.Close();

        await WaitForCodesHiddenAsync(1);
        await _plugin.Received().UnregisterQrCodeAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>A console that comes up on a remembered key never signs in, so nothing but the
    /// restore path can raise its code.</summary>
    [Fact]
    public async Task ARememberedSession_PutsTheCodeUpWithoutAnyoneSigningIn()
    {
        var responses = new Dictionary<string, string>
        {
            ["session/open"] = OkSession,
            ["session/info"] = OkSessionInfo,
            ["session/remote"] = OkRemote,
            ["search/top"] = """<response status="OK"><list /></response>""",
        };
        responses["session/info"] = OkSessionInfoWithRemote;
        var socket = new FakeKcsSocket { AuthenticatesOnConnect = true };
        var after = await SignInThenRestartAsync(responses, socket: socket);
        _plugin.ClearReceivedCalls();
        Interlocked.Exchange(ref _codesShown, 0);

        await after.SearchAsync("neon moon");

        await WaitForCodesShownAsync(1);
        await _plugin.Received(1).RegisterQrCodeAsync(
            "www.karafun.com/411854", Arg.Any<string?>(), QrCodeFeatures.SongEnqueue, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignOut_TakesTheCodeDown()
    {
        var responses = new Dictionary<string, string> { ["session/remote"] = OkRemote };
        var socket = new FakeKcsSocket { AuthenticatesOnConnect = true };
        responses["session/info"] = OkSessionInfoWithRemote;
        var (provider, _) = await SignedInAsync(responses, socket);
        await WaitForConnectAsync(socket);
        await WaitForCodesShownAsync(1);

        await provider.InvokeButtonAsync("session");

        await _plugin.Received().UnregisterQrCodeAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>The endpoint's shape is inferred, not captured, so this failing is a live
    /// possibility; a code that opens nothing is worse than no code.</summary>
    [Fact]
    public async Task ARemoteThatWillNotOpen_ShowsNoCodeAndSaysSo()
    {
        await RoomLiveAsync(new Dictionary<string, string>
        {
            ["session/remote"] = """<response status="Error"><error action="fail">MINIMUM_PLAN_NOT_REACHED</error></response>""",
        });

        await WaitForAsync(
            () => _plugin.ReceivedCalls().Any(call => call.GetArguments() is [string message]
                && message.Contains("MINIMUM_PLAN_NOT_REACHED")),
            "the host being told the remote would not open");

        AssertNoCodeRegistered();

        _flash.Received(1).Show(
            Arg.Is<string>(m => m.StartsWith("KaraFun:") && m.Contains("guest room")), FlashType.Warning);
    }

    /// <summary>Unlike a lapsed session, nothing here forgets the account: the socket keeps
    /// authenticating on every reconnect, and each pass retries the same failing room open. A flash
    /// per retry would spam the console all night for the one cause.</summary>
    [Fact]
    public async Task ARemoteThatWillNotOpen_FlashesOnceDespiteRepeatedReconnects()
    {
        var responses = new Dictionary<string, string>
        {
            ["session/info"] = OkSessionInfoWithRemote,
            ["session/remote"] = """<response status="Error"><error action="fail">MINIMUM_PLAN_NOT_REACHED</error></response>""",
        };
        var socket = new FakeKcsSocket();
        await SignedInAsync(responses, socket);
        await WaitForConnectAsync(socket);

        var authenticated = new Message
        {
            Event = new Message.Types.Event { Socket = new SocketEvent { AuthenticatedEvent = new ModelsEmpty() } },
        };

        socket.Deliver(authenticated);
        await WaitForCallAsync(() => _plugin.ReceivedCalls().Any(call => call.GetArguments() is [string m] && m.Contains("MINIMUM_PLAN_NOT_REACHED")));
        socket.Deliver(authenticated);
        socket.Deliver(authenticated);

        // Give a second, spurious flash a chance to land before asserting there is only one.
        await Task.Delay(50);
        _flash.Received(1).Show(Arg.Any<string>(), FlashType.Warning);
    }

    /// <summary>A signed GET on <c>session/remote</c>, like every other read here. Pinned because a
    /// signed POST fails everywhere in this API, masking a routing regression as a signing bug.</summary>
    [Fact]
    public async Task TheRemote_IsOpenedWithASignedGet()
    {
        var responses = new Dictionary<string, string>
        {
            ["session/open"] = OkSession,
            ["session/info"] = OkSessionInfoWithRemote,
            ["session/remote"] = OkRemote,
        };
        var handler = new CountingHandler(responses);
        ArrangeCredentialPrompt("ada@example.com", "hunter2");
        var socket = new FakeKcsSocket { AuthenticatesOnConnect = true };
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)), socket);

        await provider.InvokeButtonAsync("session");
        await WaitForCodesShownAsync(1);

        Assert.Equal("GET", handler.Method("session/remote"));
        Assert.Equal(0, handler.Count("remote"));
    }

    /// <summary>A channel belongs to the session that opened it, so signing back in claims a new
    /// one rather than leaving a code that pairs a guest's phone to a session that is gone.</summary>
    [Fact]
    public async Task SigningBackIn_ClaimsAFreshChannel()
    {
        var handler = new CountingHandler(new Dictionary<string, string>
        {
            ["session/open"] = OkSession,
            ["session/info"] = OkSessionInfoWithRemote,
            ["session/remote"] = OkRemote,
        });
        ArrangeCredentialPrompt("ada@example.com", "hunter2");

        // One socket per channel: signing out disposes the first with its channel, and a disposed
        // socket hangs up on the next one the moment it reads.
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)),
            () => new FakeKcsSocket { AuthenticatesOnConnect = true });

        await provider.InvokeButtonAsync("session");
        await WaitForCodesShownAsync(1);

        await provider.InvokeButtonAsync("session");
        await provider.InvokeButtonAsync("session");
        await WaitForCodesShownAsync(2);

        Assert.Equal(2, handler.Count("session/remote"));
    }

    // ---- the room's picks ------------------------------------------------

    /// <summary>Stands in for a guest picking a song on their phone.</summary>
    private static Message RemotePick(uint songId, string singer, string remoteId = "") => new()
    {
        Request = new Message.Types.Request
        {
            Id = 1,
            Player = new PlayerRequest
            {
                AddToQueue = new PlayerAddToQueueRequest
                {
                    Song = new PlayerSongIdentifier { Id = songId, Type = PlayerSongIdentifier.Types.TypeEnum.Catalog },
                    RemoteId = remoteId,
                    Options = new PlayerSongOptions { Singer = singer },
                },
            },
        },
    };

    private async Task<FakeKcsSocket> RoomOpenAsync(Dictionary<string, string> responses)
    {
        responses["session/info"] = OkSessionInfoWithRemote;
        var socket = new FakeKcsSocket();
        await SignedInAsync(responses, socket);

        await WaitForConnectAsync(socket);

        return socket;
    }

    /// <summary>Authenticating, not signing in, is what raises the code: the only moment this host
    /// knows something is on the other end of the socket.</summary>
    private async Task<FakeKcsSocket> RoomLiveAsync(Dictionary<string, string> responses)
    {
        responses["session/info"] = OkSessionInfoWithRemote;

        var socket = new FakeKcsSocket { AuthenticatesOnConnect = true };
        await SignedInAsync(responses, socket);
        await WaitForConnectAsync(socket);

        return socket;
    }

    private Task WaitForCodesShownAsync(int count) => WaitForAsync(() => Volatile.Read(ref _codesShown) >= count,
        $"{count} code(s) on the screens");

    private Task WaitForCodesHiddenAsync(int count) => WaitForAsync(() => Volatile.Read(ref _codesHidden) >= count,
        $"{count} code(s) taken down");

    private static async Task WaitForAsync(Func<bool> settled, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (settled())
                return;

            await Task.Delay(5);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    /// <summary>Signing in reads the session for the socket's address; the channel connects there.</summary>
    private static async Task WaitForConnectAsync(FakeKcsSocket socket)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (socket.ConnectedTo is null && DateTime.UtcNow < deadline)
            await Task.Delay(5);

        Assert.NotNull(socket.ConnectedTo);
    }

    private static async Task WaitForCallAsync(Func<bool> made)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (made())
                return;

            await Task.Delay(5);
        }

        throw new TimeoutException("The call never came.");
    }

    private async Task WaitForEnqueueAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (_performances.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(IPerformanceService.TryCreateAndEnqueueAsync)))
                return;

            await Task.Delay(5);
        }

        throw new TimeoutException("Nothing was enqueued.");
    }

    /// <summary>The whole point of the channel: a name typed into a phone becomes a singer on the
    /// list with a song under them, without the host touching anything.</summary>
    [Fact]
    public async Task RemotePick_ForANameNobodyHas_AddsThatSingerToTheQueueWithTheirSong()
    {
        ArrangeNoSingerNamed("Ada");
        var created = new KHostUser { Id = Guid.NewGuid(), Name = "Ada" };
        _users.CreateAsync(Arg.Any<KHostUser>()).Returns(created);
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = Guid.NewGuid() });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForEnqueueAsync();

        await _users.Received(1).CreateAsync(Arg.Is<KHostUser>(user => user.Name == "Ada"));
        await _queue.Received(1).AddUserAsync(created.Id);
        await _performances.Received(1).TryCreateAndEnqueueAsync(Arg.Is<Performance>(p => p.SingerId == created.Id), EnqueueOrigin.Remote);
    }

    /// <summary>Phase one has written nothing, so a cancelled row goes with it rather than staying
    /// behind as a Broken row nobody asked for.</summary>
    [Fact]
    public async Task CancellingDuringTheDownload_DiscardsTheRowRatherThanBreakingIt()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var mediaId = Guid.NewGuid();
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>())
            .Returns(new ImportTicket { MediaId = mediaId, Cancellation = cancelled.Token });

        var discarded = 0;
        _library.When(library => library.DiscardImportAsync(mediaId))
            .Do(_ => Interlocked.Increment(ref discarded));

        ArrangeNoSingerNamed("Ada");
        _users.CreateAsync(Arg.Any<KHostUser>()).Returns(new KHostUser { Id = Guid.NewGuid(), Name = "Ada" });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));

        await WaitForAsync(() => Volatile.Read(ref discarded) > 0, "the cancelled import to be discarded");

        await _library.Received(1).DiscardImportAsync(mediaId);
        await _library.DidNotReceive().FailImportAsync(Arg.Any<Guid>());
        await _library.DidNotReceive().CompleteImportAsync(Arg.Any<Guid>());
    }

    /// <summary>Left on disk otherwise, the next attempt at that song takes the file-already-there
    /// path and imports the truncated download as Ready.</summary>
    [Fact]
    public async Task CancellingADownload_DeletesWhateverWasLeftAtTheDestination()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var destination = Path.Combine(_mediaDirectory, "karafun", "6229.kit");
        var mediaId = Guid.NewGuid();

        _library.BeginImportAsync(Arg.Any<MediaImportRequest>())
            .Returns(_ =>
            {
                // Stands in for the download having written part of the file: it has to appear
                // after the check that short-circuits on an already-downloaded song, not before it.
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(destination, "half a download");
                return new ImportTicket { MediaId = mediaId, Cancellation = cancelled.Token };
            });

        var discarded = 0;
        _library.When(library => library.DiscardImportAsync(mediaId))
            .Do(_ => Interlocked.Increment(ref discarded));

        ArrangeNoSingerNamed("Ada");
        _users.CreateAsync(Arg.Any<KHostUser>()).Returns(new KHostUser { Id = Guid.NewGuid(), Name = "Ada" });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));

        await WaitForAsync(() => Volatile.Read(ref discarded) > 0, "the cancelled import to be discarded");

        Assert.False(File.Exists(destination), "the partial download outlived the cancel");
    }

    /// <summary>The fetch is the half that had nothing to report: a spinner for as long as it ran,
    /// then a bar that was really the download's. It counts bytes now.</summary>
    [Fact]
    public async Task ADownloadThatFails_SaysWhyRatherThanJustFailing()
    {
        var mediaId = Guid.NewGuid();
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>())
            .Returns(new ImportTicket { MediaId = mediaId });

        var reasons = new List<string?>();
        _library.When(library => library.FailImportAsync(mediaId, Arg.Any<string?>()))
            .Do(call => { lock (reasons) reasons.Add(call.ArgAt<string?>(1)); });

        ArrangeNoSingerNamed("Ada");
        _users.CreateAsync(Arg.Any<KHostUser>()).Returns(new KHostUser { Id = Guid.NewGuid(), Name = "Ada" });

        // No song/request response, so the fetch cannot produce a .kit at all.
        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));

        await WaitForAsync(() => { lock (reasons) return reasons.Count > 0; }, "the failure to be reported");

        lock (reasons)
            Assert.False(string.IsNullOrWhiteSpace(reasons[0]), "the row failed with no reason to show");
    }

    /// <summary>A download the host (or the guest's remote pick) started failing is exactly the
    /// "download/processing failures the host started" case the flash guidance names.</summary>
    [Fact]
    public async Task ADownloadThatFails_AlsoFlashesNamingKaraFunAndTheSong()
    {
        var mediaId = Guid.NewGuid();
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = mediaId });

        ArrangeNoSingerNamed("Ada");
        _users.CreateAsync(Arg.Any<KHostUser>()).Returns(new KHostUser { Id = Guid.NewGuid(), Name = "Ada" });

        // No song/request response, so the fetch cannot produce a .kit at all.
        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));

        await WaitForCallAsync(() => _flash.ReceivedCalls().Any());

        _flash.Received(1).Show(
            Arg.Is<string>(m => m.StartsWith("KaraFun:") && m.Contains("Neon Moon")),
            FlashType.Warning);
    }

    /// <summary>A guest's phone could not be described is the "guest remote status changes the host
    /// must know" case; it is a live room event, not a retry, so no dedup guard applies to it.</summary>
    [Fact]
    public async Task ARemotePickKaraFunCannotDescribe_FlashesRatherThanJustReporting()
    {
        var socket = await RoomOpenAsync(new()
        {
            // song/info answers Error, so the pick cannot be described at all.
            ["song/info"] = """<response status="Error"><error action="fail">UNKNOWN_SONG</error></response>""",
        });

        socket.Deliver(RemotePick(6229, "Ada"));

        await WaitForCallAsync(() => _plugin.ReceivedCalls()
            .Any(call => call.GetArguments() is [string message] && message.Contains("6229")));

        _flash.Received(1).Show(
            Arg.Is<string>(m => m.StartsWith("KaraFun:") && m.Contains("Ada")),
            FlashType.Warning);
    }

    /// <summary>This queue is song-first: a guest's typed name belongs to the song rather than the
    /// account it reached.</summary>
    [Fact]
    public async Task RemotePick_RecordsTheNameTheGuestTypedOnThatSong()
    {
        ArrangeNoSingerNamed("Ada");
        _users.CreateAsync(Arg.Any<KHostUser>()).Returns(new KHostUser { Id = Guid.NewGuid(), Name = "Ada" });
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = Guid.NewGuid() });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForEnqueueAsync();

        await _performances.Received(1).TryCreateAndEnqueueAsync(
            Arg.Is<Performance>(performance => performance.SungAs == "Ada"), EnqueueOrigin.Remote);
    }

    /// <summary>The host turns the pick away without a word and the phone was told it went through,
    /// so the flash is the only place anyone learns the song is not coming, and who has it.</summary>
    [Fact]
    public async Task RemotePick_ForASongAnotherSingerHasQueued_TellsTheHostWhoHasIt()
    {
        // The account is named apart from the signup, so the name the room knows them by is the one read.
        var robert = new KHostUser { Id = Guid.NewGuid(), Name = "Robert" };
        _users.ReadAsync(robert.Id).Returns(robert);
        ArrangeBlockedPick(EnqueueResultType.QueuedForAnotherSinger, new Performance { SingerId = robert.Id, SungAs = "Bob" });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForFlashAsync();

        _flash.Received(1).Show("KaraFun: Ada tried to sign up for 'Neon Moon', but it's already queued for Bob.", FlashType.Warning);
    }

    [Fact]
    public async Task RemotePick_ForASongAnotherSingerHasQueuedUnderTheirOwnName_NamesThemFromTheirAccount()
    {
        var bob = new KHostUser { Id = Guid.NewGuid(), Name = "Bob" };
        _users.ReadAsync(bob.Id).Returns(bob);
        ArrangeBlockedPick(EnqueueResultType.QueuedForAnotherSinger, new Performance { SingerId = bob.Id, SungAs = null });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForFlashAsync();

        _flash.Received(1).Show(Arg.Is<string>(text => text.EndsWith("already queued for Bob.")), FlashType.Warning);
    }

    [Fact]
    public async Task RemotePick_ForASongTheSingerAlreadyHasQueued_TellsTheHost()
    {
        ArrangeBlockedPick(EnqueueResultType.AlreadyQueued, new Performance { SingerId = Guid.NewGuid(), SungAs = "Ada" });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForFlashAsync();

        _flash.Received(1).Show("KaraFun: Ada tried to sign up for 'Neon Moon', but they already have it queued.", FlashType.Warning);
    }

    /// <summary>A queued pick and a host who declined the duplicate warning both need no word: the
    /// first is what was asked for, the second the host just did themselves.</summary>
    [Theory]
    [InlineData(EnqueueResultType.Queued)]
    [InlineData(EnqueueResultType.DeclinedAtWarning)]
    public async Task RemotePick_NotBlockedByAnotherTurn_FlashesNothing(EnqueueResultType type)
    {
        // A conflict alongside, so this reads the result's type rather than a missing turn.
        ArrangeBlockedPick(type, new Performance { SingerId = Guid.NewGuid(), SungAs = "Bob" });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForEnqueueAsync();
        await Task.Delay(50);

        // The fixture serves no download, so that failure flashes; only the signup line is at issue.
        _flash.DidNotReceive().Show(Arg.Is<string>(text => text.Contains("tried to sign up")), Arg.Any<FlashType>());
    }

    [Fact]
    public async Task RemotePick_OverTheLimit_IsIgnoredAndTheHostToldOnce()
    {
        var (socket, _) = await RoomOpenWithPickLimitAsync(limit: 2);

        for (var i = 0; i < 4; i++)
            socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));
        await WaitForAsync(() => EnqueueCalls() == 2 && IgnoringFlashes() == 1, "two picks queued and one warning");
        await Task.Delay(100);

        Assert.Equal(2, EnqueueCalls());
        _flash.Received(1).Show("KaraFun: Ignoring song queue requests from Ada — more than 2 in 10 minutes.", FlashType.Warning);
    }

    /// <summary>The window slides: once the oldest pick ages out, the guest may pick again, and a
    /// fresh burst past the limit tells the host again.</summary>
    [Fact]
    public async Task RemotePick_OnceTheWindowHasPassed_IsAdmittedAndANewBurstWarnsAgain()
    {
        var (socket, clock) = await RoomOpenWithPickLimitAsync(limit: 2);
        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));
        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));
        await WaitForAsync(() => EnqueueCalls() == 2, "two picks queued");
        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));
        await WaitForAsync(() => IgnoringFlashes() == 1, "the first warning");

        clock.Advance(TimeSpan.FromMinutes(10));
        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));
        await WaitForAsync(() => EnqueueCalls() == 3, "the pick after the window");
        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));
        await WaitForAsync(() => EnqueueCalls() == 4, "the second slot of the new window");
        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));

        await WaitForAsync(() => IgnoringFlashes() == 2, "a second warning for the second burst");
    }

    /// <summary>A save made while the room is open reaches the next pick.</summary>
    [Fact]
    public async Task RemotePick_LimitSavedAfterTheRoomOpened_AppliesToTheNextPick()
    {
        var (socket, _) = await RoomOpenWithPickLimitAsync(limit: 1);
        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));
        await WaitForAsync(() => EnqueueCalls() == 1, "the first pick queued");

        _settings.CurrentValue.Returns(new KaraFunSettings { RemotePickLimit = 2, RemotePickWindowMinutes = 10 });
        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));

        await WaitForAsync(() => EnqueueCalls() == 2, "the second pick under the raised limit");
    }

    [Fact]
    public async Task RemotePick_WindowSavedAfterTheRoomOpened_AppliesToTheNextPick()
    {
        var (socket, clock) = await RoomOpenWithPickLimitAsync(limit: 1);
        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));
        await WaitForAsync(() => EnqueueCalls() == 1, "the first pick queued");

        clock.Advance(TimeSpan.FromMinutes(5));
        _settings.CurrentValue.Returns(new KaraFunSettings { RemotePickLimit = 1, RemotePickWindowMinutes = 3 });
        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));

        await WaitForAsync(() => EnqueueCalls() == 2, "the second pick once the shortened window has passed");
    }

    [Fact]
    public async Task RemotePick_RefusalNamesTheLimitSavedNow()
    {
        var (socket, _) = await RoomOpenWithPickLimitAsync(limit: 1);
        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));
        await WaitForAsync(() => EnqueueCalls() == 1, "the first pick queued");

        _settings.CurrentValue.Returns(new KaraFunSettings { RemotePickLimit = 1, RemotePickWindowMinutes = 20 });
        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));

        await WaitForAsync(() => IgnoringFlashes() == 1, "the warning");
        _flash.Received(1).Show("KaraFun: Ignoring song queue requests from Ada — more than 1 in 20 minutes.", FlashType.Warning);
    }

    /// <summary>One guest's flood must not cost another guest their turn.</summary>
    [Fact]
    public async Task RemotePick_FromAnotherPhone_IsCountedApart()
    {
        var (socket, _) = await RoomOpenWithPickLimitAsync(limit: 1);

        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));
        socket.Deliver(RemotePick(6229, "Bob", remoteId: "phone-2"));

        await WaitForAsync(() => EnqueueCalls() == 2, "one pick from each phone");
        Assert.Equal(0, IgnoringFlashes());
    }

    /// <summary>A phone is the guest, whatever name it types: renaming must not reset the count.</summary>
    [Fact]
    public async Task RemotePick_UnderANewNameFromTheSamePhone_StillCounts()
    {
        var (socket, _) = await RoomOpenWithPickLimitAsync(limit: 1);

        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));
        await WaitForAsync(() => EnqueueCalls() == 1, "the first pick");
        socket.Deliver(RemotePick(6229, "Not Ada", remoteId: "phone-1"));

        await WaitForAsync(() => IgnoringFlashes() == 1, "the renamed pick refused");
        Assert.Equal(1, EnqueueCalls());
    }

    /// <summary>A phone that sends no id is told apart by the name it types.</summary>
    [Fact]
    public async Task RemotePick_WithNoPhoneId_IsCountedByName()
    {
        var (socket, _) = await RoomOpenWithPickLimitAsync(limit: 1);

        socket.Deliver(RemotePick(6229, "Ada"));
        socket.Deliver(RemotePick(6229, "Bob"));
        await WaitForAsync(() => EnqueueCalls() == 2, "one pick from each name");
        socket.Deliver(RemotePick(6229, "Ada"));

        await WaitForAsync(() => IgnoringFlashes() == 1, "Ada's second pick refused");
        Assert.Equal(2, EnqueueCalls());
    }

    [Fact]
    public async Task RemotePick_LimitOfZero_IsNoLimit()
    {
        var (socket, _) = await RoomOpenWithPickLimitAsync(limit: 0);

        for (var i = 0; i < 6; i++)
            socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));

        await WaitForAsync(() => EnqueueCalls() == 6, "every pick queued");
        Assert.Equal(0, IgnoringFlashes());
    }

    private async Task<(FakeKcsSocket Socket, ManualClock Clock)> RoomOpenWithPickLimitAsync(int limit)
    {
        _settings.CurrentValue.Returns(new KaraFunSettings { RemotePickLimit = limit, RemotePickWindowMinutes = 10 });
        ArrangeSingerNamed("Ada");
        ArrangeSingerNamed("Bob");
        ArrangeSingerNamed("Not Ada");
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = Guid.NewGuid() });

        var clock = new ManualClock();
        var socket = new FakeKcsSocket();
        var (provider, _) = await SignedInAsync(new() { ["song/info"] = CatalogInfo, ["session/info"] = OkSessionInfoWithRemote }, socket);
        provider.Clock = clock;
        await WaitForConnectAsync(socket);

        return (socket, clock);
    }

    private int EnqueueCalls()
        => _performances.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IPerformanceService.TryCreateAndEnqueueAsync));

    private int IgnoringFlashes()
        => _flash.ReceivedCalls().Count(call => call.GetArguments()[0] is string text && text.StartsWith("KaraFun: Ignoring song queue requests"));

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 7, 20, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>By name, so neither overload slips past: asserting one leaves a call to the other unseen.</summary>
    private void AssertNoCodeRegistered()
        => Assert.DoesNotContain(_plugin.ReceivedCalls(),
            call => call.GetMethodInfo().Name == nameof(IPluginContext.RegisterQrCodeAsync));

    /// <summary>By name, so neither overload slips past an assertion that nothing was queued.</summary>
    private IEnumerable<Performance> EnqueuedPerformances()
        => _performances.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IPerformanceService.TryCreateAndEnqueueAsync))
            .Select(call => (Performance)call.GetArguments()[0]!);

    [Fact]
    public async Task RemotePick_OverTheVenuesSongLimit_TellsTheHostHowManyTheyHave()
    {
        ArrangeVenueSongLimit(3);
        ArrangeBlockedPick(EnqueueResultType.SingerAtLimit, conflict: null);

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForFlashAsync();

        _flash.Received(1).Show("KaraFun: Ada tried to sign up for 'Neon Moon', but already has 3 songs queued, the most this venue allows.", FlashType.Warning);
    }

    [Fact]
    public async Task RemotePick_OverALimitOfOne_SaysSong()
    {
        ArrangeVenueSongLimit(1);
        ArrangeBlockedPick(EnqueueResultType.SingerAtLimit, conflict: null);

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForFlashAsync();

        _flash.Received(1).Show(Arg.Is<string>(text => text.Contains("already has 1 song queued")), FlashType.Warning);
    }

    /// <summary>The refusal stands even when the venue cannot be read back to word it.</summary>
    [Fact]
    public async Task RemotePick_OverALimitThatCannotBeRead_StillTellsTheHost()
    {
        ArrangeBlockedPick(EnqueueResultType.SingerAtLimit, conflict: null);

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForFlashAsync();

        _flash.Received(1).Show(Arg.Is<string>(text => text.Contains("as many songs queued as this venue allows")), FlashType.Warning);
    }

    /// <summary>A guest's pick that waits behind another guest's download is still a guest's: the
    /// venue's song limit must hold it as it holds the first.</summary>
    [Fact]
    public async Task RemotePick_JoiningARunningDownload_IsStillARemoteSignup()
    {
        ArrangeSingerNamed("Ada");
        ArrangeSingerNamed("Bob");
        var importBegun = new TaskCompletionSource<ImportTicket>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(importBegun.Task);

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));
        await WaitForAsync(() => _library.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(IMediaAcquisitionService.BeginImportAsync)),
            "the first pick's import to begin");
        socket.Deliver(RemotePick(6229, "Bob", remoteId: "phone-2"));
        await Task.Delay(50);
        importBegun.SetResult(new ImportTicket { MediaId = Guid.NewGuid() });
        await WaitForAsync(() => EnqueuedPerformances().Count() == 2, "both picks queued");

        var origins = _performances.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IPerformanceService.TryCreateAndEnqueueAsync))
            .Select(call => call.GetArguments()[1]);
        Assert.All(origins, origin => Assert.Equal(EnqueueOrigin.Remote, origin));
        await _library.Received(1).BeginImportAsync(Arg.Any<MediaImportRequest>());
    }

        private void ArrangeVenueSongLimit(int limit)
    {
        var venues = Substitute.For<IVenuesService>();
        venues.ReadSelectedVenueAsync().Returns(new Venue { Name = "The Bar", Settings = { RemoteSongLimit = limit } });
        _services.GetService(typeof(IVenuesService)).Returns(venues);
    }

    private void ArrangeBlockedPick(EnqueueResultType type, Performance? conflict)
    {
        ArrangeNoSingerNamed("Ada");
        _users.CreateAsync(Arg.Any<KHostUser>()).Returns(new KHostUser { Id = Guid.NewGuid(), Name = "Ada" });
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = Guid.NewGuid() });
        _performances.TryCreateAndEnqueueAsync(Arg.Any<Performance>(), Arg.Any<EnqueueOrigin>()).Returns(new EnqueueResult(type, Conflict: conflict));
    }

    private Task WaitForFlashAsync()
        => WaitForCallAsync(() => _flash.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(IFlashService.Show)));

    /// <summary>A second "ada" beside "Ada" would split the regular's history and rotation turn.</summary>
    [Fact]
    public async Task RemotePick_ForANameTheHostAlreadyHas_UsesThatSingerRatherThanASecondOne()
    {
        var existing = ArrangeSingerNamed("ada");
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = Guid.NewGuid() });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "ada"));
        await WaitForEnqueueAsync();

        await _users.DidNotReceive().CreateAsync(Arg.Any<KHostUser>());
        await _performances.Received(1).TryCreateAndEnqueueAsync(Arg.Is<Performance>(p => p.SingerId == existing.Id), EnqueueOrigin.Remote);
    }

    /// <summary>A pick goes on the singer it names, never on whoever is at the microphone: the
    /// console's selection belongs to the host, and a guest must not move it.</summary>
    [Fact]
    public async Task RemotePick_DoesNotGoToTheSingerTheConsoleHasSelected()
    {
        ArrangeNoSingerNamed("Ada");
        var created = new KHostUser { Id = Guid.NewGuid(), Name = "Ada" };
        _users.CreateAsync(Arg.Any<KHostUser>()).Returns(created);
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = Guid.NewGuid() });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForEnqueueAsync();

        Assert.NotEqual(_singerId, created.Id);
        Assert.DoesNotContain(EnqueuedPerformances(), p => p.SingerId == _singerId);
    }

    /// <summary>A singer already on the list is not added to it twice.</summary>
    [Fact]
    public async Task RemotePick_ForASingerAlreadyInTheQueue_DoesNotAddThemAgain()
    {
        var existing = ArrangeSingerNamed("Ada");
        _queue.Users.Returns([existing]);
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = Guid.NewGuid() });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForEnqueueAsync();

        await _queue.DidNotReceive().AddUserAsync(Arg.Any<Guid>());
    }

    /// <summary>The wire carries an id; the queue's row needs the title and artist behind it.</summary>
    [Fact]
    public async Task RemotePick_ReadsTheSongsDetailsBeforeQueueingIt()
    {
        ArrangeNoSingerNamed("Ada");
        _users.CreateAsync(Arg.Any<KHostUser>()).Returns(new KHostUser { Id = Guid.NewGuid(), Name = "Ada" });
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = Guid.NewGuid() });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForEnqueueAsync();

        await _library.Received(1).BeginImportAsync(Arg.Is<MediaImportRequest>(
            r => r.Title == "Neon Moon" && r.Artist == "Brooks & Dunn"));
    }

    /// <summary>The phone before the name: a guest can spell their name differently each pick,
    /// but the id on it does not move.</summary>
    [Fact]
    public async Task RemotePick_FromAPhoneTheHostKnows_GoesToThatSingerWhateverNameIsTyped()
    {
        var ada = ArrangeRemoteKnownAs("phone-1", "Ada");
        ArrangeNoSingerNamed("A.D.A.");
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = Guid.NewGuid() });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "A.D.A.", remoteId: "phone-1"));
        await WaitForEnqueueAsync();

        await _performances.Received(1).TryCreateAndEnqueueAsync(Arg.Is<Performance>(p => p.SingerId == ada.Id), EnqueueOrigin.Remote);
        await _users.DidNotReceive().CreateAsync(Arg.Any<KHostUser>());
    }

    /// <summary>Recognised by name the first time, then tied to the phone so the next pick skips
    /// it. Ephemeral, since a KaraFun id belongs to a pairing, not a person.</summary>
    [Fact]
    public async Task RemotePick_FromAnUnknownPhone_TiesItToTheSingerAsAnEphemeralKey()
    {
        var ada = ArrangeSingerNamed("Ada");
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = Guid.NewGuid() });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));
        await WaitForEnqueueAsync();

        await _users.Received(1).AddForeignKeyAsync(ada.Id, "KaraFun", "phone-1", true);
    }

    [Fact]
    public async Task RemotePick_WithNoPhoneOnIt_TiesNothing()
    {
        ArrangeSingerNamed("Ada");
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = Guid.NewGuid() });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForEnqueueAsync();

        await _users.DidNotReceive().AddForeignKeyAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    /// <summary>A key another singer holds is a guest who left between lookup and write; the pick
    /// still belongs to the singer just resolved, only the shortcut for their next pick is lost.</summary>
    [Fact]
    public async Task RemotePick_WhoseKeyIsAlreadyTaken_StillQueuesTheSong()
    {
        var ada = ArrangeSingerNamed("Ada");
        _users.AddForeignKeyAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(Task.FromException(new KHostException("taken", "", "KH-USER-FOREIGN-KEY-TAKEN")));
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = Guid.NewGuid() });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada", remoteId: "phone-1"));
        await WaitForEnqueueAsync();

        await _performances.Received(1).TryCreateAndEnqueueAsync(Arg.Is<Performance>(p => p.SingerId == ada.Id), EnqueueOrigin.Remote);
    }

    // ---- tying a phone to its singer -------------------------------------

    /// <summary>Stands in for the host's foreign-key table, keyed by remoteId, so a test reads
    /// which singer a phone is tied to rather than which calls happened to be made.</summary>
    private readonly Dictionary<string, KHostUser> _tied = [];
    private readonly Dictionary<Guid, KHostUser> _people = [];

    private void ArrangeKeyStore()
    {
        _users.ReadByForeignKeyAsync("KaraFun", Arg.Any<string>())
            .Returns(call => { lock (_tied) return Task.FromResult(_tied.GetValueOrDefault(call.ArgAt<string>(1))); });

        _users.AddForeignKeyAsync(Arg.Any<Guid>(), "KaraFun", Arg.Any<string>(), Arg.Any<bool>())
            .Returns(call =>
            {
                lock (_tied)
                {
                    var key = call.ArgAt<string>(2);
                    if (_tied.ContainsKey(key))
                        return Task.FromException(new KHostException("taken", "", "KH-USER-FOREIGN-KEY-TAKEN"));

                    _tied[key] = _people[call.ArgAt<Guid>(0)];
                }

                return Task.CompletedTask;
            });

        _users.RemoveForeignKeyAsync(Arg.Any<Guid>(), "KaraFun", Arg.Any<string>())
            .Returns(call =>
            {
                lock (_tied)
                    if (_tied.TryGetValue(call.ArgAt<string>(2), out var holder) && holder.Id == call.ArgAt<Guid>(0))
                        _tied.Remove(call.ArgAt<string>(2));

                return Task.CompletedTask;
            });

        _users.DeleteEphemeralForeignKeysAsync("KaraFun")
            .Returns(_ =>
            {
                lock (_tied)
                {
                    var count = _tied.Count;
                    _tied.Clear();
                    return Task.FromResult(count);
                }
            });
    }

    /// <summary>A singer the host already has, findable by name.</summary>
    private KHostUser Person(string name)
    {
        var user = ArrangeSingerNamed(name);
        _people[user.Id] = user;

        return user;
    }

    private void Tie(string remoteId, KHostUser singer)
    {
        lock (_tied)
            _tied[remoteId] = singer;
    }

    private KHostUser? TiedTo(string remoteId)
    {
        lock (_tied)
            return _tied.GetValueOrDefault(remoteId);
    }

    private static Message GuestJoined(string userId, string remoteId, string name) => new()
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

    private static Message GuestLeft(string userId) => new()
    {
        Event = new Message.Types.Event
        {
            Remote = new RemoteEvent { UserLeft = new RemoteUserLeftEvent { UserId = userId } },
        },
    };

    private static Message GuestList(params RemoteUser[] users) => new()
    {
        Response = new Message.Types.Response
        {
            Remote = new RemoteResponse { UserList = new RemoteUserListResponse { Users = { users } } },
        },
    };

    /// <summary>Every tie pass looks the phone up first, so this many lookups is this many passes.</summary>
    private Task WaitForLookupsAsync(string remoteId, int count)
        => WaitForCallAsync(() => _users.ReceivedCalls().Count(call =>
            call.GetMethodInfo().Name == nameof(IUsersService.ReadByForeignKeyAsync)
            && call.GetArguments()[1] is string key && key == remoteId) >= count);

    /// <summary>A phone leaves whenever it locks, so leaving is not unpairing: the badge and the
    /// shortcut for the guest's next pick must both survive it.</summary>
    [Fact]
    public async Task GuestLeaving_KeepsTheirPhonesTie()
    {
        ArrangeKeyStore();
        var ada = Person("Ada");
        Tie("phone-1", ada);
        var socket = await RoomOpenAsync([]);

        socket.Deliver(GuestJoined("u1", "phone-1", "Ada"));
        socket.Deliver(GuestLeft("u1"));
        // Read in order behind the leave, so its pass proves the leave has been handled.
        socket.Deliver(GuestJoined("u2", "phone-2", "Bob"));
        await WaitForLookupsAsync("phone-2", 1);

        Assert.Same(ada, TiedTo("phone-1"));
        await _users.DidNotReceive().RemoveForeignKeyAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task GuestRejoining_UnderTheSameName_KeepsTheTieWithoutAPick()
    {
        ArrangeKeyStore();
        var ada = Person("Ada");
        Tie("phone-1", ada);
        var socket = await RoomOpenAsync([]);

        socket.Deliver(GuestJoined("u1", "phone-1", "Ada"));
        socket.Deliver(GuestLeft("u1"));
        socket.Deliver(GuestJoined("u1", "phone-1", "Ada"));
        await WaitForLookupsAsync("phone-1", 2);

        Assert.Same(ada, TiedTo("phone-1"));
        await _users.DidNotReceive().RemoveForeignKeyAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
        await _users.DidNotReceive().CreateAsync(Arg.Any<KHostUser>());
    }

    /// <summary>A tie that is gone (an older build dropped it on leaving) comes back on the join,
    /// so the badge returns before the guest picks anything.</summary>
    [Fact]
    public async Task GuestRejoining_WithNoTieLeft_IsTiedBackByNameWithoutAPick()
    {
        ArrangeKeyStore();
        var ada = Person("Ada");
        var socket = await RoomOpenAsync([]);

        socket.Deliver(GuestJoined("u1", "phone-1", "Ada"));

        await WaitForAsync(() => TiedTo("phone-1") is not null, "phone-1 tied");
        Assert.Same(ada, TiedTo("phone-1"));
        await _users.DidNotReceive().CreateAsync(Arg.Any<KHostUser>());
    }

    /// <summary>Joining is not asking to sing: a stranger's join makes no singer.</summary>
    [Fact]
    public async Task GuestJoining_UnderANameNobodyHas_CreatesNoSinger()
    {
        ArrangeKeyStore();
        ArrangeNoSingerNamed("Zed");
        var socket = await RoomOpenAsync([]);

        socket.Deliver(GuestJoined("u1", "phone-1", "Zed"));
        await WaitForCallAsync(() => _users.ReceivedCalls().Any(call =>
            call.GetMethodInfo().Name == nameof(IUsersService.FindByNameAsync)));

        Assert.Null(TiedTo("phone-1"));
        await _users.DidNotReceive().CreateAsync(Arg.Any<KHostUser>());
    }

    /// <summary>A remoteId under a different nickname is a different guest, and the tie goes with
    /// the name rather than staying with whoever held the number before.</summary>
    [Fact]
    public async Task SameRemoteId_UnderANewName_MovesTheTie()
    {
        ArrangeKeyStore();
        var ada = Person("Ada");
        var bob = Person("Bob");
        Tie("phone-1", ada);
        var socket = await RoomOpenAsync([]);

        socket.Deliver(GuestJoined("u1", "phone-1", "Bob"));

        await WaitForAsync(() => TiedTo("phone-1")?.Id == bob.Id, "phone-1 tied to Bob");
    }

    /// <summary>Compared against the nickname the phone joined under this run, not the singer's own
    /// name: a guest who picked as somebody else is still the same guest when they come back.</summary>
    [Fact]
    public async Task SameRemoteId_UnderTheNicknameItJoinedWith_KeepsATieMadeUnderAnotherSingersName()
    {
        ArrangeKeyStore();
        ArrangeNoSingerNamed("Grace");
        var ada = Person("Ada");
        var socket = await RoomOpenAsync([]);

        socket.Deliver(GuestJoined("u1", "phone-1", "Grace"));
        await WaitForLookupsAsync("phone-1", 1);
        // Their pick named Ada, so the phone is tied to Ada.
        Tie("phone-1", ada);

        socket.Deliver(GuestLeft("u1"));
        socket.Deliver(GuestJoined("u1", "phone-1", "Grace"));
        await WaitForLookupsAsync("phone-1", 2);

        Assert.Same(ada, TiedTo("phone-1"));
    }

    /// <summary>A reconnect or a restart into the same room hears no joins for the guests already
    /// in it; the guest list is what ties them back.</summary>
    [Fact]
    public async Task GuestList_OnReconnect_TiesTheRoomsGuestsBack()
    {
        ArrangeKeyStore();
        var ada = Person("Ada");
        var socket = await RoomOpenAsync([]);

        socket.Deliver(GuestList(new RemoteUser { Id = "u1", RemoteId = "phone-1", Name = "Ada" }));

        await WaitForAsync(() => TiedTo("phone-1") is not null, "phone-1 tied");
        Assert.Same(ada, TiedTo("phone-1"));
    }

    private const string LastRoom = "www.karafun.com/411854";

    /// <summary>A sign-up link the ties were not made under is a new room, and a remoteId from the
    /// old one may name somebody else in it.</summary>
    [Fact]
    public async Task ANewRoom_DropsThePhonesTiedInTheLastOne()
    {
        ArrangeKeyStore();
        Tie("phone-1", Person("Ada"));
        await _cache.SaveAsync("karafun-room", "www.karafun.com/000000");

        await RoomLiveAsync(new Dictionary<string, string> { ["session/remote"] = OkRemote });
        await WaitForCodesShownAsync(1);

        Assert.Null(TiedTo("phone-1"));
        Assert.Equal(LastRoom, await _cache.LoadAsync<string>("karafun-room"));
    }

    /// <summary>A restart comes back to the same link, and its guests are the same people.</summary>
    [Fact]
    public async Task TheSameRoom_KeepsItsTies()
    {
        ArrangeKeyStore();
        var ada = Person("Ada");
        Tie("phone-1", ada);
        await _cache.SaveAsync("karafun-room", LastRoom);

        await RoomLiveAsync(new Dictionary<string, string> { ["session/remote"] = OkRemote });
        await WaitForCodesShownAsync(1);

        Assert.Same(ada, TiedTo("phone-1"));
        await _users.DidNotReceive().DeleteEphemeralForeignKeysAsync(Arg.Any<string>());
    }

    /// <summary>The guest list can land before the new room is known; the clear must not leave the
    /// guests it already tied untied.</summary>
    [Fact]
    public async Task ANewRoom_TiesItsGuestsAgainAfterTheClear()
    {
        ArrangeKeyStore();
        var ada = Person("Ada");
        await _cache.SaveAsync("karafun-room", "www.karafun.com/000000");
        var minted = new TaskCompletionSource();
        var handler = new HeldEndpointHandler(new Dictionary<string, string>
        {
            ["session/open"] = OkSession,
            ["session/info"] = OkSessionInfoWithRemote,
            ["session/remote"] = OkRemote,
        }, "session/remote", minted.Task);
        ArrangeCredentialPrompt("ada@example.com", "hunter2");
        var socket = new FakeKcsSocket();
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)), socket);
        await provider.InvokeButtonAsync("session");
        await WaitForConnectAsync(socket);

        socket.Deliver(new Message { Event = new Message.Types.Event { Socket = new SocketEvent { AuthenticatedEvent = new ModelsEmpty() } } });
        socket.Deliver(GuestList(new RemoteUser { Id = "u1", RemoteId = "phone-1", Name = "Ada" }));
        await WaitForAsync(() => TiedTo("phone-1") is not null, "phone-1 tied from the list");

        minted.SetResult();
        await WaitForCodesShownAsync(1);

        await _users.Received(1).DeleteEphemeralForeignKeysAsync("KaraFun");
        Assert.Same(ada, TiedTo("phone-1"));
    }

    [Fact]
    public async Task SignOut_DropsEveryPhoneTie()
    {
        ArrangeKeyStore();
        Tie("phone-1", Person("Ada"));
        await _cache.SaveAsync("karafun-room", LastRoom);
        var socket = new FakeKcsSocket { AuthenticatesOnConnect = true };
        var (provider, _) = await SignedInAsync(new()
        {
            ["session/info"] = OkSessionInfoWithRemote,
            ["session/remote"] = OkRemote,
        }, socket);
        await WaitForCodesShownAsync(1);

        await provider.InvokeButtonAsync("session");

        Assert.Null(TiedTo("phone-1"));
        Assert.Null(await _cache.LoadAsync<string>("karafun-room"));
    }

    /// <summary>Turning the guest remote off for the venue closes the room, and its ties with it.</summary>
    [Fact]
    public async Task GuestRemoteTurnedOff_DropsEveryPhoneTie()
    {
        ArrangeKeyStore();
        Tie("phone-1", Person("Ada"));
        await _cache.SaveAsync("karafun-room", LastRoom);

        var venue = new Venue { Name = "The Bar" };
        var venues = Substitute.For<IVenuesService>();
        venues.ReadSelectedVenueAsync().Returns(venue);
        _services.GetService(typeof(IVenuesService)).Returns(venues);

        Action<SelectedVenueChanged>? venueChanged = null;
        _broker.When(broker => broker.Subscribe(Arg.Any<Action<SelectedVenueChanged>>()))
            .Do(call => venueChanged = call.Arg<Action<SelectedVenueChanged>>());

        var socket = new FakeKcsSocket { AuthenticatesOnConnect = true };
        await SignedInAsync(new()
        {
            ["session/info"] = OkSessionInfoWithRemote,
            ["session/remote"] = OkRemote,
        }, socket);
        await WaitForCodesShownAsync(1);
        Assert.NotNull(TiedTo("phone-1"));

        venue.Settings.AllowGuestRemote = false;
        venueChanged!(new SelectedVenueChanged());

        await WaitForAsync(() => TiedTo("phone-1") is null, "phone-1 untied");
    }

    /// <summary>A venue whose first singer arrives through the remote never touches the console,
    /// so without the entry point the socket that guest's phone reaches is never opened.</summary>
    [Fact]
    public async Task Startup_OpensTheRoomWithoutWaitingForASearch()
    {
        var handler = new CountingHandler(new Dictionary<string, string>
        {
            ["session/open"] = OkSession,
            ["session/info"] = OkSessionInfoWithRemote,
        });
        ArrangeCredentialPrompt("ada@example.com", "hunter2");

        // Signed in on a previous run, on its own socket: sharing one with the relaunch below
        // would let this sign-in's connection stand in for the one being asserted.
        var signInSocket = new FakeKcsSocket();
        var before = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)), signInSocket);
        await before.InvokeButtonAsync("session");

        var startupSocket = new FakeKcsSocket();
        var relaunched = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)), startupSocket);

        await new KaraFunPlugin(relaunched).InitializeAsync(_plugin);

        // Nothing searched, nothing played, no button pressed, and the room is reachable.
        await WaitForConnectAsync(startupSocket);
    }

    /// <summary>The host runs the entry point blocking on its startup thread; awaiting a session's
    /// round trip over the room's wifi there is a console that will not open.</summary>
    [Fact]
    public async Task Startup_DoesNotWaitForKaraFunToAnswer()
    {
        var released = new TaskCompletionSource();
        var handler = new BlockingHandler(released.Task);
        await _cache.SaveAsync("karafun-session", new { Key = "sk-1", Login = "ada" });
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)), new FakeKcsSocket());

        var initialize = new KaraFunPlugin(provider).InitializeAsync(_plugin);

        // Returned while KaraFun is still holding the connection open.
        Assert.True(initialize.IsCompleted);

        released.SetResult();
        await initialize;
    }

    /// <summary>Signing out takes the room's way in down with it, or a socket left running on a
    /// dead session keeps queueing songs the host can no longer play.</summary>
    [Fact]
    public async Task SignOut_ClosesTheRoomsChannel()
    {
        var socket = new FakeKcsSocket();
        var (provider, _) = await SignedInAsync(new() { ["session/info"] = OkSessionInfoWithRemote }, socket);
        await WaitForConnectAsync(socket);

        await provider.InvokeButtonAsync("session");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!socket.Disposed && DateTime.UtcNow < deadline)
            await Task.Delay(5);

        Assert.True(socket.Disposed);
    }

    private const string OkSession =
        """<response status="OK"><session><key>sk-1</key><user_login>ada@example.com</user_login></session></response>""";

    // <community id meta/> is the shape a live top-search returns for a community hit; the rest of
    // its wire shape is read from the binary, so the client parses it leniently.
    private const string CatalogSearch =
        """<response status="OK"><list><song id="6229" content="a" meta="b" /></list></response>""";
    private const string CatalogInfo =
        """<response status="OK"><song id="6229" len="243"><title>Neon Moon</title><artist id="1">Brooks &amp; Dunn</artist></song></response>""";
    private const string CommunitySearch =
        """<response status="OK"><list><community id="77456" meta="53c25a" /></list></response>""";
    private const string CommunityInfo =
        """<response status="OK"><community id="77456" len="180"><title>Home Sweet Home</title><artist id="9">Mötley Crüe</artist></community></response>""";

    /// <summary>A signed-in provider over a counting handler, to see which endpoints ran.</summary>
    private async Task<(KaraFunMediaProvider Provider, CountingHandler Handler)> SignedInAsync(
        Dictionary<string, string> responses, FakeKcsSocket? socket = null, HttpClient? cdn = null)
    {
        responses["session/open"] = OkSession;
        var handler = new CountingHandler(responses);
        ArrangeCredentialPrompt("ada@example.com", "hunter2");
        var api = new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler), cdn: cdn);
        var provider = socket is null ? Provider(api) : Provider(api, socket);
        await provider.InvokeButtonAsync("session");

        return (provider, handler);
    }

    /// <summary>Restarted rather than freshly signed in, since session/info (read on restore) is
    /// the only response ever captured carrying an image template.</summary>
    private async Task<KaraFunMediaProvider> SessionWithATemplateAsync(Dictionary<string, string> responses)
    {
        responses["session/open"] = OkSession;
        responses["session/info"] = OkSessionInfoWithRemote;
        return await SignInThenRestartAsync(responses);
    }

    /// <summary>Nobody on the list answers to that name, so the host gains a singer.</summary>
    private void ArrangeNoSingerNamed(string name)
        => _users.FindByNameAsync(name).Returns((KHostUser?)null);

    private KHostUser ArrangeSingerNamed(string name)
    {
        var user = new KHostUser { Id = Guid.NewGuid(), Name = name };
        _users.FindByNameAsync(name).Returns(user);

        return user;
    }

    /// <summary>A phone the host has already tied to a singer.</summary>
    private KHostUser ArrangeRemoteKnownAs(string remoteId, string name)
    {
        var user = new KHostUser { Id = Guid.NewGuid(), Name = name };
        _users.ReadByForeignKeyAsync("KaraFun", remoteId).Returns(user);

        return user;
    }

    private static MediaSearchEntity CommunityOffer(List<MediaSearchEntity> results) => results[^1];

    /// <summary>Neither offer is a song, so the console draws a spanning row instead of columns
    /// sized for one, since "Search Community Songs" spills out of an "Enqueue"-sized cell.</summary>
    [Fact]
    public async Task SearchAsync_NoSession_TheSignInOfferSpansTheTable()
        => Assert.True(SignInRow(await Provider().SearchAsync("bohemian rhapsody")).SpansAllColumns);

    [Fact]
    public async Task Search_TheCommunityOffer_SpansTheTable()
    {
        var (provider, _) = await SignedInAsync(new() { ["search/top"] = CatalogSearch, ["song/info"] = CatalogInfo });

        var results = await provider.SearchAsync("neon moon");

        Assert.True(CommunityOffer(results).SpansAllColumns);
        // The songs beside it are ordinary rows and keep their columns.
        Assert.False(results[0].SpansAllColumns);
    }

    /// <summary>The offer goes last, under the catalog's own answers: somewhere else to look.</summary>
    [Fact]
    public async Task Search_CatalogResults_EndWithTheCommunityOffer()
    {
        var (provider, handler) = await SignedInAsync(new() { ["search/top"] = CatalogSearch, ["song/info"] = CatalogInfo });

        var results = await provider.SearchAsync("neon moon");

        Assert.Equal("Neon Moon", results[0].Title);

        // The catalog's own ids come off the <song> hits: the <community> ones in the same list
        // belong to the other catalog, and asking song/info for those would answer for nothing.
        Assert.Contains("song=6229", handler.Query("song/info"));
        var offer = CommunityOffer(results);
        Assert.Equal("Search Community Songs", offer.Title);
        Assert.Equal("Search Community Songs", Assert.Single(offer.SupportedActions).DisplayName);
    }

    /// <summary>The catalog having nothing is exactly when the other one is worth offering.</summary>
    [Fact]
    public async Task Search_CatalogFoundNothing_StillOffersTheCommunityCatalog()
    {
        var (provider, _) = await SignedInAsync(new()
        {
            ["search/top"] = """<response status="OK"><list /></response>""",
            ["song/info"] = """<response status="OK" />""",
        });

        var results = await provider.SearchAsync("obscure b-side");

        Assert.Equal("Search Community Songs", Assert.Single(results).Title);
    }

    /// <summary>Community ids are their own space, so this must go to community/info: song/info
    /// would answer for a different track, or for none.</summary>
    [Fact]
    public async Task CommunityOffer_Clicked_SearchesTheCommunityCatalogOnTheRefresh()
    {
        var (provider, handler) = await SignedInAsync(new()
        {
            ["search/top"] = CatalogSearch,
            ["song/info"] = CatalogInfo,
            ["search/community"] = CommunitySearch,
            ["community/info"] = CommunityInfo,
        });
        var offer = CommunityOffer(await provider.SearchAsync("home sweet home"));

        await Assert.Single(offer.SupportedActions).PerformAsync(offer);
        var results = await provider.SearchAsync("home sweet home");

        var song = Assert.Single(results);
        Assert.Equal("Home Sweet Home", song.Title);
        Assert.Equal("Mötley Crüe", song.Artist);
        Assert.Equal(TimeSpan.FromSeconds(180), song.Duration);
        Assert.Equal(1, handler.Count("search/community"));
        Assert.Equal(1, handler.Count("community/info"));

        // The id has to come off the <community> hits: reading <song> there instead would ask
        // community/info for nothing at all.
        Assert.Contains("id=77456", handler.Query("community/info"));
    }

    /// <summary>One click buys one search: leaving it armed would strand the host in the other
    /// catalog with no way back but retyping, the offer row not being among community results.</summary>
    [Fact]
    public async Task CommunityOffer_Clicked_IsSpentByThatOneSearch()
    {
        var (provider, _) = await SignedInAsync(new()
        {
            ["search/top"] = CatalogSearch,
            ["song/info"] = CatalogInfo,
            ["search/community"] = CommunitySearch,
            ["community/info"] = CommunityInfo,
        });
        var offer = CommunityOffer(await provider.SearchAsync("neon moon"));
        await Assert.Single(offer.SupportedActions).PerformAsync(offer);
        await provider.SearchAsync("neon moon");

        var results = await provider.SearchAsync("neon moon");

        Assert.Equal("Neon Moon", Assert.Single(SongRows(results)).Title);
    }

    /// <summary>A host who retypes before the refresh lands gets the catalog for the new words,
    /// not the community catalog for words they have moved on from.</summary>
    [Fact]
    public async Task CommunityOffer_ClickedThenADifferentQuery_SearchesTheCatalog()
    {
        var (provider, handler) = await SignedInAsync(new()
        {
            ["search/top"] = CatalogSearch,
            ["song/info"] = CatalogInfo,
            ["search/community"] = CommunitySearch,
            ["community/info"] = CommunityInfo,
        });
        var offer = CommunityOffer(await provider.SearchAsync("neon moon"));
        await Assert.Single(offer.SupportedActions).PerformAsync(offer);

        var results = await provider.SearchAsync("something else");

        Assert.Equal("Neon Moon", Assert.Single(SongRows(results)).Title);
        Assert.Equal(0, handler.Count("search/community"));
    }

    /// <summary>Community rows enqueue like any other; only the endpoints behind them differ.</summary>
    [Fact]
    public async Task Enqueue_CommunitySong_RequestsItFromTheCommunityEndpoint()
    {
        var (provider, handler) = await SignedInAsync(new()
        {
            ["search/community"] = CommunitySearch,
            ["community/info"] = CommunityInfo,
            // No <stream>, so the download gives up before any CDN fetch, enough to see which
            // endpoint was asked.
            ["community/request"] = """<response status="OK"><community /></response>""",
        });
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = Guid.NewGuid() });

        await EnqueueAction(provider).PerformAsync(SongEntity("community:77456"));

        Assert.Equal(1, handler.Count("community/request"));
        Assert.Equal(0, handler.Count("song/request"));
    }

    /// <summary>Community id 6229 and catalog id 6229 are different tracks; sharing a filename
    /// would have whichever downloaded first play for both.</summary>
    [Fact]
    public async Task Enqueue_CommunityAndCatalogSongsOfTheSameId_ImportToDifferentFiles()
    {
        var (provider, _) = await SignedInAsync([]);
        var directory = Path.Combine(_mediaDirectory, "karafun");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "6229.kit"), "the catalog's");
        await File.WriteAllTextAsync(Path.Combine(directory, "community-6229.kit"), "the community's");

        await EnqueueAction(provider).PerformAsync(SongEntity("6229"));
        await EnqueueAction(provider).PerformAsync(SongEntity("community:6229"));

        // Matched on the file name, not a suffix: "community-6229.kit" ends with "6229.kit" too,
        // so both import straight from their own file rather than one download standing in for both.
        await _library.Received(1).ImportAsync(Arg.Is<MediaImportRequest>(r => Path.GetFileName(r.FilePath) == "6229.kit"));
        await _library.Received(1).ImportAsync(Arg.Is<MediaImportRequest>(r => Path.GetFileName(r.FilePath) == "community-6229.kit"));
        await _library.DidNotReceive().BeginImportAsync(Arg.Any<MediaImportRequest>());
    }

    /// <summary>What session/info answers for a key the server still knows.</summary>
    private const string OkSessionInfo = """<response status="OK"><session><key>sk-1</key></session></response>""";

    /// <summary>Live session/info carries the socket's address as a kcs_url child of the session.</summary>
    private const string OkSessionInfoWithRemote =
        """<response status="OK"><session><key>sk-1</key><kcs_url>wss://kcs.example.com/ws?t=tok</kcs_url><images-url-schema png="https://img.example.test/i/api/{id}/{format}.png">https://img.example.test/i/api/{id}/{format}.jpg</images-url-schema></session></response>""";

    /// <summary>A stub answers whatever it is asked, so without reading the query back this passes
    /// on a call that sent no password at all.</summary>
    [Fact]
    public async Task Session_BeingRenewed_SendsTheStoredCredentialAndThePreviousKey()
    {
        var handler = new CountingHandler(new Dictionary<string, string>
        {
            ["session/open"] = OkSession,
            ["session/info"] = DeadSession,
        });
        ArrangeCredentialPrompt("ada@example.com", "hunter2");

        var before = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        await before.InvokeButtonAsync("session");

        var after = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        await after.SearchAsync("neon moon");

        var query = handler.Query("session/open");

        Assert.Contains($"pwd={PasswordService.Hash("hunter2")}", query);
        Assert.Contains("last_ses=sk-1", query);
    }

    /// <summary>An entry from before credentials were kept has nothing to renew with; opening a
    /// session with a blank password would be a guess against a real account, so it asks instead.</summary>
    [Fact]
    public async Task Session_StoredBeforeCredentialsWereKept_AsksRatherThanRenewingBlind()
    {
        await _cache.SaveAsync("karafun-session", new { Key = "sk-old", Login = "ada@example.com" });
        var handler = new CountingHandler(new Dictionary<string, string>
        {
            ["session/info"] = DeadSession,
        });
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));

        var row = SignInRow(await provider.SearchAsync("neon moon"));

        Assert.Equal("Sign in to KaraFun", row.Title);
        Assert.Equal(0, handler.Count("session/open"));
        Assert.Null(_secrets.GetValueOrDefault("session"));
    }

    /// <summary>A host who signed in before the secret store existed must not be asked again, but
    /// the plaintext file is the whole reason for the move, so it goes in the same breath.</summary>
    [Fact]
    public async Task Session_StoredInTheOldCache_MovesIntoTheSecretStoreAndLeavesNoFile()
    {
        await _cache.SaveAsync("karafun-session",
            new { Key = "sk-1", Login = "ada@example.com", PasswordHash = "hashed" });

        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(
            new FakeHandler(new Dictionary<string, string> { ["session/info"] = OkSessionInfo }))));

        // Reading the button is enough: restoring happens when the plugin is built.
        Assert.Equal("Sign out (ada@example.com)", provider.DescribeButton("session").Label);

        Assert.Contains("hashed", _secrets["session"]);
        Assert.Null(await _cache.LoadAsync<object>("karafun-session"));
    }

    /// <summary>The move happens once; afterwards the store is the only thing consulted.</summary>
    [Fact]
    public async Task Session_AlreadyInTheSecretStore_IsNotSoughtInTheCache()
    {
        _secrets["session"] = """{"Key":"sk-store","Login":"store@example.com","PasswordHash":"hashed"}""";
        await _cache.SaveAsync("karafun-session",
            new { Key = "sk-cache", Login = "cache@example.com", PasswordHash = "stale" });

        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(
            new FakeHandler(new Dictionary<string, string> { ["session/info"] = OkSessionInfo }))));

        Assert.Equal("Sign out (store@example.com)", provider.DescribeButton("session").Label);
    }

    private const string WrongPassword =
        """<response status="Error"><error action="fail">WRONG_PASSWORD</error></response>""";

    /// <summary>What session/info answers for a key gone bad: the restart action.</summary>
    private const string DeadSession = """<response status="Error"><error action="restart">INVALID_SESSION</error></response>""";

    [Fact]
    public void SessionButton_WhenSignedOut_ReadsSignIn()
        => Assert.Equal("Sign in", Provider().DescribeButton("session").Label);

    [Fact]
    public async Task SessionButton_ClickedWhileSignedOut_PromptsAndOpensASession()
    {
        var handler = new FakeHandler(new Dictionary<string, string> { ["session/open"] = OkSession });
        ArrangeCredentialPrompt("host@example.com", "hunter2");
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));

        await provider.InvokeButtonAsync("session");

        // The account is named on the button so a venue with two of them can see which is open.
        Assert.Equal("Sign out (ada@example.com)", provider.DescribeButton("session").Label);
    }

    /// <summary>The point of persisting: a host who signed in last night does not sign in again.
    /// A second provider over the same cache is what a restart looks like from here.</summary>
    [Fact]
    public async Task Session_SurvivesARestart_WithoutPromptingAgain()
    {
        var handler = new FakeHandler(new Dictionary<string, string>
        {
            ["session/open"] = OkSession,
            ["session/info"] = OkSessionInfo,
            ["search/top"] = """<response status="OK"><list><song id="6229" content="a" meta="b" /></list></response>""",
            ["song/info"] = """<response status="OK"><song id="6229" len="243"><title>Neon Moon</title><artist id="1">Brooks &amp; Dunn</artist></song></response>""",
        });
        ArrangeCredentialPrompt("ada@example.com", "hunter2");

        var before = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        await before.InvokeButtonAsync("session");

        _dispatcher.ClearReceivedCalls();

        var after = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        var results = await after.SearchAsync("neon moon");

        Assert.Equal("Neon Moon", Assert.Single(SongRows(results)).Title);
        Assert.Equal("Sign out (ada@example.com)", after.DescribeButton("session").Label);
        await _dispatcher.DidNotReceive().RequestAsync(Arg.Any<TextPromptRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A key can lapse between launches; checked once, on first use, so the host is
    /// asked to sign in again rather than being told the catalog is empty.</summary>
    [Fact]
    public async Task Session_RestoredButExpired_RenewsItselfWithoutAskingAnyone()
    {
        // A session key is not a credential and does not outlive the shift, so a dead one needs
        // another session/open, not a sign-out: nobody stands at a restarted console to type one.
        var after = await SignInThenRestartAsync(new Dictionary<string, string>
        {
            ["session/open"] = OkSession,
            ["session/info"] = DeadSession,
            ["search/top"] = CatalogSearch,
            ["song/info"] = CatalogInfo,
        });

        var results = await after.SearchAsync("neon moon");

        Assert.Equal("Neon Moon", Assert.Single(SongRows(results)).Title);
        Assert.Equal("Sign out (ada@example.com)", after.DescribeButton("session").Label);
        Assert.NotNull(_secrets.GetValueOrDefault("session"));
    }

    /// <summary>A Windows smoke test caught this exact double: two starts of the remote channel
    /// saw the same stale key rejected within the same launch, and each spent its own
    /// <c>session/open</c> renewing it. The second must find the lock already holding a fresh key
    /// and skip its own round trip rather than run one for nothing.</summary>
    [Fact]
    public async Task RenewSession_TwoConcurrentCallersOnTheSameStaleKey_RenewsOnlyOnce()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Signed in once, over a plain handler, to leave a stored session ("sk-1") the way a real
        // launch finds one left over from last night.
        ArrangeCredentialPrompt("ada@example.com", "hunter2");
        var before = Provider(new KaraFunApi(ClientProfile.MacOS,
            http: new HttpClient(new FakeHandler(new Dictionary<string, string> { ["session/open"] = OkSession }))));
        await before.InvokeButtonAsync("session");

        // A fresh provider over the same secret store, as a restart looks like from here — "sk-1"
        // is the stale key both concurrent callers below see rejected.
        var handler = new GatedOpenHandler(reached, release);
        var after = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));

        var first = after.RenewSessionAsync("sk-1", CancellationToken.None);
        await reached.Task;                                        // first holds the lock, mid session/open

        var second = after.RenewSessionAsync("sk-1", CancellationToken.None);

        // The lock alone should hold the second back; bounded so a regression fails in seconds
        // rather than by hanging.
        var secondFinishedEarly = await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(200))) == second;

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.False(secondFinishedEarly, "the second caller should wait on the lock, not run free of it");
        Assert.Equal(1, handler.Count("session/open"));
    }

    /// <summary>A credential the server refuses is a changed password, not a stale session, the
    /// one case that really does need a host, or a console quietly finds no songs with no reason.</summary>
    [Fact]
    public async Task Session_WhoseStoredCredentialIsRefused_ForgetsItAndOffersTheSignInRow()
    {
        var responses = new Dictionary<string, string>
        {
            ["session/open"] = OkSession,
            ["session/info"] = DeadSession,
        };
        var after = await SignInThenRestartAsync(responses);

        // The stored credential no longer opens anything: the account's password changed.
        responses["session/open"] = WrongPassword;

        var row = SignInRow(await after.SearchAsync("neon moon"));

        Assert.Equal("Sign in to KaraFun", row.Title);
        Assert.Equal("Sign in", after.DescribeButton("session").Label);
        Assert.Null(_secrets.GetValueOrDefault("session"));
        _plugin.Received(1).AddWarning(Arg.Is<string>(m => m.Contains("WRONG_PASSWORD")));
        _flash.Received(1).Show(
            Arg.Is<string>(m => m.StartsWith("KaraFun:") && m.Contains("Signed out")),
            FlashType.Warning);
    }

    /// <summary>The session is only ever validated once a launch, so a second search behind the
    /// first must not raise a second flash for the same lapsed sign-in.</summary>
    [Fact]
    public async Task Session_WhoseStoredCredentialStaysRefused_FlashesOnlyOnceAcrossSearches()
    {
        var responses = new Dictionary<string, string>
        {
            ["session/open"] = OkSession,
            ["session/info"] = DeadSession,
        };
        var after = await SignInThenRestartAsync(responses);
        responses["session/open"] = WrongPassword;

        SignInRow(await after.SearchAsync("neon moon"));
        SignInRow(await after.SearchAsync("neon moon"));

        _flash.Received(1).Show(Arg.Is<string>(m => m.Contains("Signed out")), FlashType.Warning);
    }

    /// <summary>The venue's wifi being down is not the server rejecting the session; dropping it
    /// here would put a sign-in dialog in front of a host mid-set and offline.</summary>
    [Fact]
    public async Task Session_RestoredButTheCheckFails_IsKept()
    {
        var after = await SignInThenRestartAsync(new Dictionary<string, string>
        {
            ["session/open"] = OkSession,
            ["search/top"] = """<response status="OK"><list /></response>""",
        }, sessionInfoThrows: true);

        await after.SearchAsync("neon moon");

        Assert.Equal("Sign out (ada@example.com)", after.DescribeButton("session").Label);
    }

    /// <summary>One round trip a launch: the gate runs on every load and cannot spend one each.</summary>
    [Fact]
    public async Task Session_Restored_IsCheckedOncePerLaunch()
    {
        var handler = new CountingHandler(new Dictionary<string, string>
        {
            ["session/open"] = OkSession,
            ["session/info"] = OkSessionInfo,
            ["search/top"] = """<response status="OK"><list /></response>""",
        });
        ArrangeCredentialPrompt("ada@example.com", "hunter2");

        var before = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        await before.InvokeButtonAsync("session");

        var after = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        await after.SearchAsync("one");
        await after.SearchAsync("two");
        await after.CanAsync(MediaAction.Play, new Media { FilePath = "/x.kit", Title = "x" });

        Assert.Equal(1, handler.Count("session/info"));
    }

    /// <summary>Signing out has to reach the stored copy, or the next launch signs itself back in.</summary>
    [Fact]
    public async Task SignOut_ForgetsTheStoredSession()
    {
        var after = await SignInThenRestartAsync(new Dictionary<string, string>
        {
            ["session/open"] = OkSession,
            ["session/info"] = OkSessionInfo,
        });

        await after.InvokeButtonAsync("session");

        Assert.Equal("Sign in", after.DescribeButton("session").Label);
        Assert.Null(_secrets.GetValueOrDefault("session"));
    }

    /// <summary>Signs in on one provider and hands back a second over the same cache.</summary>
    private async Task<KaraFunMediaProvider> SignInThenRestartAsync(
        Dictionary<string, string> responses, bool sessionInfoThrows = false, FakeKcsSocket? socket = null)
    {
        ArrangeCredentialPrompt("ada@example.com", "hunter2");

        var before = Provider(new KaraFunApi(ClientProfile.MacOS,
            http: new HttpClient(new FakeHandler(responses))));
        await before.InvokeButtonAsync("session");

        _dispatcher.ClearReceivedCalls();

        var api = new KaraFunApi(ClientProfile.MacOS,
            http: new HttpClient(new FakeHandler(responses, throwOn: sessionInfoThrows ? "session/info" : null)));

        return socket is null ? Provider(api) : Provider(api, socket);
    }

    /// <summary>The button and the search share one session: signing in through the button is
    /// what the search-result sign-in row's absence afterwards depends on.</summary>
    [Fact]
    public async Task SessionButton_SignedIn_ThenSearchReturnsSongs()
    {
        var handler = new FakeHandler(new Dictionary<string, string>
        {
            ["session/open"] = OkSession,
            ["search/top"] = """<response status="OK"><list><song id="6229" content="a" meta="b" /></list></response>""",
            ["song/info"] = """<response status="OK"><song id="6229" len="243"><title>Neon Moon</title><artist id="1">Brooks &amp; Dunn</artist></song></response>""",
        });
        ArrangeCredentialPrompt("host@example.com", "hunter2");
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));

        await provider.InvokeButtonAsync("session");
        var results = await provider.SearchAsync("neon moon");

        Assert.Equal("Neon Moon", Assert.Single(SongRows(results)).Title);
    }

    [Fact]
    public async Task SessionButton_ClickedWhileSignedIn_SignsOut()
    {
        var handler = new FakeHandler(new Dictionary<string, string> { ["session/open"] = OkSession });
        ArrangeCredentialPrompt("host@example.com", "hunter2");
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        await provider.InvokeButtonAsync("session");

        await provider.InvokeButtonAsync("session");

        Assert.Equal("Sign in", provider.DescribeButton("session").Label);
        // Signed out again, search offers the sign-in row rather than results.
        SignInRow(await provider.SearchAsync("neon moon"));
    }

    /// <summary>Signing out asks nothing, just drops the in-memory key.</summary>
    [Fact]
    public async Task SessionButton_SignOut_DoesNotPrompt()
    {
        var handler = new FakeHandler(new Dictionary<string, string> { ["session/open"] = OkSession });
        ArrangeCredentialPrompt("host@example.com", "hunter2");
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        await provider.InvokeButtonAsync("session");
        _dispatcher.ClearReceivedCalls();

        await provider.InvokeButtonAsync("session");

        await _dispatcher.DidNotReceive().RequestAsync(Arg.Any<TextPromptRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void DescribeButton_UnknownKey_IsDefault()
        => Assert.Equal(PluginButtonState.Default, Provider().DescribeButton("nope"));

    [Fact]
    public void ProviderId_IsThisPluginsAssemblyName()
        => Assert.Equal("KHost.Plugins.KaraFun", Provider().ProviderId);

    [Theory]
    [InlineData(MediaAction.Queue)]
    [InlineData(MediaAction.Play)]
    public async Task CanAsync_WhenSignedOutAndTheHostCancelsTheSignIn_IsBlockedWithAReason(MediaAction action)
    {
        ArrangeCredentialPromptCancelled();

        var result = await Provider().CanAsync(action, new Media { FilePath = "/x.kit", Title = "x" });

        Assert.False(result.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    /// <summary>A render runs behind the host with nobody watching a dialog, so it is refused
    /// outright: a prompt nobody is standing at would hold the render until it timed out.</summary>
    [Fact]
    public async Task CanAsync_RenderWhenSignedOut_IsRefusedWithoutAsking()
    {
        ArrangeCredentialPrompt("host@example.com", "hunter2");

        var result = await Provider().CanAsync(MediaAction.Render, new Media { FilePath = "/x.kit", Title = "x" });

        Assert.False(result.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        await _dispatcher.DidNotReceive().RequestAsync(Arg.Any<TextPromptRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The reason names the moment, so a host blocked at the queue is not told the song
    /// will not play.</summary>
    [Theory]
    [InlineData(MediaAction.Queue, "queue")]
    [InlineData(MediaAction.Play, "play")]
    public async Task CanAsync_WhenTheSignInIsCancelled_TheReasonNamesTheAction(MediaAction action, string verb)
    {
        ArrangeCredentialPromptCancelled();

        var result = await Provider().CanAsync(action, new Media { FilePath = "/x.kit", Title = "x" });

        Assert.Contains(verb, result.Reason);
    }

    /// <summary>The track is already on disk, so a bare refusal reads as a broken file; the
    /// blocked load asks for the sign-in it is waiting on and carries on once it has one.</summary>
    [Theory]
    [InlineData(MediaAction.Queue)]
    [InlineData(MediaAction.Play)]
    public async Task CanAsync_WhenSignedOut_AsksForTheSignInAndAllowsItAfter(MediaAction action)
    {
        var handler = new FakeHandler(new Dictionary<string, string> { ["session/open"] = OkSession });
        ArrangeCredentialPrompt("host@example.com", "hunter2");
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));

        var result = await provider.CanAsync(action, new Media { FilePath = "/x.kit", Title = "x" });

        Assert.True(result.Allowed);
        await _dispatcher.Received(1).RequestAsync(Arg.Any<TextPromptRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Two loads blocked at once, a double-clicked play or an auto-advance behind one,
    /// must not stack two dialogs on the host mid-show.</summary>
    [Fact]
    public async Task CanAsync_TwoBlockedLoadsAtOnce_AsksOnlyOnce()
    {
        var handler = new FakeHandler(new Dictionary<string, string> { ["session/open"] = OkSession });
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));

        // The dialog has to still be open when the second load arrives, or every substitute here
        // answers synchronously and the test passes with no guard at all.
        var opened = new TaskCompletionSource();
        var answered = new TaskCompletionSource<IReadOnlyDictionary<string, string>?>();
        _dispatcher.RequestAsync(Arg.Any<TextPromptRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                opened.TrySetResult();
                return answered.Task;
            });

        var media = new Media { FilePath = "/x.kit", Title = "x" };

        var first = provider.CanAsync(MediaAction.Play, media);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = provider.CanAsync(MediaAction.Play, media);
        answered.SetResult(new Dictionary<string, string> { ["login"] = "host@example.com", ["password"] = "hunter2" });

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, result => Assert.True(result.Allowed));
        await _dispatcher.Received(1).RequestAsync(Arg.Any<TextPromptRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A live account is the whole rule: once there is one, every moment is allowed.</summary>
    [Theory]
    [InlineData(MediaAction.Queue)]
    [InlineData(MediaAction.Render)]
    [InlineData(MediaAction.Play)]
    public async Task CanAsync_WhenSignedIn_IsAllowed(MediaAction action)
    {
        var handler = new FakeHandler(new Dictionary<string, string> { ["session/open"] = OkSession });
        ArrangeCredentialPrompt("host@example.com", "hunter2");
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        await provider.InvokeButtonAsync("session");

        var result = await provider.CanAsync(action, new Media { FilePath = "/x.kit", Title = "x" });

        Assert.True(result.Allowed);
    }

    /// <summary>A kit carries no readable tag, so ownership is decided by the name.</summary>
    [Theory]
    [InlineData("/songs/x.kit")]
    [InlineData("/songs/x.KIT")]
    public void Claims_AnswersForAKit(string path) => Assert.True(Provider().Claims(path));

    /// <summary>Anything else on disk is somebody else's file and must play ungated.</summary>
    [Theory]
    [MemberData(nameof(KitClaims.NotAKit), MemberType = typeof(KitClaims))]
    public void Claims_NothingButAKit(string path) => Assert.False(Provider().Claims(path));

    private static MediaSearchEntity SongEntity(string foreignKey) => new()
    {
        SourceDisplayName = "KaraFun",
        Source = "KaraFun",
        ForeignKey = foreignKey,
        Title = "Song " + foreignKey,
    };

    private MediaProviderAction EnqueueAction(KaraFunMediaProvider provider)
        => provider.Actions.Single(action => action.DisplayName == "Enqueue");

    /// <summary>A file already downloaded is reused: no second download or import of the same song.</summary>
    [Fact]
    public async Task Enqueue_FileAlreadyOnDisk_ReusesItWithoutDownloading()
    {
        var handler = new FakeHandler(new Dictionary<string, string> { ["session/open"] = OkSession });
        ArrangeCredentialPrompt("host@example.com", "hunter2");
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        await provider.InvokeButtonAsync("session");

        var directory = Path.Combine(_mediaDirectory, "karafun");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "6229.kit"), "already downloaded");

        await EnqueueAction(provider).PerformAsync(SongEntity("6229"));

        await _library.Received(1).ImportAsync(Arg.Is<MediaImportRequest>(r => r.FilePath.EndsWith("6229.kit")));
        await _library.DidNotReceive().BeginImportAsync(Arg.Any<MediaImportRequest>());
    }

    /// <summary>A stale search result clicked after the account signed out elsewhere is the host
    /// pressing Enqueue and being refused, the textbook flash case.</summary>
    [Fact]
    public async Task Enqueue_AfterSigningOutElsewhere_FlashesRatherThanSilentlyDoingNothing()
    {
        var provider = Provider();

        await EnqueueAction(provider).PerformAsync(SongEntity("6229"));

        await _library.DidNotReceive().ImportAsync(Arg.Any<MediaImportRequest>());
        await _library.DidNotReceive().BeginImportAsync(Arg.Any<MediaImportRequest>());
        _flash.Received(1).Show(
            Arg.Is<string>(m => m.StartsWith("KaraFun:") && m.Contains("Signed out")), FlashType.Warning);
    }

    /// <summary>No singer selected is a console state, not anything KaraFun did or the host asked
    /// of it, so it stays on the log and must not also pop a flash.</summary>
    [Fact]
    public async Task Enqueue_WithNoSingerSelected_DoesNotFlash()
    {
        // Already on disk, so this never reaches the download: isolates the no-singer path from
        // the download-failure flashes, which are a different cause entirely.
        var handler = new FakeHandler(new Dictionary<string, string> { ["session/open"] = OkSession });
        ArrangeCredentialPrompt("host@example.com", "hunter2");
        _queue.SelectedUserId.Returns((Guid?)null);
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        await provider.InvokeButtonAsync("session");
        var directory = Path.Combine(_mediaDirectory, "karafun");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "6229.kit"), "already downloaded");

        await EnqueueAction(provider).PerformAsync(SongEntity("6229"));

        Assert.Empty(EnqueuedPerformances());
        _flash.DidNotReceive().Show(Arg.Any<string>(), Arg.Any<FlashType>());
    }

    /// <summary>The download is the library row: the kit exactly as KaraFun served it.</summary>
    [Fact]
    public async Task Enqueue_ANewDownload_BeginsTheImportAgainstTheKitItself()
    {
        var handler = new FakeHandler(new Dictionary<string, string> { ["session/open"] = OkSession });
        ArrangeCredentialPrompt("host@example.com", "hunter2");
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = Guid.NewGuid() });
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        await provider.InvokeButtonAsync("session");

        await EnqueueAction(provider).PerformAsync(SongEntity("6229"));

        await _library.Received(1).BeginImportAsync(Arg.Is<MediaImportRequest>(r =>
            r.FilePath.EndsWith("6229.kit", StringComparison.Ordinal)));
    }

    /// <summary>A double-click before the download finishes must not start a second: it is a
    /// multi-minute download. The second is released once the first has begun.</summary>
    [Fact]
    public async Task Enqueue_ClickedTwiceWhileDownloading_StartsOnlyOneDownload()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GatedHandler(reached, release);
        ArrangeCredentialPrompt("host@example.com", "hunter2");
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = Guid.NewGuid() });
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler)));
        await provider.InvokeButtonAsync("session");

        var first = EnqueueAction(provider).PerformAsync(SongEntity("6229"));
        await reached.Task;                                              // first is in-flight, past BeginImport
        var second = EnqueueAction(provider).PerformAsync(SongEntity("6229"));

        // The guard makes the second return at once; without it, it would run to its own download
        // and block on the same gate. Bounded so a regression fails in seconds, not on a hang.
        var secondReturned = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5))) == second;

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.True(secondReturned, "the second click should return immediately, not start another download");
        await _library.Received(1).BeginImportAsync(Arg.Any<MediaImportRequest>());

        // The finally removed the song from the in-flight set, so it can be enqueued again once
        // settled; a dropped removal would block it forever.
        await EnqueueAction(provider).PerformAsync(SongEntity("6229"));
        await _library.Received(2).BeginImportAsync(Arg.Any<MediaImportRequest>());
    }

    /// <summary>A second guest picking a song still arriving for the first was dropped without a word.
    /// It now goes on the queue against the row the running download fills.</summary>
    [Fact]
    public async Task Enqueue_SecondSingerWhileDownloading_QueuesAgainstTheSameRow()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ArrangeCredentialPrompt("host@example.com", "hunter2");
        var mediaId = Guid.NewGuid();
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = mediaId });
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(new GatedHandler(reached, release))));
        await provider.InvokeButtonAsync("session");
        var ada = Guid.NewGuid();
        var bob = Guid.NewGuid();

        _queue.SelectedUserId.Returns(ada);
        var first = EnqueueAction(provider).PerformAsync(SongEntity("6229"));
        await reached.Task;
        _queue.SelectedUserId.Returns(bob);
        await EnqueueAction(provider).PerformAsync(SongEntity("6229"));
        release.SetResult();
        await first;

        await _performances.Received(1).TryCreateAndEnqueueAsync(Arg.Is<Performance>(p => p.SingerId == bob && p.MediaId == mediaId), EnqueueOrigin.Host);
        await _library.Received(1).BeginImportAsync(Arg.Any<MediaImportRequest>());
    }

    /// <summary>A pick arriving before the first has its row waits for that row rather than giving up.</summary>
    [Fact]
    public async Task Enqueue_SecondSingerBeforeTheFirstHasItsRow_WaitsForTheRow()
    {
        ArrangeCredentialPrompt("host@example.com", "hunter2");
        var mediaId = Guid.NewGuid();
        var importBegun = new TaskCompletionSource<ImportTicket>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(importBegun.Task);
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(new FakeHandler(new Dictionary<string, string> { ["session/open"] = OkSession }))));
        await provider.InvokeButtonAsync("session");
        var bob = Guid.NewGuid();

        _queue.SelectedUserId.Returns(Guid.NewGuid());
        var first = EnqueueAction(provider).PerformAsync(SongEntity("6229"));
        await WaitForAsync(() => _library.ReceivedCalls().Any(), "the first import to begin");
        _queue.SelectedUserId.Returns(bob);
        var second = EnqueueAction(provider).PerformAsync(SongEntity("6229"));
        importBegun.SetResult(new ImportTicket { MediaId = mediaId });
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        await _performances.Received(1).TryCreateAndEnqueueAsync(Arg.Is<Performance>(p => p.SingerId == bob && p.MediaId == mediaId), EnqueueOrigin.Host);
    }

    /// <summary>The first pick reports its own failure; the one waiting on it must neither hang nor
    /// queue a row that was never made.</summary>
    [Fact]
    public async Task Enqueue_SecondSingerWhenTheFirstImportFails_QueuesNothingAndDoesNotHang()
    {
        ArrangeCredentialPrompt("host@example.com", "hunter2");
        var importBegun = new TaskCompletionSource<ImportTicket>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(importBegun.Task);
        var provider = Provider(new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(new FakeHandler(new Dictionary<string, string> { ["session/open"] = OkSession }))));
        await provider.InvokeButtonAsync("session");

        _queue.SelectedUserId.Returns(Guid.NewGuid());
        var first = EnqueueAction(provider).PerformAsync(SongEntity("6229"));
        await WaitForAsync(() => _library.ReceivedCalls().Any(), "the first import to begin");
        var second = EnqueueAction(provider).PerformAsync(SongEntity("6229"));
        importBegun.SetException(new InvalidOperationException("library full"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(EnqueuedPerformances());
    }

    /// <summary>Blocks song/request until released, then answers with no stream so the download
    /// settles without a CDN fetch.</summary>
    private sealed class GatedHandler(TaskCompletionSource reached, TaskCompletionSource release) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var endpoint = request.RequestUri!.AbsolutePath.Trim('/');

            if (endpoint == "song/request")
            {
                reached.TrySetResult();
                await release.Task;
                return Ok("""<response status="OK"><song></song></response>""");
            }

            return Ok(endpoint == "session/open" ? OkSession
                : """<response status="Error"><error action="fail">NOT_STUBBED</error></response>""");
        }

        private static HttpResponseMessage Ok(string body)
            => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    /// <summary>Blocks every session/open until released, counting how many were made and always
    /// answering with a different key than whatever was stale — a caller that skips the round trip
    /// on a fresh key rather than a stale one proves the guard ran, not just that the count matched
    /// by coincidence.</summary>
    private sealed class GatedOpenHandler(TaskCompletionSource reached, TaskCompletionSource release) : HttpMessageHandler
    {
        private int _sessionOpenCount;

        public int Count(string endpoint) => endpoint == "session/open" ? Volatile.Read(ref _sessionOpenCount) : 0;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var endpoint = request.RequestUri!.AbsolutePath.Trim('/');

            if (endpoint == "session/open")
            {
                Interlocked.Increment(ref _sessionOpenCount);
                reached.TrySetResult();
                await release.Task;

                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """<response status="OK"><session><key>sk-2</key><user_login>ada@example.com</user_login></session></response>"""),
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""<response status="Error"><error action="fail">NOT_STUBBED</error></response>"""),
            };
        }
    }

    /// <summary>Holds every request open until released, standing in for a slow karaoke server.</summary>
    private sealed class BlockingHandler(Task released) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await released;

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""<response status="OK"><session><key>sk-1</key></session></response>"""),
            };
        }
    }

    /// <summary>A FakeHandler that also says how often each endpoint was asked for.</summary>
    private sealed class CountingHandler(IReadOnlyDictionary<string, string> responsesByEndpoint) : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _counts = [];
        private readonly Dictionary<string, string> _queries = [];
        private readonly Dictionary<string, string> _methods = [];

        public int Count(string endpoint) => _counts.TryGetValue(endpoint, out var count) ? count : 0;

        /// <summary>Which verb an endpoint was called with: the API is not uniform about it.</summary>
        public string Method(string endpoint) => _methods.GetValueOrDefault(endpoint, "");

        /// <summary>Stubs answer whatever they are asked, so without this a test cannot tell a call
        /// that carried the right ids from one that carried none.</summary>
        public string Query(string endpoint) => _queries.GetValueOrDefault(endpoint, "");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var endpoint = request.RequestUri!.AbsolutePath.Trim('/');
            _counts[endpoint] = Count(endpoint) + 1;
            _queries[endpoint] = request.RequestUri!.Query;
            _methods[endpoint] = request.Method.Method;

            var body = responsesByEndpoint.TryGetValue(endpoint, out var xml)
                ? xml
                : """<response status="Error"><error action="fail">NOT_STUBBED</error></response>""";

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }

    /// <summary>Held by the fixture rather than the provider, so a test can build a second
    /// provider over it, which is what a restart looks like from here.</summary>
    private sealed class InMemoryCache : ICacheService
    {
        private readonly Dictionary<string, string?> _entries = [];

        public int Saves { get; private set; }

        public Task<T?> LoadAsync<T>(string key)
            => Task.FromResult(_entries.TryGetValue(key, out var json) && json is not null
                ? System.Text.Json.JsonSerializer.Deserialize<T>(json, System.Text.Json.JsonSerializerOptions.Web)
                : default);

        public Task SaveAsync<T>(string key, T state)
        {
            Saves++;
            _entries[key] = state is null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(state, System.Text.Json.JsonSerializerOptions.Web);
            return Task.CompletedTask;
        }
    }

    /// <summary>Holds one endpoint's answer until the test opens the gate, so a test can decide
    /// what lands first.</summary>
    private sealed class HeldEndpointHandler(
        IReadOnlyDictionary<string, string> responsesByEndpoint, string gatedEndpoint, Task gate) : HttpMessageHandler
    {
        private readonly FakeHandler _inner = new(responsesByEndpoint);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Trim('/') == gatedEndpoint)
                await gate.WaitAsync(cancellationToken);

            return await new HttpMessageInvoker(_inner, disposeHandler: false).SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Routes each call by the path segment after the host, ignoring the query string.</summary>
    private sealed class FakeHandler(
        IReadOnlyDictionary<string, string> responsesByEndpoint, string? throwOn = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var endpoint = request.RequestUri!.AbsolutePath.Trim('/');

            // Stands in for the room's wifi being down, which is not the server saying anything.
            if (throwOn is not null && endpoint == throwOn)
                throw new HttpRequestException("No route to host");

            var body = responsesByEndpoint.TryGetValue(endpoint, out var xml)
                ? xml
                : """<response status="Error"><error action="fail">NOT_STUBBED</error></response>""";

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }
    /// <summary>The cover needs the template and the template needs the id, and the two arrive
    /// from different calls: the session read and song/info.</summary>
    [Fact]
    public async Task ASearchResult_CarriesTheCoverBuiltFromTheSessionsOwnTemplate()
    {
        var provider = await SessionWithATemplateAsync(new()
        {
            ["search/top"] = CatalogSearch,
            ["song/info"] = """<response status="OK"><song id="6229" len="243"><title>Neon Moon</title><artist id="1">Brooks &amp; Dunn</artist><image id="0004d488abdf3b" /></song></response>""",
        });

        var song = Assert.Single(SongRows(await provider.SearchAsync("neon moon")));

        Assert.Equal("https://img.example.test/i/api/0004d488abdf3b/sq200.jpg",
            song.Fields[MediaResultColumn.ThumbnailKey]);
    }

    /// <summary>Parsed by the same reader, so a community upload with artwork gets a picture on
    /// the same terms a catalog song does.</summary>
    [Fact]
    public async Task ACommunityResult_CarriesItsCoverTheSameWay()
    {
        var provider = await SessionWithATemplateAsync(new()
        {
            ["search/top"] = CatalogSearch,
            ["song/info"] = CatalogInfo,
            ["search/community"] = CommunitySearch,
            ["community/info"] = """<response status="OK"><community id="77456" len="180"><title>Home Sweet Home</title><artist id="9">Mötley Crüe</artist><image id="0123abc456def7" /></community></response>""",
        });
        var offer = CommunityOffer(await provider.SearchAsync("home sweet home"));
        await Assert.Single(offer.SupportedActions).PerformAsync(offer);

        var song = Assert.Single(await provider.SearchAsync("home sweet home"));

        Assert.Equal("https://img.example.test/i/api/0123abc456def7/sq200.jpg",
            song.Fields[MediaResultColumn.ThumbnailKey]);
    }

    [Fact]
    public async Task ASongWithNoArtwork_GetsNoThumbnailRatherThanABrokenOne()
    {
        var provider = await SessionWithATemplateAsync(new()
        {
            ["search/top"] = CatalogSearch,
            ["song/info"] = CatalogInfo,
        });

        var song = Assert.Single(SongRows(await provider.SearchAsync("neon moon")));

        Assert.False(song.Fields.ContainsKey(MediaResultColumn.ThumbnailKey));
    }

    [Fact]
    public async Task ASessionThatCarriedNoTemplate_LeavesEveryRowWithoutAPicture()
    {
        // A session read with no images-url-schema on it: nothing can be built from an id alone.
        var responses = new Dictionary<string, string>
        {
            ["session/open"] = OkSession,
            ["session/info"] = """<response status="OK"><session><key>sk-store</key></session></response>""",
            ["search/top"] = CatalogSearch,
            ["song/info"] = """<response status="OK"><song id="6229" len="243"><title>Neon Moon</title><artist id="1">Brooks &amp; Dunn</artist><image id="0004d488abdf3b" /></song></response>""",
        };
        var provider = await SignInThenRestartAsync(responses);

        var song = Assert.Single(SongRows(await provider.SearchAsync("neon moon")));

        Assert.False(song.Fields.ContainsKey(MediaResultColumn.ThumbnailKey));
    }

    // ---- offline ---------------------------------------------------------

    private const string Unreachable = "Could not reach KaraFun. Check this computer is online, then try again.";

    private static readonly Dictionary<string, string> OnlineResponses = new()
    {
        ["session/open"] = OkSession,
        ["search/top"] = CatalogSearch,
        ["song/info"] = CatalogInfo,
        ["session/info"] = OkSessionInfoWithRemote,
        ["session/remote"] = OkRemote,
    };

    private async Task<KaraFunMediaProvider> SignedInOverAsync(ToggleHandler handler, FakeKcsSocket? socket = null)
    {
        ArrangeCredentialPrompt("ada@example.com", "hunter2");
        var api = new KaraFunApi(ClientProfile.MacOS, http: new HttpClient(handler));
        var provider = socket is null ? Provider(api) : Provider(api, socket);
        provider.ScreenCodeRetryDelay = TimeSpan.FromMilliseconds(20);
        await provider.InvokeButtonAsync("session");

        return provider;
    }

    private bool WarnedWith(string text)
        => _plugin.ReceivedCalls().Any(call => call.GetArguments() is [string message] && message.Contains(text));

    [Fact]
    public async Task Search_Offline_FlashesOneLineNamingKaraFunAndReturnsNoRows()
    {
        var handler = new ToggleHandler(OnlineResponses);
        var provider = await SignedInOverAsync(handler);
        handler.Fail("search/top");

        Assert.Empty(await provider.SearchAsync("neon moon"));

        _flash.Received(1).Show(Unreachable, FlashType.Warning);
    }

    /// <summary>The console searches as the host types, so a flash per search would bury the screen.</summary>
    [Fact]
    public async Task Search_OfflineRepeatedly_FlashesOnlyOnce()
    {
        var handler = new ToggleHandler(OnlineResponses);
        var provider = await SignedInOverAsync(handler);
        handler.Fail("search/top");

        await provider.SearchAsync("neon");
        await provider.SearchAsync("neon m");
        await provider.SearchAsync("neon mo");

        _flash.Received(1).Show(Arg.Any<string>(), FlashType.Warning);
    }

    [Fact]
    public async Task Search_FailingAgainAfterASuccess_FlashesAgain()
    {
        var handler = new ToggleHandler(OnlineResponses);
        var provider = await SignedInOverAsync(handler);

        handler.Fail("search/top");
        await provider.SearchAsync("neon");
        handler.Heal("search/top");
        Assert.NotEmpty(await provider.SearchAsync("neon"));
        handler.Fail("search/top");
        await provider.SearchAsync("neon");

        _flash.Received(2).Show(Unreachable, FlashType.Warning);
    }

    [Fact]
    public async Task Search_FailingForAnotherReason_SaysWhy()
    {
        var handler = new ToggleHandler(OnlineResponses);
        var provider = await SignedInOverAsync(handler);
        handler.Fail("search/top", () => new InvalidOperationException("kaput"));

        Assert.Empty(await provider.SearchAsync("neon"));

        _flash.Received(1).Show("KaraFun search failed: kaput", FlashType.Warning);
    }

    [Fact]
    public async Task CommunitySearch_Offline_IsCaughtToo()
    {
        var handler = new ToggleHandler(OnlineResponses);
        var provider = await SignedInOverAsync(handler);
        var offer = CommunityOffer(await provider.SearchAsync("neon"));
        handler.Fail("search/community");

        await Assert.Single(offer.SupportedActions).PerformAsync(offer);

        Assert.Empty(await provider.SearchAsync("neon"));
        _flash.Received(1).Show(Unreachable, FlashType.Warning);
    }

    [Fact]
    public async Task SignIn_Offline_SaysItPlainlyAndKeepsTheResolverTextOut()
    {
        var handler = new ToggleHandler(OnlineResponses);
        handler.Fail("session/open", () => new HttpRequestException("nodename nor servname provided, or not known"));

        await SignedInOverAsync(handler);

        _flash.Received(1).Show(Unreachable, FlashType.Warning);
        _plugin.Received(1).AddWarning(Unreachable);
    }

    [Fact]
    public async Task SignIn_RefusedForAnotherReason_FlashesNamingKaraFun()
    {
        var handler = new ToggleHandler(new Dictionary<string, string>
        {
            ["session/open"] = """<response status="Error"><error action="fail">WRONG_PASSWORD</error></response>""",
        });

        await SignedInOverAsync(handler);

        _flash.Received(1).Show("KaraFun sign-in failed: WRONG_PASSWORD", FlashType.Warning);
    }

    private (Guid MediaId, List<string?> Reasons) ArrangeDownload()
    {
        var mediaId = Guid.NewGuid();
        _library.BeginImportAsync(Arg.Any<MediaImportRequest>()).Returns(new ImportTicket { MediaId = mediaId });

        var reasons = new List<string?>();
        _library.When(library => library.FailImportAsync(mediaId, Arg.Any<string?>()))
            .Do(call => { lock (reasons) reasons.Add(call.ArgAt<string?>(1)); });

        return (mediaId, reasons);
    }

    private static async Task EnqueueFirstRowAsync(KaraFunMediaProvider provider)
    {
        var row = (await provider.SearchAsync("neon moon"))[0];
        await Assert.Single(row.SupportedActions).PerformAsync(row);
    }

    [Fact]
    public async Task ADownloadOffline_FlashesPlainlyAndKeepsTheRawTextOnTheRow()
    {
        var handler = new ToggleHandler(OnlineResponses);
        var provider = await SignedInOverAsync(handler);
        var (mediaId, reasons) = ArrangeDownload();
        handler.Fail("song/request", () => new HttpRequestException("nodename nor servname provided, or not known"));

        await EnqueueFirstRowAsync(provider);

        _flash.Received(1).Show(Unreachable, FlashType.Warning);
        Assert.Equal("nodename nor servname provided, or not known", Assert.Single(reasons));
        await _library.DidNotReceive().DiscardImportAsync(mediaId);
    }

    /// <summary>HttpClient reports its own timeout as a cancel nobody asked for.</summary>
    [Fact]
    public async Task ADownloadThatTimesOut_FailsTheImportRatherThanDiscardingItAsACancel()
    {
        var handler = new ToggleHandler(OnlineResponses);
        var provider = await SignedInOverAsync(handler);
        var (mediaId, reasons) = ArrangeDownload();
        handler.Fail("song/request", () => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        await EnqueueFirstRowAsync(provider);

        _flash.Received(1).Show(Unreachable, FlashType.Warning);
        Assert.Single(reasons);
        await _library.DidNotReceive().DiscardImportAsync(mediaId);
    }

    [Fact]
    public async Task ADownloadThatFailsForAnotherReason_NamesTheSongAndWhy()
    {
        var handler = new ToggleHandler(OnlineResponses);
        var provider = await SignedInOverAsync(handler);
        ArrangeDownload();
        handler.Fail("song/request", () => new InvalidOperationException("kaput"));

        await EnqueueFirstRowAsync(provider);

        _flash.Received(1).Show("KaraFun download of 'Neon Moon' failed: kaput", FlashType.Warning);
    }

    [Fact]
    public async Task ARoomThatCannotBeReached_WarnsAndTheNextAuthenticateTakesItBack()
    {
        var handler = new ToggleHandler(OnlineResponses);
        _plugin.AddWarning(Arg.Any<string>()).Returns(7);
        var socket = new FakeKcsSocket();
        handler.Fail("session/remote");
        await SignedInOverAsync(handler, socket);
        await WaitForConnectAsync(socket);

        var authenticated = new Message
        {
            Event = new Message.Types.Event { Socket = new SocketEvent { AuthenticatedEvent = new ModelsEmpty() } },
        };
        socket.Deliver(authenticated);
        await WaitForAsync(() => WarnedWith("guest room is closed"), "the room warning");
        _plugin.DidNotReceive().ClearWarning(Arg.Any<int>());

        // The mint still fails, so only the authenticate itself can be what took the warning back.
        socket.Deliver(authenticated);
        await WaitForAsync(() => _plugin.ReceivedCalls().Any(call => call.GetMethodInfo().Name == "ClearWarning"), "the warning being cleared");

        _plugin.Received(1).ClearWarning(7);
    }

    [Fact]
    public async Task ARoomOpenedAtStartup_OffersNoRoomWarning()
    {
        await RoomLiveAsync(new Dictionary<string, string> { ["session/remote"] = OkRemote });
        await WaitForCodesShownAsync(1);

        Assert.False(WarnedWith("guest room is closed"));
    }

    [Fact]
    public async Task ASessionLookupThatCannotBeReached_WarnsThatTheRoomIsClosed()
    {
        var handler = new ToggleHandler(OnlineResponses);
        handler.Fail("session/info");

        await SignedInOverAsync(handler, new FakeKcsSocket());

        await WaitForAsync(() => WarnedWith("guest room is closed"), "the room warning");
    }

    /// <summary>The wording says the connection is down; a refusal is not that.</summary>
    [Fact]
    public async Task ASessionLookupThatFailsForAnotherReason_DoesNotClaimTheNetworkIsDown()
    {
        var handler = new ToggleHandler(OnlineResponses);
        handler.Fail("session/info", () => new InvalidOperationException("kaput"));

        await SignedInOverAsync(handler, new FakeKcsSocket());
        await Task.Delay(100);

        Assert.False(WarnedWith("guest room is closed"));
    }

    [Fact]
    public async Task AFailedQrMint_WarnsAndRetriesWithoutWaitingForAReconnect()
    {
        var handler = new ToggleHandler(OnlineResponses);
        _plugin.AddWarning(Arg.Any<string>()).Returns(7);
        handler.Fail("session/remote");
        var socket = new FakeKcsSocket { AuthenticatesOnConnect = true };
        await SignedInOverAsync(handler, socket);
        await WaitForAsync(() => WarnedWith("guest room is closed"), "the room warning");
        Assert.Equal(0, Volatile.Read(ref _codesShown));

        handler.Heal("session/remote");

        await WaitForCodesShownAsync(1);
        _plugin.Received(1).ClearWarning(7);
    }

    [Fact]
    public async Task AFailedQrMint_ForAnotherReason_WarnsWithTheReason()
    {
        var handler = new ToggleHandler(OnlineResponses);
        handler.Fail("session/remote", () => new InvalidOperationException("kaput"));

        await SignedInOverAsync(handler, new FakeKcsSocket { AuthenticatesOnConnect = true });

        await WaitForAsync(() => WarnedWith("kaput"), "the warning naming the reason");
        Assert.False(WarnedWith("guest room is closed"));
    }

    [Fact]
    public async Task AGuestPickWhoseLookupCannotBeReached_FlashesInsteadOfVanishing()
    {
        var handler = new ToggleHandler(OnlineResponses);
        var socket = new FakeKcsSocket();
        await SignedInOverAsync(handler, socket);
        await WaitForConnectAsync(socket);
        handler.Fail("song/info");

        socket.Deliver(RemotePick(6229, "Ada"));

        await WaitForCallAsync(() => _flash.ReceivedCalls().Any());
        _flash.Received(1).Show($"{Unreachable} The pick from Ada was not queued.", FlashType.Warning);
        await _library.DidNotReceive().BeginImportAsync(Arg.Any<MediaImportRequest>());
    }

    [Fact]
    public async Task AGuestPickWhoseLookupFailsForAnotherReason_SaysWhy()
    {
        var handler = new ToggleHandler(OnlineResponses);
        var socket = new FakeKcsSocket();
        await SignedInOverAsync(handler, socket);
        await WaitForConnectAsync(socket);
        handler.Fail("song/info", () => new InvalidOperationException("kaput"));

        socket.Deliver(RemotePick(6229, "Ada"));

        await WaitForCallAsync(() => _flash.ReceivedCalls().Any());
        _flash.Received(1).Show("KaraFun song lookup failed: kaput The pick from Ada was not queued.", FlashType.Warning);
    }

    /// <summary>A FakeHandler whose endpoints can be made to fail, and made to recover, mid-test.</summary>
    private sealed class ToggleHandler(IReadOnlyDictionary<string, string> responsesByEndpoint) : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<Exception>> _failing = new();

        public void Fail(string endpoint, Func<Exception>? exception = null)
            => _failing[endpoint] = exception ?? (() => new HttpRequestException("No route to host"));

        public void Heal(string endpoint) => _failing.TryRemove(endpoint, out _);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var endpoint = request.RequestUri!.AbsolutePath.Trim('/');

            if (_failing.TryGetValue(endpoint, out var exception))
                throw exception();

            var body = responsesByEndpoint.TryGetValue(endpoint, out var xml)
                ? xml
                : """<response status="Error"><error action="fail">NOT_STUBBED</error></response>""";

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static Media KaraFunRow(string destination, string sourceKey, string source = "KaraFun") => new()
    {
        FilePath = destination,
        Title = "Neon Moon",
        Source = source,
        SourceKey = sourceKey,
    };

    // Mirrors the documented container layout: magic, index [id][total], chunks [id][size][flags][data].
    private static byte[] KitWithTiming()
    {
        var xml = System.Text.Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><karaoke duration=\"1\"></karaoke>");
        using var ms = new MemoryStream();
        ms.Write(KitContainer.Magic);
        ms.WriteByte(1);
        Span<byte> u32 = stackalloc byte[4];
        ms.WriteByte(1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)xml.Length);
        ms.Write(u32);
        ms.WriteByte(1);
        ms.Write(u32);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(u32, 0);
        ms.Write(u32);
        ms.Write(xml);
        return ms.ToArray();
    }

    private static HttpClient Cdn(byte[] body)
        => new(new FakeCdn(body));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "The service publishes an MD5 beside each kit; the stub has to speak it.")]
    private static string SongRequestFor(byte[] kit, string? md5 = null)
        => $"""<response status="OK"><song><stream>https://cdn.example.test/6229.kit</stream><md5>{md5 ?? Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(kit))}</md5><filesize>{kit.Length}</filesize></song></response>""";

    private sealed class FakeCdn(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage { Content = new ByteArrayContent(body) });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADownloadedSong_IsImportedWithItsKaraFunIdAndTheSingleUseSetting(bool singleUse)
    {
        _settings.CurrentValue.Returns(new KaraFunSettings { SingleUse = singleUse });
        MediaImportRequest? request = null;
        _library.BeginImportAsync(Arg.Do<MediaImportRequest>(r => request = r))
            .Returns(new ImportTicket { MediaId = Guid.NewGuid() });
        ArrangeNoSingerNamed("Ada");
        _users.CreateAsync(Arg.Any<KHostUser>()).Returns(new KHostUser { Id = Guid.NewGuid(), Name = "Ada" });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForAsync(() => request is not null, "the import to begin");

        Assert.Equal("6229", request!.SourceKey);
        Assert.Equal(singleUse, request.IsSingleUse);
        Assert.False(request.IsEphemeral);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SingleUseSavedAfterTheRoomOpened_AppliesToTheNextDownload(bool singleUse)
    {
        MediaImportRequest? request = null;
        _library.BeginImportAsync(Arg.Do<MediaImportRequest>(r => request = r))
            .Returns(new ImportTicket { MediaId = Guid.NewGuid() });
        ArrangeNoSingerNamed("Ada");
        _users.CreateAsync(Arg.Any<KHostUser>()).Returns(new KHostUser { Id = Guid.NewGuid(), Name = "Ada" });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        _settings.CurrentValue.Returns(new KaraFunSettings { SingleUse = singleUse });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForAsync(() => request is not null, "the import to begin");

        Assert.Equal(singleUse, request!.IsSingleUse);
    }

    /// <summary>The search takes no count, so the whole page is fetched: cutting it short would skip the
    /// rest of it, since the next request asks for the next page. More than the old 50 cap on purpose.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Search_FetchesEverySongThePageAnswersWith_WhateverPageSizeTheHostAsks(int pageSize)
    {
        var ids = Enumerable.Range(1, 60).ToList();
        var search = $"""<response status="OK"><list>{string.Concat(ids.Select(id => $"<song id=\"{id}\" />"))}</list></response>""";
        var (provider, handler) = await SignedInAsync(new() { ["search/top"] = search, ["song/info"] = CatalogInfo });

        await provider.SearchAsync("neon moon", pageSize: pageSize);

        Assert.Equal(60, handler.Query("song/info").Split("%2C").Length);
    }

    /// <summary>A kit already on disk skips the download and goes through the other import.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AKitAlreadyOnDisk_IsImportedWithItsKaraFunIdAndTheSingleUseSetting(bool singleUse)
    {
        _settings.CurrentValue.Returns(new KaraFunSettings { SingleUse = singleUse });
        var destination = Path.Combine(_mediaDirectory, "karafun", "6229.kit");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "already here");

        MediaImportRequest? request = null;
        _library.ImportAsync(Arg.Do<MediaImportRequest>(r => request = r)).Returns(Guid.NewGuid());
        ArrangeNoSingerNamed("Ada");
        _users.CreateAsync(Arg.Any<KHostUser>()).Returns(new KHostUser { Id = Guid.NewGuid(), Name = "Ada" });

        var socket = await RoomOpenAsync(new() { ["song/info"] = CatalogInfo });
        socket.Deliver(RemotePick(6229, "Ada"));
        await WaitForAsync(() => request is not null, "the import");

        Assert.Equal("6229", request!.SourceKey);
        Assert.Equal(singleUse, request.IsSingleUse);
        await _library.DidNotReceive().BeginImportAsync(Arg.Any<MediaImportRequest>());
    }

    [Theory]
    [InlineData("KaraFun", "6229", true)]
    [InlineData("KaraFun", "community:77456", true)]
    [InlineData("KaraFun", "", false)]
    [InlineData("KaraFun", "community:", false)]
    [InlineData("KaraFun", "not-a-number", false)]
    [InlineData("YouTube", "6229", false)]
    [InlineData("", "6229", false)]
    public void CanRefetch_AnswersFromTheRowsSourceAndKey(string source, string sourceKey, bool expected)
        => Assert.Equal(expected, Provider().CanRefetch(KaraFunRow("/nowhere/6229.kit", sourceKey, source)));

    [Fact]
    public async Task Refetch_DownloadsToTheRowsPathAndCompletesTheTicket()
    {
        var kit = KitWithTiming();
        var destination = Path.Combine(_mediaDirectory, "karafun", "6229.kit");
        var (provider, handler) = await SignedInAsync(new() { ["song/request"] = SongRequestFor(kit) }, cdn: Cdn(kit));
        var ticket = new ImportTicket { MediaId = Guid.NewGuid() };

        await provider.RefetchAsync(KaraFunRow(destination, "6229"), ticket);

        Assert.Equal(kit, await File.ReadAllBytesAsync(destination));
        Assert.Contains("song=6229", handler.Query("song/request"));
        await _library.Received(1).BeginProcessingAsync(ticket.MediaId);
        await _library.Received(1).CompleteImportAsync(ticket.MediaId);
        await _library.DidNotReceive().FailImportAsync(Arg.Any<Guid>(), Arg.Any<string?>());
        await _library.DidNotReceive().DiscardImportAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task Refetch_OfACommunitySong_AsksTheCommunityEndpoint()
    {
        var kit = KitWithTiming();
        var destination = Path.Combine(_mediaDirectory, "karafun", "community-77456.kit");
        var (provider, handler) = await SignedInAsync(new() { ["community/request"] = SongRequestFor(kit) }, cdn: Cdn(kit));
        var ticket = new ImportTicket { MediaId = Guid.NewGuid() };

        await provider.RefetchAsync(KaraFunRow(destination, "community:77456"), ticket);

        Assert.Contains("id=77456", handler.Query("community/request"));
        await _library.Received(1).CompleteImportAsync(ticket.MediaId);
    }

    [Fact]
    public async Task Refetch_WhenTheChecksumDoesNotMatch_FailsWithAReasonAndWritesNothing()
    {
        var kit = KitWithTiming();
        var destination = Path.Combine(_mediaDirectory, "karafun", "6229.kit");
        var (provider, _) = await SignedInAsync(
            new() { ["song/request"] = SongRequestFor(kit, md5: "00") }, cdn: Cdn(kit));
        var ticket = new ImportTicket { MediaId = Guid.NewGuid() };

        await provider.RefetchAsync(KaraFunRow(destination, "6229"), ticket);

        await _library.Received(1).FailImportAsync(ticket.MediaId, Arg.Is<string?>(reason => !string.IsNullOrWhiteSpace(reason)));
        await _library.DidNotReceive().CompleteImportAsync(Arg.Any<Guid>());
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Refetch_OfARowWithNoKaraFunId_FailsWithAReasonRatherThanLeavingItDownloading()
    {
        var ticket = new ImportTicket { MediaId = Guid.NewGuid() };

        await Provider().RefetchAsync(KaraFunRow(Path.Combine(_mediaDirectory, "karafun", "x.kit"), ""), ticket);

        await _library.Received(1).FailImportAsync(ticket.MediaId, Arg.Is<string?>(reason => !string.IsNullOrWhiteSpace(reason)));
        await _library.DidNotReceive().CompleteImportAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task Refetch_WhenSignedOut_FailsWithAReasonTheHostCanActOn()
    {
        var ticket = new ImportTicket { MediaId = Guid.NewGuid() };

        await Provider().RefetchAsync(KaraFunRow(Path.Combine(_mediaDirectory, "karafun", "6229.kit"), "6229"), ticket);

        await _library.Received(1).FailImportAsync(ticket.MediaId, Arg.Is<string?>(reason => reason!.Contains("Sign in")));
        await _library.DidNotReceive().CompleteImportAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task Refetch_WhenCancelled_DiscardsAndLeavesNoFileBehind()
    {
        var kit = KitWithTiming();
        var destination = Path.Combine(_mediaDirectory, "karafun", "6229.kit");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "half a download");
        var (provider, _) = await SignedInAsync(new() { ["song/request"] = SongRequestFor(kit) }, cdn: Cdn(kit));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var ticket = new ImportTicket { MediaId = Guid.NewGuid(), Cancellation = cancelled.Token };

        await provider.RefetchAsync(KaraFunRow(destination, "6229"), ticket);

        await _library.Received(1).DiscardImportAsync(ticket.MediaId);
        await _library.DidNotReceive().FailImportAsync(Arg.Any<Guid>(), Arg.Any<string?>());
        await _library.DidNotReceive().CompleteImportAsync(Arg.Any<Guid>());
        Assert.False(File.Exists(destination));
    }
}
