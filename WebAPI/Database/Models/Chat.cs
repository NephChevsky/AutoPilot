using PostgresLib.Interfaces;

namespace AutoPilot.WebAPI.Database.Models
{
	public class Chat : ISoftDeletable, IOwnable, IDateTimeTrackable
	{
		public Guid Id { get; set; }
		public Guid UserId { get; set; }
		public User? User { get; set; }
		public string Title { get; set; } = string.Empty;
		public string? SessionId { get; set; }
		public List<ChatMessage> Messages { get; set; } = [];

		// ISoftDeletable
		public bool IsDeleted { get; set; }

		// IOwnable (stamped by GenericDbContext from the "sub" claim on insert; always equals UserId)
		public Guid Owner { get; set; }

		// IDateTimeTrackable
		public DateTime CreatedAt { get; set; }
		public DateTime UpdatedAt { get; set; }
	}
}
