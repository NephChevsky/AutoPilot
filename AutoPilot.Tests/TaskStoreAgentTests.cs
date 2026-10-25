using AutoPilot.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoPilot.Tests
{
    public sealed class TaskStoreAgentTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "autopilot-agenttasks-" + Guid.NewGuid().ToString("N"));
        private static readonly RepositoryInfo Repo = new("Repo", Path.Combine(Path.GetTempPath(), "repo-a"));
        private static readonly RepositoryInfo Other = new("Other", Path.Combine(Path.GetTempPath(), "repo-b"));

        private string FilePath => Path.Combine(_directory, "tasks.json");

        private TaskStore Create() => new(FilePath, NullLogger<TaskStore>.Instance);

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
        public async Task CreateIfNoPendingDuplicate_SkipsPendingDuplicate_AllowsSentOrOtherRepository()
        {
            var store = Create();
            var first = await store.CreateAsync(Repo, "Same", null);

            var (dup, created) = await store.CreateIfNoPendingDuplicateAsync(Repo, " SAME ", null, "o", "s");
            Assert.False(created);
            Assert.Equal(first.Id, dup.Id);
            Assert.Single(store.GetAll());

            var (_, createdOther) = await store.CreateIfNoPendingDuplicateAsync(Other, "Same", null, "o", "s");
            Assert.True(createdOther);

            await store.MarkSentAsync(first.Id, "x");
            var (made, createdAfterSent) = await store.CreateIfNoPendingDuplicateAsync(Repo, "Same", null, "o", "s");
            Assert.True(createdAfterSent);
            Assert.NotEqual(first.Id, made.Id);
            Assert.Equal(3, store.GetAll().Count);
        }

        [Fact]
        public async Task Create_WithoutAtomicCheck_StillAllowsDuplicates()
        {
            var store = Create();
            await store.CreateAsync(Repo, "Same", null);
            await store.CreateAsync(Repo, "Same", null, "o", "s");
            Assert.Equal(2, store.GetAll().Count);
        }

        [Fact]
        public async Task Create_WithAgent_PersistsAndRoundTrips()
        {
            var store = Create();
            var task = await store.CreateAsync(Repo, "T", null, "orchestrator", "session-1");

            Assert.True(task.IsAgentCreated);
            var reloaded = Create().Get(task.Id)!;
            Assert.Equal("orchestrator", reloaded.CreatedBy);
            Assert.Equal("session-1", reloaded.SourceSessionId);
            Assert.True(reloaded.IsAgentCreated);
        }

        [Fact]
        public async Task Create_ByUser_LeavesBothNull()
        {
            var task = await Create().CreateAsync(Repo, "T", null);

            Assert.Null(task.CreatedBy);
            Assert.Null(task.SourceSessionId);
            Assert.False(task.IsAgentCreated);
        }

        [Fact]
        public void LegacyJson_WithoutNewFields_Loads()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(FilePath, """{"version":1,"tasks":[{"id":"abc","repositoryPath":"x","repositoryName":"X","title":"Old","createdAt":"2024-01-01T00:00:00+00:00"}]}""");

            var task = Create().Get("abc")!;

            Assert.Equal("Old", task.Title);
            Assert.Null(task.CreatedBy);
            Assert.Null(task.SourceSessionId);
        }

        [Fact]
        public async Task FindPendingByTitle_MatchesTrimmedCaseInsensitive_PendingAndSameRepoOnly()
        {
            var store = Create();
            var pending = await store.CreateAsync(Repo, "Fix Thing", null);
            var sent = await store.CreateAsync(Repo, "Sent one", null);
            await store.MarkSentAsync(sent.Id, "s");
            await store.CreateAsync(Other, "Other thing", null);

            Assert.Equal(pending.Id, store.FindPendingByTitle(Repo.Path, "  fix thing ")!.Id);
            Assert.Null(store.FindPendingByTitle(Repo.Path, "sent one"));
            Assert.Null(store.FindPendingByTitle(Repo.Path, "other thing"));
            Assert.Null(store.FindPendingByTitle(Repo.Path, " "));
            Assert.NotNull(store.FindPendingByTitle(Repo.Path + Path.DirectorySeparatorChar, "FIX THING"));
        }

        [Fact]
        public async Task DeleteBySession_ClearsSourceSessionId_KeepsTask_AndDeletesSentTasks()
        {
            var store = Create();
            var created = await store.CreateAsync(Repo, "Created by agent", null, "orchestrator", "s1");
            var unrelated = await store.CreateAsync(Repo, "Other agent", null, "orchestrator", "s2");
            var sent = await store.CreateAsync(Repo, "Sent", null);
            await store.MarkSentAsync(sent.Id, "s1");

            var deleted = await store.DeleteBySessionAsync("s1");

            Assert.Equal(1, deleted);
            Assert.Null(store.Get(sent.Id));
            Assert.Null(store.Get(created.Id)!.SourceSessionId);
            Assert.Equal("orchestrator", store.Get(created.Id)!.CreatedBy);
            Assert.Equal("s2", store.Get(unrelated.Id)!.SourceSessionId);
            Assert.Null(Create().Get(created.Id)!.SourceSessionId);
        }
    }
}
