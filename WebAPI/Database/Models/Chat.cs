namespace AutoPilot.WebAPI.Database.Models
{
	public class Chat
	{
		public Guid Id { get; set; }
		public Guid UserId { get; set; }
		public User? User { get; set; }
		public string Title { get; set; } = string.Empty;
		public DateTime CreationDateTime { get; set; } = DateTime.UtcNow;
		public DateTime UpdateDateTime { get; set; } = DateTime.UtcNow;
		public List<ChatMessage> Messages { get; set; } = [];
	}
}
