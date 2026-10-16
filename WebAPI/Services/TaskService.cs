using AutoPilot.WebAPI.Database;
using AutoPilot.WebAPI.Database.Models;
using GitHub.Copilot;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoPilot.WebAPI.Services
{
	public class TaskService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<TaskService> logger) : BackgroundService
	{
		private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
		private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(30);
		private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
		// Compared against heartbeats written by other hosts (recovering host's clock vs. writing host's clock), so clocks are assumed to be roughly synchronized
		private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(2);
		private static readonly Guid InstanceId = Guid.NewGuid();
		private const string InterruptedMessage = "Task was interrupted (the worker stopped responding) and can be restarted.";
		private const string CopilotModel = "claude-sonnet-5.5";
		private const string CopilotReasoningEffort = "medium";
		private const string OrchestratorAgent = "orchestrator";

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					try
					{
						await RecoverStaleTasksAsync(stoppingToken);
					}
					catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
					{
						logger.LogError(ex, "Stale task recovery failed");
					}

					bool processed = await ProcessNextAsync(stoppingToken);

					if (!processed)
					{
						await Task.Delay(PollInterval, stoppingToken);
					}
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex)
				{
					logger.LogError(ex, "Task processing failed");
					await Task.Delay(PollInterval, stoppingToken);
				}
			}
		}

		private async Task RecoverStaleTasksAsync(CancellationToken cancellationToken)
		{
			using IServiceScope scope = scopeFactory.CreateScope();
			AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

			DateTime cutoff = DateTime.UtcNow - LeaseTimeout;
			var staleTasks = await db.Tasks
				.Where(t => t.Status == RepositoryTaskStatus.Processing && (t.HeartbeatDateTime == null || t.HeartbeatDateTime < cutoff))
				.Select(t => new { t.Id, t.ProcessingInstanceId, t.ProcessingProcessId })
				.ToListAsync(cancellationToken);

			foreach (var stale in staleTasks)
			{
				Guid taskId = stale.Id;
				await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
				// The predicate is repeated so that only one instance recovers a given task
				int updated = await db.Tasks
					.Where(t => t.Id == taskId && t.Status == RepositoryTaskStatus.Processing && (t.HeartbeatDateTime == null || t.HeartbeatDateTime < cutoff))
					.ExecuteUpdateAsync(s => s
						.SetProperty(t => t.Status, RepositoryTaskStatus.Error)
						.SetProperty(t => t.CompletionDateTime, DateTime.UtcNow)
						.SetProperty(t => t.ProcessingInstanceId, (Guid?)null)
						.SetProperty(t => t.ProcessingProcessId, (int?)null)
						.SetProperty(t => t.HeartbeatDateTime, (DateTime?)null), cancellationToken);

				if (updated == 0)
				{
					continue;
				}

				db.Conversations.Add(new Conversation { TaskId = taskId, Author = ConversationAuthor.Autopilot, Content = InterruptedMessage });
				await db.SaveChangesAsync(cancellationToken);
				await transaction.CommitAsync(cancellationToken);
				logger.LogWarning("Recovered stale task {TaskId} (instance {ProcessingInstanceId}, process {ProcessingProcessId})", taskId, stale.ProcessingInstanceId, stale.ProcessingProcessId);
			}
		}

		private async Task RunHeartbeatAsync(Guid taskId, CancellationToken cancellationToken)
		{
			using PeriodicTimer timer = new(HeartbeatInterval);

			try
			{
				while (await timer.WaitForNextTickAsync(cancellationToken))
				{
					try
					{
						using IServiceScope scope = scopeFactory.CreateScope();
						AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
						await db.Tasks
							.Where(t => t.Id == taskId && t.Status == RepositoryTaskStatus.Processing && t.ProcessingInstanceId == InstanceId)
							.ExecuteUpdateAsync(s => s.SetProperty(t => t.HeartbeatDateTime, DateTime.UtcNow), cancellationToken);
					}
					catch (Exception ex) when (ex is not OperationCanceledException)
					{
						logger.LogWarning(ex, "Could not update heartbeat for task {TaskId}", taskId);
					}
				}
			}
			catch (OperationCanceledException)
			{
			}
		}

		private async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
		{
			using IServiceScope scope = scopeFactory.CreateScope();
			AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

			RepositoryTask? task = await db.Tasks
				.Include(t => t.Repository)
				.Where(t => t.Status == RepositoryTaskStatus.PendingStart)
				.OrderBy(t => t.CreationDateTime)
				.FirstOrDefaultAsync(cancellationToken);

			if (task is null)
			{
				return false;
			}

			task.Status = RepositoryTaskStatus.Processing;
			task.ProcessingInstanceId = InstanceId;
			task.ProcessingProcessId = Environment.ProcessId;
			task.HeartbeatDateTime = DateTime.UtcNow;
			await db.SaveChangesAsync(cancellationToken);

			logger.LogInformation("Processing task {TaskId}", task.Id);

			using CancellationTokenSource heartbeatCts = new();
			Task heartbeat = RunHeartbeatAsync(task.Id, heartbeatCts.Token);

			try
			{
				await ProcessTaskAsync(db, task, cancellationToken);
			}
			finally
			{
				await heartbeatCts.CancelAsync();
				await heartbeat;
			}

			return true;
		}

		private async Task ProcessTaskAsync(AppDbContext db, RepositoryTask task, CancellationToken cancellationToken)
		{
			List<string> userMessages = await db.Conversations
				.Where(c => c.TaskId == task.Id && c.Author == ConversationAuthor.Neph)
				.OrderBy(c => c.CreationDateTime)
				.Select(c => c.Content)
				.ToListAsync(cancellationToken);
			bool isFollowUp = userMessages.Count > 1 && Directory.Exists(GetRepositoryFolder(task.Id));
			string prompt = (isFollowUp ? userMessages[^1] : userMessages.FirstOrDefault()) ?? string.Empty;

			string? response;
			RepositoryTaskStatus finalStatus;
			string? branchName = task.BranchName;
			RunCost cost = new();
			try
			{
				response = await RunWithCopilotAsync(task, prompt, isFollowUp, cost, cancellationToken);
				branchName = await FindCreatedBranchAsync(task, cancellationToken);

				finalStatus = string.IsNullOrEmpty(branchName) ? RepositoryTaskStatus.Completed : RepositoryTaskStatus.WaitingForMerge;
				logger.LogInformation("Completed task {TaskId}", task.Id);
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
			{
				finalStatus = RepositoryTaskStatus.Error;
				response = ex.ToString();
				logger.LogError(ex, "Task {TaskId} failed", task.Id);
			}
			catch (OperationCanceledException)
			{
				// Host is stopping: leave the task restartable instead of stuck in Processing
				try
				{
					await FinalizeTaskAsync(db, task, RepositoryTaskStatus.Error, task.BranchName, InterruptedMessage, cost.Aiu);
				}
				catch (Exception saveEx)
				{
					logger.LogError(saveEx, "Could not mark interrupted task {TaskId} as failed; it will be recovered when its lease expires", task.Id);
				}

				throw;
			}

			try
			{
				await FinalizeTaskAsync(db, task, finalStatus, branchName, response, cost.Aiu);
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Could not save final state of task {TaskId}; it will be recovered when its lease expires", task.Id);
				throw;
			}
		}

		// Guarded final write: only applies if this instance still owns the task (it may have been recovered as stale in the meantime)
		private async Task FinalizeTaskAsync(AppDbContext db, RepositoryTask task, 		RepositoryTaskStatus status, string? branchName, string? message, decimal runCost)
		{
			await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);

			DateTime completionDateTime = DateTime.UtcNow;
			int updated = await db.Tasks
				.Where(t => t.Id == task.Id && t.Status == RepositoryTaskStatus.Processing && t.ProcessingInstanceId == InstanceId)
				.ExecuteUpdateAsync(s => s
					.SetProperty(t => t.Status, status)
					.SetProperty(t => t.BranchName, branchName)
					.SetProperty(t => t.Cost, t => runCost > 0 ? (t.Cost ?? 0m) + runCost : t.Cost)
					.SetProperty(t => t.CompletionDateTime, completionDateTime)
					.SetProperty(t => t.ProcessingInstanceId, (Guid?)null)
					.SetProperty(t => t.ProcessingProcessId, (int?)null)
					.SetProperty(t => t.HeartbeatDateTime, (DateTime?)null), CancellationToken.None);

			if (updated == 0)
			{
				logger.LogWarning("Task {TaskId} is no longer owned by this instance (it was likely recovered as stale); skipping final update", task.Id);
				return;
			}

			if (message is not null)
			{
				db.Conversations.Add(new Conversation { TaskId = task.Id, Author = ConversationAuthor.Autopilot, Content = message });
				await db.SaveChangesAsync(CancellationToken.None);
			}

			await transaction.CommitAsync(CancellationToken.None);
			logger.LogInformation("Finalized task {TaskId} as {Status}", task.Id, status);
		}
		private static string BuildBranchInstructions(RepositoryTask task) =>
			$"\n\nIf you modify any code, do not commit to the default branch: create a new branch named `{BuildBranchName(task)}`, commit your changes to it, and push that branch to the remote.";

		private static string BuildBranchName(RepositoryTask task)
		{
			string slug = Regex.Replace(Regex.Replace(task.Title.Normalize(NormalizationForm.FormD), @"\p{M}", ""), @"[^a-zA-Z0-9]+", "-").Trim('-').ToLowerInvariant();

			if (slug.Length > 50)
			{
				slug = slug[..50].TrimEnd('-');
			}

			return $"autopilot/{(slug.Length == 0 ? task.Id : slug)}";
		}

		private static List<CustomAgentConfig> LoadAgents() =>
		[
			LoadAgent(OrchestratorAgent, "Orchestrator", "Plans the task, delegates implementation to the developer and review to the reviewer, and makes the final decision."),
			LoadAgent("developer", "Developer", "Implements the assigned task in the codebase."),
			LoadAgent("reviewer", "Reviewer", "Independently reviews code changes and reports findings without modifying code."),
		];

		private static CustomAgentConfig LoadAgent(string name, string displayName, string description) => new()
		{
			Name = name,
			DisplayName = displayName,
			Description = description,
			Prompt = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Agents", $"{name}.md")),
			Infer = true,
		};

		// Thread-safe accumulator of the Copilot cost (in nano-AIU) of a single run; usage events may arrive concurrently
		private sealed class RunCost
		{
			private long nanoAiu;

			public void Add(double nano) => Interlocked.Add(ref nanoAiu, (long)Math.Round(nano));

			public decimal Aiu => Interlocked.Read(ref nanoAiu) / 1_000_000_000m;
		}

		private async Task<string?> RunWithCopilotAsync(RepositoryTask task, string prompt, bool isFollowUp, RunCost cost, CancellationToken cancellationToken)
		{
			string token = configuration["Copilot:Token"]
				?? throw new InvalidOperationException("Copilot:Token is not configured");

			string workingDirectory = isFollowUp ? GetRepositoryFolder(task.Id) : await CheckoutRepositoryAsync(task, cancellationToken);

			await using CopilotClient client = new(new CopilotClientOptions
			{
				GitHubToken = token,
				WorkingDirectory = workingDirectory,
			});

			await client.StartAsync(cancellationToken);

			await using CopilotSession session = isFollowUp
				? await client.ResumeSessionAsync(task.Id.ToString(), new ResumeSessionConfig
				{
					Model = CopilotModel,
					ReasoningEffort = CopilotReasoningEffort,
					WorkingDirectory = workingDirectory,
					CustomAgents = LoadAgents(),
					Agent = OrchestratorAgent,
					OnPermissionRequest = PermissionHandler.ApproveAll
				}, cancellationToken)
				: await client.CreateSessionAsync(new SessionConfig
				{
					SessionId = task.Id.ToString(),
					Model = CopilotModel,
					ReasoningEffort = CopilotReasoningEffort,
					WorkingDirectory = workingDirectory,
					CustomAgents = LoadAgents(),
					Agent = OrchestratorAgent,
					OnPermissionRequest = PermissionHandler.ApproveAll
				});

			session.On<AssistantReasoningEvent>(evt =>
			{
				try
				{
					using IServiceScope scope = scopeFactory.CreateScope();
					AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
					db.Conversations.Add(new Conversation { TaskId = task.Id, Author = ConversationAuthor.Autopilot, Type = ConversationType.Reasoning, Content = evt.Data.Content });
					db.SaveChanges();
				}
				catch (Exception ex)
				{
					logger.LogWarning(ex, "Could not store session event for task {TaskId}", task.Id);
				}
			});

			// Every model call (including sub-agents') reports its own cost; the sum is the cost of this run
			session.On<AssistantUsageEvent>(evt =>
			{
				if (evt.Data.CopilotUsage is { } usage)
				{
					cost.Add(usage.TotalNanoAiu);
				}
			});

			AssistantMessageEvent? response = await session.SendAndWaitAsync(
				new MessageOptions { Prompt = $"{prompt}{BuildBranchInstructions(task)}" },
				ProcessingTimeout,
				cancellationToken);

			logger.LogInformation("Copilot response for task {TaskId}: {Response}", task.Id, response?.Data.Content);

			return response?.Data.Content;
		}

		private async Task<string?> FindCreatedBranchAsync(RepositoryTask task, CancellationToken cancellationToken)
		{
			try
			{
				string folder = GetRepositoryFolder(task.Id);
				string branch = BuildBranchName(task);

				(int exitCode, string _) = await RunGitAsync(folder, cancellationToken, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
				if (exitCode != 0)
				{
					return null;
				}

				(exitCode, string output) = await RunGitAsync(folder, cancellationToken, "rev-list", "--count", branch, "--not", "origin/HEAD");
				return exitCode == 0 && int.TryParse(output.Trim(), out int count) && count > 0 ? branch : null;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				logger.LogWarning(ex, "Could not determine branch for task {TaskId}", task.Id);
				return null;
			}
		}

		private static ProcessStartInfo CreateGitStartInfo(string? workingDirectory, params string[] args)
		{
			ProcessStartInfo startInfo = new("git")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			if (workingDirectory is not null)
			{
				startInfo.WorkingDirectory = workingDirectory;
			}

			foreach (string arg in args)
			{
				startInfo.ArgumentList.Add(arg);
			}

			return startInfo;
		}

		private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
		{
			using Process process = Process.Start(startInfo)
				?? throw new InvalidOperationException("Failed to start git");

			Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
			Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
			await process.WaitForExitAsync(cancellationToken);

			return (process.ExitCode, await stdout, await stderr);
		}

		private static async Task<(int ExitCode, string Output)> RunGitAsync(string workingDirectory, CancellationToken cancellationToken, params string[] args)
		{
			(int exitCode, string output, _) = await RunProcessAsync(CreateGitStartInfo(workingDirectory, args), cancellationToken);
			return (exitCode, output);
		}

		private static string GetRepositoryFolder(Guid taskId) => Path.Combine(AppContext.BaseDirectory, "Tasks", taskId.ToString());

		public static void DeleteRepositoryFolder(Guid taskId)
		{
			string folder = GetRepositoryFolder(taskId);

			if (!Directory.Exists(folder))
			{
				return;
			}

			// Git object files are read-only, which blocks recursive deletion on Windows
			foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
			{
				File.SetAttributes(file, FileAttributes.Normal);
			}

			Directory.Delete(folder, recursive: true);
		}

		private async Task<string> CheckoutRepositoryAsync(RepositoryTask task, CancellationToken cancellationToken)
		{
			Repository repository = task.Repository
				?? throw new InvalidOperationException("Task repository not loaded");

			string folder = GetRepositoryFolder(task.Id);

			DeleteRepositoryFolder(task.Id);

			Directory.CreateDirectory(Path.GetDirectoryName(folder)!);

			ProcessStartInfo startInfo = CreateGitStartInfo(null, "clone", repository.CloneUrl, folder);

			string? githubToken = configuration["Github:Token"];
			if (!string.IsNullOrEmpty(githubToken))
			{
				string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{githubToken}"));
				startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
				startInfo.Environment["GIT_CONFIG_KEY_0"] = "http.extraheader";
				startInfo.Environment["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: basic {credentials}";
			}

			startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

			(int exitCode, _, string error) = await RunProcessAsync(startInfo, cancellationToken);

			if (exitCode != 0)
			{
				throw new InvalidOperationException($"git clone failed: {error}");
			}

			return folder;
		}
	}
}
