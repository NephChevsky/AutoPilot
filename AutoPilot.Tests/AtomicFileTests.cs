using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public sealed class AtomicFileTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "autopilot-atomic-" + Guid.NewGuid().ToString("N"));

        public AtomicFileTests() => Directory.CreateDirectory(_directory);

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

        private string Path_(string name) => Path.Combine(_directory, name);

        /// <summary>Budget of about 30 ms, for tests that hold a lock longer than the retry budget.</summary>
        private static readonly FileRetryPolicy TinyPolicy = new(3, TimeSpan.FromMilliseconds(10));

        private static FileStream HoldOpen(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        [Fact]
        public async Task Move_SucceedsAfterDestinationLockIsReleased()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var source = Path_("source.tmp");
            var destination = Path_("dest.json");
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "old");

            var holder = HoldOpen(destination);
            var release = Task.Run(async () =>
            {
                await Task.Delay(100);
                holder.Dispose();
            });

            await AtomicFile.MoveWithRetryAsync(source, destination, CancellationToken.None);
            await release;

            Assert.Equal("new", File.ReadAllText(destination));
            Assert.False(File.Exists(source));
        }

        [Fact]
        public async Task Move_LockLongerThanBudget_ThrowsAndKeepsSource()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var source = Path_("source.tmp");
            var destination = Path_("dest.json");
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "old");

            var holder = HoldOpen(destination);
            try
            {
                var ex = await Record.ExceptionAsync(() => AtomicFile.MoveWithRetryAsync(source, destination, TinyPolicy, CancellationToken.None));
                Assert.True(ex is IOException or UnauthorizedAccessException, ex?.ToString());
            }
            finally
            {
                holder.Dispose();
            }

            Assert.True(File.Exists(source));
            Assert.Equal("old", File.ReadAllText(destination));
        }

        [Fact]
        public void ReadAllText_WorksWhileFileIsOpenForWriting()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var path = Path_("data.json");
            File.WriteAllText(path, "hello");

            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            Assert.Equal("hello", AtomicFile.ReadAllText(path));
        }

        [Fact]
        public void ReadAllText_HandlesUtf8Bom()
        {
            var path = Path_("bom.json");
            File.WriteAllText(path, "héllo", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Assert.Equal("héllo", AtomicFile.ReadAllText(path));
        }

        [Fact]
        public async Task Move_SucceedsAfterShareDeleteReaderIsReleased()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var source = Path_("source.tmp");
            var destination = Path_("dest.json");
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "old");

            // Windows' replace-move (MoveFileEx) is denied while any handle is open on the target, even with FileShare.Delete,
            // so the retry must cover a reader that is released shortly after. FileShare.Delete itself lets the file be renamed.
            var holder = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var aside = Path_("aside.json");
            File.Move(destination, aside);
            File.Move(aside, destination);

            var release = Task.Run(async () =>
            {
                await Task.Delay(100);
                holder.Dispose();
            });

            await AtomicFile.MoveWithRetryAsync(source, destination, CancellationToken.None);
            await release;

            Assert.Equal("new", File.ReadAllText(destination));
        }

        [Fact]
        public async Task Move_MissingSource_ThrowsFileNotFoundWithoutRetry()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                AtomicFile.MoveWithRetryAsync(Path_("missing.tmp"), Path_("dest.json"), new FileRetryPolicy(8, TimeSpan.FromSeconds(5)), CancellationToken.None));

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "A missing source must not be retried.");
        }

        [Fact]
        public async Task Move_Cancelled_Throws()
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AtomicFile.MoveWithRetryAsync(Path_("a.tmp"), Path_("b.json"), cts.Token));
        }

        [Fact]
        public async Task Move_CancelledDuringBackoff_Throws()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var source = Path_("source.tmp");
            var destination = Path_("dest.json");
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "old");

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            using (HoldOpen(destination))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    AtomicFile.MoveWithRetryAsync(source, destination, new FileRetryPolicy(8, TimeSpan.FromSeconds(5)), cts.Token));
            }

            Assert.True(File.Exists(source));
        }
    }
}
