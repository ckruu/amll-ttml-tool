namespace AmllTtmlTool.Core.Lyrics;

// Effective duet presentation (Phase 3 fix, step 6). The model IsDuet keeps
// exact WASM import semantics (first-<p>-relative baseline plus the legacy
// duet restore, which together mark every line of drifted files like
// broken_down.ttml). The badge/menu/ribbon duet state instead derives from
// the raw ttm:agent stashed at import: only lines whose agent differs from
// the document primary agent are shown as duet. Lines without a stash
// (new/duplicate/LRC/text) follow IsDuet. Toggling V2/BG clears the stash
// (SetLineFlagsCommand), so edited lines follow their flags and export the
// standard form.
public static class DuetDisplay
{
    // Normalized agent: missing (unstashed) counts as "v1" like the parser;
    // an explicit "" stays distinct.
    public static string NormalizeAgent(EditLine line) =>
        line.Unmapped.TryGetValue(EditLine.AgentKey, out var a) ? a : "v1";

    // Primary agent: most frequent normalized agent among non-BG lines;
    // ties go to the first non-BG line; no lines (or no non-BG lines), or
    // no stashed agents at all, resolve to the Apple main voice "v1".
    // This matches WASM/first-<p> behavior on every standard file (v1
    // first and most frequent) and only diverges where those drift: v2-first
    // files, all-v2 files, and non-v1/v2 agent ids.
    public static string PrimaryAgent(IEnumerable<EditLine> lines)
    {
        var ordered = lines.Where(l => !l.IsBg).ToList();
        if (ordered.Count == 0) return "v1";
        if (!ordered.Any(l => l.Unmapped.ContainsKey(EditLine.AgentKey))) return "v1";
        return ordered.GroupBy(NormalizeAgent)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => ordered.FindIndex(l => NormalizeAgent(l) == g.Key))
            .First().Key;
    }

    public static bool EffectiveDuet(EditLine line, string primary) =>
        line.Unmapped.ContainsKey(EditLine.AgentKey)
            ? NormalizeAgent(line) != primary
            : line.IsDuet;
}
