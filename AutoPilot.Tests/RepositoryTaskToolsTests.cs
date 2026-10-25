using AutoPilot.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoPilot.Tests
{
    public sealed class RepositoryTaskToolsTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "autopilot-tasktools-" + Guid.NewGuid().ToString("N"));
        private static readonly RepositoryInfo Repo = new("Repo", Path.Combine(Path.GetTempPath(), "repo-a"));
        private static readonly RepositoryInfo Other = new("Other", Path.Combine(Path.GetTempPath(), "repo-b"));

        private readonly TaskStore _store;
        private readonly List<(string Question, IReadOnlyList<string> Choices)> _asked = [];
        private (string Answer, bool WasFreeform) _reply = (RepositoryTaskTools.CreateChoice, false);

        public RepositoryTaskToolsTests()
        {
            _store = new TaskStore(Path.Combine(_directory, "tasks.json"), NullLogger<TaskStore>.Instance);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        private RepositoryTaskTools Tools(Func<string, IReadOnlyList<string>, CancellationToken, Task<(string, bool)>>? confirm = null) =>
            new(_store, () => [Other, Repo], Repo, "session-1", "orchestrator", confirm ?? ((q, c, _) =>
            {
                _asked.Add((q, c));
                return Task.FromResult(_reply);
            }));

        [Fact]
        public async Task Confirmed_CreatesTaskWithAgentMetadata()
        {
            var result = await Tools().CreateTaskAsync("  My task ", "Details", null);

            var task = Assert.Single(_store.GetAll());
            Assert.StartsWith("STATUS: CREATED\n", result);
            Assert.Contains("Id: " + task.Id, result);
            Assert.Contains("Repository: Repo", result);
            Assert.Contains("Title: My task", result);
            Assert.Equal("orchestrator", task.CreatedBy);
            Assert.Equal("session-1", task.SourceSessionId);
            Assert.Equal("Details", task.Description);
            var (question, choices) = Assert.Single(_asked);
            Assert.StartsWith("Create task 'My task' on repository 'Repo'?", question);
            Assert.Contains("Details", question);
            Assert.Equal(["Create task", "Don't create"], choices);
        }

        [Fact]
        public async Task DontCreate_IsDeclined()
        {
            _reply = (RepositoryTaskTools.DeclineChoice, false);

            var result = await Tools().CreateTaskAsync("T", null, null);

            Assert.StartsWith("STATUS: DECLINED", result);
            Assert.DoesNotContain("Feedback:", result);
            Assert.Empty(_store.GetAll());
        }

        [Fact]
        public async Task Freeform_IsDeclinedWithFeedback_EvenWhenTextIsCreateTask()
        {
            _reply = ("Create task", true);
            var typedCreate = await Tools().CreateTaskAsync("T", null, null);
            _reply = ("Please rename it", true);
            var result = await Tools().CreateTaskAsync("T", null, null);

            Assert.StartsWith("STATUS: DECLINED", typedCreate);
            Assert.StartsWith("STATUS: DECLINED", result);
            Assert.Contains("Feedback: Please rename it", result);
            Assert.Empty(_store.GetAll());
        }

        [Fact]
        public async Task PendingDuplicate_SkipsConfirmation()
        {
            var existing = await _store.CreateAsync(Repo, "Same Title", null);

            var result = await Tools().CreateTaskAsync(" same title ", null, null);

            Assert.StartsWith("STATUS: DUPLICATE", result);
            Assert.Contains("Id: " + existing.Id, result);
            Assert.Empty(_asked);
            Assert.Single(_store.GetAll());
        }

        [Fact]
        public async Task DuplicateCreatedDuringConfirmation_ReturnsDuplicate()
        {
            RepositoryTask? racing = null;
            var tools = Tools(async (q, c, _) =>
            {
                racing = await _store.CreateAsync(Repo, "race", null);
                return (RepositoryTaskTools.CreateChoice, false);
            });

            var result = await tools.CreateTaskAsync("Race", null, null);

            Assert.StartsWith("STATUS: DUPLICATE", result);
            Assert.Contains("Id: " + racing!.Id, result);
            Assert.Single(_store.GetAll());
        }

        [Fact]
        public async Task TitleWithNewlinesAndTabs_IsCollapsedToSingleLine()
        {
            var result = await Tools().CreateTaskAsync("  Fix\r\n\tthe   *bug*\n\nnow ", "line1\r\nline2", null);

            Assert.StartsWith("STATUS: CREATED", result);
            Assert.Equal("Fix the *bug* now", Assert.Single(_store.GetAll()).Title);
            var question = Assert.Single(_asked).Question;
            var lines = question.Split('\n');
            Assert.Equal(@"Create task 'Fix the \*bug\* now' on repository 'Repo'?", lines[0]);
            Assert.Equal("", lines[1]);
            Assert.Equal("> line1", lines[2]);
            Assert.Equal("> line2", lines[3]);
        }

        [Fact]
        public async Task TitleLengthIsMeasuredAfterCollapsing()
        {
            var title = new string('a', 100) + "\n\n\n\n" + new string('b', 99);
            Assert.StartsWith("STATUS: CREATED", await Tools().CreateTaskAsync(title, null, null));
        }

        [Fact]
        public async Task SentTaskWithSameTitle_IsNotDuplicate()
        {
            var existing = await _store.CreateAsync(Repo, "Same", null);
            await _store.MarkSentAsync(existing.Id, "x");

            Assert.StartsWith("STATUS: CREATED", await Tools().CreateTaskAsync("Same", null, null));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task EmptyTitle_Fails(string title)
        {
            AssertFailedWithoutAsking(await Tools().CreateTaskAsync(title, null, null));
        }

        [Fact]
        public async Task TitleLongerThan200_Fails_Boundary200Ok()
        {
            AssertFailedWithoutAsking(await Tools().CreateTaskAsync(new string('a', 201), null, null));
            Assert.StartsWith("STATUS: CREATED", await Tools().CreateTaskAsync(new string('a', 200), null, null));
        }

        [Fact]
        public async Task DescriptionLongerThan20000_Fails()
        {
            AssertFailedWithoutAsking(await Tools().CreateTaskAsync("T", new string('a', 20_001), null));
        }

        [Fact]
        public async Task UnknownRepository_FailsAndListsNames()
        {
            var result = await Tools().CreateTaskAsync("T", null, "nope");

            AssertFailedWithoutAsking(result);
            Assert.Contains("Repo", result);
            Assert.Contains("Other", result);
        }

        [Fact]
        public async Task Repository_IsCaseInsensitive_AndDefaultsToCurrent()
        {
            var result = await Tools().CreateTaskAsync("T1", null, " OTHER ");
            await Tools().CreateTaskAsync("T2", null, null);

            Assert.Contains("Repository: Other", result);
            Assert.Equal(Other.Path, _store.GetAll().Single(t => t.Title == "T1").RepositoryPath);
            Assert.Equal(Repo.Path, _store.GetAll().Single(t => t.Title == "T2").RepositoryPath);
        }

        [Fact]
        public async Task StoreError_ReturnsFailed()
        {
            Directory.CreateDirectory(_directory);
            var blocked = new TaskStore(Path.Combine(_directory, "file.txt", "tasks.json"), NullLogger<TaskStore>.Instance);
            File.WriteAllText(Path.Combine(_directory, "file.txt"), "x");
            var tools = new RepositoryTaskTools(blocked, () => [Repo], Repo, "s", "orchestrator", (_, _, _) => Task.FromResult((RepositoryTaskTools.CreateChoice, false)));

            Assert.StartsWith("STATUS: FAILED", await tools.CreateTaskAsync("T", null, null));
        }

        [Fact]
        public async Task ConfirmationThrowing_ReturnsFailed_NothingCreated()
        {
            var tools = Tools((_, _, _) => throw new TaskCanceledException());

            Assert.StartsWith("STATUS: FAILED", await tools.CreateTaskAsync("T", null, null));
            Assert.Empty(_store.GetAll());
        }

        [Fact]
        public async Task CancelledToolCall_Propagates()
        {
            using var cts = new CancellationTokenSource();
            var tools = Tools((_, _, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(("", false));
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.CreateTaskAsync("T", null, null, cts.Token));
        }

        [Fact]
        public void ListRepositories_MarksCurrent()
        {
            var result = Tools().ListRepositories();

            Assert.Contains("- Repo (current)", result);
            Assert.Contains("- Other", result);
            Assert.DoesNotContain("Other (current)", result);
        }

        private void AssertFailedWithoutAsking(string result)
        {
            Assert.StartsWith("STATUS: FAILED", result);
            Assert.Empty(_asked);
            Assert.Empty(_store.GetAll());
        }
    }
}
