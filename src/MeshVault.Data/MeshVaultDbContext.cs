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

        b.Entity<Paint>(e =>
        {
            e.Property(x => x.OwnerId).HasMaxLength(450);
            e.Property(x => x.Name).HasMaxLength(160);
            e.Property(x => x.Brand).HasMaxLength(80);
            e.Property(x => x.Range).HasMaxLength(80);
            e.Property(x => x.Hex).HasMaxLength(9);
            // Racks recorded before quantity existed held one of each.
            e.Property(x => x.Quantity).HasDefaultValue(1);
            // One bottle per name per rack. Two people may each own Mephiston Red.
            e.HasIndex(x => new { x.OwnerId, x.NormalizedName }).IsUnique();
        });

        b.Entity<PaintBrand>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(80);
            e.HasIndex(x => x.NormalizedName).IsUnique();
        });

        b.Entity<PaintRange>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(80);
            // A range name is only unique within its brand: several makers
            // have something called "Base" or "Air".
            e.HasIndex(x => new { x.PaintBrandId, x.NormalizedName }).IsUnique();
            e.HasOne(x => x.Brand).WithMany(x => x.Ranges)
                .HasForeignKey(x => x.PaintBrandId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PaintScheme>(e =>
        {
            e.Property(x => x.OwnerId).HasMaxLength(450);
            e.Property(x => x.OwnerName).HasMaxLength(200);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.ModelEntryId);
            e.HasOne(x => x.ModelEntry).WithMany()
                .HasForeignKey(x => x.ModelEntryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PaintStep>(e =>
        {
            e.Property(x => x.PaintName).HasMaxLength(160);
            e.Property(x => x.Technique).HasMaxLength(80);
            e.Property(x => x.Area).HasMaxLength(120);
            e.Property(x => x.Hex).HasMaxLength(9);

            e.HasOne(x => x.PaintScheme).WithMany(x => x.Steps)
                .HasForeignKey(x => x.PaintSchemeId).OnDelete(DeleteBehavior.Cascade);

            // Throwing a bottle away edits an inventory. It must not quietly
            // rewrite every recipe that mentioned it, which is why the step
            // keeps the name and the link is allowed to go null.
            e.HasOne(x => x.Paint).WithMany(x => x.Steps)
                .HasForeignKey(x => x.PaintId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<SchemePhoto>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(200);
            e.Property(x => x.ContentType).HasMaxLength(80);
            e.Property(x => x.Caption).HasMaxLength(400);
            e.HasOne(x => x.PaintScheme).WithMany(x => x.Photos)
                .HasForeignKey(x => x.PaintSchemeId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    public DbSet<PaintBrand> PaintBrands => Set<PaintBrand>();
    public DbSet<PaintRange> PaintRanges => Set<PaintRange>();
    public DbSet<Paint> Paints => Set<Paint>();
    public DbSet<PaintScheme> PaintSchemes => Set<PaintScheme>();
    public DbSet<PaintStep> PaintSteps => Set<PaintStep>();
    public DbSet<SchemePhoto> SchemePhotos => Set<SchemePhoto>();
}
