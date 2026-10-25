using KHost.Abstractions.Models;
using System.Text;
using KHost.Plugins.KaraFun.Kit;

namespace KHost.Plugins.KaraFun.Tests;

/// <summary>Turns a kit's own timing into the host's screen-facing <c>TimedLyrics</c> contract. The
/// flattening and the bounds are the parts nothing else exercises; the page window is pinned here
/// only to prove the mapper wires it through, not to re-test <c>PageWindow</c> itself (see
/// <c>TimedLyricsMapperPageWindowTests</c>).</summary>
public class TimedLyricsMapperTests
{
    private static TimingDocument Document(string karaokeChildren, string karaokeAttributes = "duration=\"30\"")
        => TimingDocument.Parse($"""
            <?xml version="1.0"?>
            <root>
              <karaoke version="1" producer="test" {karaokeAttributes}>
                {karaokeChildren}
              </karaoke>
            </root>
            """);

    private const string OneWordOnePage = """
        <page id="0">
          <line id="0">
            <word id="0"><syllabe><start>00:00:00,000</start><end>00:00:00,200</end><text>la</text></syllabe></word>
          </line>
        </page>
        """;

    // ---- flattening / spacing --------------------------------------------

    /// <summary>A word carries no trailing space of its own; joining two without inserting one
    /// between them is exactly the "Mama,lifehadjustbegun" bug this rule exists to avoid.</summary>
    [Fact]
    public void Map_AWordAfterAnother_PrependsASpaceToItsFirstSyllableOnly()
    {
        var document = Document("""
            <page id="0">
              <line id="0">
                <word id="0">
                  <syllabe><start>00:00:00,000</start><end>00:00:00,200</end><text>Ma</text></syllabe>
                  <syllabe><start>00:00:00,200</start><end>00:00:00,400</end><text>ma</text></syllabe>
                </word>
                <word id="1">
                  <syllabe><start>00:00:00,400</start><end>00:00:00,600</end><text>life</text></syllabe>
                </word>
              </line>
            </page>
            """);

        var syllables = TimedLyricsMapper.Map(document).Pages[0].Lines[0].Syllables.Select(s => s.Text).ToList();

        Assert.Equal(["Ma", "ma", " life"], syllables);
        Assert.Equal("Mama life", string.Concat(syllables));
    }

    /// <summary>The first word on the line joins nothing before it, so it keeps its bare text.
    /// </summary>
    [Fact]
    public void Map_TheFirstWordOnTheLine_GetsNoLeadingSpace()
    {
        var syllable = Assert.Single(TimedLyricsMapper.Map(Document(OneWordOnePage)).Pages[0].Lines[0].Syllables);

        Assert.Equal("la", syllable.Text);
    }

    /// <summary>Mid-word syllables never gain a space, only the first syllable of a later word.
    /// </summary>
    [Fact]
    public void Map_ASyllableInTheMiddleOfAWord_IsNeverGivenALeadingSpace()
    {
        var document = Document("""
            <page id="0">
              <line id="0">
                <word id="0">
                  <syllabe><start>00:00:00,000</start><end>00:00:00,200</end><text>ta</text></syllabe>
                  <syllabe><start>00:00:00,200</start><end>00:00:00,400</end><text>ble</text></syllabe>
                </word>
              </line>
            </page>
            """);

        var syllables = TimedLyricsMapper.Map(document).Pages[0].Lines[0].Syllables.Select(s => s.Text).ToList();

        Assert.Equal(["ta", "ble"], syllables);
    }

    /// <summary>Syllable timing survives the flattening unchanged; only the text is touched.
    /// </summary>
    [Fact]
    public void Map_SyllableTiming_PassesThroughUnchanged()
    {
        var syllable = Assert.Single(TimedLyricsMapper.Map(Document(OneWordOnePage)).Pages[0].Lines[0].Syllables);

        Assert.Equal(0, syllable.StartSeconds);
        Assert.Equal(0.2, syllable.EndSeconds, 3);
    }

    // ---- page window -------------------------------------------------------

