using System.Collections.Concurrent;
using System.Text.Json;
using KHost.Abstractions.Exceptions;
using KHost.Abstractions.Interactions;
using KHost.Abstractions.Interactions.Requests;
using KHost.Abstractions.Messaging;
using KHost.Abstractions.Messaging.Messages;
using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using KHost.Plugins.KaraFun.Client;
using KHost.Plugins.KaraFun.Client.Kcs;
using KHost.Plugins.KaraFun.Kit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KHost.Plugins.KaraFun;

/// <summary>The KaraFun service: search and download, the sign-in and the guest remote, and the gate
/// that holds a kit to a live account. Reading and playing a kit is
/// <see cref="KaraFunKitProbe"/>, <see cref="KaraFunTimedLyricsProvider"/> and
/// <see cref="KaraFunKitRenderer"/>.
/// </summary>
public class KaraFunMediaProvider : IMediaProvider, IPluginButtonHandler, IMediaPlaybackGate, IMediaRefetcher
{

    private readonly IPluginContext _plugin;
    private readonly IMessageBroker _broker;

    // Playback is resolved on use, never taken in the constructor: PlaybackService reaches every
    // IMediaPlaybackGate through IMediaGateService, and this provider is one, so asking up front
    // closes a ring the container cannot build.
    private readonly IServiceProvider _services;
    private readonly SubscriptionSet _subscriptions = new();
    private readonly IInteractionDispatcher _dispatcher;
    private readonly IMediaAcquisitionService _media;
    private readonly ISingerQueueService _queue;
    private readonly IUsersService _users;
    private readonly IPerformanceService _performances;
    private readonly ICacheService _cache;
    private readonly ILogger<KaraFunMediaProvider> _logger;
    private readonly IFlashService _flash;
    // CurrentValue is read at each use: a save made while the plugin runs reaches the next pick or search.
    private readonly IOptionsMonitor<KaraFunSettings> _options;
    private KaraFunSettings Settings => _options.CurrentValue;
    private readonly KaraFunApi _api;

    private readonly IReadOnlyList<MediaProviderAction> _enqueueActions;
    private readonly IReadOnlyList<MediaProviderAction> _signInActions;
    private readonly IReadOnlyList<MediaProviderAction> _communitySearchActions;

    // Read once and cleared, so one click buys one community search: a later plain search of the
    // same words goes back to the catalog instead of silently staying in the other one.
    private string? _communitySearchFor;

    // Kept across runs in the host's cache, never in a plugin setting (which would put it on the
    // Plugins page) and never the password that produced it (not held past the sign-in request).
    private string? _sessionKey;
    private string? _login;
    private string? _passwordHash;

    /// <summary>One entry holding the whole session, not three: a keychain has no transaction, and
    /// three entries could be half-updated by a crash, leaving a mismatched key and credential.</summary>
    private const string SessionSecretKey = "session";

    /// <summary>Where this used to live: a credential in a plaintext file, read once on the way up
    /// so an already-signed-in host is not asked again, then deleted.</summary>
    private const string LegacySessionCacheKey = "karafun-session";

    /// <summary>
    /// What a restart gets back. A cache entry that reads as null is a signed-out host.
    /// </summary>
    /// <param name="Key">
    /// The last session key. Disposable, kept only to hand back as <c>last_ses</c>, never relied
    /// on to still be open.
    /// </param>
    /// <param name="PasswordHash">
    /// The <c>pwd</c> value: md5 of the substituted password, which is the only thing
    /// <c>session/open</c> is ever given. Keeping it lets a console come back up and reach the room
    /// with nobody present to type anything, the same way the real client re-opens its session on
    /// every launch, because a session key is not a credential and does not outlive the shift.
    ///
    /// This is a credential at rest. It is not the typed password and cannot be turned back into
    /// one, but anything holding it can open sessions on the account, so it lives in the host's
    /// cache rather than in plugin settings, off the Plugins page and out of anything a host would
    /// think to share.
    /// </param>
    private sealed record StoredSession(string Key, string? Login, string? PasswordHash = null);

    // Read once, and started as soon as the plugin is built: it is a local file, and a host who
    // opens the Plugins page before searching should already see which account is signed in.
    private readonly Lazy<Task> _restored;

    // A key off the disk is only known to be good once the server says so, and that check is worth
    // one round trip per launch, not one per load, which a live show cannot wait for.
    private bool _validated;

    // Keyed by song id, answering the row the running download imports into: a second pick of a song
    // still arriving queues against that row rather than starting another download into the same
    // file. The File.Exists check covers a completed one; this one running.
    private readonly ConcurrentDictionary<string, Task<Guid>> _downloadsInFlight = new();

    // The socket the room's picks arrive on. Opened when a session settles and never before: its
    // URL is minted per session, and there is nothing to listen to without one.
    private RemoteQueueChannel? _remote;
    private readonly Func<IKcsSocket>? _remoteSocket;

    // One at a time: a join, the room's guest list and a new room's clear all rewrite the same keys,
    // and interleaved they can untie a guest another pass just tied.
    private readonly SemaphoreSlim _ties = new(1, 1);

    // remoteId → the nickname it last joined under, this run. A remoteId plus its nickname is the
    // guest: KaraFun holds a nickname for at least five minutes after its phone drops.
    private readonly ConcurrentDictionary<string, string> _guestNames = new();

    // Guest → when its recent picks arrived, oldest first. Locked on itself: a count and the pick
    // it admits must be one step, or a burst arriving together all reads the same free slot.
    private readonly Dictionary<string, RemotePickHistory> _remotePicks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sign-up link of the room the stored keys were tied in. Kept across restarts,
    /// since the room is: a restart reuses the link, a session renewal has been seen to mint another.</summary>
    private const string RoomCacheKey = "karafun-room";

    // Spent once: validating a restored session already asks session/info, and asking again to open
    // the socket moments later is a round trip a venue's wifi does not need to spend twice.
    private string? _pendingRemoteUrl;

    // Minted per session, so it is read from whichever session read happens to arrive and cleared
    // with the session, never rebuilt from a hardcoded CDN host.
    private string? _imageUrlTemplate;

    // A failed mint must read as distinguishable from one never attempted, or an outage during
    // startup leaves the room with no way in all night and nothing else asks for one again.
    private readonly SemaphoreSlim _screenCode = new(1, 1);
    private bool _screenCodeShown;

    // Bumped every time the room goes down, so a mint that started before a flap discards itself
    // rather than drawing a code for the connection that has already ended.
    private long _roomEpoch;

    // The gate raises the sign-in itself, so two blocked loads (a double-clicked play, an
    // auto-advance behind it) would otherwise each raise their own dialog.
    private readonly SemaphoreSlim _session = new(1, 1);

    // Guards the room-failure flash that a socket's reconnect loop could otherwise raise on every
    // retry; cleared the moment a code mints successfully, never on a timer.
    private int _roomFailureFlashed;

    // Once per outage, cleared when a search succeeds: the console searches as the host types, and a
    // flash for each would bury the screen.
    private int _searchFailureFlashed;

    // The standing "room is closed" warning, so the authenticate that ends the outage can take it back.
    private int _roomWarning;

    private int _screenCodeRetrying;

