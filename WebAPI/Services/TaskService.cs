using AutoPilot.WebAPI.Database;
using AutoPilot.WebAPI.Database.Models;
using GitHub.Copilot;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
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
		private const string CancelledMessage = "Task was cancelled.";
		// Running tasks of this instance, so that a cancel request can stop the matching run
		private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> RunningTasks = new();
		// One lock per repository: tasks of the same repository share one folder and must not use it simultaneously
		private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RepositoryLocks = new();
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

		// Requests cancellation of a task being processed by this instance; returns false if it is not running here
		public static bool TryCancel(Guid taskId)
		{
			if (!RunningTasks.TryGetValue(taskId, out CancellationTokenSource? cts))
			{
				return false;
			}

			try
			{
				cts.Cancel();
				return true;
			}
			catch (ObjectDisposedException)
			{
				return false;
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
				.OrderBy(t => t.CreatedAt)
				.FirstOrDefaultAsync(cancellationToken);

			if (task is null)
			{
				return false;
			}

			using CancellationTokenSource heartbeatCts = new();
			using CancellationTokenSource taskCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			// Registered before the claim so a cancel request can never see a Processing task without a CTS
			RunningTasks[task.Id] = taskCts;
			Task? heartbeat = null;

			try
			{
				DateTime claimedAt = DateTime.UtcNow;
				// Guarded claim: a task cancelled while pending must not be moved back to Processing
				int claimed = await db.Tasks
					.Where(t => t.Id == task.Id && t.Status == RepositoryTaskStatus.PendingStart)
					.ExecuteUpdateAsync(s => s
						.SetProperty(t => t.Status, RepositoryTaskStatus.Processing)
						.SetProperty(t => t.ProcessingInstanceId, InstanceId)
						.SetProperty(t => t.ProcessingProcessId, Environment.ProcessId)
						.SetProperty(t => t.HeartbeatDateTime, claimedAt), cancellationToken);
				if (claimed == 0)
				{
					return true;
				}

				task.Status = RepositoryTaskStatus.Processing;
				task.ProcessingInstanceId = InstanceId;
				task.ProcessingProcessId = Environment.ProcessId;
				task.HeartbeatDateTime = claimedAt;
				// The claim was already written by ExecuteUpdate; without this, any later SaveChanges on this context
				// would write the stale Processing state back over the finalized status
				db.Entry(task).State = EntityState.Unchanged;

				logger.LogInformation("Processing task {TaskId}", task.Id);

				heartbeat = RunHeartbeatAsync(task.Id, heartbeatCts.Token);
				await ProcessTaskAsync(db, task, taskCts.Token, cancellationToken);
			}
			finally
			{
				RunningTasks.TryRemove(task.Id, out _);
				await heartbeatCts.CancelAsync();
				if (heartbeat is not null)
				{
					await heartbeat;
				}
			}

			return true;
		}

		private async Task ProcessTaskAsync(AppDbContext db, RepositoryTask task, CancellationToken cancellationToken, CancellationToken hostToken)
		{
			string? response;
			RepositoryTaskStatus finalStatus;
			string? branchName = task.BranchName;
			RunCost cost = new();
			try
			{
				List<string> userMessages = await db.Conversations
					.Where(c => c.TaskId == task.Id && c.Author == ConversationAuthor.Neph)
					.OrderBy(c => c.CreatedAt)
					.Select(c => c.Content)
					.ToListAsync(cancellationToken);
				bool isFollowUp = userMessages.Count > 1 && task.Repository is not null && Directory.Exists(GetRepositoryFolder(task.Repository));
				string prompt = (isFollowUp ? userMessages[^1] : userMessages.FirstOrDefault()) ?? string.Empty;

				response = await RunWithCopilotAsync(task, prompt, isFollowUp, cost, cancellationToken);
				branchName = await FindCreatedBranchAsync(task, cancellationToken);

				finalStatus = RepositoryTaskStatus.WaitingForReview;
				logger.LogInformation("Completed task {TaskId}", task.Id);
			}
			catch (Exception) when (cancellationToken.IsCancellationRequested && !hostToken.IsCancellationRequested)
			{
				// Cancelled by the user: the cost of the work done so far is still saved
				finalStatus = RepositoryTaskStatus.Cancelled;
				response = CancelledMessage;
				try
				{
					// Best-effort and not cancellable, so a branch the agent already created is still persisted
					branchName = await FindCreatedBranchAsync(task, CancellationToken.None) ?? branchName;
				}
				catch (Exception ex)
				{
					logger.LogWarning(ex, "Could not look up the branch of cancelled task {TaskId}", task.Id);
				}

				logger.LogInformation("Task {TaskId} was cancelled", task.Id);
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !hostToken.IsCancellationRequested)
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
		private async Task FinalizeTaskAsync(AppDbContext db, RepositoryTask task, RepositoryTaskStatus status, string? branchName, string? message, decimal runCost)
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

		private async Task<string?> RunWithCopilotAsync(RepositoryTask task, string prompt, bool isFollowUp, RunCost cost, CancellationToken cancellationToken)
		{
			string token = configuration["Copilot:Token"]
				?? throw new InvalidOperationException("Copilot:Token is not configured");

			Repository repository = task.Repository
				?? throw new InvalidOperationException("Task repository not loaded");

			using IDisposable repositoryLock = await AcquireRepositoryLockAsync(repository, cancellationToken);

			string workingDirectory = await PrepareRepositoryAsync(task, repository, isFollowUp, cancellationToken);

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

			// Events without an agent id come from the root agent; sub-agents are identified by the id given at spawn
			ConcurrentDictionary<string, ConversationAuthor> subAgents = new();
			session.On<SubagentStartedEvent>(evt =>
			{
				if (evt.AgentId is { } agentId && Enum.TryParse(evt.Data.AgentName, true, out ConversationAuthor author))
				{
					subAgents[agentId] = author;
				}
			});

			session.On<AssistantReasoningEvent>(evt =>
			{
				ConversationAuthor author = evt.AgentId is { } id && subAgents.TryGetValue(id, out ConversationAuthor subAgent) ? subAgent : ConversationAuthor.Autopilot;
				try
				{
					using IServiceScope scope = scopeFactory.CreateScope();
					AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
					db.Conversations.Add(new Conversation { TaskId = task.Id, Author = author, Type = ConversationType.Reasoning, Content = evt.Data.Content });
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

			AssistantMessageEvent? response;
			try
			{
				response = await session.SendAndWaitAsync(
					new MessageOptions { Prompt = $"{prompt}{BuildBranchInstructions(task)}" },
					ProcessingTimeout,
					cancellationToken);
			}
			catch (OperationCanceledException)
			{
				// Make sure the agent process does not keep running (and spending) after a cancel
				try
				{
					await client.ForceStopAsync();
				}
				catch (Exception ex)
				{
					logger.LogWarning(ex, "Could not stop the agent process of task {TaskId}", task.Id);
				}

				throw;
			}

			logger.LogInformation("Copilot response for task {TaskId}: {Response}", task.Id, response?.Data.Content);

			return response?.Data.Content;
		}

		private async Task<string?> FindCreatedBranchAsync(RepositoryTask task, CancellationToken cancellationToken)
		{
			try
			{
				Repository repository = task.Repository
					?? throw new InvalidOperationException("Task repository not loaded");

				using IDisposable repositoryLock = await AcquireRepositoryLockAsync(repository, cancellationToken);

				string folder = GetRepositoryFolder(repository);
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

		// One folder per repository, reused across tasks and never deleted
		private static string GetRepositoryFolder(Repository repository) => Path.Combine(AppContext.BaseDirectory, "Repositories", repository.Id.ToString());

		private sealed class LockReleaser(SemaphoreSlim semaphore) : IDisposable
		{
			public void Dispose() => semaphore.Release();
		}

		private static async Task<IDisposable> AcquireRepositoryLockAsync(Repository repository, CancellationToken cancellationToken)
		{
			SemaphoreSlim semaphore = RepositoryLocks.GetOrAdd(repository.Id, _ => new SemaphoreSlim(1, 1));
			await semaphore.WaitAsync(cancellationToken);
			return new LockReleaser(semaphore);
		}

		private void ApplyGitCredentials(ProcessStartInfo startInfo)
		{
			string? githubToken = configuration["Github:Token"];
			if (!string.IsNullOrEmpty(githubToken))
			{
				string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{githubToken}"));
				startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
				startInfo.Environment["GIT_CONFIG_KEY_0"] = "http.extraheader";
				startInfo.Environment["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: basic {credentials}";
			}

			startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
		}

		private async Task RunGitCheckedAsync(string folder, bool authenticated, CancellationToken cancellationToken, params string[] args)
		{
			ProcessStartInfo startInfo = CreateGitStartInfo(folder, args);
			if (authenticated)
			{
				ApplyGitCredentials(startInfo);
			}

			(int exitCode, _, string error) = await RunProcessAsync(startInfo, cancellationToken);

			if (exitCode != 0)
			{
				throw new InvalidOperationException($"git {args[0]} failed: {error}");
			}
		}

		// Must be called while holding the repository lock
		private async Task<string> PrepareRepositoryAsync(RepositoryTask task, Repository repository, bool isFollowUp, CancellationToken cancellationToken)
		{
			string folder = GetRepositoryFolder(repository);

			try
			{
				if (await IsValidCloneAsync(folder, cancellationToken))
				{
					return await SyncRepositoryAsync(task, repository, folder, isFollowUp, cancellationToken);
				}
			}
			catch (InvalidOperationException)
			{
				// Corrupted clone: fall through and re-clone once
			}

			await CloneRepositoryAsync(repository, folder, cancellationToken);
			return await SyncRepositoryAsync(task, repository, folder, isFollowUp, cancellationToken);
		}

		private async Task<bool> IsValidCloneAsync(string folder, CancellationToken cancellationToken)
		{
			if (!Directory.Exists(Path.Combine(folder, ".git")))
			{
				return false;
			}

			(int exitCode, _) = await RunGitAsync(folder, cancellationToken, "rev-parse", "--git-dir");
			return exitCode == 0;
		}

		private async Task<string> SyncRepositoryAsync(RepositoryTask task, Repository repository, string folder, bool isFollowUp, CancellationToken cancellationToken)
		{
			string defaultBranch = string.IsNullOrWhiteSpace(repository.DefaultBranch) ? "master" : repository.DefaultBranch;
			string branch = BuildBranchName(task);

			// A stale lock from a killed git process would block every later command (we hold the repository lock)
			string indexLock = Path.Combine(folder, ".git", "index.lock");
			if (File.Exists(indexLock))
			{
				File.Delete(indexLock);
			}

			await RunGitCheckedAsync(folder, false, cancellationToken, "remote", "set-url", "origin", repository.CloneUrl);
			await RunGitCheckedAsync(folder, true, cancellationToken, "fetch", "--prune", "origin");

			if (isFollowUp)
			{
				// Continue on the task branch when it still exists
				(int exitCode, _) = await RunGitAsync(folder, cancellationToken, "checkout", "--force", branch, "--");
				if (exitCode == 0)
				{
					(int remoteExists, _) = await RunGitAsync(folder, cancellationToken, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}");
					if (remoteExists == 0)
					{
						await RunGitCheckedAsync(folder, false, cancellationToken, "reset", "--hard", $"origin/{branch}");
					}

					await RunGitCheckedAsync(folder, false, cancellationToken, "clean", "-fdx");
					return folder;
				}
			}

			await RunGitCheckedAsync(folder, false, cancellationToken, "checkout", "--force", defaultBranch, "--");
			await RunGitCheckedAsync(folder, false, cancellationToken, "reset", "--hard", $"origin/{defaultBranch}");
			await RunGitCheckedAsync(folder, false, cancellationToken, "clean", "-fdx");

			if (!isFollowUp)
			{
				// A local branch left by an earlier task with the same name must not leak into this one
				await RunGitAsync(folder, cancellationToken, "branch", "-D", branch);
			}

			return folder;
		}

		private async Task CloneRepositoryAsync(Repository repository, string folder, CancellationToken cancellationToken)
		{
			// A leftover folder without a valid clone (e.g. an interrupted clone) is removed so the clone can succeed
			if (Directory.Exists(folder))
			{
				// Git object files are read-only, which blocks recursive deletion on Windows
				foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
				{
					File.SetAttributes(file, FileAttributes.Normal);
				}

				Directory.Delete(folder, recursive: true);
			}

			Directory.CreateDirectory(Path.GetDirectoryName(folder)!);

			ProcessStartInfo startInfo = CreateGitStartInfo(null, "clone", repository.CloneUrl, folder);
			ApplyGitCredentials(startInfo);

			(int exitCode, _, string error) = await RunProcessAsync(startInfo, cancellationToken);

			if (exitCode != 0)
			{
				throw new InvalidOperationException($"git clone failed: {error}");
			}
		}
	}
}
