using AmllTtmlTool.Core.Lyrics;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Tests;

// Step 6: raw ttm:agent stash + effective duet display rule. The model
// IsDuet keeps exact WASM semantics everywhere (all goldens hold); only the
// presentation derives from raw agents plus the document primary agent.
public sealed class AgentDisplayTests
{
    private static string Line(string agentAttr, string word = "w") =>
        $"<p begin=\"0.000\" end=\"1.000\"{agentAttr}><span begin=\"0.000\" end=\"1.000\">{word}</span></p>";

    private static string Doc(params string[] lines) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\">" +
        "<head></head><body><div>" + string.Concat(lines) + "</div></body></tt>";

    private static TtmlLyric Parse(string xml)
    {
        var parsed = TtmlParser.Parse(xml);
        Assert.True(parsed.Success);
        return LegacyTtml.RepairLegacyImport(parsed.Data!, xml).Result;
    }

    private static EditLine Edit(bool isBg = false, bool isDuet = false, string? agent = null, bool stash = true)
    {
        var line = EditLine.New();
        line.IsBg = isBg;
        line.IsDuet = isDuet;
        if (stash && agent is not null) line.Unmapped[EditLine.AgentKey] = agent;
        return line;
    }

    [Fact]
    public void Parser_Stashes_Raw_Agents()
    {
        var records = Parse(Doc(
            Line(" ttm:agent=\"v1\"", "a"),
            Line(" ttm:agent=\"v2\"", "b"),
            Line("", "c"),
            Line(" ttm:agent=\"\"", "d")));
        Assert.Equal("v1", records.LyricLines[0].Agent);
        Assert.Equal("v2", records.LyricLines[1].Agent);
        Assert.Null(records.LyricLines[2].Agent);
        Assert.Equal(string.Empty, records.LyricLines[3].Agent);
    }

    [Fact]
    public void BrokenDownShape_Model_Stays_WasmExact_And_Agents_Preserved()
    {
        // v2-first with a v1 majority: WASM marks every line duet (first-p
        // baseline v2 marks the v1 lines, the legacy restore the v2 lines).
        // The port keeps that model exactly (goldens hold); the DISPLAY rule
        // below is what changes.
        var records = Parse(Doc(
            Line(" ttm:agent=\"v2\"", "a"),
            Line(" ttm:agent=\"v2\"", "b"),
            Line(" ttm:agent=\"v1\"", "c"),
            Line(" ttm:agent=\"v1\"", "d"),
            Line(" ttm:agent=\"v1\"", "e")));
        Assert.All(records.LyricLines, l => Assert.True(l.IsDuet));
        Assert.Equal(["v2", "v2", "v1", "v1", "v1"],
            records.LyricLines.Select(l => l.Agent).ToList());
    }

    [Fact]
    public void PrimaryAgent_Is_Most_Frequent_NonBg()
    {
        var doc = new EditDocument();
        doc.Lines.Add(Edit(isDuet: true, agent: "v2"));
        doc.Lines.Add(Edit(isDuet: true, agent: "v2"));
        doc.Lines.Add(Edit(isDuet: true, agent: "v1"));
        doc.Lines.Add(Edit(isDuet: true, agent: "v1"));
        doc.Lines.Add(Edit(isDuet: true, agent: "v1"));
        Assert.Equal("v1", DuetDisplay.PrimaryAgent(doc.Lines));
        var eff = doc.Lines.Select(l => DuetDisplay.EffectiveDuet(l, "v1")).ToList();
        Assert.Equal([true, true, false, false, false], eff);
    }

    [Fact]
    public void PrimaryAgent_Tie_Goes_To_First_NonBg()
    {
        var doc = new EditDocument();
        doc.Lines.Add(Edit(agent: "v2"));
        doc.Lines.Add(Edit(agent: "v1"));
        Assert.Equal("v2", DuetDisplay.PrimaryAgent(doc.Lines));
    }

    [Fact]
    public void PrimaryAgent_Ignores_Bg_And_Falls_Back_To_V1()
    {
        var doc = new EditDocument();
        doc.Lines.Add(Edit(isBg: true, agent: "v9"));
        Assert.Equal("v1", DuetDisplay.PrimaryAgent(doc.Lines));
        Assert.Equal("v1", DuetDisplay.PrimaryAgent([]));
        var plain = new EditDocument();
        plain.Lines.Add(Edit(isDuet: true));
        Assert.Equal("v1", DuetDisplay.PrimaryAgent(plain.Lines));
        // Unstashed lines follow the model flag directly.
        Assert.True(DuetDisplay.EffectiveDuet(plain.Lines[0], "v1"));
    }

