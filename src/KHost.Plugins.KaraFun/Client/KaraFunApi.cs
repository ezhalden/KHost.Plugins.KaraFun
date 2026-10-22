using System.Diagnostics.CodeAnalysis;
namespace KHost.Plugins.KaraFun.Client;

/// <summary>Client for the KaraFun REST API. Paths and signing are confirmed live, not assumed.</summary>
/// <remarks>Only requests carrying a session key are signed; the open-session request is not.</remarks>
public sealed class KaraFunApi
{
    public const string ProdHost = "api.karafun.com";

    private readonly HttpClient _http;
    private readonly TimeSpan _bodyIdleTimeout;

    // Where a kit is fetched from; null asks the factory. A seam so a test can serve the bytes.
    private readonly HttpClient? _cdn;
    private readonly IHttpClientFactory? _factory;

    /// <summary>Named clients the host's factory is asked for, one per purpose.</summary>
    internal const string ApiClientName = "karafun-api";
    internal const string CdnClientName = "karafun-cdn";

    /// <summary>HttpClient.Timeout stops applying once headers arrive, so a kit body that stalls
    /// mid-transfer would otherwise wait on the read until something cancels it. Not a setting:
    /// nobody has a reason to tune it, and a progress report resets it.</summary>
    private static readonly TimeSpan DefaultBodyIdleTimeout = TimeSpan.FromSeconds(30);

    public string Host { get; }
    public string AppVersion { get; }
    public string Protocol { get; }
    public string OsVersion { get; }
    public string Locale { get; }
    public ClientProfile Profile { get; }

    /// <summary>Build number appended to client_version, e.g. "2.17.2.3426" (CFBundleVersion).</summary>
    public string Build { get; set; } = "3426";
    public string ClientVersion => $"{AppVersion}.{Build}";

    public KaraFunApi(
        ClientProfile profile,
        string host = ProdHost,
        string appVersion = "2.17.2",
        string protocol = "1", // confirmed literal in the open-session funclet
        string? osVersion = null,
        string locale = "en-US",
        HttpClient? http = null,
        TimeSpan? bodyIdleTimeout = null,
        HttpClient? cdn = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        _cdn = cdn;
        _factory = httpClientFactory;
        _bodyIdleTimeout = bodyIdleTimeout ?? DefaultBodyIdleTimeout;
        Profile = profile;
        Host = host;
        AppVersion = appVersion;
        Protocol = protocol;
        OsVersion = osVersion ?? Environment.OSVersion.Version.ToString();
        Locale = locale;
        _http = http
            ?? httpClientFactory?.CreateClient(ApiClientName)
            ?? throw new ArgumentException("Either an HttpClient or an IHttpClientFactory is required.", nameof(http));
        // The app UA has two '/', which .NET's strict UA parser rejects; send it verbatim.
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", profile.UserAgent(appVersion));
    }

