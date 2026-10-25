using KHost.Plugins.KaraFun.Kit;

namespace KHost.Plugins.KaraFun.Tests;

/// <summary>Against a hand-authored timing document, never a real download (see KitContainerTests).</summary>
public class TimingDocumentTests
{
    [Theory]
    [InlineData("00:00:01,500", 1.5)]
    [InlineData("00:01:02,000", 62.0)]
    [InlineData("01:00:00,250", 3600.25)]
    public void ParseCue_SrtStyleTimestamp_ReadsAsSeconds(string cue, double expectedSeconds)
        => Assert.Equal(expectedSeconds, TimingDocument.ParseCue(cue));

    [Fact]
    public void ParseCue_Blank_IsZero()
        => Assert.Equal(0, TimingDocument.ParseCue(null));

    [Fact]
    public void Parse_OnePageOneLineOneWord_ReadsPositionAndSyllableTiming()
    {
        const string xml = """
            <?xml version="1.0"?>
            <root>
              <karaoke version="1" producer="test" duration="2.5">
                <page id="0" activecolor="#FF0000" inactivecolor="#FFFFFF">
                  <fadeinstart>00:00:00,000</fadeinstart>
                  <fadeinduration>00:00:00,500</fadeinduration>
                  <fadeoutstart>00:00:02,000</fadeoutstart>
                  <fadeoutduration>00:00:00,500</fadeoutduration>
                  <line id="0">
                    <position x="40" width="200" y="40" height="48" />
                    <word id="0">
                      <syllabe>
                        <start>00:00:00,500</start>
                        <end>00:00:01,000</end>
                        <text>la</text>
                      </syllabe>
                    </word>
                  </line>
                </page>
              </karaoke>
            </root>
            """;

        var doc = TimingDocument.Parse(xml);

        Assert.Equal(2.5, doc.DurationSec);
        var page = Assert.Single(doc.Pages);
        Assert.Equal(new TimingDocument.Color(0xFF, 0, 0), page.Active);
        Assert.False(page.IsRightToLeft);

        var line = Assert.Single(page.Lines);
        Assert.Equal(new TimingDocument.Box(40, 200, 40, 48), line.Position);

        var word = Assert.Single(line.Words);
        var syllable = Assert.Single(word.Syllables);
        Assert.Equal("la", syllable.Text);
        Assert.Equal(0.5, syllable.StartSec);
        Assert.Equal(1.0, syllable.EndSec);
    }

    [Fact]
    public void Parse_RightToLeftPage_ReadsTheFlag()
    {
        const string xml = """
            <?xml version="1.0"?>
            <root><karaoke version="1" producer="test" duration="1">
              <page id="0" isrighttoleft="1"></page>
            </karaoke></root>
            """;

        Assert.True(TimingDocument.Parse(xml).Pages.Single().IsRightToLeft);
    }

    [Fact]
    public void AllSyllables_AcrossMultipleWords_EnumeratesEveryOne()
    {
        const string xml = """
            <?xml version="1.0"?>
            <root><karaoke version="1" producer="test" duration="1">
              <page id="0">
                <line id="0">
                  <word id="0"><syllabe><start>00:00:00,000</start><end>00:00:00,300</end><text>la</text></syllabe></word>
                  <word id="1"><syllabe><start>00:00:00,300</start><end>00:00:00,600</end><text>di</text></syllabe></word>
                </line>
              </page>
            </karaoke></root>
            """;

        Assert.Equal(["la", "di"], TimingDocument.Parse(xml).AllSyllables().Select(s => s.Text));
    }

    [Fact]
    public void Parse_PictureExcerpt_ReadsEverySilenceInDocumentOrder()
    {
        var silences = TimingDocument.Parse(PictureTimingExcerpt.Xml).Silences;

        Assert.Equal(2, silences.Count);
        Assert.Equal((1.0, 10.28), (silences[0].StartSec, silences[0].EndSec));
        Assert.Equal(new TimingDocument.Color(0x0B, 0x96, 0xCA), silences[0].Active);

        var gap = silences[1];
        Assert.Equal((165.38, 186.4), (gap.StartSec, gap.EndSec));
        Assert.Equal(1.0, gap.StepSec);
        Assert.Equal(4, gap.Count);
        Assert.Equal(new TimingDocument.Box(60, 520, 282.5, 35), gap.Position);
        Assert.Equal(new TimingDocument.Color(0xF5, 0x2C, 0x77), gap.Active);
        Assert.Equal(new TimingDocument.Color(0xFD, 0xD7, 0xE6), gap.Inactive);
        Assert.Equal(new TimingDocument.Color(0, 0, 0), gap.ActiveBorder);
        Assert.Equal(new TimingDocument.Color(0, 0, 0), gap.InactiveBorder);
        Assert.Equal(2.5, gap.BorderSize);
    }

    [Fact]
    public void Parse_PictureExcerpt_ReadsTheProgressiveBarOnlyOnTheLinesCarryingOne()
    {
        var pages = TimingDocument.Parse(PictureTimingExcerpt.Xml).Pages;

        Assert.Equal(new TimingDocument.ProgressiveBar(110.5, 185.2), pages.Single(p => p.Id == 41).Lines.Single().Bar);
        Assert.Null(pages.Single(p => p.Id == 40).Lines.Single().Bar);
    }

    /// <summary>A mod's nested karaoke repeats the song's silences per singer; the pages are read
    /// from the primary one only, and its count-ins have to match them.</summary>
    [Fact]
    public void Parse_SilenceInsideAModsKaraoke_IsNotRead()
    {
        const string xml = """
            <?xml version="1.0"?>
            <root>
              <karaoke version="1" producer="test" duration="30"></karaoke>
              <mods><mod id="1"><karaoke>
                <silence step="00:00:01,000" count="4"><position x="0" width="10" y="0" height="10" /><start>00:00:01,000</start><end>00:00:20,000</end></silence>
              </karaoke></mod></mods>
            </root>
            """;

        Assert.Empty(TimingDocument.Parse(xml).Silences);
    }

    [Fact]
    public void Parse_KitWithNeither_ReadsNoSilenceAndNoBar()
    {
        var doc = TimingDocument.Parse("""
            <?xml version="1.0"?>
            <root><karaoke version="1" producer="test" duration="1">
              <page id="0"><line id="0"><position x="40" width="200" y="40" height="48" />
                <word id="0"><syllabe><start>00:00:00,000</start><end>00:00:00,300</end><text>la</text></syllabe></word>
              </line></page>
            </karaoke></root>
            """);

        Assert.Empty(doc.Silences);
        Assert.Null(doc.Pages.Single().Lines.Single().Bar);
    }
}