    /// <summary>The mapper's job is to hand back the settled window, not the raw fade times, so a
    /// screen never has to work out when an un-faded page belongs on screen.</summary>
    [Fact]
    public void Map_APagesWindow_IsWhatPageWindowComputes()
    {
        var document = Document("""
            <page id="0">
              <fadeinstart>00:00:05,000</fadeinstart>
              <fadeoutstart>00:00:09,000</fadeoutstart>
              <fadeoutduration>00:00:01,000</fadeoutduration>
              <line id="0">
                <word id="0"><syllabe><start>00:00:06,000</start><end>00:00:07,000</end><text>la</text></syllabe></word>
              </line>
            </page>
            """);

        var page = TimedLyricsMapper.Map(document).Pages[0];
        var (show0, show1) = TimedLyricsMapper.PageWindow(document.Pages[0]);

        Assert.Equal(show0, page.ShowFromSeconds, 3);
        Assert.Equal(show1, page.ShowUntilSeconds, 3);
    }

    /// <summary>A page with no fade times of its own falls back to its syllables, the same as the
    /// painter's own fallback.</summary>
    [Fact]
    public void Map_APageWithNoFadeTimes_FallsBackToItsSyllablesLikeThePainterDoes()
    {
        var document = Document(OneWordOnePage);

        var page = TimedLyricsMapper.Map(document).Pages[0];

        Assert.Equal(-0.5, page.ShowFromSeconds, 3);
        Assert.Equal(1.2, page.ShowUntilSeconds, 3);
    }

    // ---- bounds --------------------------------------------------------------

    /// <summary>A kit with no line positioned past the floor gets the floor itself, so a screen
    /// always has a sane space to scale against.</summary>
    [Fact]
    public void Map_ALineWithNoPositionOfItsOwn_BoundsFallBackToTheDefaultCanvas()
    {
        var bounds = TimedLyricsMapper.Map(Document(OneWordOnePage)).Bounds;

        Assert.Equal(640, bounds.Width);
        Assert.Equal(360, bounds.Height);
        Assert.Equal(0, bounds.X);
        Assert.Equal(0, bounds.Y);
    }

    /// <summary>A line positioned past the floor grows the bounds to its own far edge.</summary>
    [Fact]
    public void Map_ALinePositionedPastTheDefaultCanvas_GrowsTheBounds()
    {
        var document = Document("""
            <page id="0">
              <line id="0">
                <position x="600" width="200" y="300" height="100" />
                <word id="0"><syllabe><start>00:00:00,000</start><end>00:00:00,200</end><text>la</text></syllabe></word>
              </line>
            </page>
            """);

        var bounds = TimedLyricsMapper.Map(document).Bounds;

        Assert.Equal(800, bounds.Width);
        Assert.Equal(400, bounds.Height);
    }

    /// <summary>The largest line decides, so a smaller one sharing the page must not shrink the
    /// bounds a screen already scaled the other lines against.</summary>
    [Fact]
    public void Map_ASmallerLineAmongLargerOnes_DoesNotShrinkTheBounds()
    {
        var document = Document("""
            <page id="0">
              <line id="0">
                <position x="600" width="200" y="300" height="100" />
                <word id="0"><syllabe><start>00:00:00,000</start><end>00:00:00,200</end><text>la</text></syllabe></word>
              </line>
              <line id="1">
                <position x="0" width="10" y="0" height="10" />
                <word id="0"><syllabe><start>00:00:01,000</start><end>00:00:01,200</end><text>la</text></syllabe></word>
              </line>
            </page>
            """);

        var bounds = TimedLyricsMapper.Map(document).Bounds;

        Assert.Equal(800, bounds.Width);
        Assert.Equal(400, bounds.Height);
    }

    // ---- the rest of the shape -------------------------------------------------

    [Fact]
    public void Map_ADocumentWithADuration_UsesItRatherThanTheSyllables()
        => Assert.Equal(30, TimedLyricsMapper.Map(Document(OneWordOnePage)).DurationSeconds, 3);

