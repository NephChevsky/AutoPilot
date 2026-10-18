using AutoPilot.WebAPI.Database;
using Microsoft.EntityFrameworkCore;

namespace AutoPilot.WebAPI.Services
{
	// Generates and stores chat titles in the background, independently of the request that created the chat
	public class ChatTitleService(IServiceScopeFactory scopeFactory, IHostApplicationLifetime lifetime, ILogger<ChatTitleService> logger)
	{
		private const int MaxTitleLength = 60;

		// Fire-and-forget: never throws, stops on application shutdown
		public void Enqueue(Guid chatId, string placeholderTitle, string userMessage, string assistantReply)
		{
			CancellationToken stopping = lifetime.ApplicationStopping;
			_ = Task.Run(() => GenerateAsync(chatId, placeholderTitle, userMessage, assistantReply, stopping), CancellationToken.None);
		}

		private async Task GenerateAsync(Guid chatId, string placeholderTitle, string userMessage, string assistantReply, CancellationToken cancellationToken)
		{
			try
			{
				// New scope = new DbContext with no HTTP user: the owner filter and stamping do not apply
				using IServiceScope scope = scopeFactory.CreateScope();
				ChatService chatService = scope.ServiceProvider.GetRequiredService<ChatService>();
				AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

				string? title = SanitizeTitle(await chatService.GenerateTitleAsync(userMessage, assistantReply, cancellationToken));
				if (title is null)
				{
					return;
				}

				// ExecuteUpdate bypasses SaveChanges stamping, so UpdatedAt (list ordering) is untouched;
				// the title is only replaced while it is still the placeholder
				await dbContext.Chats
					.IgnoreQueryFilters()
					.Where(c => c.Id == chatId && !c.IsDeleted && c.Title == placeholderTitle)
					.ExecuteUpdateAsync(s => s.SetProperty(c => c.Title, title), cancellationToken);
			}
			catch (OperationCanceledException)
			{
				// Shutdown or timeout: keep the placeholder
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Could not generate a title for chat {ChatId}, keeping the placeholder", chatId);
			}
		}

		internal static string? SanitizeTitle(string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				return null;
			}

			string firstLine = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
			string title = string.Join(' ', firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
				.Trim().Trim('"', '\'', '`', '“', '”', '‘', '’', '«', '»', '*', '#').Trim()
				.TrimEnd('.', ',', ';', ':', '!', '?', '…', '。').TrimEnd();
			if (title.Length == 0)
			{
				return null;
			}

			return title.Length <= MaxTitleLength ? title : title[..MaxTitleLength].TrimEnd();
		}
	}
}