    /// <summary>Builds the exact request URL. Query keys are sorted and values percent-encoded.</summary>
    /// <remarks>The space must be %20; signing with '+' returns WRONG_SIGNATURE (confirmed live).</remarks>
    private string BuildUrl(string endpoint, IReadOnlyDictionary<string, string?> query)
    {
        var pairs = query.Where(kv => kv.Value is not null).OrderBy(kv => kv.Key, StringComparer.Ordinal);
        string qs = string.Join("&", pairs.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        return $"https://{Host}/{endpoint.Trim('/')}" + (qs.Length > 0 ? "?" + qs : "");
    }

    /// <summary>Signed GET. Unsigned when <paramref name="sessionKey"/> is null.</summary>
    private Task<(ApiEnvelope Env, string Raw)> GetAsync(
        string endpoint, IReadOnlyDictionary<string, string?> query, string? sessionKey = null,
        CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, endpoint, query, sessionKey, ct);

    private async Task<(ApiEnvelope Env, string Raw)> SendAsync(
        HttpMethod method, string endpoint, IReadOnlyDictionary<string, string?> query,
        string? sessionKey = null, CancellationToken ct = default)
    {
        var q = new Dictionary<string, string?>(query);
        if (sessionKey is not null) q["sk"] = sessionKey; // KFAPICoordinator.additionalQueryItems

        // One canonical URL string used verbatim for BOTH the request line and the signature.
        string url = BuildUrl(endpoint, q);
        var sig = RequestSigner.SignExact(url, sessionKey);

        using var req = new HttpRequestMessage(method, new Uri(url, UriKind.Absolute));
        if (sig is not null)
        {
            req.Headers.TryAddWithoutValidation("X-Request-Timestamp", sig.Timestamp);
            req.Headers.TryAddWithoutValidation("X-Request-Signature", sig.Value);
        }
        req.Headers.TryAddWithoutValidation("session-language", Locale);

        using var resp = await _http.SendAsync(req, ct);
        string raw = await resp.Content.ReadAsStringAsync(ct);

        // A non-XML body (an edge error page, a bad path) reads as an empty envelope, not a failed call.
        if (!ApiEnvelope.TryParse(raw, out var env))
            env = ApiEnvelope.Unreachable(endpoint, (int)resp.StatusCode);

        return (env, raw);
    }

    /// <summary>Opens a session (login) with POST <c>session/open</c>. Unsigned; no key exists yet.</summary>
    /// <param name="passwordHash">
    /// The already-hashed <c>pwd</c>, when the caller kept that instead of the password, so a
    /// session can reopen without asking a host to type anything. Wins over
    /// <paramref name="password"/> when both are given.
    /// </param>
    /// <param name="lastSessionKey">
    /// The key of the session being replaced (<c>last_ses</c>, "for continuity" in the app's own
    /// parameter list). Sent so a re-open reads to the server as this device carrying on rather
    /// than as a stranger arriving.
    /// </param>
    public async Task<(Session? Session, ApiEnvelope Env)> OpenSessionAsync(
        string? login = null, string? password = null, string? deviceName = null,
        string? passwordHash = null, string? lastSessionKey = null, CancellationToken ct = default)
    {
        // pwd is md5(substitute(password)); see PasswordService (confirmed live).
        string pwd = passwordHash ?? (password is null ? "" : PasswordService.Hash(password));
        var q = new Dictionary<string, string?>
        {
            ["key"]            = Profile.Key,
            ["client"]         = Profile.Id.ToString(),
            ["protocol"]       = Protocol,
            ["login"]          = login ?? "",
            ["os"]             = OsVersion,
            ["client_version"] = ClientVersion,
            ["pwd"]            = pwd,
            ["locale"]         = Locale,
            ["device_name"]    = deviceName ?? "",
        };

        // Omitted rather than sent empty on a first sign-in: there is no previous session to carry
        // on from, and an empty one is not the same statement as none.
        if (!string.IsNullOrEmpty(lastSessionKey))
            q["last_ses"] = lastSessionKey;

        var (env, _) = await SendAsync(HttpMethod.Post, "session/open", q, sessionKey: null, ct: ct);
        return (Session.From(env), env);
    }

    /// <summary>Opens (or re-claims) the remote-control session guests join to add songs.</summary>
    /// <remarks>It is a signed GET, not POST; re-asking returns the already-claimed channel.</remarks>
    public async Task<(Remote? Remote, ApiEnvelope Env)> RemoteAsync(
        string sessionKey, string? channel = null, bool force = false, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["client"] = Profile.Id.ToString(),
        };

        // A preference for which channel to be given, named as the app's own builder names it.
        if (!string.IsNullOrWhiteSpace(channel))
            query["pref"] = channel;

        if (force)
            query["force"] = "1";

        var (env, _) = await GetAsync("session/remote", query, sessionKey, ct);

        return (Remote.From(env), env);
    }

    /// <summary>Reads the session back; it is the only way to check a kept key is still good.</summary>
    /// <remarks>A dead key answers INVALID_SESSION rather than failing the transport.</remarks>
    public async Task<(Session? Session, ApiEnvelope Env)> SessionInfoAsync(
        string sessionKey, CancellationToken ct = default)
    {
        var (env, _) = await GetAsync("session/info", new Dictionary<string, string?>
        {
            ["client"] = Profile.Id.ToString(),
        }, sessionKey, ct);

        return (Session.From(env), env);
    }

    /// <summary>Searches the top catalog. Section is a path component, not a query param.</summary>
    /// <remarks>Returns ids only. Follow with <see cref="SongInfoAsync"/> for full records.</remarks>
    public Task<(ApiEnvelope Env, string Raw)> SearchAsync(
        string sessionKey, string query, string section = "top", int page = 1, CancellationToken ct = default)
        => GetAsync($"search/{section}", new Dictionary<string, string?>
        {
            ["query"]  = query,
            ["offset"] = page.ToString(),
        }, sessionKey, ct);

    /// <summary>Fetches full song records by id (<c>song/info?song=&lt;csv ids&gt;</c>).</summary>
    /// <remarks>Multiple-id response shape is assumed, never confirmed live.</remarks>
    public async Task<IReadOnlyList<CatalogSong>> SongInfoAsync(
        string sessionKey, IEnumerable<int> songIds, CancellationToken ct = default)
    {
        var (env, _) = await GetAsync("song/info", new Dictionary<string, string?>
        {
            ["song"] = string.Join(",", songIds),
        }, sessionKey, ct);

        return [.. env.Root.Elements("song")
            .Select(CatalogSong.From)
            .Where(song => song is not null)
            .Select(song => song!)];
    }

