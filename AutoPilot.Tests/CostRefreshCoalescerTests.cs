using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class CostRefreshCoalescerTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Fact]
        public void Constructor_NullRefresh_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new CostRefreshCoalescer(null!));
        }

        [Fact]
        public async Task FirstRequest_RunsImmediately()
        {
            var started = Gate();
            using var coalescer = new CostRefreshCoalescer(() => { started.TrySetResult(); return Task.CompletedTask; });

            coalescer.Request();

            await started.Task.WaitAsync(Wait);
            await coalescer.WhenIdle().WaitAsync(Wait);
        }

        [Fact]
        public async Task RequestsDuringRun_CoalesceIntoOneFollowUp_WithoutOverlap()
        {
            var runs = 0;
            var concurrent = 0;
            var maxConcurrent = 0;
            var firstStarted = Gate();
            var release = Gate();
            using var coalescer = new CostRefreshCoalescer(async () =>
            {
                var n = Interlocked.Increment(ref runs);
                var c = Interlocked.Increment(ref concurrent);
                maxConcurrent = Math.Max(maxConcurrent, c);
                if (n == 1)
                {
                    firstStarted.TrySetResult();
                    await release.Task;
                }

                Interlocked.Decrement(ref concurrent);
            });

            coalescer.Request();
            await firstStarted.Task.WaitAsync(Wait);
            for (var i = 0; i < 10; i++)
            {
                coalescer.Request();
            }

            Assert.Equal(1, Volatile.Read(ref runs));
            release.SetResult();
            await coalescer.WhenIdle().WaitAsync(Wait);

            Assert.Equal(2, runs);
            Assert.Equal(1, maxConcurrent);
        }

        [Fact]
        public async Task RefreshException_DoesNotStopFutureRefreshes()
        {
            var runs = 0;
            using var coalescer = new CostRefreshCoalescer(() =>
            {
                if (Interlocked.Increment(ref runs) == 1)
                {
                    throw new InvalidOperationException("boom");
                }

                return Task.CompletedTask;
            });

            coalescer.Request();
            await coalescer.WhenIdle().WaitAsync(Wait);
            coalescer.Request();
            await coalescer.WhenIdle().WaitAsync(Wait);

            Assert.Equal(2, runs);
        }

        [Fact]
        public async Task Dispose_DropsQueuedFollowUp_AndIgnoresFutureRequests()
        {
            var runs = 0;
            var firstStarted = Gate();
            var release = Gate();
            var coalescer = new CostRefreshCoalescer(async () =>
            {
                Interlocked.Increment(ref runs);
                firstStarted.TrySetResult();
                await release.Task;
            });

            coalescer.Request();
            await firstStarted.Task.WaitAsync(Wait);
            coalescer.Request();
            coalescer.Dispose();
            release.SetResult();
            await coalescer.WhenIdle().WaitAsync(Wait);

            coalescer.Request();
            await coalescer.WhenIdle().WaitAsync(Wait);

            Assert.Equal(1, runs);
        }
    }
}
