using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Tests;

/// <summary>
/// Every sort the Browse page offers, run against SQLite.
/// </summary>
/// <remarks>
/// SQLite refuses to ORDER BY a DateTimeOffset, and EF throws rather than
/// degrading, so a sort that reads perfectly well in C# can still fail the
/// moment a person picks it. Nothing but running each one catches that.
/// </remarks>
public class ModelSortTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly ModelCatalog _catalog;

    private sealed class FakeUser : ICurrentUser
    {
        public string UserId => "alice";
    }

    private sealed class Factory(SqliteConnection conn) : IDbContextFactory<MeshVaultDbContext>
    {
        public MeshVaultDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<MeshVaultDbContext>().UseSqlite(conn).Options);
    }

    public ModelSortTests()
    {
        _conn.Open();
        var factory = new Factory(_conn);

        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Libraries.Add(new Library { Name = "L", Path = "/l" });
        db.SaveChanges();

        for (var i = 0; i < 3; i++)
        {
            db.Models.Add(new ModelEntry
            {
                LibraryId = 1,
                Name = $"model {i}",
                RelativePath = $"m{i}",
                TotalBytes = i * 1000,
                AddedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i),
                FileModifiedUtc = DateTimeOffset.UtcNow,
            });
        }
        db.SaveChanges();

        _catalog = new ModelCatalog(factory, new FakeUser());
    }

    [Theory]
    [InlineData(ModelSort.Name)]
    [InlineData(ModelSort.Newest)]
    [InlineData(ModelSort.Largest)]
    public async Task Every_sort_the_page_offers_actually_runs(ModelSort sort)
    {
        var result = await _catalog.SearchAsync(new ModelQuery { Sort = sort });

        Assert.Equal(3, result.TotalCount);
    }

    [Fact]
    public async Task Newest_really_does_put_the_newest_first()
    {
        var result = await _catalog.SearchAsync(new ModelQuery { Sort = ModelSort.Newest });

        Assert.Equal(["model 2", "model 1", "model 0"], result.Items.Select(i => i.Model.Name));
    }

    [Fact]
    public async Task Largest_really_does_put_the_largest_first()
    {
        var result = await _catalog.SearchAsync(new ModelQuery { Sort = ModelSort.Largest });

        Assert.Equal(["model 2", "model 1", "model 0"], result.Items.Select(i => i.Model.Name));
    }

    public void Dispose() => _conn.Dispose();
}
