using AutoPilot.WebAPI.Database.Models;
using Microsoft.EntityFrameworkCore;
using PostgresLib;

namespace AutoPilot.WebAPI.Database
{
	public class AppDbContext(DbContextOptions<AppDbContext> options, IHttpContextAccessor? httpContextAccessor = null)
		: GenericDbContext<AppDbContext>(options, httpContextAccessor)
	{
		public DbSet<User> Users { get; set; }
		public DbSet<Device> Devices { get; set; }
		public DbSet<Repository> Repositories { get; set; }

		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			base.OnModelCreating(modelBuilder);

			modelBuilder.Entity<User>(entity =>
			{
				entity.Property(e => e.Name)
					.HasColumnType("citext")
					.IsRequired();

				entity.Property(e => e.PasswordHash)
					.IsRequired();

				entity.Property(e => e.Role)
					.IsRequired();
			});

			modelBuilder.Entity<Device>(entity =>
			{
				entity.Property(e => e.RefreshToken)
					.IsRequired();

				entity.Property(e => e.RefreshTokenExpiresAt)
					.IsRequired();
			});

			modelBuilder.Entity<Repository>(entity =>
			{
				entity.HasKey(e => e.Id);

				entity.Property(e => e.Id)
					.ValueGeneratedOnAdd();

				entity.HasIndex(e => e.GithubId)
					.IsUnique();

				entity.Property(e => e.Name)
					.IsRequired();

				entity.Property(e => e.CloneUrl)
					.IsRequired();

				entity.Property(e => e.HtmlUrl)
					.IsRequired();

				entity.Property(e => e.DefaultBranch)
					.IsRequired();
			});
		}
	}
}
