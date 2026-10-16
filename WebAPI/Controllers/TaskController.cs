using AutoPilot.WebAPI.Database;
using AutoPilot.WebAPI.Database.Models;
using AutoPilot.WebAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Octokit;

namespace AutoPilot.WebAPI.Controllers
{
	[ApiController]
	[Authorize]
	[Route("api/[controller]")]
	public class TaskController(AppDbContext dbContext, IConfiguration configuration, ILogger<TaskController> logger) : ControllerBase
	{
		public record CreateTaskRequest(Guid RepositoryId, string Title, string Task);

		public record UpdateTaskRequest(string Title, string Task);

		public record FollowUpRequest(string Message);

		public record TaskResponse(
			Guid Id,
			Guid RepositoryId,
			DateTime CreationDateTime,
			DateTime? CompletionDateTime,
			string Title,
			RepositoryTaskStatus Status,
			string? Response,
			string? BranchName,
			decimal? Cost,
			List<ConversationItemResponse> Conversation);

		public record ConversationItemResponse(ConversationAuthor Author, ConversationType Type, string Content, DateTime CreationDateTime);

		[HttpGet]
		public async Task<IActionResult> GetTasks([FromQuery] Guid? repositoryId)
		{
			IQueryable<RepositoryTask> query = dbContext.Tasks.AsNoTracking();

			if (repositoryId is not null)
			{
				query = query.Where(t => t.RepositoryId == repositoryId);
			}

			List<TaskResponse> tasks = await query
				.OrderByDescending(t => t.CreationDateTime)
				.Select(t => new TaskResponse(
					t.Id,
					t.RepositoryId,
					t.CreationDateTime,
					t.CompletionDateTime,
					t.Title,
					t.Status,
					t.Conversations.Where(c => c.Author == ConversationAuthor.Autopilot && c.Type == ConversationType.Message).OrderByDescending(c => c.CreationDateTime).Select(c => c.Content).FirstOrDefault(),
					t.BranchName,
					t.Cost,
					t.Conversations.OrderBy(c => c.CreationDateTime).Select(c => new ConversationItemResponse(c.Author, c.Type, c.Content, c.CreationDateTime)).ToList()))
				.ToListAsync();

			return Ok(tasks);
		}

		public record RepositoryStatusSummary(Guid RepositoryId, List<RepositoryTaskStatus> Statuses);

		[HttpGet("status-summary")]
		public async Task<IActionResult> GetStatusSummary()
		{
			var pairs = await dbContext.Tasks.AsNoTracking()
				.Select(t => new { t.RepositoryId, t.Status })
				.Distinct()
				.ToListAsync();

			List<RepositoryStatusSummary> summary = pairs
				.GroupBy(p => p.RepositoryId)
				.Select(g => new RepositoryStatusSummary(g.Key, g.Select(p => p.Status).ToList()))
				.ToList();

			return Ok(summary);
		}

		[HttpPost]
		public async Task<IActionResult> CreateTask(CreateTaskRequest request)
		{
			if (string.IsNullOrWhiteSpace(request.Title))
			{
				return BadRequest("Task title is required.");
			}

			if (string.IsNullOrWhiteSpace(request.Task))
			{
				return BadRequest("Task text is required.");
			}

			bool repositoryExists = await dbContext.Repositories.AnyAsync(r => r.Id == request.RepositoryId);
			if (!repositoryExists)
			{
				return NotFound("Repository not found.");
			}

			string taskText = request.Task.Trim();
			RepositoryTask task = new()
			{
				RepositoryId = request.RepositoryId,
				Title = request.Title.Trim(),
				Conversations =
				[
					new Conversation { Author = ConversationAuthor.Neph, Content = taskText }
				]
			};

			dbContext.Tasks.Add(task);
			await dbContext.SaveChangesAsync();

			return Ok(new TaskResponse(task.Id, task.RepositoryId, task.CreationDateTime, task.CompletionDateTime, task.Title, task.Status, null, task.BranchName, task.Cost,
				[.. task.Conversations.Select(c => new ConversationItemResponse(c.Author, c.Type, c.Content, c.CreationDateTime))]));
		}

