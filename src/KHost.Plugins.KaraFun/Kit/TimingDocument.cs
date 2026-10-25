using System.Globalization;
using System.Xml.Linq;

namespace KHost.Plugins.KaraFun.Kit;

/// <summary>Parses .kit's id-1 timing/lyric XML, keeping only the subtree drawing the words needs.
/// <c>duration</c> is float seconds; every lyric cue is instead an "HH:MM:SS,mmm" string.</summary>
public sealed class TimingDocument
{
    public double DurationSec { get; private set; }
    public IReadOnlyList<Page> Pages => _pages;

    /// <summary>The count-in bars the primary <c>karaoke</c> marks its long gaps with, in document order.</summary>
    public IReadOnlyList<Silence> Silences => _silences;

    /// <summary>The song's singers, from <c>root/karaoke/kfntracks</c>, in document order.</summary>
    public IReadOnlyList<Track> Tracks => _tracks;

    /// <summary><c>root/files/file</c> labels by index, the first for an index winning, as
    /// <see cref="KitStems"/> reads them.</summary>
    public IReadOnlyDictionary<byte, string> FileLabels => _fileLabels;

    private readonly List<Page> _pages = [];
    private readonly List<Silence> _silences = [];
    private readonly List<Track> _tracks = [];
    private readonly Dictionary<byte, string> _fileLabels = [];

    public sealed record Color(byte R, byte G, byte B);
    public sealed record Box(double X, int Width, double Y, int Height);
    public sealed record Syllable(double StartSec, double EndSec, string Text);
    public sealed record Word(int Id, IReadOnlyList<Syllable> Syllables);
    public sealed record Line(int Id, Box? Position, IReadOnlyList<Word> Words, ProgressiveBar? Bar = null);

    /// <summary>A line's <c>progressivebar</c>: the marker that runs in to its first syllable.</summary>
    public sealed record ProgressiveBar(double X, double StartSec);

    /// <summary>One <c>silence</c>: a bar filling across a gap, with <paramref name="Count"/> steps of
    /// <paramref name="StepSec"/> counted down before its end.</summary>
    public sealed record Silence(
        double StartSec, double EndSec, double StepSec, int Count, Box? Position,
        Color? Active, Color? Inactive, Color? ActiveBorder, Color? InactiveBorder, double BorderSize);
    public sealed record Page(
        int Id, Color? Active, Color? Inactive,
        double FadeInStartSec, double FadeInDurationSec,
        double FadeOutStartSec, double FadeOutDurationSec, IReadOnlyList<Line> Lines,
        bool IsRightToLeft, int? TrackId = null);

    /// <summary>One <c>kfntrack</c>: a singer a page names by <see cref="Page.TrackId"/>.</summary>
    /// <param name="FileIndex">The stem carrying this singer's lead; null when the kit names none,
    /// which it does for a singer sharing the one lead stem and, on some kits, for no reason.</param>
    public sealed record Track(int Id, string? Caption, byte? FileIndex);

    /// <summary>All syllables across all pages, in document order.</summary>
    public IEnumerable<Syllable> AllSyllables()
        => _pages.SelectMany(p => p.Lines).SelectMany(l => l.Words).SelectMany(w => w.Syllables);

    // "HH:MM:SS,mmm" -> seconds. This is the ONLY timestamp form in the file (never a float here).
    public static double ParseCue(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var t = s.Trim();
        int c1 = t.IndexOf(':'), c2 = t.IndexOf(':', c1 + 1), comma = t.IndexOf(',');
        int hh = int.Parse(t[..c1], CultureInfo.InvariantCulture);
        int mm = int.Parse(t[(c1 + 1)..c2], CultureInfo.InvariantCulture);
        int ss = int.Parse(t[(c2 + 1)..comma], CultureInfo.InvariantCulture);
        int ms = int.Parse(t[(comma + 1)..], CultureInfo.InvariantCulture);
        return hh * 3600 + mm * 60 + ss + ms / 1000.0;
    }

    private static double F(string? s) =>
        string.IsNullOrWhiteSpace(s) ? 0 : double.Parse(s, CultureInfo.InvariantCulture);
    private static int I(string? s) =>
        string.IsNullOrWhiteSpace(s) ? 0 : int.Parse(s, CultureInfo.InvariantCulture);

