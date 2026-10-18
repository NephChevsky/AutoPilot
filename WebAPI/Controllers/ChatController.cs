using AutoPilot.WebAPI.Database;
using AutoPilot.WebAPI.Database.Models;
using AutoPilot.WebAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace AutoPilot.WebAPI.Controllers
{
	[ApiController]
	[Authorize]
	[Route("api/[controller]")]
	public class ChatController(AppDbContext dbContext, ChatService chatService, ILogger<ChatController> logger) : ControllerBase
	{
		private const int MaxTitleLength = 60;
		private const int MaxMessageLength = 8000;

		// One in-flight message per chat, so concurrent sends cannot interleave
		private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ChatLocks = new();

		public record SendMessageRequest(string Message);

		public record ChatSummaryResponse(Guid Id, string Title, DateTime CreationDateTime, DateTime UpdateDateTime, decimal Cost);

		public record ChatMessageResponse(Guid Id, ChatMessageRole Role, string Content, decimal? Cost, DateTime CreationDateTime);

		public record ChatCostsResponse(decimal Total, decimal Month);

		public record ChatResponse(Guid Id, string Title, DateTime CreationDateTime, DateTime UpdateDateTime, List<ChatMessageResponse> Messages, decimal Cost);

		private Guid? GetUserId()
		{
			string? value = User.FindFirstValue("UserId") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
			return Guid.TryParse(value, out Guid id) ? id : null;
		}

		private static ChatMessageResponse ToResponse(ChatMessage m) => new(m.Id, m.Role, m.Content, m.Cost, m.CreatedAt);

		private static ChatResponse ToResponse(Chat chat) => new(
			chat.Id,
			chat.Title,
			chat.CreatedAt,
			chat.UpdatedAt,
			[.. chat.Messages.OrderBy(m => m.CreatedAt).ThenBy(m => m.Role).Select(ToResponse)],
			chat.Messages.Sum(m => m.Cost ?? 0m));

		private static string BuildTitle(string message)
		{
			string title = string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
			return title.Length <= MaxTitleLength ? title : title[..MaxTitleLength].TrimEnd() + "…";
		}

		[HttpGet]
		public async Task<IActionResult> GetChats()
		{
			if (GetUserId() is not { } userId)
			{
				return Unauthorized();
			}

			List<ChatSummaryResponse> chats = await dbContext.Chats.AsNoTracking()
				.Where(c => c.UserId == userId)
				.OrderByDescending(c => c.UpdatedAt)
				.Select(c => new ChatSummaryResponse(c.Id, c.Title, c.CreatedAt, c.UpdatedAt, c.Messages.Sum(m => m.Cost ?? 0m)))
				.ToListAsync();

			return Ok(chats);
		}

		// All-time and current (UTC) month cost of the user's chat messages
		[HttpGet("costs")]
		public async Task<IActionResult> GetCosts()
		{
			if (GetUserId() is not { } userId)
			{
				return Unauthorized();
			}

			DateTime now = DateTime.UtcNow;
			DateTime monthStart = new(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

			IQueryable<ChatMessage> messages = dbContext.ChatMessages.AsNoTracking().Where(m => m.Chat!.UserId == userId && m.Cost != null);
			decimal total = await messages.SumAsync(m => m.Cost ?? 0m);
			decimal month = await messages.Where(m => m.CreatedAt >= monthStart).SumAsync(m => m.Cost ?? 0m);

			return Ok(new ChatCostsResponse(total, month));
		}

		[HttpGet("{id:guid}")]
		public async Task<IActionResult> GetChat(Guid id)
		{
			if (GetUserId() is not { } userId)
			{
				return Unauthorized();
			}

			Chat? chat = await dbContext.Chats.AsNoTracking()
				.Include(c => c.Messages)
				.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);

			return chat is null ? NotFound("Chat not found.") : Ok(ToResponse(chat));
		}

		// Creates a chat from its first message and returns it with the assistant reply
		[HttpPost]
		public async Task<IActionResult> CreateChat(SendMessageRequest request, CancellationToken cancellationToken)
		{
			if (GetUserId() is not { } userId)
			{
				return Unauthorized();
			}

			if (string.IsNullOrWhiteSpace(request.Message))
			{
				return BadRequest("Message is required.");
			}

			if (request.Message.Length > MaxMessageLength)
			{
				return BadRequest($"Message is too long (max {MaxMessageLength} characters).");
			}

			string message = request.Message.Trim();
			Chat chat = new() { UserId = userId, Title = BuildTitle(message) };

			return await AskAsync(chat, [], message, cancellationToken);
		}

		[HttpPost("{id:guid}/messages")]
		public async Task<IActionResult> SendMessage(Guid id, SendMessageRequest request, CancellationToken cancellationToken)
		{
			if (GetUserId() is not { } userId)
			{
				return Unauthorized();
			}

			if (string.IsNullOrWhiteSpace(request.Message))
			{
				return BadRequest("Message is required.");
			}

			if (request.Message.Length > MaxMessageLength)
			{
				return BadRequest($"Message is too long (max {MaxMessageLength} characters).");
			}

			SemaphoreSlim chatLock = ChatLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
			if (!await chatLock.WaitAsync(0, cancellationToken))
			{
				return Conflict("This chat is already waiting for an answer.");
			}

			try
			{
				Chat? chat = await dbContext.Chats
					.Include(c => c.Messages)
					.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken);
				if (chat is null)
				{
					return NotFound("Chat not found.");
				}

				List<ChatMessage> history = [.. chat.Messages.OrderBy(m => m.CreatedAt).ThenBy(m => m.Role)];

				return await AskAsync(chat, history, request.Message.Trim(), cancellationToken);
			}
			finally
			{
				chatLock.Release();
			}
		}

		[HttpDelete("{id:guid}")]
		public async Task<IActionResult> DeleteChat(Guid id)
		{
			if (GetUserId() is not { } userId)
			{
				return Unauthorized();
			}

			Chat? chat = await dbContext.Chats.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
			if (chat is null)
			{
				return NotFound("Chat not found.");
			}

			dbContext.Chats.Remove(chat);
			await dbContext.SaveChangesAsync();

			return NoContent();
		}

		// Both messages are stored only once the assistant answered, so a failed call leaves the chat untouched
		private async Task<IActionResult> AskAsync(Chat chat, List<ChatMessage> history, string message, CancellationToken cancellationToken)
		{
			// The user message is stamped before the model call and the reply after, so the order is deterministic
			DateTime userTime = DateTime.UtcNow;
			ChatService.ChatReply reply;
			try
			{
				reply = await chatService.AskAsync(chat.SessionId, history, message, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Could not get a chat reply for chat {ChatId}", chat.Id);
				return StatusCode(StatusCodes.Status502BadGateway, "The assistant could not answer.");
			}

			DateTime replyTime = DateTime.UtcNow;
			if (replyTime <= userTime)
			{
				replyTime = userTime.AddTicks(1);
			}
			ChatMessage userMessage = new() { Chat = chat, Role = ChatMessageRole.User, Content = message, CreatedAt = userTime };
			ChatMessage assistantMessage = new() { Chat = chat, Role = ChatMessageRole.Assistant, Content = reply.Content, Cost = reply.Cost, CreatedAt = replyTime };
			chat.SessionId = reply.SessionId;

			bool isNewChat = dbContext.Entry(chat).State == EntityState.Detached;
			if (isNewChat)
			{
				dbContext.Chats.Add(chat);
			}
			chat.UpdatedAt = assistantMessage.CreatedAt;
			dbContext.ChatMessages.AddRange(userMessage, assistantMessage);
			await dbContext.SaveChangesAsync(CancellationToken.None);

			// Best effort: the messages are already stored, so a title failure keeps the placeholder title
			if (isNewChat)
			{
				try
				{
					string? title = SanitizeTitle(await chatService.GenerateTitleAsync(message, reply.Content, cancellationToken));
					if (title is not null)
					{
						chat.Title = title;
						await dbContext.SaveChangesAsync(CancellationToken.None);
					}
				}
				catch (Exception ex)
				{
					logger.LogWarning(ex, "Could not generate a title for chat {ChatId}, keeping the placeholder", chat.Id);
				}
			}

			return Ok(ToResponse(chat));
		}

		private static string? SanitizeTitle(string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				return null;
			}

			string firstLine = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
			string title = string.Join(' ', firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
				.Trim().Trim('"', '\'', '`', '“', '”', '‘', '’', '«', '»', '*', '#').Trim();
			if (title.Length == 0)
			{
				return null;
			}

			return title.Length <= MaxTitleLength ? title : title[..MaxTitleLength].TrimEnd();
		}
	}
}