    [Fact]
    public void PrimaryAgent_AllV2_Shows_Nothing()
    {
        var doc = new EditDocument();
        doc.Lines.Add(Edit(isDuet: true, agent: "v2"));
        doc.Lines.Add(Edit(isDuet: true, agent: "v2"));
        Assert.Equal("v2", DuetDisplay.PrimaryAgent(doc.Lines));
        Assert.All(doc.Lines, l => Assert.False(DuetDisplay.EffectiveDuet(l, "v2")));
    }

    [Fact]
    public void PrimaryAgent_Custom_Ids()
    {
        var doc = new EditDocument();
        doc.Lines.Add(Edit(isDuet: true, agent: "singer-a"));
        doc.Lines.Add(Edit(isDuet: true, agent: "singer-a"));
        doc.Lines.Add(Edit(isDuet: true, agent: "singer-b"));
        Assert.Equal("singer-a", DuetDisplay.PrimaryAgent(doc.Lines));
        Assert.Equal([false, false, true],
            doc.Lines.Select(l => DuetDisplay.EffectiveDuet(l, "singer-a")).ToList());
    }

    [Fact]
    public void Adapter_RoundTrips_Agent_Through_Unmapped()
    {
        var doc = new EditDocument();
        var stashed = Edit(isDuet: true, agent: "v2");
        var plain = Edit(isDuet: false);
        doc.Lines.Add(stashed);
        doc.Lines.Add(plain);
        var records = LyricAdapters.ToRecords(doc);
        Assert.Equal("v2", records.LyricLines[0].Agent);
        Assert.Null(records.LyricLines[1].Agent);
        var back = LyricAdapters.FromRecords(records, "test");
        Assert.Equal("v2", back.Lines[0].Unmapped[EditLine.AgentKey]);
        Assert.False(back.Lines[1].Unmapped.ContainsKey(EditLine.AgentKey));
        Assert.Equal(
            LyricAdapters.ToRecords(back).LyricLines.Select(l => l.Agent).ToList(),
            records.LyricLines.Select(l => l.Agent).ToList());
    }

    [Fact]
    public void Toggle_V2_And_BG_Retire_The_Stash_Undo_Restores()
    {
        var doc = new EditDocument();
        doc.Lines.Add(Edit(isDuet: true, agent: "v2"));
        var id = doc.Lines[0].Id;
        doc.Execute(new AmllTtmlTool.Core.Lyrics.Commands.SetLineFlagsCommand(id, isDuet: false));
        Assert.False(doc.Lines[0].Unmapped.ContainsKey(EditLine.AgentKey));
        Assert.False(doc.Lines[0].IsDuet);
        doc.Undo();
        Assert.True(doc.Lines[0].IsDuet);
        Assert.Equal("v2", doc.Lines[0].Unmapped[EditLine.AgentKey]);

        doc.Execute(new AmllTtmlTool.Core.Lyrics.Commands.SetLineFlagsCommand(id, isBg: true));
        Assert.True(doc.Lines[0].IsBg);
        Assert.False(doc.Lines[0].Unmapped.ContainsKey(EditLine.AgentKey));
        doc.Undo();
        Assert.Equal("v2", doc.Lines[0].Unmapped[EditLine.AgentKey]);

        // ignoreSync alone never touches the agent.
        doc.Execute(new AmllTtmlTool.Core.Lyrics.Commands.SetLineFlagsCommand(id, ignoreSync: true));
        Assert.Equal("v2", doc.Lines[0].Unmapped[EditLine.AgentKey]);
    }

    [Fact]
    public void Export_Derives_From_Model_As_Before()
    {
        // Unedited drifted lines export exactly like today (model IsDuet);
        // the stash only feeds the display rule, never the generator.
        var doc = new EditDocument();
        doc.Lines.Add(Edit(isDuet: true, agent: "v1"));
        var records = LyricAdapters.ToRecords(doc);
        var gen = TtmlGenerator.Generate(records, new(), new(false, false, false));
        Assert.True(gen.Success);
        Assert.Contains("ttm:agent=\"v2\"", gen.Data);
    }
}