    private static Color? ParseColor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().TrimStart('#');
        if (t.Length < 6) return null;
        return new Color(Convert.ToByte(t[..2], 16), Convert.ToByte(t[2..4], 16), Convert.ToByte(t[4..6], 16));
    }

    public static TimingDocument Parse(ReadOnlySpan<byte> xmlBytes)
        => Parse(System.Text.Encoding.UTF8.GetString(xmlBytes));

    public static TimingDocument Parse(string xml)
    {
        var doc = new TimingDocument();
        var root = XDocument.Parse(xml).Root ?? throw new InvalidDataException("empty timing XML");
        var karaoke = root.Elements().FirstOrDefault(e => e.Name.LocalName == "karaoke");
        if (karaoke is not null) doc.ReadKaraoke(karaoke);

        foreach (var files in root.Elements().Where(e => e.Name.LocalName == "files"))
            foreach (var file in files.Elements().Where(e => e.Name.LocalName == "file"))
                if (byte.TryParse((string?)file.Attribute("index"), out var index)
                    && (string?)file.Attribute("label") is { Length: > 0 } label)
                    doc._fileLabels.TryAdd(index, label);

        return doc;
    }

    private void ReadKaraoke(XElement k)
    {
        DurationSec = F((string?)k.Attribute("duration"));
        foreach (var el in k.Elements())
        {
            if (el.Name.LocalName == "page") _pages.Add(ReadPage(el));
            else if (el.Name.LocalName == "kfntracks") _tracks.AddRange(ReadTracks(el));
            else if (el.Name.LocalName == "silence") _silences.Add(ReadSilence(el));
        }
    }

    private Page ReadPage(XElement p)
    {
        var lines = new List<Line>();
        double fis = 0, fid = 0, fos = 0, fod = 0;
        foreach (var el in p.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "fadeinstart": fis = ParseCue(el.Value); break;
                case "fadeinduration": fid = ParseCue(el.Value); break;
                case "fadeoutstart": fos = ParseCue(el.Value); break;
                case "fadeoutduration": fod = ParseCue(el.Value); break;
                case "line": lines.Add(ReadLine(el)); break;
            }
        }
        return new Page(
            I((string?)p.Attribute("id")),
            ParseColor((string?)p.Attribute("activecolor")),
            ParseColor((string?)p.Attribute("inactivecolor")),
            fis, fid, fos, fod, lines,
            (string?)p.Attribute("isrighttoleft") == "1",
            int.TryParse((string?)p.Attribute("kfntrackid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var track) ? track : null);
    }

    // @step is a cue like every other time in the file, not a float of seconds.
    private static Silence ReadSilence(XElement s)
    {
        Box? pos = null;
        double start = 0, end = 0;
        foreach (var el in s.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "position": pos = ReadBox(el); break;
                case "start": start = ParseCue(el.Value); break;
                case "end": end = ParseCue(el.Value); break;
            }
        }
        return new Silence(
            start, end, ParseCue((string?)s.Attribute("step")), I((string?)s.Attribute("count")), pos,
            ParseColor((string?)s.Attribute("activecolor")),
            ParseColor((string?)s.Attribute("inactivecolor")),
            ParseColor((string?)s.Attribute("activebordercolor")),
            ParseColor((string?)s.Attribute("inactivebordercolor")),
            F((string?)s.Attribute("bordersize")));
    }

    private static Box ReadBox(XElement el)
        => new(F((string?)el.Attribute("x")), I((string?)el.Attribute("width")),
               F((string?)el.Attribute("y")), I((string?)el.Attribute("height")));

    private static IEnumerable<Track> ReadTracks(XElement tracks)
    {
        foreach (var el in tracks.Elements())
            if (el.Name.LocalName == "kfntrack"
                && int.TryParse((string?)el.Attribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                yield return new Track(
                    id,
                    (string?)el.Attribute("caption"),
                    byte.TryParse((string?)el.Attribute("file_index"), out var file) ? file : null);
    }

    private Line ReadLine(XElement l)
    {
        Box? pos = null;
        ProgressiveBar? bar = null;
        var words = new List<Word>();
        foreach (var el in l.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "position": pos = ReadBox(el); break;
                case "progressivebar":
                    bar = new ProgressiveBar(F((string?)el.Attribute("x")),
                        ParseCue(el.Elements().FirstOrDefault(e => e.Name.LocalName == "start")?.Value));
                    break;
                case "word": words.Add(ReadWord(el)); break;
            }
        }
        return new Line(I((string?)l.Attribute("id")), pos, words, bar);
    }

    private Word ReadWord(XElement w)
    {
        var sy = new List<Syllable>();
        foreach (var el in w.Elements())
            if (el.Name.LocalName == "syllabe") sy.Add(ReadSyllable(el));
        return new Word(I((string?)w.Attribute("id")), sy);
    }

    private Syllable ReadSyllable(XElement s)
    {
        double start = 0, end = 0; string text = "";
        foreach (var el in s.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "start": start = ParseCue(el.Value); break;
                case "end": end = ParseCue(el.Value); break;
                case "text": text = el.Value; break;
            }
        }
        return new Syllable(start, end, text);
    }
}
