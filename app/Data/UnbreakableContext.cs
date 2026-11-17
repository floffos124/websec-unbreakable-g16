using Microsoft.EntityFrameworkCore;
using Unbreakable.Models;

namespace Unbreakable.Data;

public class UnbreakableContext : DbContext
{
    public UnbreakableContext(DbContextOptions<UnbreakableContext> options)
        : base(options)
    {
    }

    public DbSet<Message> Messages { get; set; }

    public DbSet<AppUser> Users { get; set; }

    public DbSet<FgaState> FgaStates { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Namen en types moeten overeenkomen met SchemaUpgrade.cs, zodat een
        // nieuwe databank (EnsureCreated) en een bestaande databank (ALTER)
        // hetzelfde schema krijgen.
        modelBuilder.Entity<Message>(e =>
        {
            e.ToTable("Messages");
            e.Property(m => m.SenderId).HasMaxLength(128);
            e.Property(m => m.RecipientId).HasMaxLength(128);
            e.HasIndex(m => m.FgaSynced).HasDatabaseName("IX_Messages_FgaSynced");
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.ToTable("Users");
            e.HasKey(u => u.Sub);
            e.Property(u => u.Sub).HasMaxLength(128);
            e.Property(u => u.Username).HasMaxLength(40).IsRequired();
            e.HasIndex(u => u.Username).IsUnique().HasDatabaseName("IX_Users_Username");
        });

        modelBuilder.Entity<FgaState>(e =>
        {
            e.ToTable("FgaState");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.StoreId).HasMaxLength(64).IsRequired();
            e.Property(s => s.ModelId).HasMaxLength(64).IsRequired();
            e.Property(s => s.ModelHash).HasMaxLength(64).IsRequired();
        });
    }
}
