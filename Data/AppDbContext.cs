using Microsoft.EntityFrameworkCore;
using RedwoodIloilo.Common.Entities;

namespace BackupAgent.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<RagDocument> RagDocuments => Set<RagDocument>();
    public DbSet<RagChunk> RagChunks => Set<RagChunk>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasPostgresExtension("vector");
    }
}
