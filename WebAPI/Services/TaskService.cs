using AutoPilot.WebAPI.Database;
using AutoPilot.WebAPI.Database.Models;
using GitHub.Copilot;
using Microsoft.EntityFrameworkCore;

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
				await RunWithCopilotAsync(task, cancellationToken);

				task.Status = RepositoryTaskStatus.Completed;
				logger.LogInformation("Completed task {TaskId}", task.Id);
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
			{
				task.Status = RepositoryTaskStatus.Error;
				logger.LogError(ex, "Task {TaskId} failed", task.Id);
			}

			task.CompletionDateTime = DateTime.UtcNow;
			await db.SaveChangesAsync(CancellationToken.None);

			return true;
		}

		private async Task RunWithCopilotAsync(RepositoryTask task, CancellationToken cancellationToken)
		{
			string token = configuration["Copilot:Token"]
				?? throw new InvalidOperationException("Copilot:Token is not configured");

			await using CopilotClient client = new(new CopilotClientOptions
			{
				GitHubToken = token,
			});

			await client.StartAsync(cancellationToken);

			await using CopilotSession session = await client.CreateSessionAsync(new SessionConfig
			{
				Model = "claude-sonnet-5.5",
				ReasoningEffort = "medium",
				OnPermissionRequest = PermissionHandler.ApproveAll,
			});

			AssistantMessageEvent? response = await session.SendAndWaitAsync(
				new MessageOptions { Prompt = task.Task },
				ProcessingTimeout,
				cancellationToken);

			logger.LogInformation("Copilot response for task {TaskId}: {Response}", task.Id, response?.Data.Content);
		}
	}
}
