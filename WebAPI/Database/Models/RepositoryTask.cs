namespace AutoPilot.WebAPI.Database.Models
{
	public class RepositoryTask
	{
		public Guid Id { get; set; }
		public Guid RepositoryId { get; set; }
		public Repository? Repository { get; set; }
		public DateTime CreationDateTime { get; set; } = DateTime.UtcNow;
		public DateTime? CompletionDateTime { get; set; }
		public string Title { get; set; } = string.Empty;
		public RepositoryTaskStatus Status { get; set; } = RepositoryTaskStatus.NotStarted;
		public string? BranchName { get; set; }
		public Guid? ProcessingInstanceId { get; set; }
		public int? ProcessingProcessId { get; set; }
		public DateTime? HeartbeatDateTime { get; set; }
		// Accumulated Copilot cost of all runs of this task, in AI units (AIU)
		public decimal? Cost { get; set; }
		public List<Conversation> Conversations { get; set; } = [];
	}
}
