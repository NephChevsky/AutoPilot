using AutoPilot.WebAPI.Database;
using AutoPilot.WebAPI.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPilot.WebAPI.Services
{
	public class TaskService(IServiceScopeFactory scopeFactory, ILogger<TaskService> logger) : BackgroundService
	{
		private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
		private static readonly TimeSpan ProcessingDuration = TimeSpan.FromMinutes(1);

		protected override async System.Threading.Tasks.Task ExecuteAsync(CancellationToken stoppingToken)
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
					await System.Threading.Tasks.Task.Delay(PollInterval, stoppingToken);
				}
			}
		}

		private async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
		{
			using IServiceScope scope = scopeFactory.CreateScope();
			AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

			RepositoryTask? task = await db.Tasks
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

			await System.Threading.Tasks.Task.Delay(ProcessingDuration, cancellationToken);

			task.Status = RepositoryTaskStatus.Completed;
			task.CompletionDateTime = DateTime.UtcNow;
			await db.SaveChangesAsync(cancellationToken);

			logger.LogInformation("Completed task {TaskId}", task.Id);

			return true;
		}
	}
}
