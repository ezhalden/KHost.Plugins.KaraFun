namespace KHost.Plugins.KaraFun.Tests;

/// <summary>The count-in and lead-in elements of a real duet kit's timing, around its intro and its
/// long instrumental gap at 2:45, with every time, colour and position as that kit has them. The
/// words are placeholders: a lyric is never reproduced here.</summary>
internal static class PictureTimingExcerpt
{
    public const string Xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <root>
          <karaoke version="1.0" producer="Recisio" duration="300.68378684807">
            <kfntracks>
              <kfntrack id="8" caption="Singer A" />
              <kfntrack id="9" caption="Singer B" />
            </kfntracks>
            <silence step="00:00:01,000" count="4" activecolor="#0B96CA" inactivecolor="#D7F2FD" activebordercolor="#000000" inactivebordercolor="#000000" bordersize="2.50">
              <position x="60.00" width="520" y="282.50" height="35" />
              <start>00:00:01,000</start>
              <end>00:00:10,280</end>
            </silence>
            <page id="1" activecolor="#0B96CA" inactivecolor="#D7F2FD" activebordercolor="#000000" inactivebordercolor="#000000" kfntrackid="8">
              <fadeinstart>00:00:09,080</fadeinstart>
              <fadeinduration>00:00:00,600</fadeinduration>
              <fadeoutstart>00:00:14,180</fadeoutstart>
              <fadeoutduration>00:00:00,230</fadeoutduration>
              <line id="1">
                <position x="73.50" width="493" y="265.70" height="52" />
                <progressivebar x="41.50"><start>00:00:09,080</start></progressivebar>
                <word id="1"><syllabe><start>00:00:10,280</start><end>00:00:10,580</end><text>aa</text></syllabe></word>
                <word id="2"><syllabe><start>00:00:10,580</start><end>00:00:13,830</end><text>bbbb</text></syllabe></word>
              </line>
            </page>
            <page id="40" activecolor="#F52C77" inactivecolor="#FDD7E6" activebordercolor="#000000" inactivebordercolor="#000000" kfntrackid="9">
              <fadeinstart>00:02:37,640</fadeinstart>
              <fadeinduration>00:00:00,600</fadeinduration>
              <fadeoutstart>00:02:44,980</fadeoutstart>
              <fadeoutduration>00:00:00,600</fadeoutduration>
              <line id="40">
                <position x="82.00" width="476" y="155.50" height="52" />
                <word id="1"><syllabe><start>00:02:38,840</start><end>00:02:43,930</end><text>ccccc</text></syllabe></word>
              </line>
            </page>
            <silence step="00:00:01,000" count="4" activecolor="#F52C77" inactivecolor="#FDD7E6" activebordercolor="#000000" inactivebordercolor="#000000" bordersize="2.50">
              <position x="60.00" width="520" y="282.50" height="35" />
              <start>00:02:45,380</start>
              <end>00:03:06,400</end>
            </silence>
            <page id="41" activecolor="#F52C77" inactivecolor="#FDD7E6" activebordercolor="#000000" inactivebordercolor="#000000" kfntrackid="9">
              <fadeinstart>00:03:05,200</fadeinstart>
              <fadeinduration>00:00:00,600</fadeinduration>
              <fadeoutstart>00:03:08,840</fadeoutstart>
              <fadeoutduration>00:00:00,600</fadeoutduration>
              <line id="41">
                <position x="142.50" width="355" y="95.70" height="52" />
                <progressivebar x="110.50"><start>00:03:05,200</start></progressivebar>
                <word id="1"><syllabe><start>00:03:06,400</start><end>00:03:06,710</end><text>d</text></syllabe></word>
                <word id="2"><syllabe><start>00:03:06,710</start><end>00:03:08,240</end><text>eee</text></syllabe></word>
              </line>
            </page>
          </karaoke>
        </root>
        """;
}
