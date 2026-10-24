using System.Text;
using KHost.Abstractions.Models;
using KHost.Plugins.KaraFun.Kit;

namespace KHost.Plugins.KaraFun.Tests;

/// <summary>Which stem is which comes from the kit's <c>files</c> labels, not its stream ids.</summary>
public class KitStemsTests
{
    /// <summary>Two singers, a lead stem each, bound by <c>kfntrack/@file_index</c>; the pages sit
    /// ahead of <c>files</c>, as they do in a real kit.</summary>
    internal const string PerSingerDuet = """
        <?xml version="1.0" encoding="UTF-8"?>
        <root><karaoke duration="209"><kfntracks><kfntrack id="7" caption="♀" file_index="5"/><kfntrack id="8" caption="♂" file_index="4"/></kfntracks><page id="1"/></karaoke>
        <files><file index="1" label="xml">xml</file><file index="2" label="ins.ogg">ins.ogg</file><file index="3" label="bv.ogg">bv.ogg</file><file index="4" label="ld_♂.ogg">ld_.ogg</file><file index="5" label="ld_♀.ogg">ld_.ogg</file></files>
        <mods><mod id="2"><karaoke><kfntracks><kfntrack id="7" caption="mod only" file_index="4"/></kfntracks></karaoke></mod></mods></root>
        """;

    /// <summary>Two singers sharing one uncaptioned lead, and no backing vocal at all.</summary>
    internal const string SharedLeadDuet = """
        <?xml version="1.0" encoding="UTF-8"?>
        <root><karaoke duration="266"><kfntracks><kfntrack id="6" caption="Warren G"/><kfntrack id="7" caption="Nate Dogg"/></kfntracks><page id="1"/></karaoke>
        <files><file index="1" label="xml">xml</file><file index="2" label="ins.ogg">ins.ogg</file><file index="3" label="ld.ogg">ld.ogg</file></files></root>
        """;

    private const string SingleLead = """
        <?xml version="1.0" encoding="UTF-8"?>
        <root><karaoke duration="243"><kfntracks><kfntrack id="8"/></kfntracks><page id="1"/></karaoke>
        <files><file index="1" label="xml">xml</file><file index="2" label="ins.ogg">ins.ogg</file><file index="3" label="bv.ogg">bv.ogg</file><file index="4" label="ld.ogg">ld.ogg</file></files></root>
        """;

    [Fact]
    public void Describe_ADuetWithAStemPerSinger_PutsBothLeadsOnLeadUnderTheirCaptions()
    {
        var stems = KitStems.Describe(Kit(PerSingerDuet, 2, 3, 4, 5));

        Assert.Equal(
            [
                (AudioTrackRole.Music, "Instrumental"),
                (AudioTrackRole.Backing, "Backing Vocal"),
                (AudioTrackRole.Lead, "Lead Vocal (♂)"),
                (AudioTrackRole.Lead, "Lead Vocal (♀)"),
            ],
            stems.Select(stem => (stem.Role, stem.Name)));
    }

    [Fact]
    public void Describe_ASharedUncaptionedLead_IsTheOneLeadEvenOnStreamThree()
    {
        var stems = KitStems.Describe(Kit(SharedLeadDuet, 2, 3));

        Assert.Equal(
            [(AudioTrackRole.Music, "Instrumental"), (AudioTrackRole.Lead, "Lead Vocal")],
            stems.Select(stem => (stem.Role, stem.Name)));
    }

    [Fact]
    public void Describe_APlainKit_IsMusicBackingAndLead()
    {
        var stems = KitStems.Describe(Kit(SingleLead, 2, 3, 4));

        Assert.Equal(
            [(AudioTrackRole.Music, "Instrumental"), (AudioTrackRole.Backing, "Backing Vocal"), (AudioTrackRole.Lead, "Lead Vocal")],
            stems.Select(stem => (stem.Role, stem.Name)));
    }

    /// <summary>A mod's nested karaoke names its own singers; the song's captions are the ones a
    /// host sees.</summary>
    [Fact]
    public void Describe_IgnoresTheCaptionsInsideAMod()
    {
        const string timing = """
            <?xml version="1.0" encoding="UTF-8"?>
            <root><karaoke duration="266"><kfntracks><kfntrack id="6" caption="Warren G"/></kfntracks><page id="1"/></karaoke>
            <files><file index="2" label="ins.ogg">ins.ogg</file><file index="3" label="ld.ogg">ld.ogg</file></files>
            <mods><mod id="1"><karaoke><kfntracks><kfntrack id="6" caption="Warren G" file_index="3"/></kfntracks></karaoke></mod></mods></root>
            """;

        var stems = KitStems.Describe(Kit(timing, 2, 3));

        Assert.Equal("Lead Vocal", stems.Single(stem => stem.Id == 3).Name);
    }