    /// <summary>A beat past the last syllable sung, when the document states no duration of its own.
    /// </summary>
    [Fact]
    public void Map_ADocumentWithNoDuration_FallsBackPastTheLastSyllable()
    {
        var document = Document("""
            <page id="0">
              <line id="0">
                <word id="0"><syllabe><start>00:00:00,000</start><end>00:00:03,000</end><text>la</text></syllabe></word>
              </line>
            </page>
            """, karaokeAttributes: "");

        Assert.Equal(5, TimedLyricsMapper.Map(document).DurationSeconds, 3);
    }

    [Fact]
    public void Map_APageWithNoActiveColor_LeavesItNullForTheScreensOwnTheme()
        => Assert.Null(TimedLyricsMapper.Map(Document(OneWordOnePage)).Pages[0].Active);

    [Fact]
    public void Map_APageWithAnActiveColor_CarriesItThrough()
    {
        var document = Document("""
            <page id="0" activecolor="#FF0000">
              <line id="0"><word id="0"><syllabe><start>00:00:00,000</start><end>00:00:00,200</end><text>la</text></syllabe></word></line>
            </page>
            """);

        var active = TimedLyricsMapper.Map(document).Pages[0].Active;

        Assert.NotNull(active);
        Assert.Equal(255, (int)active!.R);
        Assert.Equal(0, (int)active.G);
        Assert.Equal(0, (int)active.B);
    }

    /// <summary>One flag for the whole song where a kit carries it per page: nothing seen mixes
    /// direction page to page, and the contract has room for only one answer.</summary>
    [Fact]
    public void Map_AnyPageMarkedRightToLeft_MakesTheWholeDocumentRightToLeft()
    {
        var document = Document($"""
            <page id="0" isrighttoleft="0">{PageBody("first")}</page>
            <page id="1" isrighttoleft="1">{PageBody("second")}</page>
            """);

        Assert.True(TimedLyricsMapper.Map(document).IsRightToLeft);
    }

    // ---- who sings each page ----------------------------------------------

    /// <summary>A kit's pages, each naming its singer by kfntrack, with the song's <c>files</c> and
    /// <c>kfntracks</c> around them as a real kit lays them out.</summary>
    private static string Kit(string tracks, string files, params (string? TrackId, string Word)[] pages)
    {
        var body = string.Concat(pages.Select((page, i) => page.TrackId is null
            ? $"""<page id="{i}">{PageBody(page.Word)}</page>"""
            : $"""<page id="{i}" kfntrackid="{page.TrackId}">{PageBody(page.Word)}</page>"""));

        return $"""
            <?xml version="1.0"?>
            <root><karaoke duration="30"><kfntracks>{tracks}</kfntracks>{body}</karaoke>
            <files><file index="1" label="xml">xml</file><file index="2" label="ins.ogg">ins.ogg</file>{files}</files></root>
            """;
    }

    /// <summary>What the host sees on the stems: the voices <see cref="KitStems.Describe"/> gives,
    /// which a page's voice has to equal for the two to name the same singer.</summary>
    private static IReadOnlyList<string?> StemVoices(string timing, params byte[] stemIds)
        => [.. KitStems.Describe(
                [new(1, "xml", Encoding.UTF8.GetBytes(timing.TrimStart())), .. stemIds.Select(id => new KitContainer.Stream(id, "ogg", [id]))])
            .Select(stem => stem.Voice)];

    /// <summary>Daddy Cool: a stem per singer, bound by <c>file_index</c>, the pages alternating.</summary>
    [Fact]
    public void Map_ADuetWithAStemPerSinger_NamesEachPageForItsSingersStem()
    {
        var timing = Kit(
            """<kfntrack id="7" caption="♀" file_index="5"/><kfntrack id="8" caption="♂" file_index="4"/>""",
            """<file index="4" label="ld_♂.ogg">ld_.ogg</file><file index="5" label="ld_♀.ogg">ld_.ogg</file>""",
            ("8", "Da"), ("7", "dy"), ("8", "cool"));

        var voices = TimedLyricsMapper.Map(TimingDocument.Parse(timing)).Pages.Select(page => page.Voice).ToList();

        Assert.Equal(["♂", "♀", "♂"], voices);
        Assert.Equal([null, "♂", "♀"], StemVoices(timing, 2, 4, 5));
    }

