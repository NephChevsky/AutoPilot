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

		public record ChatSummaryResponse(Guid Id, string Title, DateTime CreationDateTime, DateTime UpdateDateTime);

		public record ChatMessageResponse(Guid Id, ChatMessageRole Role, string Content, DateTime CreationDateTime);

		public record ChatResponse(Guid Id, string Title, DateTime CreationDateTime, DateTime UpdateDateTime, List<ChatMessageResponse> Messages);

		private Guid? GetUserId()
		{
			string? value = User.FindFirstValue("UserId") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
			return Guid.TryParse(value, out Guid id) ? id : null;
		}

		private static ChatMessageResponse ToResponse(ChatMessage m) => new(m.Id, m.Role, m.Content, m.CreationDateTime);

		private static ChatResponse ToResponse(Chat chat) => new(
			chat.Id,
			chat.Title,
			chat.CreationDateTime,
			chat.UpdateDateTime,
			[.. chat.Messages.OrderBy(m => m.CreationDateTime).ThenBy(m => m.Role).Select(ToResponse)]);

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
				.OrderByDescending(c => c.UpdateDateTime)
				.Select(c => new ChatSummaryResponse(c.Id, c.Title, c.CreationDateTime, c.UpdateDateTime))
				.ToListAsync();

			return Ok(chats);
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

				List<ChatMessage> history = [.. chat.Messages.OrderBy(m => m.CreationDateTime).ThenBy(m => m.Role)];

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
			string reply;
			try
			{
				reply = await chatService.AskAsync(history, message, cancellationToken);
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
			ChatMessage userMessage = new() { Chat = chat, Role = ChatMessageRole.User, Content = message, CreationDateTime = userTime };
			ChatMessage assistantMessage = new() { Chat = chat, Role = ChatMessageRole.Assistant, Content = reply, CreationDateTime = replyTime };

			if (dbContext.Entry(chat).State == EntityState.Detached)
			{
				dbContext.Chats.Add(chat);
			}
			chat.UpdateDateTime = assistantMessage.CreationDateTime;
			dbContext.ChatMessages.AddRange(userMessage, assistantMessage);
			await dbContext.SaveChangesAsync(CancellationToken.None);

			return Ok(ToResponse(chat));
		}
	}
}
