using JwtLib.Interfaces;

namespace AutoPilot.WebAPI.Database.Models
{
	public class User : IJwtUser
	{
		public Guid Id { get; set; }
		public string Name { get; set; } = string.Empty;
		public string PasswordHash { get; set; } = string.Empty;
		public string Role { get; set; } = string.Empty;
	}
}
