using AutoPilot.WebAPI.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Octokit;
using System.Linq.Expressions;
using DbRepository = AutoPilot.WebAPI.Database.Models.Repository;
using GithubRepo = Octokit.Repository;

namespace AutoPilot.WebAPI.Controllers
{
	[ApiController]
	[Route("api/[controller]")]
	public class GithubController(ILogger<GithubController> logger, IConfiguration configuration, AppDbContext dbContext) : ControllerBase
	{
		public record RepositoryResponse(
			Guid Id,
			long GithubId,
			string Name,
			string CloneUrl,
			string HtmlUrl,
			string DefaultBranch,
			bool Pinned);

		public record SetPinnedRequest(bool Pinned);

		public record CreateRepositoryRequest(string? Name);

		[HttpGet("repositories")]
		public async Task<IActionResult> GetRepositories()
		{
			List<RepositoryResponse> repositories = await dbContext.Repositories
				.AsNoTracking()
				.OrderByDescending(r => r.Pinned)
				.ThenBy(r => r.Name)
				.Select(ToResponse)
				.ToListAsync();

			return Ok(repositories);
		}

		[HttpGet("repositories/{id:guid}")]
		public async Task<IActionResult> GetRepository(Guid id)
		{
			RepositoryResponse? repository = await dbContext.Repositories
				.AsNoTracking()
				.Where(r => r.Id == id)
				.Select(ToResponse)
				.FirstOrDefaultAsync();

			return repository is null ? NotFound() : Ok(repository);
		}

		private static readonly Expression<Func<DbRepository, RepositoryResponse>> ToResponse =
			r => new RepositoryResponse(r.Id, r.GithubId, r.Name, r.CloneUrl, r.HtmlUrl, r.DefaultBranch, r.Pinned);

		[HttpPut("repositories/{id:guid}/pinned")]
		public async Task<IActionResult> SetPinned(Guid id, SetPinnedRequest request)
		{
			int updated = await dbContext.Repositories
				.Where(r => r.Id == id)
				.ExecuteUpdateAsync(s => s.SetProperty(r => r.Pinned, request.Pinned));

			return updated == 0 ? NotFound() : NoContent();
		}

		[HttpPost("repositories")]
		public async Task<IActionResult> CreateRepository(CreateRepositoryRequest request)
		{
			string name = request.Name?.Trim() ?? string.Empty;
			if (name.Length == 0)
			{
				return BadRequest("Repository name is required.");
			}

			string? token = configuration["Github:Token"];
			if (string.IsNullOrWhiteSpace(token))
			{
				logger.LogError("Github:Token is not configured");
				return StatusCode(StatusCodes.Status500InternalServerError, "GitHub integration is not configured.");
			}

			GitHubClient client = new(new ProductHeaderValue("AutoPilot"))
			{
				Credentials = new Credentials(token)
			};

			try
			{
				GithubRepo github = await client.Repository.Create(new NewRepository(name) { Private = true, AutoInit = true });

				DbRepository? repository = await dbContext.Repositories.FirstOrDefaultAsync(r => r.GithubId == github.Id);
				if (repository is null)
				{
					repository = new DbRepository { GithubId = github.Id };
					dbContext.Repositories.Add(repository);
				}

				repository.Name = github.Name;
				repository.CloneUrl = github.CloneUrl;
				repository.HtmlUrl = github.HtmlUrl;
				repository.DefaultBranch = github.DefaultBranch;

				await dbContext.SaveChangesAsync();

				return Ok(new RepositoryResponse(repository.Id, repository.GithubId, repository.Name, repository.CloneUrl, repository.HtmlUrl, repository.DefaultBranch, repository.Pinned));
			}
			catch (RepositoryExistsException)
			{
				return Conflict("A repository with this name already exists.");
			}
			catch (ApiValidationException ex)
			{
				logger.LogWarning(ex, "GitHub rejected repository creation");
				return UnprocessableEntity("GitHub rejected the repository name or settings.");
			}
			catch (ApiException ex)
			{
				logger.LogError(ex, "GitHub API error while creating repository");
				return StatusCode(StatusCodes.Status502BadGateway, "Failed to create repository on GitHub.");
			}
		}

		[HttpPost("repositories/refresh")]
		public async Task<IActionResult> RefreshRepositories()
		{
			string? token = configuration["Github:Token"];
			if (string.IsNullOrWhiteSpace(token))
			{
				logger.LogError("Github:Token is not configured");
				return StatusCode(StatusCodes.Status500InternalServerError, "GitHub integration is not configured.");
			}

			GitHubClient client = new(new ProductHeaderValue("AutoPilot"))
			{
				Credentials = new Credentials(token)
			};

			try
			{
				IReadOnlyList<GithubRepo> githubRepositories = await client.Repository.GetAllForCurrent();

				Dictionary<long, DbRepository> existing = await dbContext.Repositories.ToDictionaryAsync(r => r.GithubId);

				foreach (GithubRepo github in githubRepositories)
				{
					if (!existing.TryGetValue(github.Id, out DbRepository? repository))
					{
						repository = new DbRepository { GithubId = github.Id };
						dbContext.Repositories.Add(repository);
					}

					repository.Name = github.Name;
					repository.CloneUrl = github.CloneUrl;
					repository.HtmlUrl = github.HtmlUrl;
					repository.DefaultBranch = github.DefaultBranch;
				}

				await dbContext.SaveChangesAsync();

				return await GetRepositories();
			}
			catch (ApiException ex)
			{
				logger.LogError(ex, "GitHub API error while listing repositories");
				return StatusCode(StatusCodes.Status502BadGateway, "Failed to retrieve repositories from GitHub.");
			}
		}
	}
}
