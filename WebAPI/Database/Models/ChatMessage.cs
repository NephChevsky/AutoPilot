using PostgresLib.Interfaces;

namespace AutoPilot.WebAPI.Database.Models
{
	public class ChatMessage : ISoftDeletable, IOwnable, IDateTimeTrackable
	{
		public Guid Id { get; set; }
		public Guid ChatId { get; set; }
		public Chat? Chat { get; set; }
		public ChatMessageRole Role { get; set; }
		public string Content { get; set; } = string.Empty;
		// Copilot cost of the reply in AIU; only set on assistant messages
		public decimal? Cost { get; set; }

		// ISoftDeletable
		public bool IsDeleted { get; set; }

		// IOwnable (stamped by GenericDbContext from the "sub" claim on insert)
		public Guid Owner { get; set; }

		// IDateTimeTrackable
		public DateTime CreatedAt { get; set; }
		public DateTime UpdatedAt { get; set; }
	}

	public enum ChatMessageRole
	{
		User,
		Assistant
	}
}
