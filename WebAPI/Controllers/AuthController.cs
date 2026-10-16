using AutoPilot.WebAPI.Database;
using AutoPilot.WebAPI.Database.Models;
using JwtLib.Controllers;
using NotifierLib;

namespace AutoPilot.WebAPI.Controllers
{
	public class AuthController(ILogger<AuthController> logger, AppDbContext db, IConfiguration configuration, INotifier notifier, IHostEnvironment environment)
		: AuthController<AppDbContext, User, Device>(logger, db, configuration, notifier, environment, db.Users, db.Devices)
	{
	}
}
