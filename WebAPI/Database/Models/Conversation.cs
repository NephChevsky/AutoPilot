using PostgresLib.Interfaces;

namespace AutoPilot.WebAPI.Database.Models
{
	public class Conversation : ISoftDeletable, IDateTimeTrackable
	{
		public Guid Id { get; set; }
		public Guid TaskId { get; set; }
		public RepositoryTask? Task { get; set; }
		public ConversationAuthor Author { get; set; }
		public ConversationType Type { get; set; } = ConversationType.Message;
		public string Content { get; set; } = string.Empty;

		// ISoftDeletable
		public bool IsDeleted { get; set; }

		// IDateTimeTrackable
		public DateTime CreatedAt { get; set; }
		public DateTime UpdatedAt { get; set; }
	}

	public enum ConversationType
	{
		Message,
		Reasoning
	}

	public enum ConversationAuthor
	{
		Neph,
		Autopilot,
		Developer,
		Reviewer
	}
}
