using AutoPilot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoPilot.Tests
{
    public sealed class RepositoryTaskTests
    {
        [Fact]
        public void ToPrompt_TitleOnly_WhenDescriptionNullOrBlank()
        {
            Assert.Equal("T", new RepositoryTask { Title = "T" }.ToPrompt());
            Assert.Equal("T", new RepositoryTask { Title = "T", Description = "  " }.ToPrompt());
        }

        [Fact]
        public void ToPrompt_AddsBlankLineAndDescription()
        {
            Assert.Equal("T\n\nD", new RepositoryTask { Title = "T", Description = "D" }.ToPrompt());
        }

        [Fact]
        public void NewTask_HasGuidNFormatId()
        {
            Assert.True(Guid.TryParseExact(new RepositoryTask().Id, "N", out _));
        }
    }

    public sealed class TaskStoreTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "autopilot-tasks-" + Guid.NewGuid().ToString("N"));
        private static readonly RepositoryInfo Repo = new("Repo", Path.Combine(Path.GetTempPath(), "repo-a"));
        private static readonly RepositoryInfo Other = new("Other", Path.Combine(Path.GetTempPath(), "repo-b"));

        private string FilePath => Path.Combine(_directory, "tasks", "tasks.json");

        private TaskStore Create(ILogger<TaskStore>? logger = null) => new(FilePath, logger ?? NullLogger<TaskStore>.Instance);

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

        [Fact]
        public async Task Create_TrimsAndStores()
        {
            var store = Create();

            var task = await store.CreateAsync(Repo, "  Title  ", "  Desc  ");

            Assert.Equal("Title", task.Title);
            Assert.Equal("Desc", task.Description);
            Assert.Equal(Repo.Path, task.RepositoryPath);
            Assert.Equal("Repo", task.RepositoryName);
            Assert.False(task.IsSent);
            Assert.Equal("Title", store.Get(task.Id)!.Title);
        }

        [Fact]
        public async Task Create_BlankDescription_StoresNull()
        {
            var task = await Create().CreateAsync(Repo, "T", "   ");

            Assert.Null(task.Description);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Create_BlankTitle_Throws(string title)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Create().CreateAsync(Repo, title, null));
        }

        [Fact]
        public async Task Create_NullRepository_Throws()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => Create().CreateAsync(null!, "T", null));
        }

        [Fact]
        public async Task Persists_AcrossInstances()
        {
            var store = Create();
            var task = await store.CreateAsync(Repo, "T", "D");
            await store.MarkSentAsync(task.Id, "s1");

            var loaded = Create().Get(task.Id)!;

            Assert.Equal("T", loaded.Title);
            Assert.Equal("D", loaded.Description);
            Assert.True(loaded.IsSent);
            Assert.Equal("s1", loaded.SessionId);
            Assert.Contains("\"version\": 1", File.ReadAllText(FilePath));
        }

        [Fact]
        public async Task ReturnedObjects_AreCopies()
        {
            var store = Create();
            var task = await store.CreateAsync(Repo, "T", null);

            task.Title = "changed";
            store.Get(task.Id)!.Title = "changed";
            store.GetAll()[0].Title = "changed";

            Assert.Equal("T", store.Get(task.Id)!.Title);
        }

        [Fact]
        public async Task Update_ChangesPending_RejectsSentAndMissing()
        {
            var store = Create();
            var task = await store.CreateAsync(Repo, "T", "D");

            Assert.True(await store.UpdateAsync(task.Id, " New ", " "));
            var updated = store.Get(task.Id)!;
            Assert.Equal("New", updated.Title);
            Assert.Null(updated.Description);

            await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateAsync(task.Id, " ", null));
            Assert.False(await store.UpdateAsync("missing", "x", null));

            await store.MarkSentAsync(task.Id, "s");
            Assert.False(await store.UpdateAsync(task.Id, "Again", null));
            Assert.Equal("New", store.Get(task.Id)!.Title);
        }

        [Fact]
        public async Task Delete_RemovesPendingAndSent_FalseWhenMissing()
        {
            var store = Create();
            var pending = await store.CreateAsync(Repo, "P", null);
            var sent = await store.CreateAsync(Repo, "S", null);
            await store.MarkSentAsync(sent.Id, "s");

            Assert.True(await store.DeleteAsync(pending.Id));
            Assert.True(await store.DeleteAsync(sent.Id));
            Assert.False(await store.DeleteAsync(sent.Id));
            Assert.Empty(Create().GetAll());
        }

        [Fact]
        public async Task MarkSent_SetsFields_AndRules()
        {
            var store = Create();
            var task = await store.CreateAsync(Repo, "T", null);
            var before = DateTimeOffset.UtcNow;

            Assert.True(await store.MarkSentAsync(task.Id, "sess"));

            var sent = store.Get(task.Id)!;
            Assert.True(sent.IsSent);
            Assert.Equal("sess", sent.SessionId);
            Assert.True(sent.SentAt >= before.AddSeconds(-1));
            Assert.False(await store.MarkSentAsync(task.Id, "other"));
            Assert.Equal("sess", store.Get(task.Id)!.SessionId);
            Assert.False(await store.MarkSentAsync("missing", "x"));
            await Assert.ThrowsAsync<ArgumentException>(() => store.MarkSentAsync(task.Id, " "));
        }

        [Fact]
        public async Task DeleteBySession_DeletesAllMatching_AndCounts()
        {
            var store = Create();
            var a = await store.CreateAsync(Repo, "A", null);
            var b = await store.CreateAsync(Repo, "B", null);
            var c = await store.CreateAsync(Repo, "C", null);
            var d = await store.CreateAsync(Repo, "D", null);
            await store.MarkSentAsync(a.Id, "s1");
            await store.MarkSentAsync(b.Id, "s1");
            await store.MarkSentAsync(c.Id, "S1");

            var raised = 0;
            store.Changed += () => raised++;

            Assert.Equal(2, await store.DeleteBySessionAsync("s1"));
            Assert.Equal(1, raised);
            Assert.Equal(["C", "D"], store.GetAll().Select(t => t.Title).ToArray());
            Assert.Equal(2, Create().GetAll().Count);
        }

        [Fact]
        public async Task DeleteBySession_NoMatch_DoesNotWriteOrRaise()
        {
            var store = Create();
            await store.CreateAsync(Repo, "A", null);
            var stamp = File.GetLastWriteTimeUtc(FilePath);
            var raised = 0;
            store.Changed += () => raised++;

            Assert.Equal(0, await store.DeleteBySessionAsync("none"));

            Assert.Equal(0, raised);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(FilePath));
        }

        [Fact]
        public async Task Changed_RaisedOnMutations_HandlerExceptionsSwallowed()
        {
            var store = Create();
            var raised = 0;
            store.Changed += () => raised++;
            store.Changed += () => throw new InvalidOperationException("boom");

            var task = await store.CreateAsync(Repo, "T", null);
            await store.UpdateAsync(task.Id, "U", null);
            await store.MarkSentAsync(task.Id, "s");
            await store.DeleteAsync(task.Id);
            await store.DeleteAsync(task.Id); // no change

            Assert.Equal(4, raised);
        }

        [Fact]
        public async Task GetForRepository_FiltersCaseInsensitively_OldestFirst()
        {
            var store = Create();
            var first = await store.CreateAsync(Repo, "first", null);
            await Task.Delay(5);
            await store.CreateAsync(Other, "other", null);
            await Task.Delay(5);
            var third = await store.CreateAsync(new RepositoryInfo("Repo", Repo.Path.ToUpperInvariant() + Path.DirectorySeparatorChar), "third", null);

            var list = store.GetForRepository(Repo.Path.ToLowerInvariant());

            Assert.Equal([first.Id, third.Id], list.Select(t => t.Id).ToArray());
            Assert.Single(store.GetForRepository(Other.Path));
            Assert.Empty(store.GetForRepository(Path.Combine(Path.GetTempPath(), "none")));
        }

        [Fact]
        public async Task GetAll_SortedByCreatedAt()
        {
            var store = Create();
            await store.CreateAsync(Repo, "1", null);
            await Task.Delay(5);
            await store.CreateAsync(Other, "2", null);
            await Task.Delay(5);
            await store.CreateAsync(Repo, "3", null);

            Assert.Equal(["1", "2", "3"], Create().GetAll().Select(t => t.Title).ToArray());
        }

        [Fact]
        public async Task CorruptFile_IsBackedUp_AndStoreStartsEmpty()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, "{ not json");
            var store = Create();

            Assert.Empty(store.GetAll());
            var backups = Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "tasks.json.corrupt-*");
            Assert.Single(backups);
            Assert.Equal("{ not json", File.ReadAllText(backups[0]));

            await store.CreateAsync(Repo, "T", null);
            Assert.Single(Create().GetAll());
        }

        [Fact]
        public void NullAndIdlessEntries_AreSkipped()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, """{"version":1,"tasks":[null,{"title":"no id"},{"id":"a","title":"ok"}]}""");

            var all = Create().GetAll();

            Assert.Equal("a", Assert.Single(all).Id);
        }

        [Fact]
        public void NullAndIdlessEntries_LogWarningWithCount()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, """{"version":1,"tasks":[null,{"title":"no id"},{"id":"a","title":"ok"}]}""");
            var logger = new CapturingLogger();

            Create(logger).GetAll();

            var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
            Assert.Contains("2", entry.Message);
        }

        [Fact]
        public void ValidFile_LogsNoWarning()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, """{"version":1,"tasks":[{"id":"a","title":"ok"}]}""");
            var logger = new CapturingLogger();

            Create(logger).GetAll();

            Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        }

        private sealed class CapturingLogger : ILogger<TaskStore>
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                Entries.Add((logLevel, formatter(state, exception)));
        }

        [Fact]
        public async Task UnreadableFile_IsNotOverwritten_AndRetried()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, """{"version":1,"tasks":[{"id":"a","title":"ok"}]}""");
            var store = Create();

            using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Empty(store.GetAll());
                await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(Repo, "T", null));
            }

            Assert.Equal("a", Assert.Single(store.GetAll()).Id);
            Assert.Contains("\"ok\"", File.ReadAllText(FilePath));
        }

        [Fact]
        public async Task Write_LeavesNoTempFiles()
        {
            var store = Create();
            await store.CreateAsync(Repo, "T", null);

            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "*.tmp"));
        }
    }
}
