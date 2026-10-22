using System.Xml.Linq;

namespace KHost.Plugins.KaraFun.Client;

/// <summary>The API response envelope, confirmed live to be XML rather than JSON.</summary>
public sealed class ApiEnvelope
{
    public XElement Root { get; }
    public ApiEnvelope(XElement root) => Root = root;

    public string? Status => Attr(Root, "status") ?? Root.Element("status")?.Value;
    public bool IsOk => string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);

    /// <summary>The error code text, if the response is an error (e.g. "INVALID_SESSION").</summary>
    public string? ErrorCode => Root.Element("error")?.Value?.Trim();

    /// <summary>The error's <c>action</c> attribute: restart / disconnect / fail.</summary>
    public string? ErrorAction => Attr(Root.Element("error"), "action");

    public XElement? Element(string name) => Root.Element(name);

    private static string? Attr(XElement? e, string name) => e?.Attribute(name)?.Value;

    public static ApiEnvelope Parse(string body)
    {
        var doc = XDocument.Parse(body);
        return new ApiEnvelope(doc.Root ?? new XElement("response"));
    }

    /// <summary>For a body that is not an API answer: an edge's error page, a path that does not
    /// exist. Carries the status so a caller reports the endpoint unreached, not a bad envelope.</summary>
    public static ApiEnvelope Unreachable(string endpoint, int statusCode) => new(
        new XElement("response",
            new XAttribute("status", "Error"),
            new XElement("error",
                new XAttribute("action", "fail"),
                $"HTTP_{statusCode} from {endpoint}")));

    public static bool TryParse(string body, out ApiEnvelope env)
    {
        try { env = Parse(body); return true; }
        catch { env = new ApiEnvelope(new XElement("response", new XAttribute("status", "<unparseable>"))); return false; }
    }
}

/// <summary>Subset of the Session payload needed to chain calls; a field may arrive as an
/// attribute or a child element, so it is read leniently (attribute first, then element).</summary>
public sealed class Session
{
    private readonly XElement _e;
    public Session(XElement e) => _e = e;

    public string? Key => Val("key");
    public string? Login => Val("user_login");

    /// <summary>The KCS WebSocket for guest picks (<c>kcs_url</c>). Used byte for byte, since its
    /// <c>t=</c> token is the connection's only identity, and only <c>session/info</c> carries it.</summary>
    public string? ControlSystemUrl => Val("kcs_url");

    /// <summary>Cover image template, minted per session (<c>{id}</c>, <c>{format}</c>). Only the
    /// JPEG text has ever been seen to resolve; its <c>png</c> attribute never has.</summary>
    public string? ImageUrlTemplate => _e.Element("images-url-schema")?.Value?.Trim() is { Length: > 0 } template
        ? template
        : null;

    private string? Val(string name) => _e.Attribute(name)?.Value ?? _e.Element(name)?.Value;

    /// <summary>Find a &lt;session&gt; anywhere in the response (root, child, or the root itself).</summary>
    public static Session? From(ApiEnvelope env)
    {
        var s = env.Root.Name.LocalName == "session" ? env.Root
              : env.Root.Element("session")
              ?? env.Root.Descendants("session").FirstOrDefault();
        if (s is null && (env.Root.Attribute("key") is not null || env.Root.Element("key") is not null))
            s = env.Root;
        return s is null ? null : new Session(s);
    }
}

/// <summary>A remote-control session: the pairing channel a guest's phone joins. The wire field is
/// <c>short_url</c>, not <c>url</c>; <c>qrcode</c> is a base64 PNG, not the text to encode.</summary>
public sealed class Remote
{
    private readonly XElement _e;
    public Remote(XElement e) => _e = e;

    public string? Channel => Val("channel");

    /// <summary>Where a guest's phone goes. The wire name is <c>short_url</c>.</summary>
    public string? Url => Val("short_url");

    /// <summary>How many phones may pair at once. 20 on the account this was captured against.</summary>
    public string? MaxRemotes => Val("max_remotes");

    /// <summary>What the QR should say.</summary>
    public string? Payload => Blank(Url);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string? Val(string name) => _e.Attribute(name)?.Value ?? _e.Element(name)?.Value;

    /// <summary>The payload is on the response root, not in a wrapper, but a <c>&lt;remote&gt;</c>
    /// element is still looked for first since tolerating one costs nothing.</summary>
    public static Remote? From(ApiEnvelope env)
    {
        var e = env.Root.Name.LocalName == "remote" ? env.Root
              : env.Root.Element("remote")
              ?? env.Root.Descendants("remote").FirstOrDefault();

        if (e is null && (env.Root.Attribute("short_url") is not null || env.Root.Element("short_url") is not null))
            e = env.Root;

        return e is null ? null : new Remote(e);
    }
}

/// <summary>One catalog song, as returned by <c>song/info</c>, shaped from a live capture.</summary>
public sealed record CatalogSong(int Id, string Title, string Artist, TimeSpan? Duration, string? ImageId = null)
{
    public static CatalogSong? From(XElement song)
    {
        if (!int.TryParse(song.Attribute("id")?.Value, out var id)) return null;

        var title = song.Element("title")?.Value?.Trim();
        if (string.IsNullOrEmpty(title)) return null;

        var artist = song.Element("artist")?.Value?.Trim() ?? "";

        // "len" (seconds) is the wire attribute name, not "duration"; that name belongs to the
        // decoder's Swift/JSON-side property instead.
        TimeSpan? duration = int.TryParse(song.Attribute("len")?.Value, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

        // The cover's id, which only means anything against the session's own URL template. Absent
        // on a song with no artwork, and the row then simply has no picture.
        var imageId = song.Element("image")?.Attribute("id")?.Value?.Trim();

        return new CatalogSong(id, title, artist, duration, string.IsNullOrEmpty(imageId) ? null : imageId);
    }
}
