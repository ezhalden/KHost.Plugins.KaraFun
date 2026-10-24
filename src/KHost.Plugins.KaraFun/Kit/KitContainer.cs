using System.Buffers.Binary;

namespace KHost.Plugins.KaraFun.Kit;

/// <summary>Reads the unencrypted <c>.kit</c> multiplex, ordinary container interop with no access
/// control to defeat: id 1 is XML timing, and the Ogg stems and art follow it. Which stem is which
/// comes from the timing's <c>files</c> labels (<see cref="KitStems"/>), never from the id.</summary>
public static class KitContainer
{
    public static readonly byte[] Magic = { 0x01, 0xCE, 0xD7 };

    public sealed record Chunk(byte Id, ReadOnlyMemory<byte> Data);

    /// <summary>A demultiplexed member: one stream id and its concatenated bytes + detected kind.</summary>
    public sealed record Stream(byte Id, string Kind, byte[] Data);

    public sealed record Kit(IReadOnlyList<Chunk> Chunks)
    {
        /// <summary>Concatenate each id's chunks (in order) into whole member streams.</summary>
        public IReadOnlyList<Stream> Demux()
        {
            var byId = new Dictionary<byte, List<byte>>();
            var order = new List<byte>();
            foreach (var c in Chunks)
            {
                if (!byId.TryGetValue(c.Id, out var buf)) { buf = []; byId[c.Id] = buf; order.Add(c.Id); }
                buf.AddRange(c.Data.Span);
            }
            return [.. order.Select(id => { var d = byId[id].ToArray(); return new Stream(id, Sniff(d), d); })];
        }
    }

    public static Kit Read(ReadOnlySpan<byte> file)
    {
        if (file.Length < 5 || !file[..3].SequenceEqual(Magic))
            throw new InvalidDataException("not a .kit (bad magic)");

        int count = file[3];
        int pos = 4 + count * 5; // skip the [id][totalSize] index
        var chunks = new List<Chunk>();
        while (pos + 9 <= file.Length)
        {
            byte id = file[pos];
            uint size = BinaryPrimitives.ReadUInt32BigEndian(file.Slice(pos + 1, 4));
            int dataOff = pos + 9;
            if (dataOff + (long)size > file.Length) break; // tolerate truncation
            chunks.Add(new Chunk(id, file.Slice(dataOff, (int)size).ToArray()));
            pos = dataOff + (int)size;
        }
        return new Kit(chunks);
    }

    /// <summary>Detect a member's type by leading bytes (as KitStreamer::findFileType does).</summary>
    public static string Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == (byte)'<' && b[1] == (byte)'?' && b[2] == (byte)'x') return "xml";
        if (b.Length >= 4 && b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S') return "ogg";
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return "png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpeg";
        return "bin";
    }
}
