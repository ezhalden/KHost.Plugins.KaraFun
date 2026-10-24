using System.Buffers.Binary;
using System.Text;
using KHost.Plugins.KaraFun.Kit;

namespace KHost.Plugins.KaraFun.Tests;

/// <summary>Against a hand-authored container, never a real KaraFun download: a real one carries
/// copyrighted recordings and lyrics, so none is committed anywhere.</summary>
public class KitContainerTests
{
    [Fact]
    public void Read_BadMagic_Throws()
        => Assert.Throws<InvalidDataException>(() => KitContainer.Read(new byte[] { 0, 0, 0, 0, 0 }));

    [Fact]
    public void Read_SingleXmlStream_DemuxesItsBytesBack()
    {
        var xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><karaoke duration=\"1\"></karaoke>");
        var file = BuildSingleChunkKit(id: 1, xml);

        var kit = KitContainer.Read(file);
        var streams = kit.Demux();

        var stream = Assert.Single(streams);
        Assert.Equal((byte)1, stream.Id);
        Assert.Equal("xml", stream.Kind);
        Assert.Equal(xml, stream.Data);
    }

    [Fact]
    public void Read_ChunksForTheSameId_AreConcatenatedInFileOrder()
    {
        var first = Encoding.UTF8.GetBytes("<a>");
        var second = Encoding.UTF8.GetBytes("</a>");
        var file = BuildTwoChunkKit(id: 1, first, second);

        var stream = Assert.Single(KitContainer.Read(file).Demux());

        Assert.Equal("<a></a>", Encoding.UTF8.GetString(stream.Data));
    }

    [Theory]
    [InlineData(new byte[] { (byte)'<', (byte)'?', (byte)'x' }, "xml")]
    [InlineData(new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S' }, "ogg")]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, "png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF }, "jpeg")]
    [InlineData(new byte[] { 1, 2, 3 }, "bin")]
    public void Sniff_LeadingBytes_IdentifyTheKind(byte[] leading, string expectedKind)
        => Assert.Equal(expectedKind, KitContainer.Sniff(leading));

    private static byte[] BuildSingleChunkKit(byte id, byte[] data) => BuildKit([(id, data)]);

    private static byte[] BuildTwoChunkKit(byte id, byte[] first, byte[] second)
        => BuildKit([(id, first), (id, second)]);

    // Mirrors the documented layout by hand, since there is no authoring helper: magic, index
    // [id][totalSize], then chunks [id][size][flags][data].
    private static byte[] BuildKit(IReadOnlyList<(byte Id, byte[] Data)> chunks)
    {
        var totals = new Dictionary<byte, long>();
        var order = new List<byte>();
        foreach (var (id, data) in chunks)
        {
            if (!totals.ContainsKey(id)) { totals[id] = 0; order.Add(id); }
            totals[id] += data.Length;
        }

        using var ms = new MemoryStream();
        ms.Write(KitContainer.Magic);
        ms.WriteByte((byte)order.Count);
        Span<byte> u32 = stackalloc byte[4];
        foreach (var id in order)
        {
            ms.WriteByte(id);
            BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)totals[id]);
            ms.Write(u32);
        }
        foreach (var (id, data) in chunks)
        {
            ms.WriteByte(id);
            BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)data.Length);
            ms.Write(u32);
            BinaryPrimitives.WriteUInt32BigEndian(u32, 0); // flags, unused by Read
            ms.Write(u32);
            ms.Write(data);
        }
        return ms.ToArray();
    }
}
