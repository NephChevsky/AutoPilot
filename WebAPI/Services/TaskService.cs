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

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					bool processed = await ProcessNextAsync(stoppingToken);

					if (!processed)
					{
						await System.Threading.Tasks.Task.Delay(PollInterval, stoppingToken);
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
			await db.SaveChangesAsync(cancellationToken);

			logger.LogInformation("Processing task {TaskId}", task.Id);

			string? prompt = await db.Conversations
				.Where(c => c.TaskId == task.Id && c.Author == ConversationAuthor.Neph)
				.OrderBy(c => c.CreationDateTime)
				.Select(c => c.Content)
				.FirstOrDefaultAsync(cancellationToken);

			string? response;
			try
			{
				response = await RunWithCopilotAsync(task, prompt ?? string.Empty, cancellationToken);
				task.BranchName = await FindCreatedBranchAsync(task, cancellationToken);

				task.Status = RepositoryTaskStatus.Completed;
				logger.LogInformation("Completed task {TaskId}", task.Id);
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
			{
				task.Status = RepositoryTaskStatus.Error;
				response = ex.ToString();
				logger.LogError(ex, "Task {TaskId} failed", task.Id);
			}

			if (response is not null)
			{
				db.Conversations.Add(new Conversation { TaskId = task.Id, Author = ConversationAuthor.Autopilot, Content = response });
			}

			task.CompletionDateTime = DateTime.UtcNow;
			await db.SaveChangesAsync(CancellationToken.None);

			return true;
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

		private async Task<string?> RunWithCopilotAsync(RepositoryTask task, string prompt, CancellationToken cancellationToken)
		{
			string token = configuration["Copilot:Token"]
				?? throw new InvalidOperationException("Copilot:Token is not configured");

			string workingDirectory = await CheckoutRepositoryAsync(task, cancellationToken);

			await using CopilotClient client = new(new CopilotClientOptions
			{
				GitHubToken = token,
				WorkingDirectory = workingDirectory,
			});

			await client.StartAsync(cancellationToken);

			await using CopilotSession session = await client.CreateSessionAsync(new SessionConfig
			{
				Model = "claude-sonnet-5.5",
				ReasoningEffort = "medium",
				WorkingDirectory = workingDirectory,
				OnPermissionRequest = PermissionHandler.ApproveAll
			});

			session.On<SessionEvent>(evt =>
			{
				logger.LogInformation("Received session event for task {TaskId}: {Event}", task.Id, evt);
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
				string folder = Path.Combine(AppContext.BaseDirectory, "Tasks", task.Id.ToString());
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

		private static async Task<(int ExitCode, string Output)> RunGitAsync(string workingDirectory, CancellationToken cancellationToken, params string[] args)
		{
			ProcessStartInfo startInfo = new("git")
			{
				WorkingDirectory = workingDirectory,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			foreach (string arg in args)
			{
				startInfo.ArgumentList.Add(arg);
			}

			using Process process = Process.Start(startInfo)
				?? throw new InvalidOperationException("Failed to start git");

			Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
			Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
			await process.WaitForExitAsync(cancellationToken);
			await stderr;

			return (process.ExitCode, await stdout);
		}

		private async Task<string> CheckoutRepositoryAsync(RepositoryTask task, CancellationToken cancellationToken)
		{
			Repository repository = task.Repository
				?? throw new InvalidOperationException("Task repository not loaded");

			string folder = Path.Combine(AppContext.BaseDirectory, "Tasks", task.Id.ToString());

			if (Directory.Exists(folder))
			{
				foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
				{
					File.SetAttributes(file, FileAttributes.Normal);
				}

				Directory.Delete(folder, recursive: true);
			}

			Directory.CreateDirectory(Path.GetDirectoryName(folder)!);

			ProcessStartInfo startInfo = new("git")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			startInfo.ArgumentList.Add("clone");
			startInfo.ArgumentList.Add(repository.CloneUrl);
			startInfo.ArgumentList.Add(folder);

			string? githubToken = configuration["Github:Token"];
			if (!string.IsNullOrEmpty(githubToken))
			{
				string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{githubToken}"));
				startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
				startInfo.Environment["GIT_CONFIG_KEY_0"] = "http.extraheader";
				startInfo.Environment["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: basic {credentials}";
			}

			startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

			using Process process = Process.Start(startInfo)
				?? throw new InvalidOperationException("Failed to start git");

			Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
			Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

			await process.WaitForExitAsync(cancellationToken);

			if (process.ExitCode != 0)
			{
				throw new InvalidOperationException($"git clone failed: {await stderr}");
			}

			await stdout;

			return folder;
		}
	}
}