		[HttpPut("{id:guid}")]
		public async Task<IActionResult> UpdateTask(Guid id, UpdateTaskRequest request)
		{
			if (string.IsNullOrWhiteSpace(request.Title))
			{
				return BadRequest("Task title is required.");
			}

			if (string.IsNullOrWhiteSpace(request.Task))
			{
				return BadRequest("Task text is required.");
			}

			RepositoryTask? task = await dbContext.Tasks.FirstOrDefaultAsync(t => t.Id == id);
			if (task is null)
			{
				return NotFound();
			}

			if (task.Status != RepositoryTaskStatus.NotStarted)
			{
				return Conflict("Only unstarted tasks can be edited.");
			}

			Conversation? first = await dbContext.Conversations
				.Where(c => c.TaskId == id && c.Author == ConversationAuthor.Neph)
				.OrderBy(c => c.CreationDateTime)
				.FirstOrDefaultAsync();

			task.Title = request.Title.Trim();
			string taskText = request.Task.Trim();
			if (first is null)
			{
				dbContext.Conversations.Add(new Conversation { TaskId = id, Author = ConversationAuthor.Neph, Content = taskText });
			}
			else
			{
				first.Content = taskText;
			}

			await dbContext.SaveChangesAsync();

			return Ok(await BuildTaskResponseAsync(task));
		}

		[HttpPost("{id:guid}/merge")]
		public async Task<IActionResult> MergeTask(Guid id)
		{
			RepositoryTask? task = await dbContext.Tasks.FirstOrDefaultAsync(t => t.Id == id);
			if (task is null)
			{
				return NotFound();
			}

			if (task.Status != RepositoryTaskStatus.WaitingForReview || string.IsNullOrEmpty(task.BranchName))
			{
				return Conflict("Only tasks waiting for review can be merged.");
			}

			string? token = configuration["Github:Token"];
			var repository = await dbContext.Repositories
				.Where(r => r.Id == task.RepositoryId)
				.Select(r => new { r.GithubId, r.DefaultBranch })
				.FirstOrDefaultAsync();

			if (string.IsNullOrWhiteSpace(token) || repository is null)
			{
				return StatusCode(StatusCodes.Status500InternalServerError, "GitHub integration is not configured.");
			}

			GitHubClient client = CreateGithubClient(token);
			try
			{
				await client.Repository.Merging.Create(repository.GithubId, new NewMerge(repository.DefaultBranch, task.BranchName));
			}
			catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
			{
				return Conflict("The branch could not be merged automatically because of conflicts.");
			}
			catch (NotFoundException)
			{
				return NotFound("Branch or repository not found on GitHub.");
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Could not merge branch {Branch} for task {TaskId}", task.BranchName, id);
				return StatusCode(StatusCodes.Status502BadGateway, "Could not merge the task branch.");
			}

			try
			{
				await client.Git.Reference.Delete(repository.GithubId, $"heads/{task.BranchName}");
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Merged but could not delete branch {Branch} for task {TaskId}", task.BranchName, id);
			}

			task.BranchName = null;
			task.Status = RepositoryTaskStatus.Completed;
			await dbContext.SaveChangesAsync();

			return Ok(await BuildTaskResponseAsync(task));
		}

		[HttpPost("{id:guid}/complete")]
		public async Task<IActionResult> CompleteTask(Guid id)
		{
			RepositoryTask? task = await dbContext.Tasks.FirstOrDefaultAsync(t => t.Id == id);
			if (task is null)
			{
				return NotFound();
			}

			if (task.Status != RepositoryTaskStatus.WaitingForReview)
			{
				return Conflict("Only tasks waiting for review can be marked as completed.");
			}

			if (!string.IsNullOrEmpty(task.BranchName))
			{
				string? token = configuration["Github:Token"];
				long? githubId = await dbContext.Repositories
					.Where(r => r.Id == task.RepositoryId)
					.Select(r => (long?)r.GithubId)
					.FirstOrDefaultAsync();

				bool clearBranch = true;
				if (!string.IsNullOrWhiteSpace(token) && githubId is not null)
				{
					GitHubClient client = CreateGithubClient(token);
					try
					{
						await client.Git.Reference.Delete(githubId.Value, $"heads/{task.BranchName}");
					}
					catch (NotFoundException)
					{
					}
					catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
					{
					}
					catch (Exception ex)
					{
						clearBranch = false;
						logger.LogWarning(ex, "Could not delete branch {Branch} for task {TaskId}", task.BranchName, id);
					}
				}

				if (clearBranch)
				{
					task.BranchName = null;
				}
			}

			task.Status = RepositoryTaskStatus.Completed;
			task.CompletionDateTime ??= DateTime.UtcNow;
			await dbContext.SaveChangesAsync();

			try
			{
				TaskService.DeleteRepositoryFolder(id);
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Could not delete repository folder for task {TaskId}", id);
			}

			return Ok(await BuildTaskResponseAsync(task));
		}

