using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoPilot.Services;
using Xunit;

namespace AutoPilot.Tests
{
    public class CostRefreshCoalescerAcceptanceTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private static TaskCompletionSource NewGate() =>
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        [Fact]
        public void Constructor_NullDelegate_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new CostRefreshCoalescer(null!));
        }

        [Fact]
        public async Task WhenIdle_NothingRequested_CompletesImmediately()
        {
            using var c = new CostRefreshCoalescer(() => Task.CompletedTask);
            await c.WhenIdle().WaitAsync(Timeout);
        }

        [Fact]
        public async Task Request_RunsRefreshImmediately_WithoutDelay()
        {
            var started = NewGate();
            using var c = new CostRefreshCoalescer(() => { started.TrySetResult(); return Task.CompletedTask; });
            c.Request();
            await started.Task.WaitAsync(Timeout);
            await c.WhenIdle().WaitAsync(Timeout);
        }

        [Fact]
        public async Task Request_DoesNotBlock_WhileRefreshGated()
        {
            var started = NewGate();
            var gate = NewGate();
            using var c = new CostRefreshCoalescer(() => { started.TrySetResult(); return gate.Task; });
            var t = Task.Run(() => { c.Request(); c.Request(); c.Request(); });
            await t.WaitAsync(Timeout);
            await started.Task.WaitAsync(Timeout);
            Assert.False(c.WhenIdle().IsCompleted);
            gate.SetResult();
            await c.WhenIdle().WaitAsync(Timeout);
        }

        [Fact]
        public async Task BurstDuringRun_CoalescesToExactlyOneFollowUp_NoOverlap()
        {
            int calls = 0, concurrent = 0, maxConcurrent = 0;
            var started = NewGate();
            var gate = NewGate();
            using var c = new CostRefreshCoalescer(async () =>
            {
                int n = Interlocked.Increment(ref calls);
                int cur = Interlocked.Increment(ref concurrent);
                int seen;
                while (cur > (seen = Volatile.Read(ref maxConcurrent)))
                    Interlocked.CompareExchange(ref maxConcurrent, cur, seen);
                if (n == 1)
                {
                    started.TrySetResult();
                    await gate.Task;
                }
                else
                {
                    await Task.Yield();
                }
                Interlocked.Decrement(ref concurrent);
            });

            c.Request();
            await started.Task.WaitAsync(Timeout);

            var tasks = new List<Task>();
            for (int i = 0; i < 32; i++)
                tasks.Add(Task.Run(() => { for (int j = 0; j < 50; j++) c.Request(); }));
            await Task.WhenAll(tasks).WaitAsync(Timeout);

            Assert.Equal(1, Volatile.Read(ref calls)); // no overlap, no extra run yet
            gate.SetResult();
            await c.WhenIdle().WaitAsync(Timeout);

            Assert.Equal(2, calls);
            Assert.Equal(1, maxConcurrent);
        }

        [Fact]
        public async Task RequestDuringFollowUp_QueuesAnotherFollowUp()
        {
            int calls = 0;
            var gate1 = NewGate();
            var gate2 = NewGate();
            var started2 = NewGate();
            using var c = new CostRefreshCoalescer(async () =>
            {
                int n = Interlocked.Increment(ref calls);
                if (n == 1) await gate1.Task;
                else if (n == 2) { started2.TrySetResult(); await gate2.Task; }
            });
            c.Request();
            c.Request();
            gate1.SetResult();
            await started2.Task.WaitAsync(Timeout);
            c.Request();
            c.Request();
            gate2.SetResult();
            await c.WhenIdle().WaitAsync(Timeout);
            Assert.Equal(3, calls);
        }

        [Fact]
        public async Task RequestAfterIdle_RunsAgain()
        {
            int calls = 0;
            using var c = new CostRefreshCoalescer(() => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
            c.Request();
            await c.WhenIdle().WaitAsync(Timeout);
            Assert.Equal(1, calls);
            c.Request();
            await c.WhenIdle().WaitAsync(Timeout);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task WhenIdle_WaitsForQueuedFollowUp()
        {
            int calls = 0;
            var gate = NewGate();
            using var c = new CostRefreshCoalescer(async () =>
            {
                if (Interlocked.Increment(ref calls) == 1) await gate.Task;
            });
            c.Request();
            c.Request();
            var idle = c.WhenIdle();
            gate.SetResult();
            await idle.WaitAsync(Timeout);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task SynchronousException_DoesNotBreakCoalescer()
        {
            int calls = 0;
            using var c = new CostRefreshCoalescer(() =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("boom");
                return Task.CompletedTask;
            });
            c.Request();
            await c.WhenIdle().WaitAsync(Timeout);
            c.Request();
            await c.WhenIdle().WaitAsync(Timeout);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task AsyncException_DoesNotBreakCoalescer()
        {
            int calls = 0;
            using var c = new CostRefreshCoalescer(async () =>
            {
                await Task.Yield();
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("boom");
            });
            c.Request();
            await c.WhenIdle().WaitAsync(Timeout);
            c.Request();
            await c.WhenIdle().WaitAsync(Timeout);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task ExceptionInRun_StillRunsQueuedFollowUp()
        {
            int calls = 0;
            var gate = NewGate();
            using var c = new CostRefreshCoalescer(async () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    await gate.Task;
                    throw new InvalidOperationException("boom");
                }
            });
            c.Request();
            c.Request();
            gate.SetResult();
            await c.WhenIdle().WaitAsync(Timeout);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task Dispose_ThenRequest_IsNoOp()
        {
            int calls = 0;
            var c = new CostRefreshCoalescer(() => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
            c.Dispose();
            c.Request();
            await c.WhenIdle().WaitAsync(Timeout);
            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task Dispose_DropsQueuedFollowUp_AndDoesNotInterruptRunning()
        {
            int calls = 0;
            var started = NewGate();
            var gate = NewGate();
            var c = new CostRefreshCoalescer(async () =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await gate.Task;
            });
            c.Request();
            await started.Task.WaitAsync(Timeout);
            c.Request(); // queued
            c.Dispose();
            gate.SetResult();
            await c.WhenIdle().WaitAsync(Timeout);
            Assert.Equal(1, calls);
        }

        [Fact]
        public void Dispose_IsIdempotent()
        {
            var c = new CostRefreshCoalescer(() => Task.CompletedTask);
            c.Dispose();
            c.Dispose();
        }
    }
}
