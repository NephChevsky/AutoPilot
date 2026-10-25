using AutoPilot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoPilot.Tests;

public class RepositoryTaskToolsAcceptanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rtt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static readonly RepositoryInfo RepoA = new("Alpha", Path.Combine(Path.GetTempPath(), "rtt-Repo-A"));
    private static readonly RepositoryInfo RepoB = new("Beta", Path.Combine(Path.GetTempPath(), "rtt-Repo-B"));
    private const string Sid = "session-1";

    private string NewPath() => Path.Combine(_dir, "tasks", "tasks.json");
    private static TaskStore Store(string path) => new(path, NullLogger<TaskStore>.Instance);

    private sealed class Confirm
    {
        public int Calls;
        public string? Question;
        public List<string>? Choices;
        public Func<(string, bool)> Reply = () => (RepositoryTaskTools.CreateChoice, false);
        public Task<(string Answer, bool WasFreeform)> Invoke(string q, IReadOnlyList<string> c, CancellationToken ct)
        {
            Calls++;
            Question = q;
            Choices = c.ToList();
            var r = Reply();
            return Task.FromResult((r.Item1, r.Item2));
        }
    }

    private static RepositoryTaskTools Tools(TaskStore s, Confirm c, string by = "orchestrator") =>
        new(s, () => new[] { RepoA, RepoB }, RepoA, Sid, by, c.Invoke);

    private static string Status(string r) => r.Split('\n')[0].Trim();

    private static string Detail(string r, string key) =>
        r.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))?[(key.Length + 1)..].Trim() ?? "";

    // AC-1
    [Fact]
    public void ListRepositories_ShowsNamesAndCurrent()
    {
        var r = Tools(Store(NewPath()), new Confirm()).ListRepositories();
        Assert.Contains("Alpha", r);
        Assert.Contains("Beta", r);
        var alpha = r.Split('\n').First(l => l.Contains("Alpha"));
        var beta = r.Split('\n').First(l => l.Contains("Beta"));
        Assert.Contains("current", alpha, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("current", beta, StringComparison.OrdinalIgnoreCase);
    }

    // AC-2 / AC-3 / AC-6
    [Fact]
    public async Task Create_Confirmed_CreatesWithProvenance()
    {
        var p = NewPath();
        var s = Store(p);
        var c = new Confirm();
        var r = await Tools(s, c).CreateTaskAsync("Add API", "the details", null);
        Assert.Equal("STATUS: CREATED", Status(r));
        Assert.Equal(1, c.Calls);
        Assert.Equal("Create task 'Add API' on repository 'Alpha'?", c.Question is null ? null : c.Question.Split('\n')[0].Trim());
        Assert.Contains("the details", c.Question);
        Assert.Equal(["Create task", "Don't create"], c.Choices);
        var id = Detail(r, "Id");
        Assert.Equal("Alpha", Detail(r, "Repository"));
        Assert.Equal("Add API", Detail(r, "Title"));

        var reloaded = Store(p);
        var t = reloaded.Get(id);
        Assert.NotNull(t);
        Assert.Equal("orchestrator", t!.CreatedBy);
        Assert.Equal(Sid, t.SourceSessionId);
        Assert.True(t.IsAgentCreated);
        Assert.Equal(RepoA.Path, t.RepositoryPath);
        Assert.Equal("the details", t.Description);
    }

    [Theory]
    [InlineData("beta")]
    [InlineData("BETA")]
    [InlineData("Beta")]
    public async Task Create_RepositoryNameCaseInsensitive(string name)
    {
        var s = Store(NewPath());
        var c = new Confirm();
        var r = await Tools(s, c).CreateTaskAsync("T", null, name);
        Assert.Equal("STATUS: CREATED", Status(r));
        Assert.Equal("Beta", Detail(r, "Repository"));
        Assert.Contains("'Beta'", c.Question);
        Assert.Single(s.GetForRepository(RepoB.Path));
        Assert.Empty(s.GetForRepository(RepoA.Path));
    }

    [Fact]
    public async Task Create_DefaultsToConversationRepository()
    {
        var s = Store(NewPath());
        await Tools(s, new Confirm()).CreateTaskAsync("T", null, "  ");
        Assert.Single(s.GetForRepository(RepoA.Path));
    }

    [Fact]
    public async Task Create_Declined_NothingCreated()
    {
        var s = Store(NewPath());
        var c = new Confirm { Reply = () => (RepositoryTaskTools.DeclineChoice, false) };
        var r = await Tools(s, c).CreateTaskAsync("T", "d", null);
        Assert.Equal("STATUS: DECLINED", Status(r));
        Assert.Empty(s.GetAll());
        Assert.False(File.Exists(NewPath()) && Store(NewPath()).GetAll().Count > 0);
    }

    [Fact]
    public async Task Create_Freeform_DeclinedWithFeedback()
    {
        var s = Store(NewPath());
        var c = new Confirm { Reply = () => ("please split into two", true) };
        var r = await Tools(s, c).CreateTaskAsync("T", "d", null);
        Assert.Equal("STATUS: DECLINED", Status(r));
        Assert.Contains("please split into two", r);
        Assert.Empty(s.GetAll());
    }

    [Fact]
    public async Task Create_FreeformTextEqualToCreateChoice_DoesNotCreate()
    {
        var s = Store(NewPath());
        var c = new Confirm { Reply = () => ("Create task", true) };
        var r = await Tools(s, c).CreateTaskAsync("T", "d", null);
        Assert.NotEqual("STATUS: CREATED", Status(r));
        Assert.Empty(s.GetAll());
    }

    // AC-4
    public static IEnumerable<object?[]> Invalid() =>
    [
        ["", "d", null],
        ["   ", "d", null],
        [null, "d", null],
        [new string('x', 201), "d", null],
        ["T", new string('y', 20_001), null],
        ["T", "d", "NoSuchRepo"],
    ];

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task Validation_Failed_NoQuestion_NothingCreated(string? title, string? desc, string? repo)
    {
        var s = Store(NewPath());
        var c = new Confirm();
        var r = await Tools(s, c).CreateTaskAsync(title, desc, repo);
        Assert.Equal("STATUS: FAILED", Status(r));
        Assert.Equal(0, c.Calls);
        Assert.Empty(s.GetAll());
    }

    [Fact]
    public async Task Validation_UnknownRepository_ListsValidNames()
    {
        var r = await Tools(Store(NewPath()), new Confirm()).CreateTaskAsync("T", null, "nope");
        Assert.Contains("Alpha", r);
        Assert.Contains("Beta", r);
    }

    [Fact]
    public async Task Validation_Boundaries_Accepted()
    {
        var s = Store(NewPath());
        var r = await Tools(s, new Confirm()).CreateTaskAsync(new string('x', 200), new string('y', 20_000), null);
        Assert.Equal("STATUS: CREATED", Status(r));
    }

    [Fact]
    public async Task StoreIoError_ReturnsFailed_NoThrow()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var s = Store(Path.Combine(blocker, "tasks.json"));
        var r = await Tools(s, new Confirm()).CreateTaskAsync("T", null, null);
        Assert.Equal("STATUS: FAILED", Status(r));
    }

    // AC-5
    [Fact]
    public async Task Duplicate_PendingSameTitle_NoQuestion()
    {
        var s = Store(NewPath());
        var existing = await s.CreateAsync(RepoA, "Fix It", null);
        var c = new Confirm();
        var r = await Tools(s, c).CreateTaskAsync("  fix it ", "x", null);
        Assert.Equal("STATUS: DUPLICATE", Status(r));
        Assert.Equal(existing.Id, Detail(r, "Id"));
        Assert.Equal(0, c.Calls);
        Assert.Single(s.GetAll());
    }

    [Fact]
    public async Task Duplicate_OtherRepoOrSent_NotDuplicate()
    {
        var s = Store(NewPath());
        await s.CreateAsync(RepoB, "Same", null);
        var sent = await s.CreateAsync(RepoA, "Same", null);
        await s.MarkSentAsync(sent.Id, "sess");
        var r = await Tools(s, new Confirm()).CreateTaskAsync("Same", null, null);
        Assert.Equal("STATUS: CREATED", Status(r));
    }

    // AC-6
    [Fact]
    public async Task UserCreatedTasks_HaveNullProvenance_AndPersist()
    {
        var p = NewPath();
        var s = Store(p);
        var t = await s.CreateAsync(RepoA, "u", null);
        Assert.Null(t.CreatedBy);
        Assert.Null(t.SourceSessionId);
        Assert.False(t.IsAgentCreated);
        var a = await s.CreateAsync(RepoA, "a", null, "orchestrator", "sx");
        var re = Store(p);
        Assert.Null(re.Get(t.Id)!.CreatedBy);
        Assert.Equal("sx", re.Get(a.Id)!.SourceSessionId);
        Assert.DoesNotContain("isAgentCreated", File.ReadAllText(p), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyJson_StillLoads()
    {
        var p = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, """
            {"version":1,"tasks":[{"id":"0123456789abcdef0123456789abcdef","repositoryPath":"C:\\r","repositoryName":"r","title":"Old","description":"d","createdAt":"2024-01-01T00:00:00+00:00"}]}
            """);
        var s = Store(p);
        var t = Assert.Single(s.GetAll());
        Assert.Equal("Old", t.Title);
        Assert.Null(t.CreatedBy);
        Assert.Null(t.SourceSessionId);
        Assert.False(t.IsAgentCreated);
    }

    // AC-8
    [Fact]
    public async Task DeleteBySession_DeletesSentKeepsCreatedAndClearsSource()
    {
        var p = NewPath();
        var s = Store(p);
        var sentTo = await s.CreateAsync(RepoA, "sent", null);
        await s.MarkSentAsync(sentTo.Id, Sid);
        var created = await s.CreateAsync(RepoB, "created", null, "orchestrator", Sid);
        var other = await s.CreateAsync(RepoB, "other", null, "orchestrator", "different");

        await s.DeleteBySessionAsync(Sid);

        foreach (var st in new[] { s, Store(p) })
        {
            Assert.Null(st.Get(sentTo.Id));
            var kept = st.Get(created.Id);
            Assert.NotNull(kept);
            Assert.Null(kept!.SourceSessionId);
            Assert.Equal("different", st.Get(other.Id)!.SourceSessionId);
        }
    }

    [Fact]
    public async Task DeleteBySession_TaskSentToSelfAndCreatedBySelf_IsDeleted()
    {
        var s = Store(NewPath());
        var t = await s.CreateAsync(RepoA, "both", null, "orchestrator", Sid);
        await s.MarkSentAsync(t.Id, Sid);
        await s.DeleteBySessionAsync(Sid);
        Assert.Null(s.Get(t.Id));
    }

    // AC-9
    [Fact]
    public async Task Changed_RaisedOnAgentCreation()
    {
        var s = Store(NewPath());
        var n = 0;
        s.Changed += () => n++;
        await Tools(s, new Confirm()).CreateTaskAsync("T", null, null);
        Assert.True(n >= 1);
    }

    [Fact]
    public async Task Changed_NotRaisedOnDeclineOrDuplicate()
    {
        var s = Store(NewPath());
        await s.CreateAsync(RepoA, "dup", null);
        var n = 0;
        s.Changed += () => n++;
        await Tools(s, new Confirm { Reply = () => ("Don't create", false) }).CreateTaskAsync("T", null, null);
        await Tools(s, new Confirm()).CreateTaskAsync("dup", null, null);
        Assert.Equal(0, n);
    }

    // AC-10 / AC-11
    [Fact]
    public void OnlyOrchestratorListsTools()
    {
        var dir = FindAgentsDir();
        foreach (var f in Directory.GetFiles(dir, "*.md"))
        {
            var text = File.ReadAllText(f);
            var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
            var front = text[..end];
            var has = front.Contains("list_repositories") || front.Contains("create_repository_task");
            Assert.Equal(Path.GetFileName(f) == "orchestrator.md", has);
            if (has)
            {
                Assert.Contains("list_repositories", front);
                Assert.Contains("create_repository_task", front);
                Assert.Contains("STATUS: BLOCKED", text);
            }
        }
    }

    private static string FindAgentsDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            var c = Path.Combine(d.FullName, "AutoPilot", "Agents");
            if (Directory.Exists(c)) return c;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("Agents dir");
    }
}