		[HttpPost("{id:guid}/follow-up")]
		public async Task<IActionResult> FollowUp(Guid id, FollowUpRequest request)
		{
			if (string.IsNullOrWhiteSpace(request.Message))
			{
				return BadRequest("Message is required.");
			}

			RepositoryTask? task = await dbContext.Tasks.FirstOrDefaultAsync(t => t.Id == id);
			if (task is null)
			{
				return NotFound();
			}

			if (task.Status != RepositoryTaskStatus.WaitingForReview)
			{
				return Conflict("Only tasks waiting for review can receive follow-up changes.");
			}

			dbContext.Conversations.Add(new Conversation { TaskId = id, Author = ConversationAuthor.Neph, Content = request.Message.Trim() });
			task.Status = RepositoryTaskStatus.PendingStart;
			task.CompletionDateTime = null;
			await dbContext.SaveChangesAsync();

			return Ok(await BuildTaskResponseAsync(task));
		}

		[HttpPost("{id:guid}/start")]
		public async Task<IActionResult> StartTask(Guid id)
		{
			RepositoryTask? task = await dbContext.Tasks.FirstOrDefaultAsync(t => t.Id == id);
			if (task is null)
			{
				return NotFound();
			}

			if (task.Status is not (RepositoryTaskStatus.NotStarted or RepositoryTaskStatus.Error or RepositoryTaskStatus.Cancelled))
			{
				return Conflict("Task has already been started.");
			}

			task.Status = RepositoryTaskStatus.PendingStart;
			task.CompletionDateTime = null;
			task.BranchName = null;
			DateTime? lastUserMessage = await dbContext.Conversations
				.Where(c => c.TaskId == id && c.Author == ConversationAuthor.Neph)
				.MaxAsync(c => (DateTime?)c.CreationDateTime);
			bool hasFollowUp = await dbContext.Conversations.CountAsync(c => c.TaskId == id && c.Author == ConversationAuthor.Neph) > 1;
			await dbContext.Conversations
				.Where(c => c.TaskId == id && c.Author != ConversationAuthor.Neph && (!hasFollowUp || c.CreationDateTime > lastUserMessage))
				.ExecuteDeleteAsync();
			await dbContext.SaveChangesAsync();

			return Ok(await BuildTaskResponseAsync(task));
		}

		[HttpPost("{id:guid}/cancel")]
		public async Task<IActionResult> CancelTask(Guid id)
		{
			RepositoryTask? task = await dbContext.Tasks.FirstOrDefaultAsync(t => t.Id == id);
			if (task is null)
			{
				return NotFound();
			}

			if (task.Status == RepositoryTaskStatus.PendingStart)
			{
				// Not picked up by the worker yet, so there is no run (or cost) to wait for
				int updated = await dbContext.Tasks
					.Where(t => t.Id == id && t.Status == RepositoryTaskStatus.PendingStart)
					.ExecuteUpdateAsync(s => s
						.SetProperty(t => t.Status, RepositoryTaskStatus.Cancelled)
						.SetProperty(t => t.CompletionDateTime, DateTime.UtcNow));
				if (updated > 0)
				{
					await dbContext.Entry(task).ReloadAsync();
					return Ok(await BuildTaskResponseAsync(task));
				}

				await dbContext.Entry(task).ReloadAsync();
			}

			if (task.Status != RepositoryTaskStatus.Processing)
			{
				return Conflict("Only tasks being processed can be cancelled.");
			}

			// The worker stops the agent, saves the cost of the work done so far and sets the Cancelled status
			if (!TaskService.TryCancel(id))
			{
				return Conflict("The task is not running on this server and cannot be cancelled.");
			}

			return Ok(await BuildTaskResponseAsync(task));
		}

		private async Task<TaskResponse> BuildTaskResponseAsync(RepositoryTask task)
		{
			List<ConversationItemResponse> conversation = await dbContext.Conversations
				.Where(c => c.TaskId == task.Id)
				.OrderBy(c => c.CreationDateTime)
				.Select(c => new ConversationItemResponse(c.Author, c.Type, c.Content, c.CreationDateTime))
				.ToListAsync();

			return new TaskResponse(task.Id, task.RepositoryId, task.CreationDateTime, task.CompletionDateTime, task.Title, task.Status, null, task.BranchName, task.Cost, conversation);
		}

		private static GitHubClient CreateGithubClient(string token) =>
			new(new ProductHeaderValue("AutoPilot")) { Credentials = new Credentials(token) };
	}
}
