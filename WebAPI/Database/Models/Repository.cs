using PostgresLib.Interfaces;

namespace AutoPilot.WebAPI.Database.Models
{
	public class Repository : ISoftDeletable, IDateTimeTrackable
	{
		public Guid Id { get; set; }
		public long GithubId { get; set; }
		public string Name { get; set; } = string.Empty;
		public string CloneUrl { get; set; } = string.Empty;
		public string HtmlUrl { get; set; } = string.Empty;
		public string DefaultBranch { get; set; } = string.Empty;
		public bool Pinned { get; set; }

		// ISoftDeletable
		public bool IsDeleted { get; set; }

		// IDateTimeTrackable
		public DateTime CreatedAt { get; set; }
		public DateTime UpdatedAt { get; set; }
	}
}
