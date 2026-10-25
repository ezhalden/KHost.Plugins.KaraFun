using KHost.Abstractions.Models;

namespace KHost.Plugins.KaraFun.Kit;

/// <summary>Turns a kit's own timing document into the host's <see cref="TimedLyrics"/> contract —
/// the shape a screen draws without ever learning this was KaraFun's format.</summary>
public static class TimedLyricsMapper
{
    public static TimedLyrics Map(TimingDocument document)
    {
        var voices = VoicesByTrack(document);

        return new()
        {
            DurationSeconds = DurationSecondsOf(document),
            Bounds = BoundsOf(document),
            // One flag for the whole song where a kit carries it per page: nothing seen mixes
            // direction page to page, and the contract only has room for one answer.
            IsRightToLeft = document.Pages.Any(page => page.IsRightToLeft),
            Pages = [.. document.Pages.Select(page => MapPage(page, voices))],
            CountIns = [.. document.Silences.Where(silence => silence.Position is not null).Select(MapCountIn)],
        };
    }

    // The active border only: KaraFun strokes the whole bar in it, whatever has filled.
    private static LyricCountIn MapCountIn(TimingDocument.Silence silence) => new()
    {
        StartSeconds = silence.StartSec,
        EndSeconds = silence.EndSec,
        Position = MapBox(silence.Position!),
        StepSeconds = silence.StepSec,
        Steps = silence.Count,
        Active = MapColor(silence.Active),
        Inactive = MapColor(silence.Inactive),
        Border = MapColor(silence.ActiveBorder),
        BorderWidth = silence.BorderSize,
    };

    private static LyricBox MapBox(TimingDocument.Box box) => new(box.X, box.Y, box.Width, box.Height);

    /// <summary>Each kfntrack's singer, named exactly as the stem carrying their lead is voiced, so
    /// a page and its stem agree; a track bound to no stem falls back to its own caption.</summary>
    /// <remarks>The caption is only the fallback: a kit can caption a singer differently from their
    /// stem's label ("Sherly Crow" over <c>ld_Sheryl Crow</c>), and the stem's name is what a host
    /// rides a fader by.</remarks>
    private static Dictionary<int, string?> VoicesByTrack(TimingDocument document)
    {
        var voices = new Dictionary<int, string?>();

        foreach (var track in document.Tracks)
        {
            string? voice;

            if (track.FileIndex is { } file)
                voice = document.FileLabels.TryGetValue(file, out var label)
                    ? KitStems.VoiceOfFile(label, CaptionBoundTo(document, file))
                    : null;
            else
                voice = string.IsNullOrWhiteSpace(track.Caption) ? null : track.Caption.Trim();

            voices.TryAdd(track.Id, voice);
        }

        return voices;
    }

    // The caption KitStems binds to a stem: the first kfntrack naming that file with one.
    private static string? CaptionBoundTo(TimingDocument document, byte file)
        => document.Tracks.FirstOrDefault(track => track.FileIndex == file && track.Caption is { Length: > 0 })?.Caption;

    /// <summary>The document's own duration when it states one, else a beat past the last syllable
    /// sung.</summary>
    private static double DurationSecondsOf(TimingDocument document)
        => document.DurationSec > 0
            ? document.DurationSec
            : document.AllSyllables().Select(syllable => syllable.EndSec).DefaultIfEmpty(0).Max() + 2;

    /// <summary>The kit's logical space: the far edge of every line and count-in box, floored so
    /// a kit with no positioned lines at all still gets a sane canvas to scale against.</summary>
    private static LyricBox BoundsOf(TimingDocument document)
    {
        double right = 640, bottom = 360;

        var boxes = document.Pages.SelectMany(page => page.Lines).Select(line => line.Position)
            .Concat(document.Silences.Select(silence => silence.Position));

        foreach (var box in boxes)
            if (box is not null)
            {
                right = Math.Max(right, box.X + box.Width);
                bottom = Math.Max(bottom, box.Y + box.Height);
            }

        return new LyricBox(0, 0, right, bottom);
    }

    private static LyricPage MapPage(TimingDocument.Page page, IReadOnlyDictionary<int, string?> voices)
    {
        // The result, not the fade times themselves: a screen must never have to work out when a
        // page with no fades of its own belongs on.
        var (showFrom, showUntil) = PageWindow(page);

        return new LyricPage
        {
            ShowFromSeconds = showFrom,
            ShowUntilSeconds = showUntil,
            Active = MapColor(page.Active),
            Inactive = MapColor(page.Inactive),
            Lines = [.. page.Lines.Select(MapLine)],
            Voice = page.TrackId is { } track ? voices.GetValueOrDefault(track) : null,
        };
    }

    /// <summary>When a page is on screen: its own fade times, or what its syllables imply.</summary>
    /// <remarks>A page carrying neither gets a window no time can fall inside, which is how a page
    /// with nothing to sing stays off the screen.</remarks>
    internal static (double Show0, double Show1) PageWindow(TimingDocument.Page page)
    {
        double show0 = page.FadeInStartSec;
        double show1 = page.FadeOutStartSec + page.FadeOutDurationSec;

        if (show1 > show0) return (show0, show1);

        double first = double.MaxValue, last = double.MinValue;
        foreach (var line in page.Lines)
            foreach (var word in line.Words)
                foreach (var syl in word.Syllables)
                {
                    first = Math.Min(first, syl.StartSec);
                    last = Math.Max(last, syl.EndSec);
                }

        return first > last ? (1, -1) : (first - 0.5, last + 1.0);
    }

    private static LyricColor? MapColor(TimingDocument.Color? color)
        => color is null ? null : new LyricColor(color.R, color.G, color.B);

    private static LyricLine MapLine(TimingDocument.Line line) => new()
    {
        Position = line.Position is { } box ? MapBox(box) : null,
        Syllables = [.. FlattenSyllables(line.Words)],
        LeadIn = line.Bar is { } bar ? new LyricLeadIn(bar.StartSec, bar.X) : null,
    };

    /// <summary>A word's own syllables carry no trailing space, and <see cref="LyricSyllable"/> has
    /// no word grouping to carry one in — its contract is that <c>Text</c> carries its own spacing.
    /// So every word but the first on the line prepends a space to its own first syllable. Getting
    /// this backwards produces "Mama,lifehadjustbegun".</summary>
    private static IEnumerable<LyricSyllable> FlattenSyllables(IReadOnlyList<TimingDocument.Word> words)
    {
        for (var w = 0; w < words.Count; w++)
        {
            var syllables = words[w].Syllables;

            for (var s = 0; s < syllables.Count; s++)
            {
                var syllable = syllables[s];
                var text = w > 0 && s == 0 ? " " + syllable.Text : syllable.Text;

                yield return new LyricSyllable(syllable.StartSec, syllable.EndSec, text);
            }
        }
    }
}
