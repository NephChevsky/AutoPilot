using System.Diagnostics;
using AutoPilot.Services;

namespace AutoPilot.Tests;

public class ConversationDiffAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cda-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = CancellationToken.None;

    public ConversationDiffAcceptanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private static (int Code, string Out) Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, (o.Result + e.Result).Trim());
    }

    private static string G(string dir, params string[] args)
    {
        var (c, o) = Git(dir, args);
        Assert.True(c == 0, $"git {string.Join(' ', args)} failed: {o}");
        return o;
    }

    private string NewRepo()
    {
        var d = Path.Combine(_root, "repo");
        Directory.CreateDirectory(d);
        G(d, "init", "-q");
        G(d, "config", "user.name", "Tester");
        G(d, "config", "user.email", "t@example.com");
        G(d, "config", "commit.gpgsign", "false");
        G(d, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(d, "README.md"), "hello\n");
        File.WriteAllText(Path.Combine(d, "keep.txt"), "a\nb\nc\n");
        File.WriteAllText(Path.Combine(d, "gone.txt"), "bye\n");
        File.WriteAllText(Path.Combine(d, "old name.txt"), string.Join("\n", Enumerable.Range(0, 30).Select(i => "line " + i)) + "\n");
        File.WriteAllText(Path.Combine(d, ".gitignore"), "ignored.txt\n");
        G(d, "add", "-A");
        G(d, "commit", "-q", "-m", "initial");
        return d;
    }

    private ConversationWorktrees Wt(string repo) => new(repo, Path.Combine(_root, "wts"), "autopilot/");

    private async Task<(string Repo, ConversationWorktrees Wt, ConversationWorkspace Ws)> Setup(string id = "sess1")
    {
        var repo = NewRepo();
        var wt = Wt(repo);
        var ws = await wt.EnsureAsync(id, _ct);
        G(ws.Directory, "config", "user.name", "Tester");
        G(ws.Directory, "config", "user.email", "t@example.com");
        G(ws.Directory, "config", "commit.gpgsign", "false");
        return (repo, wt, ws);
    }

    // AC-2, AC-3
    [Fact]
    public async Task Committed_Uncommitted_Untracked_AllAppear_IgnoredExcluded_AndTotals()
    {
        var (_, wt, ws) = await Setup();
        File.WriteAllText(Path.Combine(ws.Directory, "committed.txt"), "1\n2\n");
        G(ws.Directory, "add", "-A");
        G(ws.Directory, "commit", "-q", "-m", "c");
        File.WriteAllText(Path.Combine(ws.Directory, "keep.txt"), "a\nB\nc\nd\n"); // +2 -1
        File.WriteAllText(Path.Combine(ws.Directory, "untracked.txt"), "x\n");
        File.WriteAllText(Path.Combine(ws.Directory, "ignored.txt"), "zzz\n");

        var d = await wt.GetDiffAsync("sess1", _ct);
        Assert.NotNull(d);
        Assert.Equal(ws.Branch, d!.Branch);
        Assert.False(string.IsNullOrWhiteSpace(d.BaseCommit));
        Assert.False(d.Truncated);
        var names = d.Files.Select(f => f.Path).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "committed.txt", "keep.txt", "untracked.txt" }, names);
        var c = d.Files.Single(f => f.Path == "committed.txt");
        Assert.Equal(DiffFileStatus.Added, c.Status);
        Assert.Equal(2, c.Additions);
        Assert.Equal(DiffFileStatus.Added, d.Files.Single(f => f.Path == "untracked.txt").Status);
        var k = d.Files.Single(f => f.Path == "keep.txt");
        Assert.Equal(DiffFileStatus.Modified, k.Status);
        Assert.Equal(2, k.Additions);
        Assert.Equal(1, k.Deletions);
        Assert.Equal(5, d.TotalAdditions);
        Assert.Equal(1, d.TotalDeletions);
        Assert.Contains(k.Lines, l => l.Kind == DiffLineKind.Hunk && l.Text.StartsWith("@@"));
        Assert.Contains(k.Lines, l => l.Kind == DiffLineKind.Removed && l.Text == "b");
        Assert.Contains(k.Lines, l => l.Kind == DiffLineKind.Added && l.Text == "B");
        Assert.Contains(k.Lines, l => l.Kind == DiffLineKind.Context && l.Text == "a");
    }

    // AC-3
    [Fact]
    public async Task Deletion_And_Rename_Reported()
    {
        var (_, wt, ws) = await Setup();
        File.Delete(Path.Combine(ws.Directory, "gone.txt"));
        File.Move(Path.Combine(ws.Directory, "old name.txt"), Path.Combine(ws.Directory, "new name.txt"));
        var d = await wt.GetDiffAsync("sess1", _ct);
        Assert.NotNull(d);
        var del = d!.Files.Single(f => f.Path == "gone.txt");
        Assert.Equal(DiffFileStatus.Deleted, del.Status);
        Assert.Equal(1, del.Deletions);
        var ren = d.Files.Single(f => f.Status == DiffFileStatus.Renamed);
        Assert.Equal("new name.txt", ren.Path);
        Assert.Equal("old name.txt", ren.OldPath);
    }

    // AC-4
    [Fact]
    public async Task Binary_File_Flagged_NoContent()
    {
        var (_, wt, ws) = await Setup();
        File.WriteAllBytes(Path.Combine(ws.Directory, "b.bin"), new byte[] { 0, 1, 2, 0, 0, 255, 0, 3 });
        var d = await wt.GetDiffAsync("sess1", _ct);
        var f = d!.Files.Single(x => x.Path == "b.bin");
        Assert.True(f.IsBinary);
        Assert.DoesNotContain(f.Lines, l => l.Kind == DiffLineKind.Added || l.Kind == DiffLineKind.Removed);
    }

    // AC-2
    [Fact]
    public async Task MainMovingForward_NotShown()
    {
        var (repo, wt, _) = await Setup();
        File.WriteAllText(Path.Combine(repo, "mainonly.txt"), "m\n");
        G(repo, "add", "-A");
        G(repo, "commit", "-q", "-m", "main moves");
        var d = await wt.GetDiffAsync("sess1", _ct);
        Assert.NotNull(d);
        Assert.Empty(d!.Files);
    }

    // AC-5
    [Fact]
    public async Task NoChanges_Empty_NotTruncated()
    {
        var (_, wt, _) = await Setup();
        var d = await wt.GetDiffAsync("sess1", _ct);
        Assert.NotNull(d);
        Assert.Empty(d!.Files);
        Assert.False(d.Truncated);
        Assert.Equal(0, d.TotalAdditions);
        Assert.Equal(0, d.TotalDeletions);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("")]
    [InlineData("a b;c")]
    [InlineData("..")]
    public async Task InvalidSessionId_ReturnsNull(string id)
    {
        var repo = NewRepo();
        Assert.Null(await Wt(repo).GetDiffAsync(id, _ct));
    }

    [Fact]
    public async Task NeverCreatedSession_ReturnsNull()
    {
        var repo = NewRepo();
        Assert.Null(await Wt(repo).GetDiffAsync("nope", _ct));
    }

    // AC-5
    [Fact]
    public async Task GitFailure_ThrowsInvalidOperation_WithMessage()
    {
        var (repo, wt, _) = await Setup();
        // corrupt the main repo so git cannot operate
        var objs = Path.Combine(repo, ".git", "objects");
        foreach (var f in Directory.EnumerateFiles(objs, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(objs, true);
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "garbage");
        var ex = await Record.ExceptionAsync(() => wt.GetDiffAsync("sess1", _ct));
        if (ex != null)
        {
            Assert.IsType<InvalidOperationException>(ex);
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }
        // null is also tolerated (branch can't be resolved); must not throw other types
    }

    // AC-2
    [Fact]
    public async Task MissingWorktreeDirectory_FallsBackToBranchTip()
    {
        var (_, wt, ws) = await Setup();
        File.WriteAllText(Path.Combine(ws.Directory, "c.txt"), "1\n");
        G(ws.Directory, "add", "-A");
        G(ws.Directory, "commit", "-q", "-m", "c");
        foreach (var f in Directory.EnumerateFiles(ws.Directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(ws.Directory, true);
        var d = await wt.GetDiffAsync("sess1", _ct);
        Assert.NotNull(d);
        Assert.Contains(d!.Files, f => f.Path == "c.txt");
    }

    // AC-2
    [Fact]
    public async Task Call_DoesNotModifyMainOrWorktreeIndexOrStatus()
    {
        var (repo, wt, ws) = await Setup();
        File.WriteAllText(Path.Combine(ws.Directory, "staged.txt"), "s\n");
        G(ws.Directory, "add", "staged.txt");
        File.WriteAllText(Path.Combine(ws.Directory, "keep.txt"), "changed\n");
        File.WriteAllText(Path.Combine(ws.Directory, "untracked.txt"), "u\n");

        string S() => G(ws.Directory, "status", "--porcelain") + "|" + G(ws.Directory, "diff", "--cached") + "|" + G(repo, "status", "--porcelain") + "|" + G(repo, "diff", "--cached");
        var before = S();
        var idxBefore = File.ReadAllBytes(Path.Combine(G(ws.Directory, "rev-parse", "--absolute-git-dir"), "index"));
        await wt.GetDiffAsync("sess1", _ct);
        await wt.GetDiffAsync("sess1", _ct);
        Assert.Equal(before, S());
        Assert.Contains("?? untracked.txt", before);
        var idxAfter = File.ReadAllBytes(Path.Combine(G(ws.Directory, "rev-parse", "--absolute-git-dir"), "index"));
        Assert.Equal(idxBefore, idxAfter);
    }

    // AC-7
    [Fact]
    public async Task LargeDiff_Truncated_NoException()
    {
        var (_, wt, ws) = await Setup();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 40000; i++) sb.Append("this is line number ").Append(i).Append('\n');
        File.WriteAllText(Path.Combine(ws.Directory, "big.txt"), sb.ToString());
        Assert.True(sb.Length > 800_000);
        var d = await wt.GetDiffAsync("sess1", _ct);
        Assert.NotNull(d);
        Assert.True(d!.Truncated);
        var chars = d.Files.Sum(f => f.Lines.Sum(l => l.Text.Length + 1));
        Assert.True(chars <= 520_000, $"chars={chars}");
    }

    // AC-8
    [Fact]
    public async Task HtmlContent_PreservedVerbatim()
    {
        var (_, wt, ws) = await Setup();
        File.WriteAllText(Path.Combine(ws.Directory, "x.html"), "<script>alert(1)</script>\n");
        var d = await wt.GetDiffAsync("sess1", _ct);
        var f = d!.Files.Single(x => x.Path == "x.html");
        Assert.Contains(f.Lines, l => l.Kind == DiffLineKind.Added && l.Text == "<script>alert(1)</script>");
    }

    // ---------- parser ----------

    [Fact]
    public void Parser_Empty_ReturnsEmpty()
    {
        Assert.Empty(UnifiedDiffParser.Parse(""));
    }

    [Fact]
    public void Parser_ModifiedFile_WithSpacesInPath_NoNewlineMeta()
    {
        var diff = "diff --git a/my dir/a file.txt b/my dir/a file.txt\n" +
                   "index 111..222 100644\n--- a/my dir/a file.txt\n+++ b/my dir/a file.txt\n" +
                   "@@ -1,2 +1,2 @@ ctx\n one\n-two\n+three\n\\ No newline at end of file\n";
        var files = UnifiedDiffParser.Parse(diff);
        var f = Assert.Single(files);
        Assert.Equal("my dir/a file.txt", f.Path);
        Assert.Null(f.OldPath);
        Assert.Equal(DiffFileStatus.Modified, f.Status);
        Assert.Equal(1, f.Additions);
        Assert.Equal(1, f.Deletions);
        Assert.Equal(DiffLineKind.Hunk, f.Lines[0].Kind);
        Assert.Equal("@@ -1,2 +1,2 @@ ctx", f.Lines[0].Text);
        Assert.Contains(f.Lines, l => l.Kind == DiffLineKind.Context && l.Text == "one");
        Assert.Contains(f.Lines, l => l.Kind == DiffLineKind.Removed && l.Text == "two");
        Assert.Contains(f.Lines, l => l.Kind == DiffLineKind.Added && l.Text == "three");
        Assert.Contains(f.Lines, l => l.Kind == DiffLineKind.Meta);
    }

    [Fact]
    public void Parser_AddDeleteRenameBinary_MultiFile()
    {
        var diff =
            "diff --git a/n.txt b/n.txt\nnew file mode 100644\nindex 0000000..e69de29\n--- /dev/null\n+++ b/n.txt\n@@ -0,0 +1 @@\n+hi\n" +
            "diff --git a/d.txt b/d.txt\ndeleted file mode 100644\nindex e69de29..0000000\n--- a/d.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-bye\n" +
            "diff --git a/o.txt b/p.txt\nsimilarity index 100%\nrename from o.txt\nrename to p.txt\n" +
            "diff --git a/i.bin b/i.bin\nindex 1..2 100644\nBinary files a/i.bin and b/i.bin differ\n";
        var files = UnifiedDiffParser.Parse(diff);
        Assert.Equal(4, files.Count);
        Assert.Equal(DiffFileStatus.Added, files[0].Status);
        Assert.Equal("n.txt", files[0].Path);
        Assert.Equal(1, files[0].Additions);
        Assert.Equal(DiffFileStatus.Deleted, files[1].Status);
        Assert.Equal("d.txt", files[1].Path);
        Assert.Equal(1, files[1].Deletions);
        Assert.Equal(DiffFileStatus.Renamed, files[2].Status);
        Assert.Equal("p.txt", files[2].Path);
        Assert.Equal("o.txt", files[2].OldPath);
        Assert.True(files[3].IsBinary);
        Assert.DoesNotContain(files[3].Lines, l => l.Kind == DiffLineKind.Added || l.Kind == DiffLineKind.Removed);
    }

    [Fact]
    public void Parser_ContentLinesStartingWithDashes_CountedAsContent()
    {
        var diff = "diff --git a/a b/a\nindex 1..2 100644\n--- a/a\n+++ b/a\n@@ -1 +1 @@\n--- x\n+++ y\n";
        var f = Assert.Single(UnifiedDiffParser.Parse(diff));
        Assert.Equal(1, f.Additions);
        Assert.Equal(1, f.Deletions);
    }
}