    /// <summary>The caption is what KaraFun shows on the singer's slider, so it beats the label.</summary>
    [Fact]
    public void FromLabel_ACaptionedLead_IsNamedForTheCaption()
        => Assert.Equal((AudioTrackRole.Lead, "Lead Vocal (Kid Rock)"), KitStems.FromLabel("ld_KR.ogg", caption: "Kid Rock"));

    /// <summary><c>file_index</c> binds a singer's caption to their stem, whatever the file
    /// happens to be called.</summary>
    [Fact]
    public void Describe_ALeadBoundByFileIndex_TakesTheSingersCaption()
    {
        const string timing = """
            <?xml version="1.0" encoding="UTF-8"?>
            <root><karaoke duration="1"><kfntracks><kfntrack id="8" caption="Kid Rock" file_index="3"/></kfntracks></karaoke>
            <files><file index="2" label="ins.ogg">ins.ogg</file><file index="3" label="ld_KR.ogg">ld_KR.ogg</file></files></root>
            """;

        Assert.Equal("Lead Vocal (Kid Rock)", KitStems.Describe(Kit(timing, 2, 3)).Single(stem => stem.Id == 3).Name);
    }

    /// <summary>A singer with a stem of their own but no <c>file_index</c> pointing at it is named
    /// from the label.</summary>
    [Fact]
    public void FromLabel_ASingersLeadWithNoCaption_IsNamedFromTheLabel()
        => Assert.Equal((AudioTrackRole.Lead, "Lead Vocal (Sheryl Crow)"), KitStems.FromLabel("ld_Sheryl Crow.ogg", caption: null));

    /// <summary>KaraFun puts no slider on anything but a vocal, so neither does the host.</summary>
    [Fact]
    public void FromLabel_AStemThatIsNoVocal_PlaysFixedAsMusic()
        => Assert.Equal(AudioTrackRole.Music, KitStems.FromLabel("fx.ogg", caption: null).Role);

    [Fact]
    public void Describe_AKitWithNoLabels_FallsBackToTheUsualIds()
    {
        var stems = KitStems.Describe(Kit("""<?xml version="1.0"?><root><karaoke duration="1"/></root>""", 2, 3, 4));

        Assert.Equal(
            [AudioTrackRole.Music, AudioTrackRole.Backing, AudioTrackRole.Lead],
            stems.Select(stem => stem.Role));
    }

    /// <summary>The voice is what the host rides a lead by, so each singer's gets a fader of its own.</summary>
    [Fact]
    public void Describe_ADuetWithAStemPerSinger_GivesEachLeadItsSingersVoice()
        => Assert.Equal(
            [null, null, "♂", "♀"],
            KitStems.Describe(Kit(PerSingerDuet, 2, 3, 4, 5)).Select(stem => stem.Voice));

    /// <summary>One lead two singers share belongs to neither: it rides the one "Lead Vocal" fader.</summary>
    [Fact]
    public void Describe_ASharedUncaptionedLead_HasNoVoice()
        => Assert.All(KitStems.Describe(Kit(SharedLeadDuet, 2, 3)), stem => Assert.Null(stem.Voice));

    [Fact]
    public void Describe_ASingersLeadWithNoCaption_TakesTheVoiceItIsNamedFor()
    {
        const string timing = """
            <?xml version="1.0" encoding="UTF-8"?>
            <root><karaoke duration="1"/>
            <files><file index="2" label="ins.ogg">ins.ogg</file><file index="3" label="ld_Sheryl Crow.ogg">x</file></files></root>
            """;

        Assert.Equal("Sheryl Crow", KitStems.Describe(Kit(timing, 2, 3)).Single(stem => stem.Id == 3).Voice);
    }

    [Theory]
    [InlineData("Lead Vocal (♀)", "♀")]
    [InlineData("Lead Vocal (Kid Rock)", "Kid Rock")]
    [InlineData("Lead Vocal", null)]
    [InlineData("Backing Vocal", null)]
    [InlineData("Instrumental", null)]
    public void VoiceOfName_ReadsTheSingerOffALeadsName(string name, string? voice)
        => Assert.Equal(voice, KitStems.VoiceOfName(name));

    private static IReadOnlyList<KitContainer.Stream> Kit(string timing, params byte[] stemIds)
        => [new(1, "xml", Encoding.UTF8.GetBytes(timing.TrimStart())), .. stemIds.Select(id => new KitContainer.Stream(id, "ogg", [(byte)id]))];
}