    /// <summary>A caption that misspells its singer still names the stem it is bound to exactly as
    /// the stem is voiced, since that is the name a host rides the fader by.</summary>
    [Fact]
    public void Map_AMisspelledCaptionBoundToAStem_NamesThePageAsTheStemIsVoiced()
    {
        var timing = Kit(
            """<kfntrack id="8" caption="Kid Rock" file_index="5"/><kfntrack id="9" caption="Sherly Crow" file_index="4"/>""",
            """<file index="4" label="ld_Sheryl Crow.ogg">x</file><file index="5" label="ld_Kid Rock.ogg">x</file>""",
            ("9", "Picture"));

        var voice = TimedLyricsMapper.Map(TimingDocument.Parse(timing)).Pages[0].Voice;

        Assert.Equal(StemVoices(timing, 4)[0], voice);
    }

    /// <summary>Where the caption is blank, only the stem's label names the singer; reading the
    /// caption alone would leave the page naming nobody.</summary>
    [Fact]
    public void Map_ABlankCaptionBoundToAStem_TakesTheVoiceTheStemsLabelNames()
    {
        var timing = Kit(
            """<kfntrack id="9" caption="" file_index="4"/>""",
            """<file index="4" label="ld_Sheryl Crow.ogg">x</file>""",
            ("9", "Picture"));

        Assert.Equal("Sheryl Crow", TimedLyricsMapper.Map(TimingDocument.Parse(timing)).Pages[0].Voice);
        Assert.Equal(["Sheryl Crow"], StemVoices(timing, 4));
    }

    /// <summary>A track bound to a stem that names no singer gets no voice, not its caption: no
    /// stem is voiced that way, so a caption would match nothing the host rides.</summary>
    [Fact]
    public void Map_ACaptionBoundToAStemThatNamesNoSinger_HasNoVoice()
    {
        var timing = Kit("""<kfntrack id="6" caption="Choir" file_index="3"/>""", """<file index="3" label="bv.ogg">bv.ogg</file>""", ("6", "ah"));

        Assert.Null(TimedLyricsMapper.Map(TimingDocument.Parse(timing)).Pages[0].Voice);
    }

    /// <summary>A track bound to a file the kit does not list names no stem the host has, so its
    /// caption would match nothing either.</summary>
    [Fact]
    public void Map_ACaptionBoundToAFileTheKitDoesNotList_HasNoVoice()
    {
        var timing = Kit("""<kfntrack id="6" caption="Warren G" file_index="9"/>""", "", ("6", "la"));

        Assert.Null(TimedLyricsMapper.Map(TimingDocument.Parse(timing)).Pages[0].Voice);
    }

    /// <summary>Regulate: two singers sharing the one lead stem, bound to no file, so each page
    /// takes its singer's caption.</summary>
    [Fact]
    public void Map_SingersBoundToNoStem_TakeTheirCaptions()
    {
        var timing = Kit(
            """<kfntrack id="6" caption="Warren G"/><kfntrack id="7" caption=" Nate Dogg "/>""",
            """<file index="3" label="ld.ogg">ld.ogg</file>""",
            ("6", "Regulators"), ("7", "mount"), ("6", "up"));

        var voices = TimedLyricsMapper.Map(TimingDocument.Parse(timing)).Pages.Select(page => page.Voice);

        Assert.Equal(["Warren G", "Nate Dogg", "Warren G"], voices);
    }

    [Fact]
    public void Map_APageNamingNoTrack_HasNoVoice()
    {
        var timing = Kit("""<kfntrack id="6" caption="Warren G"/>""", "", (null, "la"));

        Assert.Null(TimedLyricsMapper.Map(TimingDocument.Parse(timing)).Pages[0].Voice);
    }

    [Fact]
    public void Map_APageNamingATrackTheKitDoesNotList_HasNoVoice()
    {
        var timing = Kit("""<kfntrack id="6" caption="Warren G"/>""", "", ("99", "la"));

        Assert.Null(TimedLyricsMapper.Map(TimingDocument.Parse(timing)).Pages[0].Voice);
    }


