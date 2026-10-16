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
		public string Task { get; set; } = string.Empty;
		public RepositoryTaskStatus Status { get; set; } = RepositoryTaskStatus.NotStarted;
		public string? Response { get; set; }
		public string? BranchName { get; set; }
	}
}
