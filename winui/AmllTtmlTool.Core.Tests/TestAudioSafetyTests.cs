using AmllTtmlTool.Core.Audio;
using Xunit;
using Xunit.Abstractions;

namespace AmllTtmlTool.Core.Tests;

// Item 4: test audio is never written. The D:\Audio originals are read-only
// test data; suites stage hash-verified temp copies (scripts/TestAudio.psm1)
// and the engine opens inputs strictly read-only (AudioInput).
public sealed class TestAudioSafetyTests
{
    private readonly ITestOutputHelper _out;

    public TestAudioSafetyTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void OpenReadShared_ReadOnly_File_Opens_And_Reads()
    {
        var path = Path.Combine(Path.GetTempPath(), "amll-ro-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            using var s = AudioInput.OpenReadShared(path);
            Assert.True(s.CanRead);
            Assert.False(s.CanWrite);
            Assert.Equal(4, s.Length);
            Assert.Throws<NotSupportedException>(() => s.WriteByte(9));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenReadShared_Missing_File_Throws_With_Path()
    {
        var ex = Assert.Throws<FileNotFoundException>(() =>
            AudioInput.OpenReadShared(Path.Combine(Path.GetTempPath(), "amll-nope-" + Guid.NewGuid().ToString("N"))));
        _out.WriteLine("message: " + ex.Message);
    }

    [Theory]
    [InlineData("D:\\Audio\\alac.m4a", true)]
    [InlineData("track.FLAC", true)]
    [InlineData("C:\\x\\y.mp3", true)]
    [InlineData("report.txt", false)]
    [InlineData("C:\\Temp\\clock-test-report.txt", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsAudioPath_Classifies_By_Extension(string? path, bool want) =>
        Assert.Equal(want, AudioInput.IsAudioPath(path));

    [Fact]
    public void GuardReportPath_Audio_Candidate_Falls_Back_To_Temp()
    {
        var got = AudioInput.GuardReportPath("D:\\Audio\\alac.m4a", "clock-test");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "clock-test-report.txt"), got);
    }

    [Fact]
    public void GuardReportPath_Text_Candidate_Passes_Through()
    {
        var p = Path.Combine(Path.GetTempPath(), "x-report.txt");
        Assert.Equal(p, AudioInput.GuardReportPath(p, "x"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "TestAudio.psm1")))
                return dir.FullName;
        }
        throw new InvalidOperationException(
            "repo root (scripts/TestAudio.psm1) not found above " + AppContext.BaseDirectory);
    }

    private static string SuiteText(string repo, string rel) =>
        File.ReadAllText(Path.Combine(repo, rel));

    [Fact]
    public void Staging_Module_Exists_And_Exports_Copy_Function()
    {
        var repo = RepoRoot();
        var mod = SuiteText(repo, Path.Combine("scripts", "TestAudio.psm1"));
        Assert.Contains("function New-TestAudioCopy", mod);
        Assert.Contains("Export-ModuleMember -Function New-TestAudioCopy", mod);
        Assert.Contains("TestAudio.hashes", mod);
    }

    [Fact]
    public void Hash_Manifest_Pins_Four_Originals()
    {
        var repo = RepoRoot();
        var lines = File.ReadAllLines(Path.Combine(repo, "scripts", "TestAudio.hashes"));
        var entries = lines
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, "^[0-9A-Fa-f]{64}\\s+\\S+"))
            .ToList();
        Assert.Equal(4, entries.Count);
        foreach (var e in entries) _out.WriteLine("pinned: " + e);
    }

    [Theory]
    [InlineData("ui-tests.ps1")]
    [InlineData("ui-tests-playerbar.ps1")]
    public void Player_Suites_Stage_Copies_And_Reference_No_Original(string suite)
    {
        var repo = RepoRoot();
        var text = SuiteText(repo, suite);
        Assert.Contains("TestAudio.psm1", text);
        Assert.Contains("New-TestAudioCopy", text);
        // D:\Audio may appear ONLY as a staging source: a New-TestAudioCopy
        // argument, or the $RawAudio default that flows into New-TestAudioCopy
        // on the next line. Anything else (hold args, picker stubs, report
        // paths) opens a copy, never the original.
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith('#')) continue; // comments open nothing
            if (!line.Contains("D:\\Audio") && !line.Contains("D:/Audio"))
                continue;
            var ok = line.Contains("New-TestAudioCopy")
                || (line.Contains("$RawAudio =") && i + 1 < lines.Length
                    && lines[i + 1].Contains("New-TestAudioCopy $RawAudio"));
            Assert.True(ok, $"{suite}:{i + 1} opens a library original: {line.Trim()}");
        }
    }

    [Fact]
    public void Run_Harness_Refuses_Library_Paths()
    {
        var repo = RepoRoot();
        var text = SuiteText(repo, Path.Combine("scripts", "run-harness.ps1"));
        Assert.Contains("STAGE-AUDIO-FIRST", text);
    }

    // Runs only where the library exists (this dev machine). Reports every
    // SHA256 so drift is visible in test output either way.
    [Fact]
    public void Library_Originals_ReadOnly_And_Match_Manifest()
    {
        const string lib = "D:\\Audio";
        if (!Directory.Exists(lib))
        {
            _out.WriteLine("SKIP: no D:\\Audio on this machine");
            return;
        }
        var repo = RepoRoot();
        var pinned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(Path.Combine(repo, "scripts", "TestAudio.hashes")))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, "^([0-9A-Fa-f]{64})\\s+(.+)$");
            if (m.Success) pinned[m.Groups[2].Value.Trim()] = m.Groups[1].Value.ToUpperInvariant();
        }
        var files = Directory.GetFiles(lib);
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var s = File.OpenRead(f);
            var hash = Convert.ToHexString(sha.ComputeHash(s));
            _out.WriteLine($"SHA256 {hash}  {name}");
            Assert.True((File.GetAttributes(f) & FileAttributes.ReadOnly) != 0,
                name + " is not read-only");
            if (pinned.TryGetValue(name, out var want))
                Assert.Equal(want, hash);
        }
    }
}
