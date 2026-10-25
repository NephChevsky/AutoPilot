using GitHub.Copilot;

namespace AutoPilot.Services
{
    /// <summary>Pure helpers that turn SDK metrics into snapshots and apply resume-safe deltas to stored costs.</summary>
    public static class UsageAccumulator
    {
        public const double NanoPerCredit = 1e9;
        public const int MaxTitleLength = 100;

        /// <summary>
        /// new total = stored + max(0, current - baseline), field by field (including per-model and per-agent figures).
        /// <paramref name="baseline"/> is the SDK metrics when the session was created/resumed and <paramref name="stored"/>
        /// the ledger record at that same moment, so repeated calls with growing <paramref name="current"/> never double-count.
        /// Inputs are not modified.
        /// </summary>
        public static SessionCost Apply(string sessionId, SessionCost? stored, UsageSnapshot baseline, UsageSnapshot current, string? title = null, DateTimeOffset? now = null)
        {
            var timestamp = now ?? DateTimeOffset.UtcNow;
            var result = new SessionCost
            {
                SessionId = sessionId,
                Title = stored?.Title ?? Truncate(title),
                FirstSeen = stored?.FirstSeen ?? timestamp,
                LastUpdated = timestamp,
                PremiumRequests = (stored?.PremiumRequests ?? 0) + Delta(current.PremiumRequests, baseline.PremiumRequests),
                NanoAiu = (stored?.NanoAiu ?? 0) + Delta(current.NanoAiu, baseline.NanoAiu),
                InputTokens = (stored?.InputTokens ?? 0) + Delta(current.InputTokens, baseline.InputTokens),
                OutputTokens = (stored?.OutputTokens ?? 0) + Delta(current.OutputTokens, baseline.OutputTokens),
                CacheReadTokens = (stored?.CacheReadTokens ?? 0) + Delta(current.CacheReadTokens, baseline.CacheReadTokens),
                CacheWriteTokens = (stored?.CacheWriteTokens ?? 0) + Delta(current.CacheWriteTokens, baseline.CacheWriteTokens),
                ReasoningTokens = (stored?.ReasoningTokens ?? 0) + Delta(current.ReasoningTokens, baseline.ReasoningTokens),
            };

            var storedModels = stored?.Models ?? [];
            foreach (var key in storedModels.Keys.Union(current.Models.Keys))
            {
                result.Models[key] = Combine(new ModelCost(), storedModels.GetValueOrDefault(key), baseline.Models.GetValueOrDefault(key), current.Models.GetValueOrDefault(key));
            }

            var storedAgents = stored?.Agents ?? [];
            foreach (var key in storedAgents.Keys.Union(current.Agents.Keys))
            {
                var agent = Combine(new AgentCost(), storedAgents.GetValueOrDefault(key), baseline.Agents.GetValueOrDefault(key), current.Agents.GetValueOrDefault(key));
                agent.DisplayName = current.Agents.GetValueOrDefault(key)?.DisplayName ?? storedAgents.GetValueOrDefault(key)?.DisplayName;
                result.Agents[key] = agent;
            }

            return result;
        }

        /// <summary>True when the cost records any spend (premium requests, AI credits or tokens).</summary>
        public static bool HasUsage(SessionCost cost) =>
            cost.PremiumRequests > 0 || cost.NanoAiu > 0 || cost.InputTokens > 0 || cost.OutputTokens > 0
            || cost.CacheReadTokens > 0 || cost.CacheWriteTokens > 0 || cost.ReasoningTokens > 0;

        /// <summary>Sums all records.</summary>
        public static CostTotals Total(IEnumerable<SessionCost> costs)
        {
            var count = 0;
            double premium = 0, nano = 0;
            long input = 0, output = 0, cacheRead = 0, cacheWrite = 0, reasoning = 0;
            foreach (var cost in costs)
            {
                count++;
                premium += cost.PremiumRequests;
                nano += cost.NanoAiu;
                input += cost.InputTokens;
                output += cost.OutputTokens;
                cacheRead += cost.CacheReadTokens;
                cacheWrite += cost.CacheWriteTokens;
                reasoning += cost.ReasoningTokens;
            }

            return new CostTotals(count, premium, nano, input, output, cacheRead, cacheWrite, reasoning);
        }

