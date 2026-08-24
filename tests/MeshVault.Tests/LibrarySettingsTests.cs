using MeshVault.Core.Models;
using MeshVault.Core.Services;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Tests;

/// <summary>
/// A library seeded from configuration is created once and never revisited, so
/// the only way to correct its name or its organise permission afterwards is
/// through the editor. Without these the container template's defaults are
/// permanent.
/// </summary>
public class LibrarySettingsTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly IDbContextFactory<MeshVaultDbContext> _factory;
    private readonly ModelEditor _editor;

    private sealed class FakeUser : ICurrentUser
    {
        public string UserId => Users.LocalUserId;
    }

    private sealed class Factory(SqliteConnection conn) : IDbContextFactory<MeshVaultDbContext>
    {
        public MeshVaultDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<MeshVaultDbContext>().UseSqlite(conn).Options);
    }

    public LibrarySettingsTests()
    {
        _conn.Open();
        _factory = new Factory(_conn);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Libraries.Add(new Library { Name = "Models", Path = "/models", AllowOrganize = false });
        db.SaveChanges();

        _editor = new ModelEditor(_factory, new FakeUser());
    }

    private async Task<Library> Reload()
    {
        await using var db = _factory.CreateDbContext();
        return await db.Libraries.AsNoTracking().SingleAsync();
    }

    [Fact]
    public async Task Organize_permission_can_be_turned_on_after_the_library_exists()
    {
        await _editor.UpdateLibraryAsync(1, "Models", allowOrganize: true);

        Assert.True((await Reload()).AllowOrganize);
    }

    [Fact]
    public async Task Organize_permission_can_be_turned_back_off()
    {
        await _editor.UpdateLibraryAsync(1, "Models", allowOrganize: true);
        await _editor.UpdateLibraryAsync(1, "Models", allowOrganize: false);

        Assert.False((await Reload()).AllowOrganize);
    }

    [Fact]
    public async Task A_library_can_be_renamed()
    {
        await _editor.UpdateLibraryAsync(1, "  Printables  ", allowOrganize: false);

        Assert.Equal("Printables", (await Reload()).Name);
    }

    [Fact]
    public async Task An_empty_name_falls_back_to_the_folder_name()
    {
        await _editor.UpdateLibraryAsync(1, "   ", allowOrganize: false);

        Assert.Equal("models", (await Reload()).Name);
    }

    [Fact]
    public async Task The_path_is_never_changed()
    {
        await _editor.UpdateLibraryAsync(1, "Renamed", allowOrganize: true);

        Assert.Equal("/models", (await Reload()).Path);
    }

    [Fact]
    public async Task Updating_a_library_that_is_gone_is_a_no_op()
    {
        await _editor.UpdateLibraryAsync(999, "Ghost", allowOrganize: true);

        Assert.Equal("Models", (await Reload()).Name);
    }

    public void Dispose() => _conn.Dispose();
}
