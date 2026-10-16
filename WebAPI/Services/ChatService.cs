using AutoPilot.WebAPI.Database.Models;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using System.Text;

namespace AutoPilot.WebAPI.Services
{
	public class ChatService(IConfiguration configuration, ILogger<ChatService> logger)
	{
		private const string CopilotModel = "claude-sonnet-5.5";
		private const string CopilotReasoningEffort = "medium";
		private const int MaxHistoryCharacters = 40000;
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

			// Plain Q&A needs no tools: deny everything (PermissionDecision is flagged experimental by the SDK)
#pragma warning disable GHCP001
			await using CopilotSession session = await client.CreateSessionAsync(new SessionConfig
			{
				Model = CopilotModel,
				ReasoningEffort = CopilotReasoningEffort,
				ExcludedTools = ["builtin:*"],
				OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("Tools are not available in chat"))
			}, cancellationToken);
#pragma warning restore GHCP001

			AssistantMessageEvent? response;
			try
			{
				response = await session.SendAndWaitAsync(
					new MessageOptions { Prompt = BuildPrompt(history, message) },
					ReplyTimeout,
					cancellationToken);
			}
			catch (OperationCanceledException)
			{
				// Make sure the agent process does not keep running (and spending) after a cancel
				try
				{
					await client.ForceStopAsync();
				}
				catch (Exception ex)
				{
					logger.LogWarning(ex, "Could not stop the chat agent process");
				}

				throw;
			}

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

			// Keep the most recent messages that fit in the character budget
			List<string> lines = [];
			int used = 0;
			for (int i = history.Count - 1; i >= 0; i--)
			{
				string line = $"{(history[i].Role == ChatMessageRole.User ? "User" : "Assistant")}: {history[i].Content}";
				if (used + line.Length > MaxHistoryCharacters)
				{
					break;
				}
				used += line.Length;
				lines.Add(line);
			}
			lines.Reverse();

			StringBuilder builder = new();
			if (lines.Count > 0)
			{
				builder.AppendLine("This is a conversation between a user and you, the assistant. Here is the (possibly truncated) conversation so far:");
				builder.AppendLine();
				foreach (string line in lines)
				{
					builder.AppendLine(line);
					builder.AppendLine();
				}
			}
			builder.AppendLine("Answer the user's new message below, taking the conversation into account.");
			builder.AppendLine();
			builder.Append(message);
			return builder.ToString();
		}
	}
}
