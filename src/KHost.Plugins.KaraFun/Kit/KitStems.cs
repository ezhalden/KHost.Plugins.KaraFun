using System.Xml;
using KHost.Abstractions.Models;

namespace KHost.Plugins.KaraFun.Kit;

/// <summary>What each Ogg stem in a kit carries, read from the kit's own <c>&lt;files&gt;</c>
/// labels. The one answer the probe and the stem hand-off both use.</summary>
/// <remarks>Stream ids are not roles: a duet puts its second lead on id 5, and a song with no
/// backing vocal puts its lead on id 3. Only the label says which stem is which.</remarks>
internal static class KitStems
{
    internal const string InstrumentalName = "Instrumental";
    internal const string BackingName = "Backing Vocal";
    internal const string LeadName = "Lead Vocal";

    /// <summary>One stem, in the order <see cref="Describe"/> returns it.</summary>
    /// <param name="Name">The label a host sees on the track; a per-singer lead carries the
    /// singer's caption, as KaraFun labels that singer's slider.</param>
    /// <param name="Voice">The singer a lead belongs to, which is what the host rides it by; null
    /// for a lead no singer is named on and for every other stem.</param>
    internal sealed record Stem(byte Id, AudioTrackRole Role, string Name, byte[] Data, string? Voice = null);

    /// <summary>The kit's Ogg stems ascending by stream id — the order every track index is
    /// assigned in — each with its role and name.</summary>
    internal static IReadOnlyList<Stem> Describe(IReadOnlyList<KitContainer.Stream> streams)
    {
        var timing = streams.FirstOrDefault(stream => stream.Kind == "xml");
        var (labels, captions) = timing is null ? (Empty, Empty) : ReadLabels(timing.Data);

        return
        [
            .. streams
                .Where(stream => stream.Kind == "ogg")
                .OrderBy(stream => stream.Id)
                .Select(stream =>
                {
                    var (role, name) = labels.TryGetValue(stream.Id, out var label)
                        ? FromLabel(label, captions.GetValueOrDefault(stream.Id))
                        : FromId(stream.Id);
                    return new Stem(stream.Id, role, name, stream.Data, VoiceOfName(name));
                }),
        ];
    }

    /// <summary>Reads a role and a name out of a <c>files/@label</c>: <c>ins</c> is the music,
    /// <c>bv</c> the backing vocal, and <c>ld</c> or <c>ld_&lt;singer&gt;</c> a lead.</summary>
    /// <remarks>A label outside those plays at full with no fader: KaraFun offers sliders for
    /// vocal stems only, so a stem it names nothing it controls is not one a host should ride.
    /// </remarks>
    internal static (AudioTrackRole Role, string Name) FromLabel(string label, string? caption)
    {
        var stem = Path.GetFileNameWithoutExtension(label.Trim());

        if (stem.Equals("ins", StringComparison.OrdinalIgnoreCase)) return (AudioTrackRole.Music, InstrumentalName);
        if (stem.Equals("bv", StringComparison.OrdinalIgnoreCase)) return (AudioTrackRole.Backing, BackingName);

        if (stem.Equals("ld", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("ld_", StringComparison.OrdinalIgnoreCase))
        {
            var singer = string.IsNullOrWhiteSpace(caption) ? stem.Length > 3 ? stem[3..] : "" : caption;
            return (AudioTrackRole.Lead, string.IsNullOrWhiteSpace(singer) ? LeadName : $"{LeadName} ({singer.Trim()})");
        }

        return (AudioTrackRole.Music, stem);
    }

    /// <summary>The singer a lead's name carries — its caption, else its label's suffix — or null
    /// for anything else, including the one shared <see cref="LeadName"/>.</summary>
    internal static string? VoiceOfName(string name)
        => name.StartsWith(LeadName + " (", StringComparison.Ordinal) && name.EndsWith(')')
            ? name[(LeadName.Length + 2)..^1]
            : null;

    /// <summary>The voice <see cref="Describe"/> gives the stem a <c>files/@label</c> and its bound
    /// <c>kfntrack</c> caption describe — what anything naming that singer must match.</summary>
    internal static string? VoiceOfFile(string label, string? caption)
        => VoiceOfName(FromLabel(label, caption).Name);

    // Only for a kit whose timing names no file for the stream. Every kit seen carries labels;
    // backing for the rest keeps an unknown stem in the mix, where a wrong guess only mislabels it.
    private static (AudioTrackRole Role, string Name) FromId(byte id) => id switch
    {
        2 => (AudioTrackRole.Music, InstrumentalName),
        3 => (AudioTrackRole.Backing, BackingName),
        4 => (AudioTrackRole.Lead, LeadName),
        _ => (AudioTrackRole.Backing, $"Stem {id}"),
    };

    private static readonly IReadOnlyDictionary<byte, string> Empty = new Dictionary<byte, string>();

    /// <summary><c>files/file</c> labels by stream id, and <c>kfntrack</c> captions by the
    /// <c>file_index</c> they name.</summary>
    /// <remarks>Read by depth: <c>root/files/file</c> and <c>root/karaoke/kfntracks/kfntrack</c>
    /// only, since a mod's nested karaoke carries <c>kfntracks</c> of its own that are not the
    /// song's. A malformed document answers with what it read so far rather than failing the probe.
    /// </remarks>
    private static (IReadOnlyDictionary<byte, string> Labels, IReadOnlyDictionary<byte, string> Captions) ReadLabels(byte[] xml)
    {
        var labels = new Dictionary<byte, string>();
        var captions = new Dictionary<byte, string>();

        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });

            reader.Read();

            while (!reader.EOF)
            {
                // The lyric pages are most of the document and hold neither list. Skip already
                // lands on the next node; reading again would step over it.
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "page" && reader.Depth == 2)
                {
                    reader.Skip();
                    continue;
                }

                if (reader.NodeType == XmlNodeType.Element)
                    Take(reader, labels, captions);

                reader.Read();
            }
        }
        catch (XmlException)
        {
        }

        return (labels, captions);
    }

    private static void Take(XmlReader reader, Dictionary<byte, string> labels, Dictionary<byte, string> captions)
    {
        switch (reader.LocalName)
        {
            case "file" when reader.Depth == 2 && byte.TryParse(reader.GetAttribute("index"), out var index)
                             && reader.GetAttribute("label") is { Length: > 0 } label:
                labels.TryAdd(index, label);
                break;

            case "kfntrack" when reader.Depth == 3 && byte.TryParse(reader.GetAttribute("file_index"), out var fileIndex)
                                 && reader.GetAttribute("caption") is { Length: > 0 } caption:
                captions.TryAdd(fileIndex, caption);
                break;
        }
    }
}
