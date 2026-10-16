namespace AutoPilot.WebAPI.Database.Models
{
	public class Conversation
	{
		public Guid Id { get; set; }
		public Guid TaskId { get; set; }
		public RepositoryTask? Task { get; set; }
		public ConversationAuthor Author { get; set; }
		public string Content { get; set; } = string.Empty;
		public DateTime CreationDateTime { get; set; } = DateTime.UtcNow;
	}

	public enum ConversationAuthor
	{
		Neph,
		Autopilot
	}
}
