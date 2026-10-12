namespace AmllTtmlTool.Core.Lyrics;

// Single command layer: EVERY document mutation goes through an
// ILyricCommand that can apply and revert itself. EditDocument keeps the
// undo/redo stacks; one Execute call is exactly one history step (no
// time-based coalescing — the original has none either).

public interface ILyricCommand
{
    string Description { get; }
    void Apply(EditDocument document);
    void Revert(EditDocument document);
}

public abstract class LyricCommand : ILyricCommand
{
    public abstract string Description { get; }
    public abstract void Apply(EditDocument document);
    public abstract void Revert(EditDocument document);

    protected static int ClampIndex(int index, int count) =>
        Math.Max(0, Math.Min(index, count));
}
