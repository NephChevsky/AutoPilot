using AutoPilot.WebAPI.Database;
using AutoPilot.WebAPI.Services;
using DiscordLib;
using JwtLib;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using PostgresLib;
using System.Text.Json.Serialization;

namespace AutoPilot.WebAPI
{
	public class Program
	{
		public static void Main(string[] args)
		{
			WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

			builder.Configuration.AddJsonFile("secrets.json", true);

			builder.WebHost.ConfigureKestrel(options =>
			{
				string? port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORT");
				if (port != null)
				{
					options.ListenAnyIP(int.Parse(port));
				}
			});

			string[] corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];

			builder.Services.AddCors(options =>
			{
				options.AddPolicy("FrontEndCorsPolicy",
					policy =>
					{
						policy.WithOrigins(["http://localhost:5173", .. corsOrigins])
							.AllowAnyHeader()
							.AllowAnyMethod()
							.AllowCredentials();
					});
			});

			builder.Services.AddPostgresLib<AppDbContext>(builder.Configuration);
			builder.Services.AddDiscordNotifierLib();
			builder.Services.AddJwtAuthenticationLib(builder.Configuration);
			builder.Services.AddHttpClient();
			builder.Services.AddHostedService<TaskService>();

			builder.Services.AddControllers(options =>
			{
				AuthorizationPolicy policy = new AuthorizationPolicyBuilder()
					.RequireAuthenticatedUser()
					.Build();

				options.Filters.Add(new AuthorizeFilter(policy));
			})
			.AddJsonOptions(options =>
			{
				options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
			});

			WebApplication app = builder.Build();

			using (IServiceScope scope = app.Services.CreateScope())
			{
				scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
			}

			app.UseCors("FrontEndCorsPolicy");

			app.UseJwtAuthenticationLib();
			app.MapControllers();

			app.Run();
		}
	}
}

