using JwtLib.Interfaces;
using PostgresLib.Interfaces;

namespace AutoPilot.WebAPI.Database.Models
{
	public class Device : IJwtDevice, ISoftDeletable, IDateTimeTrackable
	{
		public Guid Id { get; set; }
		public string Name { get; set; } = string.Empty;
		public Guid OwnerId { get; set; }
		public string RefreshToken { get; set; } = string.Empty;
		public DateTime? RefreshTokenExpiresAt { get; set; }

		// ISoftDeletable
		public bool IsDeleted { get; set; }

		// IDateTimeTrackable
		public DateTime CreatedAt { get; set; }
		public DateTime UpdatedAt { get; set; }
	}
}
