using AutoPilot.WebAPI.Database;
using AutoPilot.WebAPI.Database.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AutoPilot.WebAPI.Controllers
{
	[ApiController]
	[Authorize]
	[Route("api/[controller]")]
	public class TaskController(AppDbContext dbContext) : ControllerBase
	{
		public record CreateTaskRequest(Guid RepositoryId, string Title, string Task);

		public record TaskResponse(
			Guid Id,
			Guid RepositoryId,
			DateTime CreationDateTime,
			DateTime? CompletionDateTime,
			string Title,
			string Task,
			RepositoryTaskStatus Status,
			string? Response);

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
				.Select(t => new TaskResponse(t.Id, t.RepositoryId, t.CreationDateTime, t.CompletionDateTime, t.Title, t.Task, t.Status, t.Response))
				.ToListAsync();

			return Ok(tasks);
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

			RepositoryTask task = new()
			{
				RepositoryId = request.RepositoryId,
				Title = request.Title.Trim(),
				Task = request.Task.Trim()
			};

			dbContext.Tasks.Add(task);
			await dbContext.SaveChangesAsync();

			return Ok(new TaskResponse(task.Id, task.RepositoryId, task.CreationDateTime, task.CompletionDateTime, task.Title, task.Task, task.Status, task.Response));
		}

		[HttpPost("{id:guid}/start")]
		public async Task<IActionResult> StartTask(Guid id)
		{
			RepositoryTask? task = await dbContext.Tasks.FirstOrDefaultAsync(t => t.Id == id);
			if (task is null)
			{
				return NotFound();
			}

			if (task.Status is not (RepositoryTaskStatus.NotStarted or RepositoryTaskStatus.Error))
			{
				return Conflict("Task has already been started.");
			}

			task.Status = RepositoryTaskStatus.PendingStart;
			task.CompletionDateTime = null;
			task.Response = null;
			await dbContext.SaveChangesAsync();

			return Ok(new TaskResponse(task.Id, task.RepositoryId, task.CreationDateTime, task.CompletionDateTime, task.Title, task.Task, task.Status, task.Response));
		}
	}
}
