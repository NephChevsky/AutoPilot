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
		public DbSet<RepositoryTask> Tasks { get; set; }
		public DbSet<Conversation> Conversations { get; set; }
		public DbSet<Chat> Chats { get; set; }
		public DbSet<ChatMessage> ChatMessages { get; set; }

		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			base.OnModelCreating(modelBuilder);

			// GenericDbContext registers the soft-delete and owner filters with separate HasQueryFilter calls, so the
			// owner filter silently replaces the soft-delete one. Combine them for the IOwnable entities.
			modelBuilder.Entity<Chat>().HasQueryFilter(e => !e.IsDeleted && e.Owner == CurrentUserId);
			modelBuilder.Entity<ChatMessage>().HasQueryFilter(e => !e.IsDeleted && e.Owner == CurrentUserId);

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

				// Partial so a soft-deleted repository does not block re-adding the same GitHub repo
				entity.HasIndex(e => e.GithubId)
					.IsUnique()
					.HasFilter("is_deleted = false");

				entity.Property(e => e.Name)
					.IsRequired();

				entity.Property(e => e.CloneUrl)
					.IsRequired();

				entity.Property(e => e.HtmlUrl)
					.IsRequired();

				entity.Property(e => e.DefaultBranch)
					.IsRequired();
			});

			modelBuilder.Entity<RepositoryTask>(entity =>
			{
				entity.HasKey(e => e.Id);

				entity.Property(e => e.Id)
					.ValueGeneratedOnAdd();

				entity.Property(e => e.Status)
					.HasConversion<string>()
					.HasDefaultValue(RepositoryTaskStatus.NotStarted)
					.IsRequired();

				entity.HasOne(e => e.Repository)
					.WithMany()
					.HasForeignKey(e => e.RepositoryId)
					.OnDelete(DeleteBehavior.Cascade);
			});

			modelBuilder.Entity<Conversation>(entity =>
			{
				entity.HasKey(e => e.Id);

				entity.Property(e => e.Id)
					.ValueGeneratedOnAdd();

				entity.Property(e => e.Author)
					.HasConversion<string>()
					.IsRequired();

				entity.Property(e => e.Type)
					.HasConversion<string>()
					.IsRequired();

				entity.Property(e => e.Content)
					.IsRequired();

				entity.HasOne(e => e.Task)
					.WithMany(t => t.Conversations)
					.HasForeignKey(e => e.TaskId)
					.OnDelete(DeleteBehavior.Cascade);
			});

			modelBuilder.Entity<Chat>(entity =>
			{
				entity.HasKey(e => e.Id);

				entity.Property(e => e.Id)
					.ValueGeneratedOnAdd();

				entity.Property(e => e.Title)
					.IsRequired();

				entity.HasIndex(e => new { e.UserId, e.UpdatedAt });

				entity.HasOne(e => e.User)
					.WithMany()
					.HasForeignKey(e => e.UserId)
					.OnDelete(DeleteBehavior.Cascade);
			});

			modelBuilder.Entity<ChatMessage>(entity =>
			{
				entity.HasKey(e => e.Id);

				entity.Property(e => e.Id)
					.ValueGeneratedOnAdd();

				entity.Property(e => e.Role)
					.HasConversion<string>()
					.IsRequired();

				entity.Property(e => e.Content)
					.IsRequired();

				entity.HasOne(e => e.Chat)
					.WithMany(c => c.Messages)
					.HasForeignKey(e => e.ChatId)
					.OnDelete(DeleteBehavior.Cascade);
			});
		}
	}
}
