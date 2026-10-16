namespace AutoPilot.WebAPI.Database.Models
{
	public class ChatMessage
	{
		public Guid Id { get; set; }
		public Guid ChatId { get; set; }
		public Chat? Chat { get; set; }
		public ChatMessageRole Role { get; set; }
		public string Content { get; set; } = string.Empty;
		public DateTime CreationDateTime { get; set; } = DateTime.UtcNow;
	}

	public enum ChatMessageRole
	{
		User,
		Assistant
	}
}
