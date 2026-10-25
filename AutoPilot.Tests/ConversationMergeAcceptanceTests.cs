using System.Diagnostics;
using AutoPilot.Services;

namespace AutoPilot.Tests;

public class ConversationMergeAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cma-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = CancellationToken.None;
    private string _bare = "";

    public ConversationMergeAcceptanceTests() => Directory.CreateDirectory(_root);

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

    private string Bare(string repo) => G(repo, "remote", "get-url", "origin");

    private string NewRepo()
    {
        var d = Path.Combine(_root, "repo");
        Directory.CreateDirectory(d);
        G(d, "init", "-q", "-b", "main");
        G(d, "config", "user.name", "Tester");
        G(d, "config", "user.email", "t@example.com");
        G(d, "config", "commit.gpgsign", "false");
        G(d, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(d, "README.md"), "hello\n");
        File.WriteAllText(Path.Combine(d, "shared.txt"), "base\n");
        File.WriteAllText(Path.Combine(d, ".gitignore"), "bin/\n");
        G(d, "add", "-A");
        G(d, "commit", "-q", "-m", "initial");
        _bare = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(_bare);
        G(_bare, "init", "-q", "--bare", "-b", "main");
        G(d, "remote", "add", "origin", _bare);
        return d;
    }

    private string RemoteRef(string reference)
    {
        var (c, o) = Git(_root, "--git-dir=" + _bare, "rev-parse", "--verify", "-q", reference);
        return c == 0 ? o : "";
    }

    private async Task<(string Repo, ConversationWorktrees Wt, ConversationWorkspace Ws)> Setup(string id = "sess1", bool commitWork = true)
    {
        var repo = NewRepo();
        var wt = new ConversationWorktrees(repo, Path.Combine(_root, "wts"), "autopilot/");
        var ws = await wt.EnsureAsync(id, _ct);
        G(ws.Directory, "config", "user.name", "Tester");
        G(ws.Directory, "config", "user.email", "t@example.com");
        G(ws.Directory, "config", "commit.gpgsign", "false");
        if (commitWork)
        {
            File.WriteAllText(Path.Combine(ws.Directory, "feature.txt"), "feature\n");
            G(ws.Directory, "add", "-A");
            G(ws.Directory, "commit", "-q", "-m", "feature");
        }
        return (repo, wt, ws);
    }

    private static bool BranchExists(string repo, string branch) =>
        Git(repo, "rev-parse", "--verify", "-q", "refs/heads/" + branch).Code == 0;

    private static void AssertKept(string repo, ConversationWorkspace ws)
    {
        Assert.True(Directory.Exists(ws.Directory));
        Assert.True(BranchExists(repo, ws.Branch));
    }

    private static void AssertNothingChanged(string repo, string headBefore, ConversationWorkspace ws)
    {
        Assert.Equal(headBefore, G(repo, "rev-parse", "HEAD"));
        AssertKept(repo, ws);
    }

    private string TrackingRef(string repo, string branch) =>
        Git(repo, "rev-parse", "--verify", "-q", "refs/remotes/origin/" + branch).Code == 0 ? "yes" : "";

    private void PushConversationBranch(ConversationWorkspace ws) =>
        G(ws.Directory, "push", "-q", "origin", "HEAD:refs/heads/" + ws.Branch);

    // AC-2 helper: add an extra commit to the remote conversation branch from another clone
    private string PushExtraCommitFromOtherClone(ConversationWorkspace ws)
    {
        var other = Path.Combine(_root, "other-clone");
        G(_root, "clone", "-q", _bare, other);
        G(other, "config", "user.name", "O");
        G(other, "config", "user.email", "o@example.com");
        G(other, "config", "commit.gpgsign", "false");
        G(other, "checkout", "-q", "-B", ws.Branch, "origin/" + ws.Branch);
        File.WriteAllText(Path.Combine(other, "extra.txt"), "extra\n");
        G(other, "add", "-A");
        G(other, "commit", "-q", "-m", "extra on remote");
        G(other, "push", "-q", "origin", "HEAD:refs/heads/" + ws.Branch);
        return RemoteRef("refs/heads/" + ws.Branch);
    }

    // AC-1, AC-6
    [Fact]
    public async Task AC1_RemoteConversationBranch_IsDeleted_AndTrackingRefGone()
    {
        var (repo, wt, ws) = await Setup();
        PushConversationBranch(ws);
        Assert.NotEmpty(RemoteRef("refs/heads/" + ws.Branch));
        Assert.Equal("yes", TrackingRef(repo, ws.Branch));

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Merged, r.Status);
        Assert.Equal("", RemoteRef("refs/heads/" + ws.Branch));
        Assert.Equal("", TrackingRef(repo, ws.Branch));
        Assert.Equal(G(repo, "rev-parse", "HEAD"), RemoteRef("refs/heads/main"));
        Assert.False(Directory.Exists(ws.Directory));
        Assert.False(BranchExists(repo, ws.Branch));
        Assert.Contains("remote", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    // AC-2
    [Fact]
    public async Task AC2_RemoteTipNotInMerge_KeptAndCleanupFailed()
    {
        var (repo, wt, ws) = await Setup();
        PushConversationBranch(ws);
        var extraTip = PushExtraCommitFromOtherClone(ws);

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.CleanupFailed, r.Status);
        Assert.Equal(extraTip, RemoteRef("refs/heads/" + ws.Branch));
        Assert.Contains(ws.Branch, r.Message);
        Assert.Equal("main", r.TargetBranch);
        Assert.Equal(r.MergeCommit, G(repo, "rev-parse", "HEAD"));
        Assert.Equal(G(repo, "rev-parse", "HEAD"), RemoteRef("refs/heads/main"));
        Assert.False(string.IsNullOrWhiteSpace(r.Message));
    }

    // AC-3
    [Fact]
    public async Task AC3_RemoteBranchNeverPushed_MergedAsBefore()
    {
        var (repo, wt, ws) = await Setup();
        Assert.Equal("", RemoteRef("refs/heads/" + ws.Branch));

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Merged, r.Status);
        Assert.Equal("", RemoteRef("refs/heads/" + ws.Branch));
        Assert.False(Directory.Exists(ws.Directory));
        Assert.False(BranchExists(repo, ws.Branch));
    }

    // AC-4
    [Fact]
    public async Task AC4_RemoteDeleteDenied_LocalCleanupStillDone_CleanupFailedNamesBranchAndError()
    {
        var (repo, wt, ws) = await Setup();
        PushConversationBranch(ws);
        var tip = RemoteRef("refs/heads/" + ws.Branch);
        G(_root, "--git-dir=" + _bare, "config", "receive.denyDeletes", "true");

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.CleanupFailed, r.Status);
        Assert.Equal(tip, RemoteRef("refs/heads/" + ws.Branch));
        Assert.Contains(ws.Branch, r.Message);
        Assert.Contains("den", r.Message, StringComparison.OrdinalIgnoreCase); // git: "denying ref deletion"
        Assert.False(Directory.Exists(ws.Directory));
        Assert.False(BranchExists(repo, ws.Branch));
        Assert.Equal("main", r.TargetBranch);
        Assert.Equal(G(repo, "rev-parse", "HEAD"), RemoteRef("refs/heads/main"));
    }

    // AC-5
    [Fact]
    public async Task AC5_Conflict_RemoteConversationBranchKept()
    {
        var (repo, wt, ws) = await Setup(commitWork: false);
        File.WriteAllText(Path.Combine(ws.Directory, "shared.txt"), "from conversation\n");
        G(ws.Directory, "add", "-A");
        G(ws.Directory, "commit", "-q", "-m", "conv");
        PushConversationBranch(ws);
        var tip = RemoteRef("refs/heads/" + ws.Branch);
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "from main\n");
        G(repo, "add", "-A");
        G(repo, "commit", "-q", "-m", "main edit");

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Conflict, r.Status);
        Assert.Equal(tip, RemoteRef("refs/heads/" + ws.Branch));
        Assert.Equal("yes", TrackingRef(repo, ws.Branch));
        AssertKept(repo, ws);
    }

    // AC-5
    [Fact]
    public async Task AC5_Rejected_RemoteConversationBranchKept()
    {
        var (repo, wt, ws) = await Setup();
        PushConversationBranch(ws);
        var tip = RemoteRef("refs/heads/" + ws.Branch);
        File.WriteAllText(Path.Combine(ws.Directory, "README.md"), "changed\n");

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Rejected, r.Status);
        Assert.Equal(tip, RemoteRef("refs/heads/" + ws.Branch));
    }

    // AC-5
    [Fact]
    public async Task AC5_PushFailed_RemoteConversationBranchKept()
    {
        var (repo, wt, ws) = await Setup();
        PushConversationBranch(ws);
        var tip = RemoteRef("refs/heads/" + ws.Branch);
        // advance remote main from another clone so the target push is rejected
        var other = Path.Combine(_root, "other");
        G(_root, "clone", "-q", _bare, other);
        G(other, "config", "user.name", "O");
        G(other, "config", "user.email", "o@example.com");
        G(other, "config", "commit.gpgsign", "false");
        G(other, "checkout", "-q", "-B", "main");
        File.WriteAllText(Path.Combine(other, "remote-only.txt"), "r\n");
        G(other, "add", "-A");
        G(other, "commit", "-q", "-m", "remote work");
        G(other, "push", "-q", "origin", "main");

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.PushFailed, r.Status);
        Assert.Equal(tip, RemoteRef("refs/heads/" + ws.Branch));
        AssertKept(repo, ws);
    }

    // Formerly asserted the remote branch was kept (old behavior); now it is deleted (AC-1, AC-6)
    [Fact]
    public async Task HappyPath_MergesNoFf_Pushes_Cleans_DeletesRemoteBranch()
    {
        var (repo, wt, ws) = await Setup();
        G(ws.Directory, "push", "-q", "origin", ws.Branch);
        var remoteBranchBefore = RemoteRef("refs/heads/" + ws.Branch);
        Assert.NotEmpty(remoteBranchBefore);

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Merged, r.Status);
        Assert.Equal("main", r.TargetBranch);
        Assert.False(string.IsNullOrEmpty(r.MergeCommit));
        var head = G(repo, "rev-parse", "HEAD");
        Assert.Equal(head, G(repo, "rev-parse", r.MergeCommit!));
        Assert.Equal(2, G(repo, "rev-list", "--parents", "-n", "1", "HEAD").Split(' ').Length - 1);
        Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
        Assert.Equal(head, RemoteRef("refs/heads/main"));
        Assert.False(Directory.Exists(ws.Directory));
        Assert.False(BranchExists(repo, ws.Branch));
        Assert.Equal("", RemoteRef("refs/heads/" + ws.Branch));
        Assert.False(await wt.BranchExistsAsync("sess1", _ct));
    }

    // AC-3: merge happens even when main has diverged (true merge commit)
    [Fact]
    public async Task DivergedMain_NonConflicting_Merges()
    {
        var (repo, wt, ws) = await Setup();
        File.WriteAllText(Path.Combine(repo, "other.txt"), "o\n");
        G(repo, "add", "-A");
        G(repo, "commit", "-q", "-m", "main work");

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Merged, r.Status);
        Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
        Assert.True(File.Exists(Path.Combine(repo, "other.txt")));
        Assert.Equal(G(repo, "rev-parse", "HEAD"), RemoteRef("refs/heads/main"));
    }

    // Target is whatever branch is checked out
    [Fact]
    public async Task TargetBranch_IsCurrentBranchOfMainRepo()
    {
        var (repo, wt, ws) = await Setup();
        G(repo, "checkout", "-q", "-b", "develop");

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Merged, r.Status);
        Assert.Equal("develop", r.TargetBranch);
        Assert.Equal(G(repo, "rev-parse", "HEAD"), RemoteRef("refs/heads/develop"));
        Assert.Equal("", RemoteRef("refs/heads/main"));
    }

    // AC-5 ignored files do not block
    [Fact]
    public async Task IgnoredFilesInWorktree_DoNotBlock()
    {
        var (repo, wt, ws) = await Setup();
        Directory.CreateDirectory(Path.Combine(ws.Directory, "bin"));
        File.WriteAllText(Path.Combine(ws.Directory, "bin", "x.dll"), "bin");

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Merged, r.Status);
        Assert.False(BranchExists(repo, ws.Branch));
    }

    // AC-5
    [Fact]
    public async Task Rejected_WorktreeHasUncommittedTrackedChange()
    {
        var (repo, wt, ws) = await Setup();
        File.WriteAllText(Path.Combine(ws.Directory, "README.md"), "changed\n");
        var head = G(repo, "rev-parse", "HEAD");

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Rejected, r.Status);
        Assert.False(string.IsNullOrWhiteSpace(r.Message));
        AssertNothingChanged(repo, head, ws);
        Assert.Equal("", RemoteRef("refs/heads/main"));
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(ws.Directory, "README.md")).Replace("\r\n", "\n"));
    }

    // AC-5
    [Fact]
    public async Task Rejected_WorktreeHasUntrackedFile()
    {
        var (repo, wt, ws) = await Setup();
        File.WriteAllText(Path.Combine(ws.Directory, "new.txt"), "n\n");
        var head = G(repo, "rev-parse", "HEAD");

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Rejected, r.Status);
        AssertNothingChanged(repo, head, ws);
        Assert.True(File.Exists(Path.Combine(ws.Directory, "new.txt")));
    }

    // AC-5
    [Fact]
    public async Task Rejected_MainRepoHasTrackedUncommittedChanges()
    {
        var (repo, wt, ws) = await Setup();
        File.WriteAllText(Path.Combine(repo, "README.md"), "dirty\n");
        var head = G(repo, "rev-parse", "HEAD");

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Rejected, r.Status);
        AssertNothingChanged(repo, head, ws);
        Assert.Equal("dirty", File.ReadAllText(Path.Combine(repo, "README.md")).Trim());
    }

    // AC-5
    [Fact]
    public async Task Rejected_MainRepoDetachedHead()
    {
        var (repo, wt, ws) = await Setup();
        G(repo, "checkout", "-q", "--detach");
        var head = G(repo, "rev-parse", "HEAD");

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Rejected, r.Status);
        AssertNothingChanged(repo, head, ws);
    }

    // AC-5
    [Fact]
    public async Task Rejected_MainRepoMergeInProgress()
    {
        var (repo, wt, ws) = await Setup();
        G(repo, "checkout", "-q", "-b", "side");
        File.WriteAllText(Path.Combine(repo, "side.txt"), "s\n");
        G(repo, "add", "-A");
        G(repo, "commit", "-q", "-m", "side");
        G(repo, "checkout", "-q", "main");
        File.WriteAllText(Path.Combine(repo, "main.txt"), "m\n");
        G(repo, "add", "-A");
        G(repo, "commit", "-q", "-m", "main");
        G(repo, "merge", "--no-ff", "--no-commit", "side");
        Assert.True(File.Exists(Path.Combine(repo, ".git", "MERGE_HEAD")));
        var head = G(repo, "rev-parse", "HEAD");

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Rejected, r.Status);
        AssertNothingChanged(repo, head, ws);
        Assert.True(File.Exists(Path.Combine(repo, ".git", "MERGE_HEAD")));
    }

    // AC-5
    [Fact]
    public async Task Rejected_BranchDoesNotExist()
    {
        var repo = NewRepo();
        var wt = new ConversationWorktrees(repo, Path.Combine(_root, "wts"), "autopilot/");
        var head = G(repo, "rev-parse", "HEAD");

        var r = await wt.MergeAsync("nope", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Rejected, r.Status);
        Assert.Equal(head, G(repo, "rev-parse", "HEAD"));
    }

    // AC-5
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("a b")]
    [InlineData("x;y")]
    [InlineData("-rf")]
    public async Task Rejected_InvalidSessionId_DoesNotThrow(string id)
    {
        var repo = NewRepo();
        var wt = new ConversationWorktrees(repo, Path.Combine(_root, "wts"), "autopilot/");
        var head = G(repo, "rev-parse", "HEAD");

        var r = await wt.MergeAsync(id, "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Rejected, r.Status);
        Assert.Equal(head, G(repo, "rev-parse", "HEAD"));
    }

    // AC-5
    [Theory]
    [InlineData("missing-remote")]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Rejected_InvalidOrMissingRemote(string remote)
    {
        var (repo, wt, ws) = await Setup();
        var head = G(repo, "rev-parse", "HEAD");

        var r = await wt.MergeAsync("sess1", remote, _ct);

        Assert.Equal(ConversationMergeStatus.Rejected, r.Status);
        AssertNothingChanged(repo, head, ws);
        Assert.Equal("", RemoteRef("refs/heads/main"));
    }

    // AC-6
    [Fact]
    public async Task Conflict_AbortsMerge_ListsFiles_KeepsBranchAndWorktree()
    {
        var (repo, wt, ws) = await Setup(commitWork: false);
        File.WriteAllText(Path.Combine(ws.Directory, "shared.txt"), "from conversation\n");
        G(ws.Directory, "add", "-A");
        G(ws.Directory, "commit", "-q", "-m", "conv");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "from main\n");
        G(repo, "add", "-A");
        G(repo, "commit", "-q", "-m", "main edit");
        var head = G(repo, "rev-parse", "HEAD");

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Conflict, r.Status);
        Assert.Contains("shared.txt", r.Message);
        Assert.False(File.Exists(Path.Combine(repo, ".git", "MERGE_HEAD")));
        Assert.Equal("", G(repo, "status", "--porcelain", "--untracked-files=no"));
        Assert.Equal("from main", File.ReadAllText(Path.Combine(repo, "shared.txt")).Trim());
        AssertNothingChanged(repo, head, ws);
        Assert.Equal("", RemoteRef("refs/heads/main"));
    }

    // AC-7
    [Fact]
    public async Task PushFailed_KeepsMerge_AndRetrySucceeds()
    {
        var (repo, wt, ws) = await Setup();
        var good = Bare(repo);
        var headBefore = G(repo, "rev-parse", "HEAD");
        G(repo, "remote", "set-url", "origin", Path.Combine(_root, "does-not-exist.git"));

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.PushFailed, r.Status);
        Assert.Equal("main", r.TargetBranch);
        Assert.False(string.IsNullOrEmpty(r.MergeCommit));
        Assert.Equal(r.MergeCommit, G(repo, "rev-parse", "HEAD"));
        Assert.NotEqual(headBefore, G(repo, "rev-parse", "HEAD"));
        AssertKept(repo, ws);

        G(repo, "remote", "set-url", "origin", good);
        var r2 = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.Merged, r2.Status);
        Assert.Equal(G(repo, "rev-parse", "HEAD"), RemoteRef("refs/heads/main"));
        Assert.False(Directory.Exists(ws.Directory));
        Assert.False(BranchExists(repo, ws.Branch));
        // retry must not create a second merge commit
        Assert.Equal(r.MergeCommit, G(repo, "rev-parse", "HEAD"));
    }

    // AC-7: non-fast-forward rejection is a push failure and is never forced
    [Fact]
    public async Task PushRejectedByRemote_NotForced_ReportsPushFailed()
    {
        var (repo, wt, ws) = await Setup();
        // advance remote main from another clone
        var other = Path.Combine(_root, "other");
        G(_root, "clone", "-q", _bare, other);
        G(other, "config", "user.name", "O");
        G(other, "config", "user.email", "o@example.com");
        G(other, "config", "commit.gpgsign", "false");
        G(other, "checkout", "-q", "-B", "main");
        File.WriteAllText(Path.Combine(other, "remote-only.txt"), "r\n");
        G(other, "add", "-A");
        G(other, "commit", "-q", "-m", "remote work");
        G(other, "push", "-q", "origin", "main");
        var remoteHead = RemoteRef("refs/heads/main");

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.PushFailed, r.Status);
        Assert.Equal(remoteHead, RemoteRef("refs/heads/main"));
        AssertKept(repo, ws);
    }

    // AC-8: cleanup failure (locked worktree file handle) reports success + error, if reproducible
    [Fact]
    public async Task CleanupFailed_ReportsMergedPushedAndError()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (repo, wt, ws) = await Setup();
        // hold an open handle inside the worktree so deletion fails on Windows
        var locked = Path.Combine(ws.Directory, "bin-lock.tmp");
        using var fs = new FileStream(locked, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        // make it ignored so it does not block the merge
        File.AppendAllText(Path.Combine(repo, ".git", "info", "exclude"), "bin-lock.tmp\n");

        var r = await wt.MergeAsync("sess1", "origin", _ct);

        Assert.Equal(ConversationMergeStatus.CleanupFailed, r.Status);
        Assert.False(string.IsNullOrWhiteSpace(r.Message));
        Assert.Equal("main", r.TargetBranch);
        Assert.Equal(G(repo, "rev-parse", "HEAD"), RemoteRef("refs/heads/main"));
    }

    [Fact]
    public async Task Cancellation_OnlyOperationCanceledPropagates()
    {
        var (_, wt, _) = await Setup();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wt.MergeAsync("sess1", "origin", cts.Token));
    }
}
