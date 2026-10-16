using AutoPilot.WebAPI.Database;
using AutoPilot.WebAPI.Database.Models;
using GitHub.Copilot;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;

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

			try
			{
				task.Response = await RunWithCopilotAsync(task, cancellationToken);

				task.Status = RepositoryTaskStatus.Completed;
				logger.LogInformation("Completed task {TaskId}", task.Id);
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
			{
				task.Status = RepositoryTaskStatus.Error;
				task.Response = ex.ToString();
				logger.LogError(ex, "Task {TaskId} failed", task.Id);
			}

			task.CompletionDateTime = DateTime.UtcNow;
			await db.SaveChangesAsync(CancellationToken.None);

			return true;
		}

		private const string BranchInstructions =
			"\n\nIf you modify any code, do not commit to the default branch: create a new branch, commit your changes to it, and push that branch to the remote.";

		private async Task<string?> RunWithCopilotAsync(RepositoryTask task, CancellationToken cancellationToken)
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

			AssistantMessageEvent? response = await session.SendAndWaitAsync(
				new MessageOptions { Prompt = $"{task.Task}{BranchInstructions}" },
				ProcessingTimeout,
				cancellationToken);

			logger.LogInformation("Copilot response for task {TaskId}: {Response}", task.Id, response?.Data.Content);

			return response?.Data.Content;
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
