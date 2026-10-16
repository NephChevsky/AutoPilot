using AutoPilot.WebAPI.Database.Models;
using GitHub.Copilot;
using System.Text;

namespace AutoPilot.WebAPI.Services
{
	public class ChatService(IConfiguration configuration, ILogger<ChatService> logger)
	{
		private const string CopilotModel = "claude-sonnet-5.5";
		private const string CopilotReasoningEffort = "medium";
		private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMinutes(5);

		// Plain Q&A: a fresh session per message, the previous messages are replayed as context
		public async Task<string> AskAsync(IReadOnlyList<ChatMessage> history, string message, CancellationToken cancellationToken)
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
				Model = CopilotModel,
				ReasoningEffort = CopilotReasoningEffort,
				ExcludedTools = ["builtin:*"],
				OnPermissionRequest = PermissionHandler.ApproveAll
			}, cancellationToken);

			AssistantMessageEvent? response = await session.SendAndWaitAsync(
				new MessageOptions { Prompt = BuildPrompt(history, message) },
				ReplyTimeout,
				cancellationToken);

			string? content = response?.Data.Content;
			if (string.IsNullOrWhiteSpace(content))
			{
				logger.LogWarning("Copilot returned an empty chat response");
				throw new InvalidOperationException("Empty response from the assistant");
			}

			return content;
		}

		private static string BuildPrompt(IReadOnlyList<ChatMessage> history, string message)
		{
			if (history.Count == 0)
			{
				return message;
			}

			StringBuilder builder = new();
			builder.AppendLine("This is a conversation between a user and you, the assistant. Here is the conversation so far:");
			builder.AppendLine();
			foreach (ChatMessage previous in history)
			{
				builder.AppendLine($"{(previous.Role == ChatMessageRole.User ? "User" : "Assistant")}: {previous.Content}");
				builder.AppendLine();
			}
			builder.AppendLine("Answer the user's new message below, taking the conversation into account.");
			builder.AppendLine();
			builder.Append(message);
			return builder.ToString();
		}
	}
}
