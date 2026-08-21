using MeshVault.Core.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Data;

public class MeshVaultDbContext(DbContextOptions<MeshVaultDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Library> Libraries => Set<Library>();
    public DbSet<ModelEntry> Models => Set<ModelEntry>();
    public DbSet<ModelFile> Files => Set<ModelFile>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<Designer> Designers => Set<Designer>();
    public DbSet<ModelFavorite> Favorites => Set<ModelFavorite>();
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Identity brings its own configuration; ours is layered on top.
        base.OnModelCreating(b);

        b.Entity<Library>(e =>
        {
            e.HasIndex(x => x.Path).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
        });

        b.Entity<ModelEntry>(e =>
        {
            e.HasIndex(x => new { x.LibraryId, x.RelativePath }).IsUnique();
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.SourceSite);
            e.Property(x => x.Name).HasMaxLength(400);
            e.Property(x => x.SourceSite).HasMaxLength(60);
            e.Property(x => x.License).HasMaxLength(120);

            e.HasOne(x => x.Library).WithMany(x => x.Models)
                .HasForeignKey(x => x.LibraryId).OnDelete(DeleteBehavior.Cascade);

            // Removing a designer must not take their models with them.
            e.HasOne(x => x.Designer).WithMany(x => x.Models)
                .HasForeignKey(x => x.DesignerId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ModelFile>(e =>
        {
            e.HasIndex(x => new { x.ModelEntryId, x.RelativePath }).IsUnique();
            e.HasIndex(x => x.Sha256);
            e.HasOne(x => x.ModelEntry).WithMany(x => x.Files)
                .HasForeignKey(x => x.ModelEntryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Tag>(e =>
        {
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
        });

        b.Entity<Designer>(e =>
        {
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
        });

        b.Entity<Collection>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.OwnerId).HasMaxLength(450);
            // Two people may each have a collection called "To Print", but one
            // person may not have both "To Print" and "to print".
            e.HasIndex(x => new { x.OwnerId, x.NormalizedName }).IsUnique();
        });

        b.Entity<Setting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(120);
            e.Property(x => x.Value).HasMaxLength(400);
        });

        b.Entity<ModelFavorite>(e =>
        {
            e.Property(x => x.UserId).HasMaxLength(450);
            e.HasIndex(x => new { x.UserId, x.ModelEntryId }).IsUnique();
            e.HasOne(x => x.ModelEntry).WithMany(x => x.Favorites)
                .HasForeignKey(x => x.ModelEntryId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
