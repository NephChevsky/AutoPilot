using System.Diagnostics;
using AutoPilot.Services;
using Microsoft.Extensions.AI;

namespace AutoPilot.Tests;

public class ConversationBranchAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cba-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = CancellationToken.None;

    public ConversationBranchAcceptanceTests() => Directory.CreateDirectory(_root);

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

    // ---------- helpers ----------

    private static (int Code, string Out) Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.bareRepository=all");
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

    private string NewRepo(string name = "repo", bool commit = true)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        G(d, "init", "-q");
        G(d, "config", "user.name", "Tester");
        G(d, "config", "user.email", "t@example.com");
        G(d, "config", "commit.gpgsign", "false");
        if (commit)
        {
            File.WriteAllText(Path.Combine(d, "README.md"), "hello\n");
            File.WriteAllText(Path.Combine(d, ".gitignore"), "ignored.txt\n");
            G(d, "add", "-A");
            G(d, "commit", "-q", "-m", "initial");
        }
        return d;
    }

    private string NewBare()
    {
        var b = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(b);
        G(b, "init", "--bare", "-q");
        return b;
    }

    private ConversationWorktrees Wt(string repo, string prefix = "autopilot/") =>
        new(repo, Path.Combine(_root, "wts"), prefix);

    private static WorkspaceTools Tools(string dir, string? branch, string remote = "origin") =>
        new(dir, branch, remote);

    private async Task<(string Repo, string Bare, ConversationWorkspace Ws, WorkspaceTools Tools)> PushSetup(string id = "sess1")
    {
        var repo = NewRepo();
        var bare = NewBare();
        G(repo, "remote", "add", "origin", bare);
        var ws = await Wt(repo).EnsureAsync(id, _ct);
        var t = Tools(ws.Directory, ws.Branch);
        await t.CaptureBaselineAsync(_ct);
        return (repo, bare, ws, t);
    }

    // ---------- AC-8 ----------

    [Fact]
    public void AC8_Defaults()
    {
        var s = new AutoPilotSettings();
        Assert.Equal("", s.WorktreesDirectory);
        Assert.Equal("autopilot/", s.BranchPrefix);
        Assert.Equal("origin", s.GitRemote);
    }

    [Fact]
    public void AC8_ResolveWorktreesDirectory_DefaultIsSiblingOfWorkingDirectory()
    {
        var wd = Path.Combine(_root, "proj");
        var s = new AutoPilotSettings { WorkingDirectory = wd + Path.DirectorySeparatorChar };
        Assert.Equal(Path.GetFullPath(wd) + ".worktrees", s.ResolveWorktreesDirectory());
    }

    [Fact]
    public void AC8_ResolveWorktreesDirectory_ConfiguredIsFullPath()
    {
        var s = new AutoPilotSettings { WorkingDirectory = Path.Combine(_root, "proj"), WorktreesDirectory = Path.Combine(_root, "a", "..", "wt") };
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "wt")), s.ResolveWorktreesDirectory());
    }

    // ---------- naming ----------

    [Fact]
    public void Naming_BranchAndPath()
    {
        var w = new ConversationWorktrees(Path.Combine(_root, "r"), Path.Combine(_root, "wts"), "autopilot/");
        Assert.Equal("autopilot/abc-1", w.GetBranchName("abc-1"));
        Assert.Equal(Path.Combine(_root, "wts", "abc-1"), w.GetWorktreePath("abc-1"));
        var w2 = new ConversationWorktrees(Path.Combine(_root, "r"), Path.Combine(_root, "wts"), "x/");
        Assert.Equal("x/abc-1", w2.GetBranchName("abc-1"));
    }

    // ---------- AC-1 (reachable part) ----------

    [Fact]
    public async Task AC1_Ensure_CreatesBranchAtHeadAndWorktree()
    {
        var repo = NewRepo();
        var head = G(repo, "rev-parse", "HEAD");
        var w = Wt(repo);
        Assert.False(await w.BranchExistsAsync("s1", _ct));

        var ws = await w.EnsureAsync("s1", _ct);

        Assert.Equal("s1", ws.SessionId);
        Assert.Equal("autopilot/s1", ws.Branch);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "wts", "s1")), Path.GetFullPath(ws.Directory));
        Assert.True(Directory.Exists(ws.Directory));
        Assert.True(File.Exists(Path.Combine(ws.Directory, "README.md")));
        Assert.True(await w.BranchExistsAsync("s1", _ct));
        Assert.Equal(head, G(repo, "rev-parse", "autopilot/s1"));
        Assert.Equal("autopilot/s1", G(ws.Directory, "rev-parse", "--abbrev-ref", "HEAD"));
    }

    [Fact]
    public async Task AC1_Ensure_BranchesFromCurrentHeadOfMainDirectory()
    {
        var repo = NewRepo();
        File.WriteAllText(Path.Combine(repo, "second.txt"), "2");
        G(repo, "add", "-A"); G(repo, "commit", "-q", "-m", "second");
        var ws = await Wt(repo).EnsureAsync("s1", _ct);
        Assert.True(File.Exists(Path.Combine(ws.Directory, "second.txt")));
        Assert.Equal(G(repo, "rev-parse", "HEAD"), G(repo, "rev-parse", "autopilot/s1"));
    }

    [Fact]
    public async Task AC1_Ensure_CustomPrefix()
    {
        var repo = NewRepo();
        var ws = await Wt(repo, "feat/").EnsureAsync("s1", _ct);
        Assert.Equal("feat/s1", ws.Branch);
        Assert.Equal("feat/s1", G(ws.Directory, "rev-parse", "--abbrev-ref", "HEAD"));
    }

    [Fact]
    public async Task Ensure_IsIdempotent()
    {
        var repo = NewRepo();
        var w = Wt(repo);
        var a = await w.EnsureAsync("s1", _ct);
        File.WriteAllText(Path.Combine(a.Directory, "keep.txt"), "x");
        var b = await w.EnsureAsync("s1", _ct);
        Assert.Equal(a, b);
        Assert.True(File.Exists(Path.Combine(b.Directory, "keep.txt")));
    }

    // ---------- AC-3 (reachable part) ----------

    [Fact]
    public async Task AC3_Ensure_RecreatesMissingWorktreeFromBranch()
    {
        var repo = NewRepo();
        var w = Wt(repo);
        var a = await w.EnsureAsync("s1", _ct);
        File.WriteAllText(Path.Combine(a.Directory, "work.txt"), "w");
        G(a.Directory, "add", "-A"); G(a.Directory, "commit", "-q", "-m", "work");
        var sha = G(a.Directory, "rev-parse", "HEAD");

        foreach (var f in Directory.EnumerateFiles(a.Directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(a.Directory, true);
        Assert.True(await w.BranchExistsAsync("s1", _ct));

        var b = await w.EnsureAsync("s1", _ct);
        Assert.True(Directory.Exists(b.Directory));
        Assert.Equal("w", File.ReadAllText(Path.Combine(b.Directory, "work.txt")));
        Assert.Equal(sha, G(b.Directory, "rev-parse", "HEAD"));
        Assert.Equal("autopilot/s1", G(b.Directory, "rev-parse", "--abbrev-ref", "HEAD"));
    }

    // ---------- AC-2 isolation ----------

    [Fact]
    public async Task AC2_Isolation_ChangesStayInWorktree()
    {
        var repo = NewRepo();
        var w = Wt(repo);
        var a = await w.EnsureAsync("s1", _ct);
        var b = await w.EnsureAsync("s2", _ct);
        File.WriteAllText(Path.Combine(a.Directory, "only-a.txt"), "a");
        File.WriteAllText(Path.Combine(a.Directory, "README.md"), "changed\n");
        Assert.False(File.Exists(Path.Combine(repo, "only-a.txt")));
        Assert.False(File.Exists(Path.Combine(b.Directory, "only-a.txt")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(repo, "README.md")).Trim());
        Assert.Equal("", G(repo, "status", "--porcelain"));
    }

    [Fact]
    public async Task AC2_GetDiff_OperatesOnWorktree()
    {
        var repo = NewRepo();
        var a = await Wt(repo).EnsureAsync("s1", _ct);
        var t = Tools(a.Directory, a.Branch);
        await t.CaptureBaselineAsync(_ct);
        File.WriteAllText(Path.Combine(a.Directory, "new-in-wt.txt"), "brandnew");
        File.WriteAllText(Path.Combine(repo, "new-in-main.txt"), "mainonly");

        var diff = await t.GetDiffAsync(false, _ct);
        Assert.Contains("new-in-wt.txt", diff);
        Assert.DoesNotContain("new-in-main.txt", diff);
    }

    [Fact]
    public async Task AC2_GetDiff_NoChanges()
    {
        var repo = NewRepo();
        var a = await Wt(repo).EnsureAsync("s1", _ct);
        var t = Tools(a.Directory, a.Branch);
        await t.CaptureBaselineAsync(_ct);
        Assert.Equal("No changes relative to the baseline.", (await t.GetDiffAsync(false, _ct)).Trim());
        Assert.Equal("No pre-existing uncommitted changes.", (await t.GetDiffAsync(true, _ct)).Trim());
    }

    [Fact]
    public async Task AC1_ToolsDeclared_GetDiffAndCommitAndPushOnly_NoRunVerification()
    {
        var repo = NewRepo();
        var a = await Wt(repo).EnsureAsync("s1", _ct);
        var t = Tools(a.Directory, a.Branch);
        var names = t.CreateTools().Select(x => x.Name).ToList();
        Assert.Contains("get_diff", names);
        Assert.Contains("commit_and_push", names);
        Assert.DoesNotContain("run_verification", names);
        Assert.Equal(2, names.Count);
    }

    [Fact]
    public void AC1_Constructor_DefaultsBranchAndRemote()
    {
        var t = new WorkspaceTools(_root);
        Assert.Equal(["commit_and_push", "get_diff"], t.CreateTools().Select(x => x.Name).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void AC2_AC5_Settings_NoVerificationProperties()
    {
        var names = typeof(AutoPilotSettings).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("VerificationCommands", names);
        Assert.DoesNotContain("VerificationTimeoutSeconds", names);
    }

    [Fact]
    public void AC2_AppSettingsJson_HasNoVerificationKeys()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "AutoPilot", "appsettings.json"));
        Assert.DoesNotContain("VerificationCommands", text);
        Assert.DoesNotContain("VerificationTimeoutSeconds", text);
    }

    [Fact]
    public void AC3_AC4_AgentFiles_DoNotMentionRunVerification()
    {
        var files = Directory.GetFiles(Path.Combine(RepoRoot(), "AutoPilot", "Agents"), "*.md");
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            Assert.False(text.Contains("run_verification"), Path.GetFileName(f) + " still mentions run_verification");
        }
    }

    [Fact]
    public void AC3_AC4_Orchestrator_EvidenceRules()
    {
        var agents = Path.Combine(RepoRoot(), "AutoPilot", "Agents");
        var orch = File.ReadAllText(Path.Combine(agents, "orchestrator.md"));
        Assert.Contains("INCONCLUSIVE", orch);
        Assert.Contains("commit_and_push", orch);
        Assert.Contains("exit code", orch, StringComparison.OrdinalIgnoreCase);
        foreach (var n in new[] { "implementer.md", "tester.md" })
            Assert.Contains("exit code", File.ReadAllText(Path.Combine(agents, n)), StringComparison.OrdinalIgnoreCase);
        var tester = File.ReadAllText(Path.Combine(agents, "tester.md"));
        Assert.Contains("full", tester, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test suite", tester, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AC5_NoReferencesRemainInRepository()
    {
        var root = RepoRoot();
        string[] needles = ["run_verification", "VerificationCommands", "VerificationTimeoutSeconds"];
        var skipDirs = new HashSet<string>(["bin", "obj", ".git", ".vs"], StringComparer.OrdinalIgnoreCase);
        const string thisFile = "ConversationBranchAcceptanceTests.cs";
        var hits = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (skipDirs.Contains(Path.GetFileName(sub))) continue;
                pending.Push(sub);
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (string.Equals(Path.GetFileName(f), thisFile, StringComparison.OrdinalIgnoreCase)) continue;
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".cs" or ".md" or ".json" or ".csproj" or ".slnx" or ".razor" or ".yml" or ".yaml" or ".txt" or ".props")
                {
                    var text = File.ReadAllText(f);
                    if (needles.Any(n => text.Contains(n))) hits.Add(f);
                }
            }
        }
        Assert.Empty(hits);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "AutoPilot", "Agents"))) return d.FullName;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    // ---------- AC-5 ----------

    [Fact]
    public async Task AC5_CommitAndPush_Pushes()
    {
        var (repo, bare, ws, t) = await PushSetup();
        File.WriteAllText(Path.Combine(ws.Directory, "feature.txt"), "f");
        File.WriteAllText(Path.Combine(ws.Directory, "ignored.txt"), "secret");

        var r = await t.CommitAndPushAsync("Add feature", _ct);

        Assert.StartsWith("STATUS: PUSHED", r);
        Assert.Contains(ws.Branch, r);
        var sha = G(ws.Directory, "rev-parse", "HEAD");
        Assert.Contains(sha, r);
        var ls = G(repo, "ls-remote", bare, $"refs/heads/{ws.Branch}");
        Assert.StartsWith(sha, ls);
        Assert.Equal("Add feature", G(bare, "log", "-1", "--format=%s", ws.Branch));
        var files = G(bare, "ls-tree", "-r", "--name-only", ws.Branch);
        Assert.Contains("feature.txt", files);
        Assert.DoesNotContain("ignored.txt", files);
        // only that branch pushed
        Assert.DoesNotContain("refs/heads/master", G(repo, "ls-remote", bare));
        Assert.DoesNotContain("refs/heads/main", G(repo, "ls-remote", bare));
        // upstream set
        Assert.Contains(ws.Branch, G(ws.Directory, "rev-parse", "--abbrev-ref", "@{u}"));
    }

    [Fact]
    public async Task AC5_CommitAndPush_NothingToCommit_NoNewCommit()
    {
        var (_, _, ws, t) = await PushSetup();
        var before = G(ws.Directory, "rev-parse", "HEAD");
        var r = await t.CommitAndPushAsync("noop", _ct);
        Assert.Equal(before, G(ws.Directory, "rev-parse", "HEAD"));
        Assert.Equal("1", G(ws.Directory, "rev-list", "--count", "HEAD"));
        Assert.DoesNotContain("Exception", r);
    }

    [Fact]
    public async Task AC5_CommitAndPush_SecondCallPushesAdditionalCommit()
    {
        var (repo, bare, ws, t) = await PushSetup();
        File.WriteAllText(Path.Combine(ws.Directory, "a.txt"), "1");
        Assert.StartsWith("STATUS: PUSHED", await t.CommitAndPushAsync("one", _ct));
        File.WriteAllText(Path.Combine(ws.Directory, "b.txt"), "2");
        var r = await t.CommitAndPushAsync("two", _ct);
        Assert.StartsWith("STATUS: PUSHED", r);
        Assert.StartsWith(G(ws.Directory, "rev-parse", "HEAD"), G(repo, "ls-remote", bare, $"refs/heads/{ws.Branch}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AC5_EmptyMessage_Fails(string msg)
    {
        var (_, bare, ws, t) = await PushSetup();
        File.WriteAllText(Path.Combine(ws.Directory, "x.txt"), "x");
        var before = G(ws.Directory, "rev-parse", "HEAD");
        var r = await t.CommitAndPushAsync(msg, _ct);
        Assert.StartsWith("STATUS: FAILED", r);
        Assert.Equal(before, G(ws.Directory, "rev-parse", "HEAD"));
        Assert.Equal("", G(bare, "branch", "--list"));
    }

    [Fact]
    public async Task AC5_NoRemote_Fails_NoCommitMade()
    {
        var repo = NewRepo();
        var ws = await Wt(repo).EnsureAsync("s1", _ct);
        var t = Tools(ws.Directory, ws.Branch);
        await t.CaptureBaselineAsync(_ct);
        File.WriteAllText(Path.Combine(ws.Directory, "x.txt"), "x");
        var before = G(ws.Directory, "rev-parse", "HEAD");
        var r = await t.CommitAndPushAsync("msg", _ct);
        Assert.StartsWith("STATUS: FAILED", r);
        Assert.Equal(before, G(ws.Directory, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task AC5_ConfiguredRemoteNameMissing_Fails_NoCommitMade()
    {
        var (_, _, ws, _) = await PushSetup();
        var t = Tools(ws.Directory, ws.Branch, "upstream");
        File.WriteAllText(Path.Combine(ws.Directory, "x.txt"), "x");
        var before = G(ws.Directory, "rev-parse", "HEAD");
        var r = await t.CommitAndPushAsync("msg", _ct);
        Assert.StartsWith("STATUS: FAILED", r);
        Assert.Equal(before, G(ws.Directory, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task AC5_PushFailure_Fails_MentionsLocalCommitRetained()
    {
        var repo = NewRepo();
        G(repo, "remote", "add", "origin", Path.Combine(_root, "does-not-exist.git"));
        var ws = await Wt(repo).EnsureAsync("s1", _ct);
        var t = Tools(ws.Directory, ws.Branch);
        await t.CaptureBaselineAsync(_ct);
        File.WriteAllText(Path.Combine(ws.Directory, "x.txt"), "x");
        var before = G(ws.Directory, "rev-parse", "HEAD");
        var r = await t.CommitAndPushAsync("msg", _ct);
        Assert.StartsWith("STATUS: FAILED", r);
        Assert.Contains("local commit", r, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(before, G(ws.Directory, "rev-parse", "HEAD"));
        Assert.Equal("msg", G(ws.Directory, "log", "-1", "--format=%s"));
    }

    [Fact]
    public async Task AC5_BranchNull_Fails()
    {
        var repo = NewRepo();
        var bare = NewBare();
        G(repo, "remote", "add", "origin", bare);
        var t = Tools(repo, null);
        await t.CaptureBaselineAsync(_ct);
        File.WriteAllText(Path.Combine(repo, "x.txt"), "x");
        var before = G(repo, "rev-parse", "HEAD");
        var r = await t.CommitAndPushAsync("msg", _ct);
        Assert.StartsWith("STATUS: FAILED", r);
        Assert.Equal(before, G(repo, "rev-parse", "HEAD"));
        Assert.Equal("", G(bare, "branch", "--list"));
    }

    [Fact]
    public async Task AC5_CommitFails_MissingIdentity_Fails()
    {
        var repo = NewRepo();
        var bare = NewBare();
        G(repo, "remote", "add", "origin", bare);
        var ws = await Wt(repo).EnsureAsync("s1", _ct);
        // Make identity unresolvable for this test: override with empty values is unreliable, so
        // use a fresh HOME-less config by pointing at a repo-local empty identity.
        G(repo, "config", "user.useConfigOnly", "true");
        G(repo, "config", "--unset", "user.name");
        G(repo, "config", "--unset", "user.email");
        var (c, _) = Git(ws.Directory, "config", "user.email");
        if (c == 0) return; // global identity present; cannot force failure without env control
        var t = Tools(ws.Directory, ws.Branch);
        await t.CaptureBaselineAsync(_ct);
        File.WriteAllText(Path.Combine(ws.Directory, "x.txt"), "x");
        var r = await t.CommitAndPushAsync("msg", _ct);
        Assert.StartsWith("STATUS: FAILED", r);
        Assert.Equal("", G(bare, "branch", "--list"));
    }

    // ---------- AC-6 ----------

    [Fact]
    public async Task AC6_PreExisting_NotAffectedByCommits()
    {
        var repo = NewRepo();
        var bare = NewBare();
        G(repo, "remote", "add", "origin", bare);
        var ws = await Wt(repo).EnsureAsync("s1", _ct);
        File.WriteAllText(Path.Combine(ws.Directory, "pre.txt"), "pre-existing");
        var t = Tools(ws.Directory, ws.Branch);
        await t.CaptureBaselineAsync(_ct);

        File.WriteAllText(Path.Combine(ws.Directory, "run-change.txt"), "from run");
        Assert.StartsWith("STATUS: PUSHED", await t.CommitAndPushAsync("run", _ct));

        var pre = await t.GetDiffAsync(true, _ct);
        Assert.DoesNotContain("run-change.txt", pre);
        var run = await t.GetDiffAsync(false, _ct);
        Assert.Contains("run-change.txt", run);
    }

    [Fact]
    public async Task AC6_PreExisting_CommittedTrackedEditNotReported()
    {
        var (_, _, ws, t) = await PushSetup();
        File.WriteAllText(Path.Combine(ws.Directory, "README.md"), "edited by run\n");
        Assert.StartsWith("STATUS: PUSHED", await t.CommitAndPushAsync("edit", _ct));
        Assert.Equal("No pre-existing uncommitted changes.", (await t.GetDiffAsync(true, _ct)).Trim());
    }

    // ---------- AC-7 / invalid ids ----------

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("a..b")]
    [InlineData("a\\b")]
    public async Task AC7_InvalidIds_ThrowArgumentException_CreateNothing(string id)
    {
        var repo = NewRepo();
        var w = Wt(repo);
        await Assert.ThrowsAsync<ArgumentException>(() => w.EnsureAsync(id, _ct));
        var wts = Path.Combine(_root, "wts");
        Assert.True(!Directory.Exists(wts) || !Directory.EnumerateFileSystemEntries(wts).Any());
        Assert.Equal("", G(repo, "branch", "--list", "autopilot/*"));
        Assert.False(Directory.Exists(Path.Combine(_root, "x")));
    }

    [Fact]
    public async Task AC7_NotAGitRepo_ThrowsInvalidOperation_NoWorktree()
    {
        var d = Path.Combine(_root, "plain");
        Directory.CreateDirectory(d);
        var w = Wt(d);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.EnsureAsync("s1", _ct));
        Assert.False(Directory.Exists(Path.Combine(_root, "wts", "s1")));
    }

    [Fact]
    public async Task AC7_RepoWithoutCommits_ThrowsInvalidOperation_NoWorktree()
    {
        var repo = NewRepo("empty", commit: false);
        var w = Wt(repo);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.EnsureAsync("s1", _ct));
        Assert.False(Directory.Exists(Path.Combine(_root, "wts", "s1")));
    }
}