    /// <summary>How long a failed QR mint waits before trying again. Settable for the tests.</summary>
    internal TimeSpan ScreenCodeRetryDelay { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>The clock the guest pick limit counts by. Settable for the tests.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    public KaraFunMediaProvider(
        IPluginContext plugin,
        IOptionsMonitor<KaraFunSettings> options,
        IInteractionDispatcher dispatcher,
        IMediaAcquisitionService media,
        ISingerQueueService queue,
        IUsersService users,
        IPerformanceService performances,
        ICacheService cache,
        IMessageBroker broker,
        IServiceProvider services,
        ILogger<KaraFunMediaProvider> logger,
        IFlashService flash,
        IHttpClientFactory httpClientFactory)
        : this(plugin, options, dispatcher, media, queue, users, performances, cache, broker, services, logger, flash,
            new KaraFunApi(ClientProfile.MacOS, httpClientFactory: httpClientFactory), () => new ClientWebSocketAdapter())
    {
    }

    /// <param name="remoteSocket">
    /// What the remote channel opens. Null opens no channel at all: the seam a test uses, so a
    /// suite that builds a provider does not put a reconnecting socket on the network.
    /// </param>
    internal KaraFunMediaProvider(
        IPluginContext plugin,
        IOptionsMonitor<KaraFunSettings> options,
        IInteractionDispatcher dispatcher,
        IMediaAcquisitionService media,
        ISingerQueueService queue,
        IUsersService users,
        IPerformanceService performances,
        ICacheService cache,
        IMessageBroker broker,
        IServiceProvider services,
        ILogger<KaraFunMediaProvider> logger,
        IFlashService flash,
        KaraFunApi api,
        Func<IKcsSocket>? remoteSocket = null)
    {
        _plugin = plugin;
        _options = options;
        _dispatcher = dispatcher;
        _media = media;
        _queue = queue;
        _users = users;
        _performances = performances;
        _cache = cache;
        _broker = broker;
        _services = services;
        _logger = logger;
        _flash = flash;

        // The room is told what is playing by the same announcement the console redraws on. Not
        // awaited: handlers run one at a time, and a websocket send would hold up every subscriber
        // behind it for as long as the room's wifi takes.
        _subscriptions.Add(broker.Subscribe<PlaybackChanged>(changed => { _ = PublishNowPlayingAsync(); }));

        // Both move the list a guest sees: one reorders the singers, the other adds or drops a
        // song. A song ending raises both, which costs a duplicate push rather than a wrong one,
        // since every push carries the whole queue.
        _subscriptions.Add(broker.Subscribe<SingerQueueChanged>(changed => { _ = PublishQueueAsync(); }));

        // The venue owns whether guests may join at all, so editing it opens or closes the room.
        // SelectedVenueChanged rather than VenuesChanged: this is the venue being run, not the list.
        _subscriptions.Add(broker.Subscribe<SelectedVenueChanged>(changed => { _ = SyncRemoteChannelAsync(); }));
        _subscriptions.Add(broker.Subscribe<PerformancesChanged>(changed => { _ = PublishQueueAsync(); }));
        _api = api;
        _remoteSocket = remoteSocket;

        // Started here rather than on first search: reading the cache is a local file, and the
        // Plugins page has no way to await anything before it draws the button.
        _restored = new Lazy<Task>(RestoreSessionAsync);
        _ = _restored.Value;

        _enqueueActions = [
            new() {
                DisplayName = "Enqueue",
                Description = "Downloads the track and enqueues it for the selected singer",
                Icon = "plus-lg",
                PerformAsync = DownloadAndEnqueueAsync,
            }
        ];
        _signInActions = [
            new() {
                DisplayName = "Sign in",
                Description = "Enter a KaraFun account's email and password — kept only for this session, never saved",
                Icon = "key-fill",
                PerformAsync = SignInAsync,
                // The row this sits on says there is nothing to search with, so leaving it up
                // after a successful sign-in reads as the sign-in having failed.
                RefreshesResults = true,
            }
        ];
        _communitySearchActions = [
            new() {
                DisplayName = "Search Community Songs",
                Description = "Searches KaraFun's community catalog — karaoke uploaded by other members — for the same words",
                Icon = "people-fill",
                PerformAsync = SearchCommunityAsync,
                // The click does nothing but arm the next search; this is what runs it.
                RefreshesResults = true,
            }
        ];
    }

    /// <summary>KaraFun's own list-row size, a 200x200 square crop; 200px covers the console's
    /// thumbnail on a 2x display with room over.</summary>
    private const string ListThumbnailFormat = "sq200";

    private const string SongIdKey = "songId";

    /// <summary>Carries the words a row was found with, so an action on it can search them again.</summary>
    private const string QueryKey = "query";

    /// <summary>The one row shown before a session exists: a way in, not a search result.</summary>
    private const string SignInForeignKey = "sign-in";

    /// <summary>The row that offers the community catalog: an offer, not a search result.</summary>
    private const string CommunitySearchForeignKey = "community-search";

    /// <summary>Marks a row as community karaoke: id 6229 there is a different track from id 6229
    /// in the catalog, so the space travels with the id to every endpoint and file it reaches.</summary>
    private const string CommunityForeignKeyPrefix = "community:";

    /// <summary>The manifest button's key; see manifest.json's "buttons".</summary>
    private const string SessionButtonKey = "session";

    public string DisplayName => "KaraFun";

    public string SourceName => "KaraFun";

    public IEnumerable<MediaProviderAction> Actions => _enqueueActions;

    public IReadOnlyList<MediaResultColumn> Columns =>
    [
        // Headerless and droppable: it helps a host recognise a track at a glance but names
        // nothing, so it is the first thing a narrow panel should give up.
        new() { Key = MediaResultColumn.ThumbnailKey, Header = "", Kind = MediaResultColumnKind.Thumbnail, Essential = false },
        new() { Key = MediaResultColumn.TitleKey, Header = "Title" },
        new() { Key = MediaResultColumn.ArtistKey, Header = "Artist" },
        new() { Key = MediaResultColumn.DurationKey, Header = "Duration" },
    ];

    public async Task<List<MediaSearchEntity>> SearchAsync(string query, int pageNumber = 0, int pageSize = 0)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        await EnsureSessionAsync();

        if (_sessionKey is not { Length: > 0 } sessionKey)
            return [SignInPrompt()];

        try
        {
            List<MediaSearchEntity> rows;

            // Armed by the community row's click, and spent by the refresh that click asks for.
            if (Interlocked.Exchange(ref _communitySearchFor, null) == query)
                rows = await SearchCommunityAsync(sessionKey, query, pageNumber);
            else
                // Offered last, under the catalog's own answers: it is a second place to look, not a
                // result. Worth offering even when the catalog found nothing, especially then.
                rows = [.. await SearchCatalogAsync(sessionKey, query, pageNumber), CommunitySearchPrompt(query)];

            Volatile.Write(ref _searchFailureFlashed, 0);
            return rows;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KaraFun search failed");

            // Caught here so the host's own line for a throwing provider never doubles this one.
            if (Interlocked.Exchange(ref _searchFailureFlashed, 1) == 0)
                _flash.Show(KaraFunFailure.Describe("search", ex), FlashType.Warning);

            return [];
        }
    }

    private async Task<List<MediaSearchEntity>> SearchCatalogAsync(string sessionKey, string query, int pageNumber)
    {
        // "top" answers with a mixed list of <song>, <community> and <suggestion>; only the songs
        // are taken here, since the community hits have their own search below.
        var (env, _) = await _api.SearchAsync(sessionKey, query, section: "top", page: Math.Max(1, pageNumber));
        var songs = await _api.SongInfoAsync(sessionKey, IdsFrom(env, "song"));

        return [.. songs.Select(song => SongRow(song, song.Id.ToString()))];
    }

    /// <summary>A different section of the same search endpoint: hits come back as
    /// &lt;community&gt;, and only <c>community/info</c> or <c>request</c> answer for their ids.</summary>
    private async Task<List<MediaSearchEntity>> SearchCommunityAsync(string sessionKey, string query, int pageNumber)
    {
        var (env, _) = await _api.SearchAsync(sessionKey, query, section: "community", page: Math.Max(1, pageNumber));
        var songs = await _api.CommunityInfoAsync(sessionKey, IdsFrom(env, "community"));

        return [.. songs.Select(song => SongRow(song, CommunityForeignKeyPrefix + song.Id))];
    }

