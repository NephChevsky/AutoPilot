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

		private const int MaxTitleSourceCharacters = 4000;
		private static readonly TimeSpan TitleTimeout = TimeSpan.FromSeconds(30);

		public record ChatReply(string Content, string SessionId, decimal? Cost);

		// Plain Q&A: the Copilot session is resumed by id; if it is gone, a new one is created and the stored messages are replayed once
		public async Task<ChatReply> AskAsync(string? sessionId, IReadOnlyList<ChatMessage> history, string message, CancellationToken cancellationToken)
		{
			string token = configuration["Copilot:Token"]
				?? throw new InvalidOperationException("Copilot:Token is not configured");

			// Empty mode: no ambient tools/instructions; the agent only sees an empty private folder, never the host files
			string chatFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoPilot", "chat");
			Directory.CreateDirectory(chatFolder);

			await using CopilotClient client = new(new CopilotClientOptions
			{
				GitHubToken = token,
				Mode = CopilotClientMode.Empty,
				BaseDirectory = chatFolder,
				WorkingDirectory = chatFolder,
			});

			await client.StartAsync(cancellationToken);

			// Plain Q&A needs no tools: none are available and any permission request is rejected (PermissionDecision is flagged experimental by the SDK)
#pragma warning disable GHCP001
			CopilotSession? resumed = null;
			if (sessionId is not null)
			{
				try
				{
					resumed = await client.ResumeSessionAsync(sessionId, new ResumeSessionConfig
					{
						Model = CopilotModel,
						ReasoningEffort = CopilotReasoningEffort,
						WorkingDirectory = chatFolder,
						AvailableTools = [],
						ExcludedTools = ["builtin:*"],
						Memory = new MemoryConfiguration { Enabled = false },
						EnableSessionStore = false,
						OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("Tools are not available in chat"))
					}, cancellationToken);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					logger.LogWarning(ex, "Could not resume chat session {SessionId}, starting a new one", sessionId);
				}
			}

			string activeSessionId = resumed is not null ? sessionId! : Guid.NewGuid().ToString();
			await using CopilotSession session = resumed ?? await client.CreateSessionAsync(new SessionConfig
			{
				SessionId = activeSessionId,
				Model = CopilotModel,
				ReasoningEffort = CopilotReasoningEffort,
				WorkingDirectory = chatFolder,
				AvailableTools = [],
				ExcludedTools = ["builtin:*"],
				Memory = new MemoryConfiguration { Enabled = false },
				EnableSessionStore = false,
				OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("Tools are not available in chat"))
			}, cancellationToken);
#pragma warning restore GHCP001

			// Subscribed before sending so usage reported before the final message is counted too
			RunCost cost = new();
			session.On<AssistantUsageEvent>(evt =>
			{
				if (evt.Data.CopilotUsage is { } usage)
				{
					cost.Add(usage.TotalNanoAiu);
				}
			});

			AssistantMessageEvent? response;
			try
			{
				response = await session.SendAndWaitAsync(
					new MessageOptions { Prompt = resumed is not null ? message : BuildPrompt(history, message) },
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

			return new ChatReply(content, activeSessionId, cost.Aiu > 0 ? cost.Aiu : null);
		}

		// Separate one-shot query (fresh session, not the chat's one) that asks the model for a short conversation title
		public async Task<string?> GenerateTitleAsync(string userMessage, string assistantReply, CancellationToken cancellationToken)
		{
			string token = configuration["Copilot:Token"]
				?? throw new InvalidOperationException("Copilot:Token is not configured");

			string chatFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoPilot", "chat");
			Directory.CreateDirectory(chatFolder);

			using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeoutSource.CancelAfter(TitleTimeout);

			await using CopilotClient client = new(new CopilotClientOptions
			{
				GitHubToken = token,
				Mode = CopilotClientMode.Empty,
				BaseDirectory = chatFolder,
				WorkingDirectory = chatFolder,
			});

			try
			{
				await client.StartAsync(timeoutSource.Token);

#pragma warning disable GHCP001
				await using CopilotSession session = await client.CreateSessionAsync(new SessionConfig
				{
					SessionId = Guid.NewGuid().ToString(),
					Model = CopilotModel,
					ReasoningEffort = CopilotReasoningEffort,
					WorkingDirectory = chatFolder,
					AvailableTools = [],
					ExcludedTools = ["builtin:*"],
					Memory = new MemoryConfiguration { Enabled = false },
					EnableSessionStore = false,
					OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("Tools are not available in chat"))
				}, timeoutSource.Token);
#pragma warning restore GHCP001

				string prompt = "Write a concise title (at most 6 words) for the conversation below. "
					+ "Use the same language as the conversation. Do not use quotes, punctuation at the end or markdown. "
					+ "Reply with the title only.\n\n"
					+ $"User: {Truncate(userMessage)}\n\nAssistant: {Truncate(assistantReply)}";

				AssistantMessageEvent? response = await session.SendAndWaitAsync(new MessageOptions { Prompt = prompt }, TitleTimeout, timeoutSource.Token);
				return response?.Data.Content;
			}
			catch (OperationCanceledException)
			{
				try
				{
					await client.ForceStopAsync();
				}
				catch (Exception ex)
				{
					logger.LogWarning(ex, "Could not stop the title agent process");
				}

				throw;
			}
		}

		private static string Truncate(string text) => text.Length <= MaxTitleSourceCharacters ? text : text[..MaxTitleSourceCharacters];

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