    /// <summary>Fetches full community-song records by id. A community id is not a catalog id.</summary>
    /// <remarks>Accepts community or song elements; the wrapper shape was never captured live.</remarks>
    public async Task<IReadOnlyList<CatalogSong>> CommunityInfoAsync(
        string sessionKey, IEnumerable<int> communityIds, CancellationToken ct = default)
    {
        var (env, _) = await GetAsync("community/info", new Dictionary<string, string?>
        {
            ["id"] = string.Join(",", communityIds),
        }, sessionKey, ct);

        return [.. env.Root.Elements()
            .Where(e => e.Name.LocalName is "community" or "song")
            .Select(CatalogSong.From)
            .Where(song => song is not null)
            .Select(song => song!)];
    }

    public sealed record KitDownload(string StreamUrl, string Md5, long FileSize, byte[] Data, bool Md5Ok);

    /// <summary>Downloads a song: song/request, GET the .kit from the CDN, verify its md5.</summary>
    /// <remarks>Fetches the container only; it does not decode the media payloads inside.</remarks>
    public Task<KitDownload?> DownloadSongAsync(string sessionKey, int songId,
        string offline = "play", HttpClient? cdn = null, Action<long, long?>? onProgress = null,
        CancellationToken ct = default)
        => DownloadKitAsync("song/request", "song", songId, sessionKey, offline, cdn, onProgress, ct);

    /// <summary>The community equivalent of <see cref="DownloadSongAsync"/> (community/request).</summary>
    /// <remarks>The stream URL shape was never captured live; the response url is used as given.</remarks>
    public Task<KitDownload?> DownloadCommunitySongAsync(string sessionKey, int communityId,
        string offline = "play", HttpClient? cdn = null, Action<long, long?>? onProgress = null,
        CancellationToken ct = default)
        => DownloadKitAsync("community/request", "id", communityId, sessionKey, offline, cdn, onProgress, ct);

    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification =
            "Not a security control: the service publishes an MD5 beside each .kit and this " +
            "compares the bytes against it, so the algorithm is not ours to pick. It catches a " +
            "truncated download, which is what it is for; anything stronger would have nothing " +
            "to compare against.")]
    private async Task<KitDownload?> DownloadKitAsync(string endpoint, string idParam, int id,
        string sessionKey, string offline, HttpClient? cdn, Action<long, long?>? onProgress,
        CancellationToken ct)
    {
        var (env, _) = await GetAsync(endpoint, new Dictionary<string, string?>
        {
            [idParam] = id.ToString(),
            ["offline"] = offline,
        }, sessionKey, ct);

        // The catalog wraps these in <song>; community's wrapper was never captured, so fall back
        // to whichever single element the response carries, then to the root itself.
        var track = env.Element("song") ?? env.Element("community") ?? env.Root;
        string? url = track.Element("stream")?.Value?.Trim();
        string? md5 = track.Element("md5")?.Value?.Trim();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(md5)) return null;
        long size = long.TryParse(track.Element("filesize")?.Value, out var fs) ? fs : 0;

        var http = cdn ?? _cdn ?? _factory?.CreateClient(CdnClientName)
            ?? throw new InvalidOperationException("No CDN client: the API was built without an IHttpClientFactory.");

        // Rearmed after every chunk, so only a transfer that stops moving trips it.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(_bodyIdleTimeout);

        using var buffer = new MemoryStream();
        long received = 0;

        try
        {
            // Read in chunks rather than GetByteArrayAsync so the Downloads page shows a moving count.
            // The .kit is a public CDN object; this request carries no session.
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token);
            response.EnsureSuccessStatusCode();

            // The header when the CDN sends one, else what song/request already told us. Either can be
            // absent, and then the count moves with no total to measure it against.
            long? total = response.Content.Headers.ContentLength ?? (size > 0 ? size : null);

            using var body = await response.Content.ReadAsStreamAsync(idle.Token);

            if (total is { } expected and < int.MaxValue)
                buffer.Capacity = (int)expected;

            var chunk = new byte[81920];
            int read;

            while ((read = await body.ReadAsync(chunk, idle.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                received += read;
                idle.CancelAfter(_bodyIdleTimeout);
                onProgress?.Invoke(received, total);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && idle.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"the KaraFun download stalled: no data for {_bodyIdleTimeout.TotalSeconds:0} s after {received} bytes");
        }

        byte[] data = buffer.ToArray();
        string got = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(data));
        return new KitDownload(url, md5, size, data, string.Equals(got, md5, StringComparison.OrdinalIgnoreCase));
    }
}