    /// <summary>Every id a search page answers with; the records themselves come from a second call.</summary>
    /// <remarks>All of them: the search takes no count, so cutting the page short would skip the rest
    /// of it, since the next request asks for the next page.</remarks>
    private static List<int> IdsFrom(ApiEnvelope env, string element)
        => [.. (env.Element("list")?.Elements(element) ?? [])
            .Select(s => int.TryParse(s.Attribute("id")?.Value, out var id) ? id : (int?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)];

    private MediaSearchEntity SongRow(CatalogSong song, string foreignKey) => new()
    {
        Title = song.Title,
        Artist = song.Artist,
        SourceDisplayName = DisplayName,
        Source = SourceName,
        ForeignKey = foreignKey,
        Duration = song.Duration,
        SupportedActions = _enqueueActions,
        Fields = FieldsFor(song),
    };

    private Dictionary<string, string> FieldsFor(CatalogSong song)
    {
        var fields = new Dictionary<string, string> { [SongIdKey] = song.Id.ToString() };

        if (CoverUrl(song.ImageId) is { } cover)
            fields[MediaResultColumn.ThumbnailKey] = cover;

        return fields;
    }

    /// <summary>Kept from whichever session response carried it, and never cleared by one that did
    /// not, so a later read missing it does not silently strip every cover from the rows.</summary>
    private void RememberImageTemplate(Session? session)
    {
        if (session?.ImageUrlTemplate is { Length: > 0 } template)
            _imageUrlTemplate = template;
    }

    /// <summary>Null when either half is missing, so a row draws with no picture rather than a
    /// broken one: a song without artwork, or a session read that never carried the template.</summary>
    private string? CoverUrl(string? imageId)
        => imageId is { Length: > 0 } id && _imageUrlTemplate is { Length: > 0 } template
            ? template.Replace("{id}", id, StringComparison.Ordinal)
                      .Replace("{format}", ListThumbnailFormat, StringComparison.Ordinal)
            : null;

    /// <summary>Somewhere else to look when the catalog did not have it, carrying the words it was
    /// found with so the action can search the same ones without the provider remembering them.</summary>
    private MediaSearchEntity CommunitySearchPrompt(string query) => new()
    {
        Title = "Search Community Songs",
        Artist = "",
        SourceDisplayName = DisplayName,
        Source = SourceName,
        ForeignKey = CommunitySearchForeignKey,
        Notes = "Karaoke uploaded by other KaraFun members, searched separately from the main catalog.",
        SupportedActions = _communitySearchActions,
        // An offer, not a result: "Search Community Songs" has no room in a column sized for
        // "Enqueue", and spilled out of it.
        SpansAllColumns = true,
        Fields = new Dictionary<string, string> { [QueryKey] = query },
    };

    /// <summary>Arms the community search; the action's own RefreshesResults is what runs it.</summary>
    private Task SearchCommunityAsync(MediaSearchEntity entity)
    {
        _communitySearchFor = entity.Fields.GetValueOrDefault(QueryKey);
        return Task.CompletedTask;
    }

    /// <summary>A row that names itself rather than a song: returned until sign-in succeeds, since
    /// there is nothing to search for without a session.</summary>
    private MediaSearchEntity SignInPrompt() => new()
    {
        Title = "Sign in to KaraFun",
        Artist = "",
        SourceDisplayName = DisplayName,
        Source = SourceName,
        ForeignKey = SignInForeignKey,
        Notes = "Sign in with the account this venue already has. "
              + "The password is used once to open a session and is never saved — search again after signing in.",
        SupportedActions = _signInActions,
        // Not a song, so it is drawn across the table rather than under columns describing one.
        SpansAllColumns = true,
    };

    /// <summary>The search row's Sign in action; the Plugins page button runs the same core.</summary>
    private Task SignInAsync(MediaSearchEntity entity) => OpenSessionAsync();

    /// <summary>Brings the session up to date before anything decides on it: the key off the disk,
    /// then one check with the server that it is still open. Both happen once per launch.</summary>
    private async Task EnsureSessionAsync(CancellationToken cancellationToken = default)
    {
        await _restored.Value;

        if (_validated || _sessionKey is not { Length: > 0 })
            return;

        var settled = false;

        await _session.WaitAsync(cancellationToken);
        try
        {
            if (_validated || _sessionKey is not { Length: > 0 } key)
                return;

            _validated = true;

            if (!await IsStillOpenAsync(key, cancellationToken) && !await ReopenSessionAsync(cancellationToken))
                await ForgetSessionAsync();

            settled = true;
        }
        finally
        {
            _session.Release();
        }

        // The only place a remembered session ever reaches the screens on its own: nobody signed
        // in this launch, so nothing else would have put its code up.
        if (settled)
            await SyncRemoteAsync(cancellationToken);
    }

    /// <summary>From the secret store, or once from the plaintext cache the plugin used before.
    /// A migrated entry moves into the store and out of the cache in the same breath.</summary>
    private async Task<StoredSession?> ReadStoredSessionAsync()
    {
        if (await _plugin.GetSecretAsync(SessionSecretKey) is { Length: > 0 } stored)
            return JsonSerializer.Deserialize<StoredSession>(stored);

        if (await _cache.LoadAsync<StoredSession>(LegacySessionCacheKey) is not { Key.Length: > 0 } legacy)
            return null;

        _logger.LogInformation("Moving the stored KaraFun session out of the cache and into the secret store");

        await _plugin.SetSecretAsync(SessionSecretKey, JsonSerializer.Serialize(legacy));
        await _cache.SaveAsync<StoredSession?>(LegacySessionCacheKey, null);

        return legacy;
    }

    /// <summary>Reads the key kept from a previous run. A session opened here is already good.</summary>
    private async Task RestoreSessionAsync()
    {
        try
        {
            if (await ReadStoredSessionAsync() is not { Key.Length: > 0 } stored)
                return;

            await _session.WaitAsync();
            try
            {
                // A host who signed in while this was reading has the newer session of the two.
                if (_sessionKey is { Length: > 0 })
                    return;

                _sessionKey = stored.Key;
                _login = stored.Login;
                _passwordHash = stored.PasswordHash;
            }
            finally
            {
                _session.Release();
            }
        }
        catch (Exception ex)
        {
            // Nothing here is worth failing the plugin's construction over: the host signs in again.
            _logger.LogDebug(ex, "Could not read the stored KaraFun session");
        }
    }

    /// <summary>Only the server saying the key is dead counts: a venue whose wifi is down must not
    /// lose a good session on the way in, which would be a sign-in prompt mid-set.</summary>
    private async Task<bool> IsStillOpenAsync(string sessionKey, CancellationToken cancellationToken)
    {
        try
        {
            var (session, env) = await _api.SessionInfoAsync(sessionKey, cancellationToken);

            if (env.IsOk)
            {
                // The same read that proves the session is open also carries the socket's address,
                // so the channel opened a moment later spends no round trip of its own.
                _pendingRemoteUrl = session?.ControlSystemUrl;
                RememberImageTemplate(session);
                return true;
            }

            bool rejected = WasRejected(env);

            if (rejected)
                _logger.LogInformation("The stored KaraFun session is no longer open ({Error})", env.ErrorCode);

            return !rejected;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check the stored KaraFun session; keeping it");
            return true;
        }
    }

    /// <summary>Settles the session and opens the room, off the caller's thread. Nothing here is
    /// awaited or thrown, since a slow or unreachable KaraFun must cost the console nothing at all.</summary>
    internal void StartRemoteInBackground(CancellationToken cancellationToken = default)
        => _ = Task.Run(async () =>
        {
            try
            {
                await EnsureSessionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // A venue signed out, or one whose wifi is not up yet. The first search settles it
                // the old way, and the socket's own retry covers the rest.
                _logger.LogWarning(ex, "KaraFun could not open the room on startup");
            }
        }, cancellationToken);

    /// <summary>The code is only ever taken down here, never put up: minting belongs to the channel,
    /// on authenticating, so a code on screen means the room is live rather than once was.</summary>
    private async Task SyncRemoteAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionKey is not { Length: > 0 })
            await HideScreenCodeAsync();

