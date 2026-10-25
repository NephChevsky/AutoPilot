namespace AutoPilot.Services
{
    /// <summary>
    /// Runs a refresh delegate without overlap. The first request runs immediately; any number of requests made while a
    /// refresh is running coalesce into exactly one follow-up run that starts after the current one finishes.
    /// </summary>
    public sealed class CostRefreshCoalescer : IDisposable
    {
        private readonly Func<Task> _refresh;
        private readonly Lock _gate = new();
        private bool _running;
        private bool _pending;
        private bool _disposed;
        private TaskCompletionSource _idle = CompletedIdle();

        public CostRefreshCoalescer(Func<Task> refresh)
        {
            ArgumentNullException.ThrowIfNull(refresh);
            _refresh = refresh;
        }

        /// <summary>
        /// Requests a refresh. Non-blocking and thread-safe; does nothing after <see cref="Dispose"/>.
        /// </summary>
        public void Request()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_running)
                {
                    _pending = true;
                    return;
                }

                _running = true;
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _ = Task.Run(RunAsync);
        }

        /// <summary>
        /// Returns a task that completes when no refresh is running or queued.
        /// </summary>
        public Task WhenIdle()
        {
            lock (_gate)
            {
                return _idle.Task;
            }
        }

        /// <summary>
        /// Stops accepting requests and drops a queued follow-up. A refresh already running is not interrupted.
        /// </summary>
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _pending = false;
            }
        }

        private async Task RunAsync()
        {
            while (true)
            {
                try
                {
                    await _refresh();
                }
                catch
                {
                    // The delegate owns its error reporting; a failure must not stop later refreshes.
                }

                TaskCompletionSource idle;
                lock (_gate)
                {
                    if (_pending && !_disposed)
                    {
                        _pending = false;
                        continue;
                    }

                    _running = false;
                    idle = _idle;
                }

                idle.TrySetResult();
                return;
            }
        }

        private static TaskCompletionSource CompletedIdle()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.SetResult();
            return tcs;
        }
    }
}
