using GitHub.Copilot;

namespace AutoPilot.Services
{
    /// <summary>Pure helpers that turn SDK metrics into snapshots and session costs, and sum costs.</summary>
    public static class UsageAccumulator
    {
        public const double NanoPerCredit = 1e9;
        public const int MaxTitleLength = 100;

        /// <summary>Builds the cost record of a session from a metrics snapshot. The snapshot is copied, not shared.</summary>
        public static SessionCost ToSessionCost(string sessionId, UsageSnapshot snapshot, string? title, DateTimeOffset firstSeen, DateTimeOffset lastUpdated)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            var cost = new SessionCost
            {
                SessionId = sessionId,
                Title = title,
                FirstSeen = firstSeen,
                LastUpdated = lastUpdated,
                NanoAiu = snapshot.NanoAiu,
                InputTokens = snapshot.InputTokens,
                OutputTokens = snapshot.OutputTokens,
                CacheReadTokens = snapshot.CacheReadTokens,
                CacheWriteTokens = snapshot.CacheWriteTokens,
                ReasoningTokens = snapshot.ReasoningTokens,
            };

            foreach (var (key, model) in snapshot.Models)
            {
                cost.Models[key] = Copy(new ModelCost(), model);
            }

            foreach (var (key, agent) in snapshot.Agents)
            {
                var copy = Copy(new AgentCost(), agent);
                copy.DisplayName = agent.DisplayName;
                cost.Agents[key] = copy;
            }

            return cost;
        }

        private static T Copy<T>(T target, UsageFigures source) where T : UsageFigures
        {
            target.Requests = source.Requests;
            target.NanoAiu = source.NanoAiu;
            target.InputTokens = source.InputTokens;
            target.OutputTokens = source.OutputTokens;
            target.CacheReadTokens = source.CacheReadTokens;
            target.CacheWriteTokens = source.CacheWriteTokens;
            target.ReasoningTokens = source.ReasoningTokens;
            return target;
        }

        /// <summary>True when the cost records any spend (AI credits or tokens).</summary>
        public static bool HasUsage(SessionCost cost) =>
            cost.NanoAiu > 0 || cost.InputTokens > 0 || cost.OutputTokens > 0
            || cost.CacheReadTokens > 0 || cost.CacheWriteTokens > 0 || cost.ReasoningTokens > 0;

        /// <summary>Sums all records.</summary>
        public static CostTotals Total(IEnumerable<SessionCost> costs)
        {
            var count = 0;
            double nano = 0;
            long input = 0, output = 0, cacheRead = 0, cacheWrite = 0, reasoning = 0;
            foreach (var cost in costs)
            {
                count++;
                nano += cost.NanoAiu;
                input += cost.InputTokens;
                output += cost.OutputTokens;
                cacheRead += cost.CacheReadTokens;
                cacheWrite += cost.CacheWriteTokens;
                reasoning += cost.ReasoningTokens;
            }

            return new CostTotals(count, nano, input, output, cacheRead, cacheWrite, reasoning);
        }

