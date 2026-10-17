using JwtLib.Interfaces;
using PostgresLib.Interfaces;

namespace AutoPilot.WebAPI.Database.Models
{
	public class User : IJwtUser, ISoftDeletable, IDateTimeTrackable
	{
		public Guid Id { get; set; }
		public string Name { get; set; } = string.Empty;
		public string PasswordHash { get; set; } = string.Empty;
		public string Role { get; set; } = string.Empty;

		// ISoftDeletable
		public bool IsDeleted { get; set; }

		// IDateTimeTrackable
		public DateTime CreatedAt { get; set; }
		public DateTime UpdatedAt { get; set; }
	}
}
