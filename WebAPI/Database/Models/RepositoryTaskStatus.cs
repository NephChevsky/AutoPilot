namespace AutoPilot.WebAPI.Database.Models
{
	public enum RepositoryTaskStatus
	{
		NotStarted,
		PendingStart,
		Processing,
		WaitingForMerge,
		Completed,
		Error,
		Cancelled
	}
}