        /// <summary>
        /// Sums per-agent figures across all sessions, keyed by agent name (ordinal). DisplayName comes from the most recently
        /// updated session that has one. Inputs are not modified.
        /// </summary>
        public static IReadOnlyDictionary<string, AgentCost> TotalByAgent(IEnumerable<SessionCost> costs)
        {
            var result = new Dictionary<string, AgentCost>(StringComparer.Ordinal);
            var nameDates = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var cost in costs)
            {
                if (cost?.Agents is null)
                {
                    continue;
                }

                foreach (var (name, agent) in cost.Agents)
                {
                    if (agent is null)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(name, out var total))
                    {
                        total = new AgentCost();
                        result[name] = total;
                    }

                    total.Requests += agent.Requests;
                    total.NanoAiu += agent.NanoAiu;
                    total.InputTokens += agent.InputTokens;
                    total.OutputTokens += agent.OutputTokens;
                    total.CacheReadTokens += agent.CacheReadTokens;
                    total.CacheWriteTokens += agent.CacheWriteTokens;
                    total.ReasoningTokens += agent.ReasoningTokens;

                    if (!string.IsNullOrWhiteSpace(agent.DisplayName)
                        && (!nameDates.TryGetValue(name, out var seen) || cost.LastUpdated > seen))
                    {
                        total.DisplayName = agent.DisplayName;
                        nameDates[name] = cost.LastUpdated;
                    }
                }
            }

            return result;
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

        /// <summary>Maps SDK metrics to a snapshot. "main" is named <paramref name="orchestratorName"/>; agents are aggregated by name.</summary>
        public static UsageSnapshot ToSnapshot(UsageGetMetricsResult metrics, string orchestratorName)
        {
            ArgumentNullException.ThrowIfNull(metrics);
            return ToSnapshot(FromSdk(metrics), orchestratorName);
        }

        /// <summary>The single mapping used for both live SDK metrics and the metrics persisted in a session.shutdown event.</summary>
        internal static UsageSnapshot ToSnapshot(UsageMetricsData metrics, string orchestratorName)
        {
            var snapshot = new UsageSnapshot
            {
                NanoAiu = metrics.TotalNanoAiu ?? 0,
            };

            foreach (var (model, metric) in metrics.ModelMetrics ?? [])
            {
                if (metric is null)
                {
                    continue;
                }

                var cost = ToFigures(new ModelCost(), metric);
                snapshot.Models[model] = cost;
                snapshot.InputTokens += cost.InputTokens;
                snapshot.OutputTokens += cost.OutputTokens;
                snapshot.CacheReadTokens += cost.CacheReadTokens;
                snapshot.CacheWriteTokens += cost.CacheWriteTokens;
                snapshot.ReasoningTokens += cost.ReasoningTokens;
            }

            foreach (var (instanceId, metric) in metrics.AgentMetrics ?? [])
            {
                if (metric is null)
                {
                    continue;
                }

                var name = instanceId == "main" || string.IsNullOrWhiteSpace(metric.AgentName) ? orchestratorName : metric.AgentName;
                if (!snapshot.Agents.TryGetValue(name, out var agent))
                {
                    agent = new AgentCost { DisplayName = instanceId == "main" ? orchestratorName : metric.AgentDisplayName };
                    snapshot.Agents[name] = agent;
                }

                agent.NanoAiu += metric.TotalNanoAiu ?? 0;
                foreach (var (_, modelMetric) in metric.ModelMetrics ?? [])
                {
                    if (modelMetric is null)
                    {
                        continue;
                    }

                    var figures = ToFigures(new ModelCost(), modelMetric);
                    agent.Requests += figures.Requests;
                    agent.InputTokens += figures.InputTokens;
                    agent.OutputTokens += figures.OutputTokens;
                    agent.CacheReadTokens += figures.CacheReadTokens;
                    agent.CacheWriteTokens += figures.CacheWriteTokens;
                    agent.ReasoningTokens += figures.ReasoningTokens;
                }
            }

            return snapshot;
        }

        private static UsageMetricsData FromSdk(UsageGetMetricsResult metrics)
        {
            var data = new UsageMetricsData { TotalNanoAiu = ToNullableDouble(metrics.TotalNanoAiu), ModelMetrics = [], AgentMetrics = [] };
            foreach (var (model, metric) in metrics.ModelMetrics as IEnumerable<KeyValuePair<string, UsageMetricsModelMetric>> ?? [])
            {
                data.ModelMetrics[model] = FromSdk(metric);
            }

            foreach (var (instanceId, metric) in metrics.AgentMetrics as IEnumerable<KeyValuePair<string, UsageMetricsAgentMetric>> ?? [])
            {
                var agent = new UsageAgentMetricData
                {
                    AgentName = metric.AgentName,
                    AgentDisplayName = metric.AgentDisplayName,
                    TotalNanoAiu = ToNullableDouble(metric.TotalNanoAiu),
                    ModelMetrics = [],
                };
                foreach (var (model, modelMetric) in metric.ModelMetrics as IEnumerable<KeyValuePair<string, UsageMetricsModelMetric>> ?? [])
                {
                    agent.ModelMetrics[model] = FromSdk(modelMetric);
                }

                data.AgentMetrics[instanceId] = agent;
            }

            return data;
        }

        private static UsageModelMetricData FromSdk(UsageMetricsModelMetric metric) => new()
        {
            TotalNanoAiu = ToNullableDouble(metric.TotalNanoAiu),
            Requests = new UsageRequestsData { Count = ToNullableDouble(metric.Requests?.Count) },
            Usage = new UsageTokensData
            {
                InputTokens = ToNullableDouble(metric.Usage?.InputTokens),
                OutputTokens = ToNullableDouble(metric.Usage?.OutputTokens),
                CacheReadTokens = ToNullableDouble(metric.Usage?.CacheReadTokens),
                CacheWriteTokens = ToNullableDouble(metric.Usage?.CacheWriteTokens),
                ReasoningTokens = ToNullableDouble(metric.Usage?.ReasoningTokens),
            },
        };

        private static T ToFigures<T>(T target, UsageModelMetricData metric) where T : UsageFigures
        {
            target.Requests = metric.Requests?.Count ?? 0;
            target.NanoAiu = metric.TotalNanoAiu ?? 0;
            target.InputTokens = ToLong(metric.Usage?.InputTokens);
            target.OutputTokens = ToLong(metric.Usage?.OutputTokens);
            target.CacheReadTokens = ToLong(metric.Usage?.CacheReadTokens);
            target.CacheWriteTokens = ToLong(metric.Usage?.CacheWriteTokens);
            target.ReasoningTokens = ToLong(metric.Usage?.ReasoningTokens);
            return target;
        }

        private static double? ToNullableDouble(object? value) => value is null ? null : Convert.ToDouble(value);

        private static long ToLong(double? value) => value is null ? 0 : Convert.ToInt64(Math.Round(value.Value));
    }

    /// <summary>Shape of the usage metrics of the SDK and of a <c>session.shutdown</c> event (the subset AutoPilot reads).</summary>
    internal sealed class UsageMetricsData
    {
        public double? TotalNanoAiu { get; set; }

        public Dictionary<string, UsageModelMetricData?>? ModelMetrics { get; set; }

        public Dictionary<string, UsageAgentMetricData?>? AgentMetrics { get; set; }
    }

    internal sealed class UsageAgentMetricData
    {
        public string? AgentName { get; set; }

        public string? AgentDisplayName { get; set; }

        public double? TotalNanoAiu { get; set; }

        public Dictionary<string, UsageModelMetricData?>? ModelMetrics { get; set; }
    }

    internal sealed class UsageModelMetricData
    {
        public double? TotalNanoAiu { get; set; }

        public UsageRequestsData? Requests { get; set; }

        public UsageTokensData? Usage { get; set; }
    }

    internal sealed class UsageRequestsData
    {
        public double? Count { get; set; }
    }

    internal sealed class UsageTokensData
    {
        public double? InputTokens { get; set; }

        public double? OutputTokens { get; set; }

        public double? CacheReadTokens { get; set; }

        public double? CacheWriteTokens { get; set; }

        public double? ReasoningTokens { get; set; }
    }
}