        /// <summary>Maps SDK metrics to a snapshot. "main" is named <paramref name="orchestratorName"/>; agents are aggregated by name.</summary>
        public static UsageSnapshot ToSnapshot(UsageGetMetricsResult metrics, string orchestratorName)
        {
            var snapshot = new UsageSnapshot
            {
                PremiumRequests = ToDouble(metrics.TotalPremiumRequestCost),
                NanoAiu = ToDouble(metrics.TotalNanoAiu),
            };

            foreach (var (model, metric) in metrics.ModelMetrics as IEnumerable<KeyValuePair<string, UsageMetricsModelMetric>> ?? [])
            {
                var cost = ToFigures(new ModelCost(), metric);
                snapshot.Models[model] = cost;
                snapshot.InputTokens += cost.InputTokens;
                snapshot.OutputTokens += cost.OutputTokens;
                snapshot.CacheReadTokens += cost.CacheReadTokens;
                snapshot.CacheWriteTokens += cost.CacheWriteTokens;
                snapshot.ReasoningTokens += cost.ReasoningTokens;
            }

            foreach (var (instanceId, metric) in metrics.AgentMetrics as IEnumerable<KeyValuePair<string, UsageMetricsAgentMetric>> ?? [])
            {
                var name = instanceId == "main" || string.IsNullOrWhiteSpace(metric.AgentName) ? orchestratorName : metric.AgentName;
                if (!snapshot.Agents.TryGetValue(name, out var agent))
                {
                    agent = new AgentCost { DisplayName = instanceId == "main" ? orchestratorName : metric.AgentDisplayName };
                    snapshot.Agents[name] = agent;
                }

                agent.NanoAiu += ToDouble(metric.TotalNanoAiu);
                foreach (var (_, modelMetric) in metric.ModelMetrics as IEnumerable<KeyValuePair<string, UsageMetricsModelMetric>> ?? [])
                {
                    var figures = ToFigures(new ModelCost(), modelMetric);
                    agent.Requests += figures.Requests;
                    agent.PremiumCost += figures.PremiumCost;
                    agent.InputTokens += figures.InputTokens;
                    agent.OutputTokens += figures.OutputTokens;
                    agent.CacheReadTokens += figures.CacheReadTokens;
                    agent.CacheWriteTokens += figures.CacheWriteTokens;
                    agent.ReasoningTokens += figures.ReasoningTokens;
                }
            }

            return snapshot;
        }

        /// <summary>Trims and truncates a prompt to a title; null for blank input.</summary>
        public static string? Truncate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var trimmed = text.Trim();
            return trimmed.Length <= MaxTitleLength ? trimmed : trimmed[..MaxTitleLength];
        }

        private static T ToFigures<T>(T target, UsageMetricsModelMetric metric) where T : UsageFigures
        {
            target.Requests = ToDouble(metric.Requests?.Count);
            target.PremiumCost = ToDouble(metric.Requests?.Cost);
            target.NanoAiu = ToDouble(metric.TotalNanoAiu);
            target.InputTokens = ToLong(metric.Usage?.InputTokens);
            target.OutputTokens = ToLong(metric.Usage?.OutputTokens);
            target.CacheReadTokens = ToLong(metric.Usage?.CacheReadTokens);
            target.CacheWriteTokens = ToLong(metric.Usage?.CacheWriteTokens);
            target.ReasoningTokens = ToLong(metric.Usage?.ReasoningTokens);
            return target;
        }

        private static T Combine<T>(T target, UsageFigures? stored, UsageFigures? baseline, UsageFigures? current) where T : UsageFigures
        {
            target.Requests = (stored?.Requests ?? 0) + Delta(current?.Requests ?? 0, baseline?.Requests ?? 0);
            target.PremiumCost = (stored?.PremiumCost ?? 0) + Delta(current?.PremiumCost ?? 0, baseline?.PremiumCost ?? 0);
            target.NanoAiu = (stored?.NanoAiu ?? 0) + Delta(current?.NanoAiu ?? 0, baseline?.NanoAiu ?? 0);
            target.InputTokens = (stored?.InputTokens ?? 0) + Delta(current?.InputTokens ?? 0, baseline?.InputTokens ?? 0);
            target.OutputTokens = (stored?.OutputTokens ?? 0) + Delta(current?.OutputTokens ?? 0, baseline?.OutputTokens ?? 0);
            target.CacheReadTokens = (stored?.CacheReadTokens ?? 0) + Delta(current?.CacheReadTokens ?? 0, baseline?.CacheReadTokens ?? 0);
            target.CacheWriteTokens = (stored?.CacheWriteTokens ?? 0) + Delta(current?.CacheWriteTokens ?? 0, baseline?.CacheWriteTokens ?? 0);
            target.ReasoningTokens = (stored?.ReasoningTokens ?? 0) + Delta(current?.ReasoningTokens ?? 0, baseline?.ReasoningTokens ?? 0);
            return target;
        }

        private static double Delta(double current, double baseline) => Math.Max(0, current - baseline);

        private static long Delta(long current, long baseline) => Math.Max(0, current - baseline);

        private static double ToDouble(object? value) => value is null ? 0 : Convert.ToDouble(value);

        private static long ToLong(object? value) => value is null ? 0 : Convert.ToInt64(Math.Round(Convert.ToDouble(value)));
    }
}
