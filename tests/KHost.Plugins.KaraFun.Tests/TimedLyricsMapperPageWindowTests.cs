using System.Text;
using KHost.Plugins.KaraFun.Kit;

namespace KHost.Plugins.KaraFun.Tests;

/// <summary>When each page is on screen, settled once when the timing is mapped, so a display never
/// has to work it out.</summary>
public class TimedLyricsMapperPageWindowTests
{
    private static TimingDocument.Page Page(string pageXml) => TimingDocument.Parse(Encoding.UTF8.GetBytes($"""
        <?xml version="1.0"?>
        <root>
          <karaoke version="1" producer="test" duration="30">
            {pageXml}
          </karaoke>
        </root>
        """)).Pages[0];

    private const string OneSyllable = """
        <line id="0">
          <position x="40" width="200" y="40" height="48" />
          <word id="0">
            <syllabe><start>00:00:02,000</start><end>00:00:03,000</end><text>la</text></syllabe>
          </word>
        </line>
        """;

    /// <summary>A page that says when it appears is taken at its word.</summary>
    [Fact]
    public void PageWindow_WhenThePageCarriesFadeTimes_UsesThem()
    {
        var page = Page($"""
            <page id="0">
              <fadeinstart>00:00:05,000</fadeinstart>
              <fadeoutstart>00:00:09,000</fadeoutstart>
              <fadeoutduration>00:00:01,000</fadeoutduration>
              {OneSyllable}
            </page>
            """);

        var (show0, show1) = TimedLyricsMapper.PageWindow(page);

        Assert.Equal(5, show0, 3);
        Assert.Equal(10, show1, 3);
    }

    /// <summary>Otherwise the syllables decide, with a beat either side so a line is up before it
    /// is sung and stays up a moment after.</summary>
    [Fact]
    public void PageWindow_WithoutFadeTimes_SpansTheSyllablesWithAMargin()
    {
        var (show0, show1) = TimedLyricsMapper.PageWindow(Page($"<page id=\"0\">{OneSyllable}</page>"));

        Assert.Equal(1.5, show0, 3);
        Assert.Equal(4.0, show1, 3);
    }

    /// <summary>A page with nothing to sing and no times of its own must never draw. Returning a
    /// window every t falls inside would paint an empty page over the whole song.</summary>
    [Fact]
    public void PageWindow_WithNoSyllablesAtAll_CannotContainAnyMoment()
    {
        var (show0, show1) = TimedLyricsMapper.PageWindow(Page("""
            <page id="0">
              <line id="0"><position x="40" width="200" y="40" height="48" /></line>
            </page>
            """));

        Assert.True(show0 > show1, $"window {show0}..{show1} contains moments");
    }

    /// <summary>The margins are the point of the fallback, so a caller cannot read the raw
    /// syllable bounds back out of it.</summary>
    [Fact]
    public void PageWindow_WithoutFadeTimes_OpensBeforeTheFirstSyllableAndClosesAfterTheLast()
    {
        var (show0, show1) = TimedLyricsMapper.PageWindow(Page($"<page id=\"0\">{OneSyllable}</page>"));

        Assert.True(show0 < 2.0, "the page should be up before the first syllable starts");
        Assert.True(show1 > 3.0, "the page should stay up after the last syllable ends");
    }
}
