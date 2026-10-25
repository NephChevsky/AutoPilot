using System.Diagnostics;
using AutoPilot.Services;
using Microsoft.Extensions.Options;

namespace AutoPilot.Tests;

public class MultiRepositoryAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mra-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = CancellationToken.None;

    public MultiRepositoryAcceptanceTests() => Directory.CreateDirectory(_root);

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

    private static string G(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        var text = (o.Result + e.Result).Trim();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {text}");
        return text;
    }

    private string NewRepo(string name, string? parent = null, bool withRemote = false)
    {
        var d = Path.Combine(parent ?? _root, name);
        Directory.CreateDirectory(d);
        G(d, "init", "-q", "-b", "main");
        G(d, "config", "user.name", "Tester");
        G(d, "config", "user.email", "t@example.com");
        G(d, "config", "commit.gpgsign", "false");
        G(d, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(d, "README.md"), name + "\n");
        G(d, "add", "-A");
        G(d, "commit", "-q", "-m", "initial");
        if (withRemote)
        {
            var bare = Path.Combine(_root, "_remotes", name + ".git");
            Directory.CreateDirectory(bare);
            G(bare, "init", "-q", "--bare", "-b", "main");
            G(d, "remote", "add", "origin", bare);
            G(d, "push", "-q", "origin", "main");
        }
        return d;
    }

    private static RepositoryCatalog Catalog(string workingDir, string worktreesDir = "", string prefix = "autopilot/") =>
        new(Options.Create(new AutoPilotSettings { WorkingDirectory = workingDir, WorktreesDirectory = worktreesDir, BranchPrefix = prefix }));

    private static bool HasBranch(string repo, string branch)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "rev-parse", "-q", "--verify", "refs/heads/" + branch }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    // ---------- AC-1 ----------

    [Fact]
    public void AC1_Discovery_FindsOnlyGitChildren_SortedCaseInsensitive_ExcludesWorktreesAndNonRepos()
    {
        var b = NewRepo("beta");
        var a = NewRepo("Alpha");
        var c = NewRepo("charlie");
        // (a *.worktrees folder that itself contains .git is contrived; realistic case covered by the next test)
        Directory.CreateDirectory(Path.Combine(_root, "plain"));
        Directory.CreateDirectory(Path.Combine(_root, "plain", "nested"));
        NewRepo("nested-repo", Path.Combine(_root, "plain")); // grandchild: not discovered
        // .git as a file (like a worktree/submodule)
        var gitFile = Path.Combine(_root, "filegit");
        Directory.CreateDirectory(gitFile);
        File.WriteAllText(Path.Combine(gitFile, ".git"), "gitdir: somewhere");
        File.WriteAllText(Path.Combine(_root, "afile.txt"), "x");

        var repos = Catalog(_root).GetRepositories();

        Assert.Equal(new[] { "Alpha", "beta", "charlie", "filegit" }, repos.Select(r => r.Name).ToArray());
        Assert.Equal(Path.GetFullPath(a), Path.GetFullPath(repos[0].Path));
        Assert.Equal(Path.GetFullPath(b), Path.GetFullPath(repos[1].Path));
        Assert.Equal(Path.GetFullPath(c), Path.GetFullPath(repos[2].Path));
    }

    [Fact]
    public void AC1_Discovery_ExcludesWorktreeFolders_WithoutGitEntry()
    {
        NewRepo("app");
        var wt = Path.Combine(_root, "app.worktrees");
        Directory.CreateDirectory(Path.Combine(wt, "sess1"));
        Assert.DoesNotContain(Catalog(_root).GetRepositories(), r => r.Name.EndsWith(".worktrees"));
        Assert.Single(Catalog(_root).GetRepositories());
    }

    [Fact]
    public void AC1_Discovery_MissingWorkingDirectory_ReturnsEmpty()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        Assert.Empty(Catalog(missing).GetRepositories());
        Assert.Empty(RepositoryCatalog.Discover(missing));
    }

    [Fact]
    public void AC1_Discovery_EmptyRoot_ReturnsEmpty()
    {
        Assert.Empty(Catalog(_root).GetRepositories());
    }

    [Fact]
    public void AC1_Discovery_RescansOnEveryCall()
    {
        NewRepo("one");
        var catalog = Catalog(_root);
        Assert.Single(catalog.GetRepositories());
        NewRepo("two");
        Assert.Equal(new[] { "one", "two" }, catalog.GetRepositories().Select(r => r.Name).ToArray());
    }

    [Fact]
    public void AC1_StaticDiscover_MatchesInstance()
    {
        NewRepo("x");
        NewRepo("y");
        Assert.Equal(new[] { "x", "y" }, RepositoryCatalog.Discover(_root).Select(r => r.Name).ToArray());
    }

    // ---------- AC-3 settings ----------

    [Fact]
    public void AC3_ResolveWorktreesDirectory_Default_IsSiblingWorktreesFolder()
    {
        var s = new AutoPilotSettings();
        var repo = Path.Combine(_root, "myrepo");
        Assert.Equal(Path.GetFullPath(repo) + ".worktrees", s.ResolveWorktreesDirectory(repo));
        Assert.Equal(Path.GetFullPath(repo) + ".worktrees", s.ResolveWorktreesDirectory(repo + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void AC3_ResolveWorktreesDirectory_Configured_UsesRepoNameSubfolder()
    {
        var wt = Path.Combine(_root, "wts");
        var s = new AutoPilotSettings { WorktreesDirectory = wt };
        var repo = Path.Combine(_root, "myrepo");
        Assert.Equal(Path.Combine(Path.GetFullPath(wt), "myrepo"), s.ResolveWorktreesDirectory(repo));
        Assert.Equal(Path.Combine(Path.GetFullPath(wt), "myrepo"), s.ResolveWorktreesDirectory(repo + Path.DirectorySeparatorChar));
    }

    // ---------- AC-3 behaviour ----------

    [Fact]
    public void AC3_GetWorktrees_CachedPerRepository()
    {
        var a = NewRepo("a");
        var b = NewRepo("b");
        var cat = Catalog(_root);
        var repos = cat.GetRepositories();
        Assert.Same(cat.GetWorktrees(repos[0]), cat.GetWorktrees(repos[0]));
        Assert.NotSame(cat.GetWorktrees(repos[0]), cat.GetWorktrees(repos[1]));
    }

    [Fact]
    public async Task AC3_Ensure_DefaultLayout_CreatesBranchInSelectedRepoOnly()
    {
        var a = NewRepo("repoA");
        var b = NewRepo("repoB");
        // make A's HEAD distinguishable
        File.WriteAllText(Path.Combine(a, "only-a.txt"), "a");
        G(a, "add", "-A"); G(a, "commit", "-q", "-m", "a2");
        var headA = G(a, "rev-parse", "HEAD");

        var cat = Catalog(_root);
        var repoA = cat.GetRepositories().Single(r => r.Name == "repoA");
        var wts = cat.GetWorktrees(repoA);
        const string id = "11111111-1111-1111-1111-111111111111";

        Assert.False(await wts.BranchExistsAsync(id, _ct));
        var ws = await wts.EnsureAsync(id, _ct);

        var expected = Path.Combine(Path.GetFullPath(a) + ".worktrees", id);
        Assert.Equal(expected, Path.GetFullPath(ws.Directory));
        Assert.Equal(expected, wts.GetWorktreePath(id));
        Assert.Equal("autopilot/" + id, ws.Branch);
        Assert.True(Directory.Exists(expected));
        Assert.True(HasBranch(a, ws.Branch));
        Assert.False(HasBranch(b, ws.Branch));
        Assert.False(Directory.Exists(Path.Combine(Path.GetFullPath(b) + ".worktrees", id)));
        Assert.Equal(headA, G(expected, "rev-parse", "HEAD"));
        Assert.True(File.Exists(Path.Combine(expected, "only-a.txt")));
        Assert.True(await wts.BranchExistsAsync(id, _ct));
        // the other repo's worktrees object doesn't see it
        var repoB = cat.GetRepositories().Single(r => r.Name == "repoB");
        Assert.False(await cat.GetWorktrees(repoB).BranchExistsAsync(id, _ct));
    }

    [Fact]
    public async Task AC3_Ensure_ConfiguredWorktreesDirectory_UsesRepoNameSubfolder()
    {
        var a = NewRepo("repoA");
        var b = NewRepo("repoB");
        var wtRoot = Path.Combine(_root, "central");
        var cat = Catalog(_root, wtRoot);
        const string id = "22222222-2222-2222-2222-222222222222";

        var wtsB = cat.GetWorktrees(cat.GetRepositories().Single(r => r.Name == "repoB"));
        var ws = await wtsB.EnsureAsync(id, _ct);

        var expected = Path.Combine(Path.GetFullPath(wtRoot), "repoB", id);
        Assert.Equal(expected, Path.GetFullPath(ws.Directory));
        Assert.True(Directory.Exists(expected));
        Assert.True(HasBranch(b, ws.Branch));
        Assert.False(HasBranch(a, ws.Branch));
        Assert.False(Directory.Exists(Path.Combine(wtRoot, "repoA")));
        Assert.False(Directory.Exists(Path.GetFullPath(b) + ".worktrees"));
    }

    [Fact]
    public async Task AC3_Ensure_CustomBranchPrefix_AndSameIdInTwoRepos_AreIndependent()
    {
        var a = NewRepo("repoA");
        var b = NewRepo("repoB");
        var cat = Catalog(_root, prefix: "ap/");
        const string id = "33333333-3333-3333-3333-333333333333";
        var repos = cat.GetRepositories();
        var wa = await cat.GetWorktrees(repos[0]).EnsureAsync(id, _ct);
        var wb = await cat.GetWorktrees(repos[1]).EnsureAsync(id, _ct);
        Assert.Equal("ap/" + id, wa.Branch);
        Assert.NotEqual(Path.GetFullPath(wa.Directory), Path.GetFullPath(wb.Directory));
        Assert.True(HasBranch(a, wa.Branch));
        Assert.True(HasBranch(b, wb.Branch));
    }

    // ---------- AC-6 ----------

    [Fact]
    public async Task AC6_GetDiff_ReportsChangesOfOwnRepositoryOnly()
    {
        var a = NewRepo("repoA");
        var b = NewRepo("repoB");
        var cat = Catalog(_root);
        var repos = cat.GetRepositories();
        var wtsA = cat.GetWorktrees(repos.Single(r => r.Name == "repoA"));
        var wtsB = cat.GetWorktrees(repos.Single(r => r.Name == "repoB"));
        const string id = "44444444-4444-4444-4444-444444444444";

        var wsA = await wtsA.EnsureAsync(id, _ct);
        File.WriteAllText(Path.Combine(wsA.Directory, "new-in-a.txt"), "hello\n");

        var diffA = await wtsA.GetDiffAsync(id, _ct);
        Assert.NotNull(diffA);
        Assert.Contains(diffA!.Files, f => f.Path == "new-in-a.txt");
        Assert.Null(await wtsB.GetDiffAsync(id, _ct));
        Assert.False(File.Exists(Path.Combine(b, "new-in-a.txt")));
    }

    [Fact]
    public async Task AC6_Merge_MergesIntoOwnRepoCheckedOutBranch_AndPushesToItsRemote_NotOtherRepo()
    {
        var a = NewRepo("repoA", withRemote: true);
        var b = NewRepo("repoB", withRemote: true);
        // repo A has a non-default branch checked out: merge must target it
        G(a, "checkout", "-q", "-b", "develop");
        var bHeadBefore = G(b, "rev-parse", "HEAD");
        var bRemoteBefore = G(b, "ls-remote", "origin");

        var cat = Catalog(_root);
        var repos = cat.GetRepositories();
        var wtsA = cat.GetWorktrees(repos.Single(r => r.Name == "repoA"));
        const string id = "55555555-5555-5555-5555-555555555555";
        var ws = await wtsA.EnsureAsync(id, _ct);
        File.WriteAllText(Path.Combine(ws.Directory, "feature.txt"), "feat\n");
        G(ws.Directory, "add", "-A");
        G(ws.Directory, "commit", "-q", "-m", "feature");

        var result = await wtsA.MergeAsync(id, "origin", _ct);

        Assert.True(result.Status == ConversationMergeStatus.Merged, result.Status + ": " + result.Message);
        Assert.Equal("develop", result.TargetBranch);
        Assert.True(File.Exists(Path.Combine(a, "feature.txt")));
        Assert.Equal(G(a, "rev-parse", "HEAD"), G(a, "ls-remote", "origin", "refs/heads/develop").Split('\t')[0]);
        Assert.False(HasBranch(a, ws.Branch));
        Assert.False(Directory.Exists(ws.Directory));
        // repo B untouched
        Assert.Equal(bHeadBefore, G(b, "rev-parse", "HEAD"));
        Assert.Equal(bRemoteBefore, G(b, "ls-remote", "origin"));
        Assert.False(File.Exists(Path.Combine(b, "feature.txt")));
    }

    [Fact]
    public async Task AC6_Merge_UnknownConversationInOtherRepo_IsRejected_AndNothingChanges()
    {
        var a = NewRepo("repoA", withRemote: true);
        var b = NewRepo("repoB", withRemote: true);
        var cat = Catalog(_root);
        var repos = cat.GetRepositories();
        var wtsA = cat.GetWorktrees(repos.Single(r => r.Name == "repoA"));
        var wtsB = cat.GetWorktrees(repos.Single(r => r.Name == "repoB"));
        const string id = "66666666-6666-6666-6666-666666666666";
        var ws = await wtsA.EnsureAsync(id, _ct);
        File.WriteAllText(Path.Combine(ws.Directory, "f.txt"), "x\n");
        G(ws.Directory, "add", "-A"); G(ws.Directory, "commit", "-q", "-m", "f");

        var result = await wtsB.MergeAsync(id, "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Rejected, result.Status);
        Assert.True(HasBranch(a, ws.Branch));
        Assert.False(File.Exists(Path.Combine(b, "f.txt")));
    }

    // ---------- AC-5 path matching ----------

    [Fact]
    public void AC5_IsConversationDirectory_MatchesMainAndWorktreesOfRepo_Only()
    {
        var main = Path.Combine(_root, "repoA");
        var wts = main + ".worktrees";
        Assert.True(AgentChatSession.IsConversationDirectory(main, main, wts));
        Assert.True(AgentChatSession.IsConversationDirectory(main + Path.DirectorySeparatorChar, main, wts));
        Assert.True(AgentChatSession.IsConversationDirectory(Path.Combine(wts, "abc"), main, wts));
        Assert.True(AgentChatSession.IsConversationDirectory(Path.Combine(wts, "abc", "sub"), main, wts));
        Assert.False(AgentChatSession.IsConversationDirectory(wts, main, wts));
        Assert.False(AgentChatSession.IsConversationDirectory(Path.Combine(_root, "repoB"), main, wts));
        Assert.False(AgentChatSession.IsConversationDirectory(Path.Combine(_root, "repoB.worktrees", "abc"), main, wts));
        Assert.False(AgentChatSession.IsConversationDirectory(main + ".worktrees2", main, wts));
        Assert.False(AgentChatSession.IsConversationDirectory(null, main, wts));
        Assert.False(AgentChatSession.IsConversationDirectory("", main, wts));
        Assert.False(AgentChatSession.IsConversationDirectory("bad\0path", main, wts));
    }
}
