using GameFeedback.Domain;
using Microsoft.EntityFrameworkCore;

namespace GameFeedback.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Player> Players => Set<Player>();

    public DbSet<Feedback> Feedbacks => Set<Feedback>();

    public DbSet<FeedbackComment> FeedbackComments => Set<FeedbackComment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