    private static string PageBody(string word) => $"""
        <line id="0"><word id="0"><syllabe><start>00:00:00,000</start><end>00:00:00,200</end><text>{word}</text></syllabe></word></line>
        """;

    // ---- count-ins and lead-ins ------------------------------------------

    [Fact]
    public void Map_PictureExcerpt_CarriesTheGapsCountInWithTheIncomingSingersColours()
    {
        var countIns = TimedLyricsMapper.Map(TimingDocument.Parse(PictureTimingExcerpt.Xml)).CountIns;

        Assert.Equal(2, countIns.Count);
        var gap = countIns[1];
        Assert.Equal(165.38, gap.StartSeconds);
        Assert.Equal(186.4, gap.EndSeconds);
        Assert.Equal(1.0, gap.StepSeconds);
        Assert.Equal(4, gap.Steps);
        Assert.Equal(new LyricBox(60, 282.5, 520, 35), gap.Position);
        Assert.Equal(new LyricColor(0xF5, 0x2C, 0x77), gap.Active);
        Assert.Equal(new LyricColor(0xFD, 0xD7, 0xE6), gap.Inactive);
        Assert.Equal(new LyricColor(0, 0, 0), gap.Border);
        Assert.Equal(2.5, gap.BorderWidth);
    }

    [Fact]
    public void Map_PictureExcerpt_CarriesTheIntroCountInFirst()
    {
        var intro = TimedLyricsMapper.Map(TimingDocument.Parse(PictureTimingExcerpt.Xml)).CountIns[0];

        Assert.Equal((1.0, 10.28), (intro.StartSeconds, intro.EndSeconds));
        Assert.Equal(new LyricColor(0x0B, 0x96, 0xCA), intro.Active);
        Assert.Equal(new LyricColor(0xD7, 0xF2, 0xFD), intro.Inactive);
    }

    [Fact]
    public void Map_PictureExcerpt_GivesTheFirstLineAfterTheGapItsLeadIn()
    {
        var pages = TimedLyricsMapper.Map(TimingDocument.Parse(PictureTimingExcerpt.Xml)).Pages;

        var line = pages[2].Lines.Single();
        Assert.Equal(new LyricLeadIn(185.2, 110.5), line.LeadIn);
        Assert.Equal(186.4, line.Syllables[0].StartSeconds);
        Assert.Null(pages[1].Lines.Single().LeadIn);
    }

    [Fact]
    public void Map_KitWithNeither_HasNoCountInAndNoLeadIn()
    {
        var lyrics = TimedLyricsMapper.Map(Document(OneWordOnePage));

        Assert.Empty(lyrics.CountIns);
        Assert.Null(lyrics.Pages[0].Lines[0].LeadIn);
    }

    [Fact]
    public void Map_CountIn_OutlinesInTheActiveBorderColour()
    {
        var countIn = Assert.Single(TimedLyricsMapper.Map(Document(OneWordOnePage + """
            <silence step="00:00:01,000" count="4" activebordercolor="#102030" inactivebordercolor="#405060" bordersize="2.50"><position x="60" width="520" y="282.5" height="35" /><start>00:00:01,000</start><end>00:00:20,000</end></silence>
            """)).CountIns);

        Assert.Equal(new LyricColor(0x10, 0x20, 0x30), countIn.Border);
    }

    /// <summary>The bar is drawn in the same space as the lines, so a box below every line has to
    /// be inside the canvas a screen scales, or it is drawn off the bottom.</summary>
    [Fact]
    public void Map_CountInBelowEveryLine_ExtendsTheBoundsToHoldIt()
    {
        var lyrics = TimedLyricsMapper.Map(Document(OneWordOnePage + """
            <silence step="00:00:01,000" count="4"><position x="60" width="700" y="400" height="35" /><start>00:00:01,000</start><end>00:00:20,000</end></silence>
            """));

        Assert.Equal(new LyricBox(0, 0, 760, 435), lyrics.Bounds);
    }
}