        await SyncRemoteChannelAsync();
    }

    /// <summary>The URL is re-read on every reconnect rather than held: it carries the session's own
    /// token, so a socket reopened against a stale one connects as nobody.</summary>
    private async Task SyncRemoteChannelAsync()
    {
        // A venue announced during startup lands before the stored session is read, and would
        // otherwise read as a sign-out and drop every tie the room still holds.
        await _restored.Value;

        // Two ways a room closes to phones, and they end the same way: the account went, or the
        // venue says no guests.
        var open = _sessionKey is { Length: > 0 } && (await GuestPolicyAsync()).Allowed;

        if (!open)
        {
            if (_remote is { } stopping)
            {
                _remote = null;
                await stopping.DisposeAsync();
            }

            // The code outlives neither: one on screen would invite a room that cannot answer.
            await HideScreenCodeAsync();

            // Every phone the room paired is gone with it. Left behind, those keys would put the
            // next session's guests onto whoever held their number last night.
            await ForgetEveryRemoteAsync();

            return;
        }

        if (_remote is not null || _remoteSocket is null)
            return;

        _remote = new RemoteQueueChannel(
            ResolveRemoteUrlAsync, EnqueueRemotePickAsync, _logger, _remoteSocket,
            onGuestsPresent: TieGuestsAsync,
            onAuthenticated: OnRoomAuthenticatedAsync,
            onNotConnected: HideScreenCodeAsync,
            showQueueToGuests: async token => (await GuestPolicyAsync()).ShowQueue);
        _remote.Start();
    }

    private async Task<string?> ResolveRemoteUrlAsync(CancellationToken cancellationToken)
    {
        if (_sessionKey is not { Length: > 0 } sessionKey)
            return null;

        if (Interlocked.Exchange(ref _pendingRemoteUrl, null) is { Length: > 0 } pending)
            return pending;

        (Session? session, ApiEnvelope env) read;

        try
        {
            read = await _api.SessionInfoAsync(sessionKey, cancellationToken);
        }
        catch (Exception ex) when (KaraFunFailure.IsNetwork(ex, cancellationToken))
        {
            // The channel retries on its own and reports each failure at its own pace; this is the
            // standing line, which the next authenticate takes back.
            NoteRoomWarning(RoomUnreachableWarning);
            throw;
        }

        var (session, env) = read;

        RememberImageTemplate(session);

        if (session?.ControlSystemUrl is { Length: > 0 } url)
            return url;

        // The one place a session is renewed after launch, or a key that expires mid-night leaves
        // the socket retrying a dead one all night while the code on screen invites nobody in.
        if (!WasRejected(env) || !await RenewSessionAsync(sessionKey, cancellationToken))
            return null;

        if (_sessionKey is not { Length: > 0 } renewed)
            return null;

        var (reopened, _) = await _api.SessionInfoAsync(renewed, cancellationToken);

        RememberImageTemplate(reopened);

        return reopened?.ControlSystemUrl;
    }

    /// <summary>For a caller that does not hold the lock. Never disposes the channel: this runs on
    /// the channel's own reconnect, and disposing it from there waits on the pump calling it.
    /// Internal (not private) so a concurrency test can call it directly, the same way two
    /// independent starts of the remote channel — one from <see cref="StartRemoteInBackground"/>'s
    /// own settle, one from a <c>SelectedVenueChanged</c> announced while it is still running — can
    /// both see <paramref name="staleKey"/> rejected before either has renewed it.</summary>
    /// <param name="staleKey">The key this caller saw rejected. Renewing spends the stored
    /// credential on a fresh <c>session/open</c>; a second caller queued behind the lock on the
    /// same stale key must not spend a second one once the first has already replaced it.</param>
    internal async Task<bool> RenewSessionAsync(string staleKey, CancellationToken cancellationToken)
    {
        bool renewed;

        await _session.WaitAsync(cancellationToken);
        try
        {
            if (_sessionKey is { Length: > 0 } current && !string.Equals(current, staleKey, StringComparison.Ordinal))
            {
                // Someone else already renewed it while this caller waited for the lock.
                renewed = true;
            }
            else
            {
                renewed = await ReopenSessionAsync(cancellationToken);

                if (!renewed)
                    await ForgetSessionAsync();
            }
        }
        finally
        {
            _session.Release();
        }

        // Either way what is on screen is stale: a renewed session is a new channel, and a lost one
        // is no room at all. The socket puts a code back on its next authenticate.
        await HideScreenCodeAsync();

        return renewed;
    }

    /// <summary>Only a refusal of the key itself may spend the stored credential on a renewal:
    /// a venue whose wifi blinked still has a good key.</summary>
    private static bool WasRejected(ApiEnvelope env)
        => !env.IsOk
        && (string.Equals(env.ErrorCode, "INVALID_SESSION", StringComparison.OrdinalIgnoreCase)
         || string.Equals(env.ErrorAction, "restart", StringComparison.OrdinalIgnoreCase));

    /// <summary>A guest's pick, put on the queue under the name they gave it.</summary>
    private async Task EnqueueRemotePickAsync(RemoteSongRequest pick, CancellationToken cancellationToken)
    {
        if (_sessionKey is not { Length: > 0 } sessionKey)
            return;

        var settings = Settings;

        // Before the lookup: a phone flooding the room should cost nothing per pick past the limit.
        if (!TryAdmitRemotePick(pick, settings, out var firstRefused))
        {
            _logger.LogInformation("Ignored a KaraFun pick from '{Singer}': over the limit of {Limit} in {Minutes} minutes",
                pick.SingerName, settings.RemotePickLimit, settings.RemotePickWindowMinutes);

            // Once per burst: a flash for every ignored pick would hand the flood to the host.
            if (firstRefused)
                _flash.Show(
                    $"KaraFun: Ignoring song queue requests from {pick.SingerName.Trim()} — more than {settings.RemotePickLimit} in {settings.RemotePickWindowMinutes} minutes.",
                    FlashType.Warning);

            return;
        }

        CatalogSong? song;

        try
        {
            // The wire carries an id and nothing else, so the title and artist the queue shows have to
            // be read back before the row can be created.
            song = pick.Community
                ? (await _api.CommunityInfoAsync(sessionKey, [pick.SongId], cancellationToken)).FirstOrDefault()
                : (await _api.SongInfoAsync(sessionKey, [pick.SongId], cancellationToken)).FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The phone was told the pick was accepted before this ran, and the channel only logs
            // what escapes, so this is the host's only word that the pick is gone.
            _logger.LogWarning(ex, "KaraFun could not look up song {SongId} for '{Singer}'", pick.SongId, pick.SingerName);
            _flash.Show(
                $"{KaraFunFailure.Describe("song lookup", ex, cancellationToken)} The pick from {pick.SingerName} was not queued.",
                FlashType.Warning);
            return;
        }

        if (song is null)
        {
            _plugin.AddWarning($"KaraFun would not describe song {pick.SongId}, so '{pick.SingerName}' was not queued.");
            _flash.Show($"KaraFun: Could not find that song, so it was not queued for {pick.SingerName}.", FlashType.Warning);
            return;
        }

        if (await ResolveRemoteSingerAsync(pick.SingerName, pick.RemoteId) is not { } singerId)
            return;

        var foreignKey = pick.Community ? CommunityForeignKeyPrefix + song.Id : song.Id.ToString();

        await DownloadAndEnqueueAsync(SongRow(song, foreignKey), singerId, pick.SingerName?.Trim(), EnqueueOrigin.Remote);
    }

    /// <summary>Counts a guest's pick against the limit, or refuses it when the guest is over.</summary>
    /// <param name="firstRefused">True for the first pick refused since this guest's last admitted one.</param>
    /// <remarks>A guest is its phone, or its name when the phone sent no id.</remarks>
    private bool TryAdmitRemotePick(RemoteSongRequest pick, KaraFunSettings settings, out bool firstRefused)
    {
        firstRefused = false;

        var limit = settings.RemotePickLimit;
        var window = TimeSpan.FromMinutes(settings.RemotePickWindowMinutes);

        if (limit <= 0 || window <= TimeSpan.Zero)
            return true;

        var guest = pick.RemoteId.Length > 0 ? pick.RemoteId : pick.SingerName.Trim();
        var now = Clock.GetUtcNow();

        lock (_remotePicks)
        {
            if (!_remotePicks.TryGetValue(guest, out var history))
                _remotePicks[guest] = history = new RemotePickHistory();

            while (history.Arrivals.Count > 0 && now - history.Arrivals.Peek() >= window)
                history.Arrivals.Dequeue();

            if (history.Arrivals.Count < limit)
            {
                history.Arrivals.Enqueue(now);
                history.Refusing = false;
                return true;
            }

            firstRefused = !history.Refusing;
            history.Refusing = true;
            return false;
        }
    }

    /// <summary>Tells every phone in the room what is playing. The now-playing bar is gated by no
    /// permission, so this works without ViewQueue and without showing a queue at all.</summary>
    internal async Task PublishNowPlayingAsync(CancellationToken cancellationToken = default)
    {
        if (_remote is not { IsConnected: true } remote)
            return;

        var playback = _services.GetService<IPlaybackService>();
        if (playback is null)
            return;

        await remote.PublishStatusAsync(NowPlaying(playback), cancellationToken);
    }

    /// <summary>Only Playing and Paused draw an active bar; the page folds every other state into
    /// idle, so a stopped host is reported as idle rather than as something it would misread.
    /// </summary>
    internal static PlayerStatus NowPlaying(IPlaybackService playback)
    {
        var status = new PlayerStatus
        {
            State = playback.State switch
            {
                PlaybackState.Playing => PlayerStatus.Types.State.Playing,
                PlaybackState.Paused => PlayerStatus.Types.State.Paused,
                _ => PlayerStatus.Types.State.Idle,
            },
        };

        if (playback.CurrentMedia is not { } media)
            return status;

        status.Current = new PlayerQueueItem
        {
            Id = playback.CurrentPerformance?.Id.ToString() ?? media.Id.ToString(),
            Song = new PlayerQueueSong
            {
                // Never absent and never null. The page converts an identifier without a null
                // guard, and builds its whole list before assigning it, so one unidentified row
                // throws and discards the entire update rather than just itself. Zero converts
                // cleanly and draws a placeholder tile.
                Id = new PlayerSongIdentifier { Type = PlayerSongIdentifier.Types.TypeEnum.Unknown, Id = 0 },
                Title = media.Title ?? string.Empty,
                Artist = media.Artist ?? string.Empty,
            },
        };

        return status;
    }

    /// <summary>What this venue lets guests do.</summary>
    /// <remarks>Both answer yes when there is nothing to read: a console with no venue selected
    /// is the ordinary case rather than an error, and these settings default on, so a room that
    /// was working must not close because a lookup came back empty. A venue that has genuinely
    /// turned guests off has a stored answer, and that is what is read here.</remarks>
    private async Task<(bool Allowed, bool ShowQueue)> GuestPolicyAsync()
    {
        if (_services.GetService<IVenuesService>() is not { } venues)
            return (true, true);

        try
        {
            if (await venues.ReadSelectedVenueAsync() is not { } venue)
                return (true, true);

            return (venue.Settings.AllowGuestRemote, venue.Settings.ShowQueueToGuests);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "KaraFun remote: could not read the venue's guest settings");
            return (true, true);
        }
    }

    /// <summary>Shows every phone in the room what is coming, which needs ViewQueue where the
    /// now-playing bar needed nothing.</summary>
    /// <remarks>Media is read a row at a time because nothing offers a read by ids, and the reads
    /// are deduplicated because a room often queues one song for several singers.</remarks>
    internal async Task PublishQueueAsync(CancellationToken cancellationToken = default)
    {
        if (_remote is not { IsConnected: true } remote)
            return;

        // Checked here as well as in the permission: the permission only stops the page drawing
        // it, and a venue that hides the queue should not be putting it on the wire at all.
        if (!(await GuestPolicyAsync()).ShowQueue)
            return;

        // Resolved on use for the same reason playback is, and it costs nothing on a path that
        // runs when the queue moves rather than per frame.
        var library = _services.GetService<IMediaService>();
        if (library is null)
            return;

        var queued = await _performances.ReadQueuedAsync();
        var songs = new Dictionary<Guid, Media>();

        foreach (var mediaId in queued.Select(performance => performance.MediaId).Distinct())
            if (await library.ReadAsync(mediaId) is { } media)
                songs[mediaId] = media;

        await remote.PublishQueueAsync(UpcomingQueue(_queue.Users, queued, songs), cancellationToken);
    }

    /// <summary>The queue in the order the room will hear it: one song per singer, then round
    /// again, which is what dropping a finished singer to the back of the rotation produces.</summary>
    /// <remarks>Accurate for the next song of each singer and a good faith projection after that:
    /// a rotation modifier such as a VIP tier or a cooldown reorders the singers between laps, so
    /// which songs are coming is right where the exact order of the later ones may not be. The
    /// song being sung is still queued until it ends, so it leads the list, which is where the
    /// page expects the current item.</remarks>
    internal static PlayerQueue UpcomingQueue(
        IReadOnlyList<KHostUser> singers,
        IReadOnlyList<Performance> queued,
        IReadOnlyDictionary<Guid, Media> songs)
    {
        // QueuePosition counts within one singer, so it orders a singer's own songs and says
        // nothing across singers. The rotation is what interleaves them.
        var perSinger = singers
            .Select(singer => queued
                .Where(performance => performance.SingerId == singer.Id)
                .OrderBy(performance => performance.QueuePosition ?? int.MaxValue)
                .ToList())
            .ToList();

        var queue = new PlayerQueue();
        var deepest = perSinger.Count == 0 ? 0 : perSinger.Max(list => list.Count);

        for (var lap = 0; lap < deepest; lap++)
            foreach (var list in perSinger.Where(list => lap < list.Count))
                queue.Items.Add(QueueItemFor(list[lap], songs));

        return queue;
    }

    /// <summary>A queued turn as the page draws it.</summary>
    /// <remarks>The identifier is never absent and never null: the page converts it without a
    /// guard and builds the whole array before assigning it, so one bad row discards the entire
    /// refresh rather than itself. Every KHost song is unidentified as far as KaraFun is
    /// concerned, making that the normal case here. Index is left unset, the page ordering by
    /// array position.</remarks>
    private static PlayerQueueItem QueueItemFor(Performance performance, IReadOnlyDictionary<Guid, Media> songs)
    {
        songs.TryGetValue(performance.MediaId, out var media);

        return new PlayerQueueItem
        {
            Id = performance.Id.ToString(),
            Song = new PlayerQueueSong
            {
                Id = new PlayerSongIdentifier { Type = PlayerSongIdentifier.Types.TypeEnum.Unknown, Id = 0 },
                Title = media?.Title ?? string.Empty,
                Artist = media?.Artist ?? string.Empty,
            },
        };
    }

    private const string RoomUnreachableWarning =
        "KaraFun could not be reached, so the guest room is closed; it reopens on its own when the connection returns.";

    private void NoteRoomWarning(string text)
    {
        var id = _plugin.AddWarning(text);
        var previous = Interlocked.Exchange(ref _roomWarning, id);

        if (previous != 0 && previous != id)
            _plugin.ClearWarning(previous);
    }

    private void ClearRoomWarning()
    {
        if (Interlocked.Exchange(ref _roomWarning, 0) is not 0 and var id)
            _plugin.ClearWarning(id);
    }

    private Task OnRoomAuthenticatedAsync(CancellationToken cancellationToken)
    {
        // An authenticated socket proves the room is reachable again; a failed mint raises its own.
        ClearRoomWarning();

        return EnsureScreenCodeAsync(cancellationToken);
    }

    /// <summary>Called on every socket authenticate, the cheapest proof KaraFun is reachable, so a
    /// venue that started during an outage gets its code the moment the outage ends.</summary>
    private Task EnsureScreenCodeAsync(CancellationToken cancellationToken = default)
        => _screenCodeShown ? Task.CompletedTask : SyncScreenCodeAsync(cancellationToken);

    /// <summary>Guarded, since the socket reports the room is not live on every retry pass and the
    /// screens do not need telling every half minute.</summary>
    private async Task HideScreenCodeAsync()
    {
        Interlocked.Increment(ref _roomEpoch);

        await _screenCode.WaitAsync();
        try
        {
            if (!_screenCodeShown)
                return;

            // Inside the lock, not after: releasing first lets a mint draw in the gap and set the
            // flag, and this call then wipes the code it just drew while the flag still says one is up.
            await _plugin.UnregisterQrCodeAsync();

            _screenCodeShown = false;
        }
        finally
        {
            _screenCode.Release();
        }
    }

    /// <summary>Mints a channel and draws it. Taking one down is <see cref="HideScreenCodeAsync"/>.</summary>
    private async Task SyncScreenCodeAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionKey is not { Length: > 0 } sessionKey)
            return;

        // Read before the round trip below, and compared against after it.
        long epoch = Interlocked.Read(ref _roomEpoch);

        string payload;

        try
        {
            var (remote, env) = await _api.RemoteAsync(sessionKey, ct: cancellationToken);

            if (remote?.Payload is not { Length: > 0 } opened)
            {
                // The endpoint's shape is inferred, so a failure here is as likely our request as
                // the account's plan; a code that opens nothing is worse than no code.
                _plugin.AddWarning(
                    $"KaraFun would not open a remote session, so no QR code is on screen ({env.ErrorCode ?? "no url returned"}).");

                // Guarded: authenticating retries on every reconnect, and a flash for each would spam.
                if (Interlocked.Exchange(ref _roomFailureFlashed, 1) == 0)
                    _flash.Show("KaraFun: Could not open the guest room — no QR code is on screen.", FlashType.Warning);

                return;
            }

            payload = opened;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open a KaraFun remote session");

            NoteRoomWarning(KaraFunFailure.IsNetwork(ex, cancellationToken)
                ? RoomUnreachableWarning
                : $"KaraFun could not open a remote session, so no QR code is on screen: {ex.Message}");

            // Minting runs only on authenticate, so without this a blip leaves no code until the
            // socket next reconnects.
            RetryScreenCodeLater(epoch);
            return;
        }

        // Logged as well as drawn: the link is what the room is invited to scan, so it is no secret,
        // and a host whose screen is off has no other way to reach it.
        _logger.LogInformation("KaraFun remote sign-up is open at {Url}", payload);

        await NoteRoomAsync(payload);

        // Minting is a REST round trip the socket can drop during, so the connection is re-read
        // here under the lock, so a withdrawal cannot land between the check and the registration.
        await _screenCode.WaitAsync(cancellationToken);
        try
        {
            if (_remote is not { IsConnected: true } || Interlocked.Read(ref _roomEpoch) != epoch)
                return;

            // Offered, not shown: the venue names which source it takes, and this may not be it.
            // Sign-ups only: the room shows the queue too, but only while it takes sign-ups, so a
            // code kept up for the queue alone would open on a closed room.
            await _plugin.RegisterQrCodeAsync(payload, caption: null, QrCodeFeatures.SongEnqueue, cancellationToken);

            _screenCodeShown = true;
            ClearRoomWarning();
            // A fresh failure after this one is a new cause, worth its own flash.
            Volatile.Write(ref _roomFailureFlashed, 0);
        }
        finally
        {
            _screenCode.Release();
        }
    }

    private void RetryScreenCodeLater(long epoch)
    {
        if (Interlocked.Exchange(ref _screenCodeRetrying, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ScreenCodeRetryDelay);

                // Cleared before the attempt, so one that fails can schedule the next.
                Volatile.Write(ref _screenCodeRetrying, 0);

                // A drop since the failure means the next authenticate mints for the new connection.
                if (_screenCodeShown || _remote is not { IsConnected: true } || Interlocked.Read(ref _roomEpoch) != epoch)
                    return;

                await SyncScreenCodeAsync();
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _screenCodeRetrying, 0);
                _logger.LogWarning(ex, "Retrying the KaraFun guest room's QR code failed");
            }
        });
    }

    /// <summary>How a session survives a night: nothing renews the key, so a dead one is answered
    /// with another <c>session/open</c>, never a sign-in prompt. Held by the session lock's caller.</summary>
    private async Task<bool> ReopenSessionAsync(CancellationToken cancellationToken)
    {
        if (_passwordHash is not { Length: > 0 } passwordHash || _login is not { Length: > 0 } login)
            return false;

        try
        {
            var (session, env) = await _api.OpenSessionAsync(
                login, password: null, "KHost",
                passwordHash: passwordHash,
                // The key that just expired. The app's own parameter list calls it continuity, so
                // the server is told this device is carrying on rather than arriving fresh.
                lastSessionKey: _sessionKey,
                ct: cancellationToken);

            if (session?.Key is not { Length: > 0 } key)
            {
                // A refused credential is not a stale session: the password changed, or the account
                // did. Saying so beats a console that silently stops finding songs.
                _plugin.AddWarning($"KaraFun would not renew the session ({env.ErrorCode ?? "no key returned"}). Sign in again.");

                // No per-retry guard needed: a failure here always clears the stored credential
                // below, so nothing calls this again with the same cause without a fresh sign-in.
                _flash.Show("KaraFun: Signed out — sign in again to keep queueing songs.", FlashType.Warning);

                return false;
            }

            await RememberSessionAsync(key, session.Login ?? login, passwordHash);
            _logger.LogInformation("Renewed the KaraFun session for {Login}", login);

            return true;
        }
        catch (Exception ex)
        {
            // Wifi, not credentials. Keeping what we have lets the next attempt try again rather
            // than making a venue sign in because a router blinked.
            _logger.LogWarning(ex, "Could not renew the KaraFun session");
            return true;
        }
    }

    /// <summary>Held by the caller of the session lock.</summary>
    private async Task RememberSessionAsync(string key, string? login, string? passwordHash)
    {
        _sessionKey = key;
        _login = login;
        _passwordHash = passwordHash ?? _passwordHash;
        _validated = true;

        try
        {
            await _plugin.SetSecretAsync(SessionSecretKey,
                JsonSerializer.Serialize(new StoredSession(key, login, _passwordHash)));
        }
        catch (Exception ex)
        {
            // The session above already works; only keeping it failed. Left to the callers, this read
            // as a refused password on sign-in and as a network blip on renewal.
            _plugin.AddWarning($"Signed in to KaraFun, but could not save it: {ex.Message}");
        }
    }

    /// <summary>Held by the caller of the session lock. Drops the credential too, since a stored
    /// one would sign straight back in.</summary>
    private async Task ForgetSessionAsync()
    {
        _sessionKey = null;
        _login = null;
        _passwordHash = null;

        await _plugin.SetSecretAsync(SessionSecretKey, null);
    }

    private async Task OpenSessionAsync()
    {
        await _session.WaitAsync();
        try
        {
            await OpenSessionCoreAsync();
        }
        finally
        {
            _session.Release();
        }

        // Outside the lock: this reaches the network and the screens, and holding the session
        // across either stalls every other caller waiting on it.
        await SyncRemoteAsync();
    }

    /// <summary>The raw password reaches the hash call and nothing else; the key, login and hash
    /// are kept, so a venue that restarts mid-show needs nobody standing at the console.</summary>
    private async Task OpenSessionCoreAsync()
    {
        // Whoever was ahead in the queue for the prompt may already have signed in.
        if (_sessionKey is { Length: > 0 })
            return;

        var credentials = await _dispatcher.RequestAsync(new TextPromptRequest(
            "Sign in to KaraFun",
            "Remembered on this machine until you sign out, so the room stays reachable across a "
                + "restart without anyone here to sign in again.",
            [
                new TextPromptField("login", "Email"),
                new TextPromptField("password", "Password", Secret: true),
            ]));

        if (credentials is null)
            return;

        try
        {
            // Hashed here rather than inside the call, because this is the value that gets kept:
            // the typed password reaches nothing but this line.
            var passwordHash = PasswordService.Hash(credentials["password"]);

            var (session, env) = await _api.OpenSessionAsync(
                credentials["login"], password: null, "KHost", passwordHash: passwordHash);

            if (session?.Key is not { Length: > 0 } key)
                throw new InvalidOperationException(env.ErrorCode ?? "session/open did not return a key");

            RememberImageTemplate(session);

            await RememberSessionAsync(key, session.Login ?? credentials["login"], passwordHash);

        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not sign in to KaraFun");

            _plugin.AddWarning(KaraFunFailure.IsNetwork(ex) ? KaraFunFailure.Unreachable : $"Could not sign in to KaraFun: {ex.Message}");
            _flash.Show(KaraFunFailure.Describe("sign-in", ex), FlashType.Warning);
        }
    }

    /// <summary>One toggle: opens a session when there is none, drops the one in memory when there
    /// is. Dropping it host-side is all a sign-out needs; the session goes unused until it lapses.</summary>
    public async Task InvokeButtonAsync(string key, CancellationToken cancellationToken = default)
    {
        if (key != SessionButtonKey)
            return;

        await EnsureSessionAsync(cancellationToken);

        if (_sessionKey is { Length: > 0 })
        {
            await _session.WaitAsync(cancellationToken);
            try
            {
                await ForgetSessionAsync();
            }
            finally
            {
                _session.Release();
            }

            // The room's way in goes with the account: a channel left running on a dead session
            // keeps queueing songs the gate will then refuse to play.
            await SyncRemoteAsync(cancellationToken);
        }
        else
        {
            await OpenSessionAsync();
        }
    }

    public PluginButtonState DescribeButton(string key)
        => key != SessionButtonKey
            ? PluginButtonState.Default
            : new PluginButtonState { Label = SessionButtonLabel };

    /// <summary>Names the account so a venue running two of them can see which one is open.</summary>
    private string SessionButtonLabel
        => _sessionKey is not { Length: > 0 } ? "Sign in"
         : _login is { Length: > 0 } login ? $"Sign out ({login})"
         : "Sign out";

    // This plugin's own assembly name; the same string reaches the file as khost_provider.
    public string ProviderId => KitFiles.ProviderId;

    /// <summary>How the host knows whose song it is, and the only way it needs to: nothing but
    /// KaraFun produces a <c>.kit</c>, so the extension is the whole answer. Free, where reading a
    /// marker out of the container costs a full read of it.</summary>
    public bool Claims(string filePath) => KitFiles.IsAKit(filePath);

    /// <summary>One answer for queueing, rendering and playing: all three turn on a live account.
    /// </summary>
    /// <remarks>Checked against the in-memory session rather than the server, since this runs on
    /// every load as well as every enqueue and a live show cannot wait on the network for it.
    /// </remarks>
    public async Task<PlaybackGateResult> CanAsync(
        MediaAction action, Media media, CancellationToken cancellationToken = default)
    {
        await EnsureSessionAsync(cancellationToken);

        if (_sessionKey is { Length: > 0 })
            return PlaybackGateResult.Ok;

        // Nothing raises Render since the host dropped its render path, but the verdict is kept:
        // it runs with nobody watching a dialog, so it is refused rather than turned into a prompt.
        if (action is MediaAction.Render)
            return new PlaybackGateResult(false, "Sign in to KaraFun to use this track.");

        // A refusal alone reads as the track being broken, so asking here turns the blocked load
        // into the sign-in it was waiting for; the gate runs before any playback lock is taken.
        await OpenSessionAsync();

        return _sessionKey is { Length: > 0 }
            ? PlaybackGateResult.Ok
            : new PlaybackGateResult(false, $"Sign in to KaraFun to {Verb(action)} this track.");
    }

    private static string Verb(MediaAction action) => action switch
    {
        MediaAction.Queue => "queue",
        MediaAction.Render => "use",
        _ => "play",
    };

    private Task DownloadAndEnqueueAsync(MediaSearchEntity entity) => DownloadAndEnqueueAsync(entity, singerId: null, sungAs: null, EnqueueOrigin.Host);

    /// <summary><paramref name="singerId"/> null means the singer the console has selected, a host
    /// clicking Enqueue. A guest's pick names its own singer, who may not be at the microphone.</summary>
    private async Task DownloadAndEnqueueAsync(MediaSearchEntity entity, Guid? singerId, string? sungAs, EnqueueOrigin origin)
    {
        // Read at the click: the import can take long enough for the host to select someone else.
        singerId ??= _queue.SelectedUserId;

        bool community = entity.ForeignKey.StartsWith(CommunityForeignKeyPrefix, StringComparison.Ordinal);
        if (!int.TryParse(community ? entity.ForeignKey[CommunityForeignKeyPrefix.Length..] : entity.ForeignKey, out var songId))
            return;

        // A song row only ever reaches the host once SearchAsync already required a session, so
        // its absence here means something cleared it between the search and this click.
        if (_sessionKey is not { Length: > 0 } sessionKey)
        {
            _plugin.AddWarning("Signed out of KaraFun — search again to sign back in.");
            _flash.Show("KaraFun: Signed out — search again to sign back in before enqueuing.", FlashType.Warning);
            return;
        }

        var directory = Path.Combine(_media.MediaDirectory, "karafun");
        Directory.CreateDirectory(directory);

        // The downloaded kit is the row: no standard, openable copy of the licensed content sits on
        // disk at rest, and its stems go to the host as they are at play time.
        var baseName = community ? $"community-{songId}" : $"{songId}";
        var destination = Path.Combine(directory, baseName + KitFiles.KitExtension);

        var request = new MediaImportRequest
        {
            FilePath = destination,
            Title = entity.Title,
            Artist = entity.Artist,
            Duration = entity.Duration,
            Source = DisplayName,
            // The foreign key already says catalog or community, which is all a re-download needs.
            SourceKey = entity.ForeignKey,
            IsSingleUse = Settings.SingleUse,
        };

        if (File.Exists(destination))
        {
            var readyId = await _media.ImportAsync(request);
            await EnqueueAsync(readyId, entity.Title, singerId, sungAs, origin);
            return;
        }

        var started = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = _downloadsInFlight.GetOrAdd(entity.ForeignKey, started.Task);

        if (running != started.Task)
        {
            await EnqueueBehindRunningDownloadAsync(running, entity.Title, singerId, sungAs, origin);
            return;
        }

        try
        {
            ImportTicket ticket;
            try
            {
                ticket = await _media.BeginImportAsync(request);
                started.TrySetResult(ticket.MediaId);
            }
            finally
            {
                // Canceled rather than faulted: a pick waiting on this has nothing to add to the
                // failure this one reports, and a fault nobody awaits is logged as unobserved.
                started.TrySetCanceled();
            }

            await EnqueueAsync(ticket.MediaId, entity.Title, singerId, sungAs, origin);

            await DownloadKitAsync(ticket, sessionKey, songId, community, destination, entity.Title);
        }
        finally
        {
            _downloadsInFlight.TryRemove(entity.ForeignKey, out _);
        }
    }

    /// <summary>Everything between a begun import and its settle, shared by a fresh download and a
    /// refetch: both arrive holding a ticket for a row in Downloading and settle it exactly once.
    /// </summary>
    private async Task DownloadKitAsync(
        ImportTicket ticket, string sessionKey, int songId, bool community, string destination, string title)
    {
        // Fired when the host cancels the download from the Downloads page, when the queued
        // performance is dequeued, and on shutdown.
        var cancellation = ticket.Cancellation;

        try
        {
            // The host can cancel between the row being created and this starting, a dequeue
            // of a pick that was still arriving, leaving nothing here to fetch.
            cancellation.ThrowIfCancellationRequested();

            void OnBytes(long received, long? total) => _ = ReportBytesSafelyAsync(ticket.MediaId, received, total);

            var download = community
                ? await _api.DownloadCommunitySongAsync(sessionKey, songId, ct: cancellation, onProgress: OnBytes)
                : await _api.DownloadSongAsync(sessionKey, songId, ct: cancellation, onProgress: OnBytes);

            if (download is null)
            {
                await _media.FailImportAsync(ticket.MediaId, "KaraFun did not return a download for this song");
                _flash.Show($"KaraFun: Could not download '{title}' — no file was returned.", FlashType.Warning);
                return;
            }

            if (!download.Md5Ok)
            {
                await _media.FailImportAsync(ticket.MediaId, "the download's checksum did not match");
                _flash.Show($"KaraFun: Could not download '{title}' — the download was corrupted.", FlashType.Warning);
                return;
            }

            // Phase two: the bytes are in and verified, and everything past here is CPU, the
            // long half the host's Downloads progress reports.
            await _media.BeginProcessingAsync(ticket.MediaId);

            // Demuxed only to validate the download, never to convert it: the kit itself is
            // what gets written to disk, so the shape it plays in is decided at play time.
            var kit = KitContainer.Read(download.Data).Demux();
            var timing = kit.FirstOrDefault(stream => stream.Kind == "xml");

            if (timing is null)
            {
                await _media.FailImportAsync(ticket.MediaId, "the download carried no timing/lyric stream");
                _flash.Show($"KaraFun: Could not download '{title}' — it had no lyrics track.", FlashType.Warning);
                return;
            }

            await File.WriteAllBytesAsync(destination, download.Data, cancellation);

            await _media.CompleteImportAsync(ticket.MediaId);
        }
        // A cancel only when the ticket's own token says so: HttpClient reports its timeout as
        // a TaskCanceledException too, and that is a failure the host must hear about.
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            DeletePartialFiles(destination);

            // The host reads the destination itself and keeps the row as Broken if the delete
            // above did not take, so a cancelled song leaves the library as it found it.
            await _media.DiscardImportAsync(ticket.MediaId);

            _logger.LogInformation("Cancelled the KaraFun download of song {SongId}", songId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download KaraFun song {SongId}", songId);

            // A half-written file outlives the failure otherwise, and the next attempt takes
            // the File.Exists path above and imports the truncated file as Ready.
            DeletePartialFiles(destination);

            // The message, not the type or the trace: this is read on the Downloads page at a
            // glance.
            await _media.FailImportAsync(ticket.MediaId, ex.Message);
            _flash.Show(KaraFunFailure.Describe($"download of '{title}'", ex, cancellation), FlashType.Warning);
        }
    }

    /// <summary>Answers from the row alone: nothing here may touch the disk or the network.</summary>
    public bool CanRefetch(Media media) => TryParseSourceKey(media, out _, out _);

    private bool TryParseSourceKey(Media media, out int songId, out bool community)
    {
        songId = 0;
        community = false;

        if (!string.Equals(media.Source, DisplayName, StringComparison.Ordinal))
            return false;

        community = media.SourceKey.StartsWith(CommunityForeignKeyPrefix, StringComparison.Ordinal);
        return int.TryParse(community ? media.SourceKey[CommunityForeignKeyPrefix.Length..] : media.SourceKey, out songId);
    }

    /// <summary>The host has already moved the row to Downloading, so a refusal here must settle the
    /// ticket rather than return.</summary>
    public async Task RefetchAsync(Media media, ImportTicket ticket)
    {
        if (!TryParseSourceKey(media, out var songId, out var community))
        {
            await _media.FailImportAsync(ticket.MediaId, "this song has no KaraFun id to download again");
            return;
        }

        // Not given the ticket's token: a cancel is settled as a discard by the download below, and
        // an exception from here would settle the row as failed instead.
        await EnsureSessionAsync();

        if (_sessionKey is not { Length: > 0 } sessionKey)
        {
            await _media.FailImportAsync(ticket.MediaId, "Sign in to KaraFun to download this song again.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(media.FilePath)!);
        await DownloadKitAsync(ticket, sessionKey, songId, community, media.FilePath, media.Title);
    }

    private async Task EnqueueBehindRunningDownloadAsync(Task<Guid> running, string title, Guid? singerId, string? sungAs, EnqueueOrigin origin)
    {
        Guid mediaId;

        try
        {
            mediaId = await running;
        }
        catch (OperationCanceledException)
        {
            // The first pick's import never began, and it reports why; this one has no row to join.
            _logger.LogWarning("Not queueing '{Title}': the download it was waiting on never started", title);
            return;
        }

        await EnqueueAsync(mediaId, title, singerId, sungAs, origin);
    }

    private async Task EnqueueAsync(Guid mediaId, string title, Guid? singerId, string? sungAs, EnqueueOrigin origin)
    {
        if ((singerId ?? _queue.SelectedUserId) is not { } singer)
        {
            _logger.LogWarning("Cannot enqueue: no singer selected");
            return;
        }

        var result = await _performances.TryCreateAndEnqueueAsync(new()
        {
            MediaId = mediaId,
            SingerId = singer,
            // This queue is song-first, so a guest's typed name belongs to the song rather than the
            // account it reached; null falls back to the singer's own name.
            SungAs = sungAs,
            CreatedDate = DateTime.UtcNow,
        }, origin);

        // The host refuses these without a word, and the phone was told the pick went through, so
        // this is the only place anyone learns the song is not coming.
        if (result is { Type: EnqueueResultType.AlreadyQueued or EnqueueResultType.QueuedForAnotherSinger, Conflict: { } conflict })
            _flash.Show(await DescribeBlockedSignupAsync(result.Type, title, singer, sungAs, conflict), FlashType.Warning);
        else if (result is { Type: EnqueueResultType.SingerAtLimit })
            _flash.Show(await DescribeSingerAtLimitAsync(title, singer, sungAs), FlashType.Warning);
    }

    private async Task<string> DescribeSingerAtLimitAsync(string title, Guid singer, string? sungAs)
    {
        var who = await NameOfAsync(singer, sungAs) ?? "A singer";

        return await RemoteSongLimitAsync() is { } limit
            ? $"KaraFun: {who} tried to sign up for '{title}', but already has {limit} {(limit == 1 ? "song" : "songs")} queued, the most this venue allows."
            : $"KaraFun: {who} tried to sign up for '{title}', but already has as many songs queued as this venue allows.";
    }

    /// <summary>The venue's limit as the host applied it, or null when it cannot be read.</summary>
    private async Task<int?> RemoteSongLimitAsync()
    {
        try
        {
            return _services.GetService<IVenuesService>() is { } venues
                && await venues.ReadSelectedVenueAsync() is { Settings.RemoteSongLimit: > 0 and var limit }
                ? limit
                : null;
        }
        catch (Exception ex)
        {
            // Only the wording depends on it: the refusal has already happened.
            _logger.LogDebug(ex, "KaraFun: could not read the venue's song limit");
            return null;
        }
    }

    private async Task<string> DescribeBlockedSignupAsync(EnqueueResultType type, string title, Guid singer, string? sungAs, Performance conflict)
    {
        var who = await NameOfAsync(singer, sungAs) ?? "A singer";

        if (type == EnqueueResultType.AlreadyQueued)
            return $"KaraFun: {who} tried to sign up for '{title}', but they already have it queued.";

        var holder = await NameOfAsync(conflict.SingerId, conflict.SungAs) ?? "another singer";

        return $"KaraFun: {who} tried to sign up for '{title}', but it's already queued for {holder}.";
    }

    /// <summary>The name a song was signed up under, else the singer's own.</summary>
    private async Task<string?> NameOfAsync(Guid singerId, string? sungAs)
        => !string.IsNullOrWhiteSpace(sungAs) ? sungAs : (await _users.ReadAsync(singerId))?.Name;

    /// <summary>The phone is asked first, the name second: a guest can spell their name differently
    /// each pick while the id does not move, and name alone puts three rows on the list for one.</summary>
    private async Task<Guid?> ResolveRemoteSingerAsync(string name, string remoteId)
    {
        if (remoteId.Length > 0 && await _users.ReadByForeignKeyAsync(SourceName, remoteId) is { } known)
            return await QueuedAsync(known);

        name = name.Trim();

        if (name.Length == 0)
            return null;

        // Looked up, not searched: a search matches on contains and answers with a page, so an
        // exact name can sit past the end of it and read as nobody. This creates on a miss.
        var singer = await _users.FindByNameAsync(name)
            ?? await _users.CreateAsync(new KHostUser { Name = name });

        await RememberRemoteAsync(singer.Id, remoteId);

        return await QueuedAsync(singer);
    }

    /// <summary>Ties the phone to the resolved singer so their next pick skips the name entirely.
    /// Ephemeral, since a KaraFun remote id belongs to a pairing, not the same id tomorrow.</summary>
    private async Task RememberRemoteAsync(Guid singerId, string remoteId)
    {
        if (remoteId.Length == 0)
            return;

        try
        {
            await _users.AddForeignKeyAsync(singerId, SourceName, remoteId, isEphemeral: true);
        }
        catch (KHostException ex)
        {
            // Somebody else already holds it: the room's guest list tied it between the lookup and
            // here. Only the shortcut for their next pick is lost.
            _logger.LogDebug(ex, "Could not tie KaraFun remote {RemoteId} to singer {SingerId}", remoteId, singerId);
        }
    }

    /// <summary>A singer the queue does not hold would read as a song belonging to nobody.</summary>
    private async Task<Guid> QueuedAsync(KHostUser singer)
    {
        if (!_queue.Users.Any(user => user.Id == singer.Id))
            await _queue.AddUserAsync(singer.Id);

        return singer.Id;
    }

    /// <summary>Drops every phone this plugin tied to a singer (durable keys are not ours to touch),
    /// so the next holder of a number does not inherit its former guest's picks. Nothing else
    /// clears them: the host keeps a plugin's ephemeral keys until the plugin drops them.</summary>
    private async Task ForgetEveryRemoteAsync()
    {
        await _ties.WaitAsync();
        try
        {
            await ForgetEveryRemoteCoreAsync();
        }
        finally
        {
            _ties.Release();
        }
    }

    /// <summary>Held by the caller of <see cref="_ties"/>.</summary>
    private async Task ForgetEveryRemoteCoreAsync()
    {
        _guestNames.Clear();

        try
        {
            if (await _users.DeleteEphemeralForeignKeysAsync(SourceName) is > 0 and var dropped)
                _logger.LogInformation("KaraFun remote: dropped {Count} phone tie(s) from the last room", dropped);
        }
        catch (Exception ex)
        {
            // Signing out must finish either way. The room is forgotten below regardless, so the
            // next room this plugin sees counts as new and tries the clear again.
            _logger.LogWarning(ex, "Could not clear the KaraFun remote keys");
        }

        try
        {
            await _cache.SaveAsync<string?>(RoomCacheKey, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not forget the KaraFun room");
        }
    }

    /// <summary>Clears the ties when the room is not the one they were made in, then ties whoever
    /// is in this one already.</summary>
    /// <remarks>A room is its sign-up link. A restart or a reconnect comes back to the same link,
    /// and its guests are the same people on the same remoteIds; a session renewal has been seen to
    /// mint a new link, and a remoteId from the old room may name somebody else in the new one.
    /// No room on record (first run, or after a sign-out) counts as new.</remarks>
    private async Task NoteRoomAsync(string room)
    {
        await _ties.WaitAsync();
        try
        {
            string? last = null;

            try
            {
                last = await _cache.LoadAsync<string>(RoomCacheKey);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read the last KaraFun room");
            }

            if (string.Equals(last, room, StringComparison.Ordinal))
                return;

            _logger.LogInformation("KaraFun remote: {Room} is a new room (was {Last}); dropping the last room's phone ties",
                room, last ?? "none");

            await ForgetEveryRemoteCoreAsync();
            await _cache.SaveAsync(RoomCacheKey, room);
        }
        finally
        {
            _ties.Release();
        }

        // The guest list may have been tied before the clear above, so it is tied again after it.
        if (_remote?.Guests is { Count: > 0 } guests)
            await TieGuestsAsync(guests, CancellationToken.None);
    }

    /// <summary>Ties each guest in the room back to their singer, without waiting for a pick.</summary>
    private async Task TieGuestsAsync(IReadOnlyList<RemoteUser> guests, CancellationToken cancellationToken)
    {
        await _ties.WaitAsync(cancellationToken);
        try
        {
            foreach (var guest in guests)
            {
                try
                {
                    await TieGuestAsync(guest.RemoteId, guest.Name);
                }
                catch (Exception ex)
                {
                    // One guest's tie failing must not keep the rest of the room untied.
                    _logger.LogWarning(ex, "KaraFun remote: could not tie '{Guest}' back to their singer", guest.Name);
                }
            }
        }
        finally
        {
            _ties.Release();
        }
    }

    /// <summary>A remoteId and a nickname together are the guest. The same pair keeps (or regains)
    /// its tie; the same remoteId under another name is somebody else, so the tie moves.</summary>
    /// <remarks>Never creates a singer: joining a room is not asking to sing, and the first pick
    /// creates one by name the way it always has. Held by the caller of <see cref="_ties"/>.</remarks>
    private async Task TieGuestAsync(string remoteId, string name)
    {
        name = name?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(remoteId) || name.Length == 0)
            return;

        if (await _users.ReadByForeignKeyAsync(SourceName, remoteId) is { } holder)
        {
            // The nickname seen this run when there is one; after a restart only the singer's name
            // is left to compare, which with AskOptions off is the nickname their first pick carried.
            var knownAs = _guestNames.TryGetValue(remoteId, out var seen) ? seen : holder.Name;

            _guestNames[remoteId] = name;

            if (string.Equals(knownAs?.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("KaraFun remote: '{Guest}' is still tied to their singer", name);
                return;
            }

            await _users.RemoveForeignKeyAsync(holder.Id, SourceName, remoteId);
            _logger.LogInformation(
                "KaraFun remote: remote {RemoteId} came back as '{Guest}', not '{Previous}'; untied it from '{Singer}'",
                remoteId, name, knownAs, holder.Name);
        }
        else
        {
            _guestNames[remoteId] = name;
        }

        if (await _users.FindByNameAsync(name) is not { } singer)
            return;

        await RememberRemoteAsync(singer.Id, remoteId);
        _logger.LogInformation("KaraFun remote: tied '{Guest}' back to their singer without a pick", name);
    }

    /// <summary>A failed download leaves whatever it had already written at the destination, which
    /// the next attempt's <c>File.Exists</c> check must not trust. Best-effort: a file still held
    /// open matters less than the settle that follows.</summary>
    private void DeletePartialFiles(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete the partial file at {Path}", path);
            }
        }
    }

    private async Task ReportBytesSafelyAsync(Guid mediaId, long received, long? total)
    {
        try
        {
            await _media.ReportDownloadProgressAsync(mediaId, received, total);
        }
        catch
        {
            // Best-effort UI update only; nothing here is worth failing the download over.
        }
    }

    private sealed class RemotePickHistory
    {
        public Queue<DateTimeOffset> Arrivals { get; } = new();

        /// <summary>Whether a pick has been refused since the last one admitted.</summary>
        public bool Refusing { get; set; }
    }
}
