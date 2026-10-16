namespace AutoPilot.WebAPI.Database.Models
{
	public enum RepositoryTaskStatus
	{
		NotStarted,
		PendingStart,
		Processing,
		WaitingForReview,
		Completed,
		Error,
		Cancelled
	}
}
