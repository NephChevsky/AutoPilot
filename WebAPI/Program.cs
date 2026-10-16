using AutoPilot.WebAPI.Database;
using DiscordLib;
using JwtLib;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using PostgresLib;
using System.Text.Json.Serialization;

namespace AutoPilot.WebAPI
{
	public class Program
	{
		public static void Main(string[] args)
		{
			WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

			builder.Services.AddCors(options =>
			{
				options.AddPolicy("FrontEndCorsPolicy",
					policy =>
					{
						policy.WithOrigins("http://localhost:5173")
							.AllowAnyHeader()
							.AllowAnyMethod()
							.AllowCredentials();
					});
			});

			builder.Services.AddPostgresLib<AppDbContext>(builder.Configuration);
			builder.Services.AddDiscordNotifierLib();
			builder.Services.AddJwtAuthenticationLib(builder.Configuration);
			builder.Services.AddHttpClient();

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

			app.UseCors("FrontEndCorsPolicy");

			app.UseJwtAuthenticationLib();
			app.MapControllers();

			app.Run();
		}
	}
}
