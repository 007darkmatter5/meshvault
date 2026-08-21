using MeshVault.Core.Models;
using MeshVault.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Tests;

/// <summary>
/// Preferences chosen in the UI must survive a restart. Pausing preview
/// building and finding it running again after a restart would defeat the point.
/// </summary>
public class SettingsStoreTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly SettingsStore _store;

    private sealed class Factory(SqliteConnection conn) : IDbContextFactory<MeshVaultDbContext>
    {
        public MeshVaultDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<MeshVaultDbContext>().UseSqlite(conn).Options);
    }

    public SettingsStoreTests()
    {
        _conn.Open();
        var factory = new Factory(_conn);
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        _store = new SettingsStore(factory);
    }

    [Fact]
    public async Task An_unset_key_returns_the_fallback()
    {
        Assert.False(await _store.GetBoolAsync(SettingKeys.PreviewBuildingPaused));
        Assert.True(await _store.GetBoolAsync("nothing.here", fallback: true));
    }

    [Fact]
    public async Task A_value_round_trips()
    {
        await _store.SetBoolAsync(SettingKeys.PreviewBuildingPaused, true);

        Assert.True(await _store.GetBoolAsync(SettingKeys.PreviewBuildingPaused));
        // The fallback must not override a stored value.
        Assert.True(await _store.GetBoolAsync(SettingKeys.PreviewBuildingPaused, fallback: false));
    }

    [Fact]
    public async Task Writing_the_same_key_twice_updates_rather_than_duplicating()
    {
        await _store.SetBoolAsync(SettingKeys.PreviewBuildingPaused, true);
        await _store.SetBoolAsync(SettingKeys.PreviewBuildingPaused, false);

        Assert.False(await _store.GetBoolAsync(SettingKeys.PreviewBuildingPaused));

        await using var db = new Factory(_conn).CreateDbContext();
        Assert.Equal(1, await db.Settings.CountAsync());
    }

    [Fact]
    public async Task Keys_are_independent()
    {
        await _store.SetBoolAsync("a", true);
        await _store.SetBoolAsync("b", false);

        Assert.True(await _store.GetBoolAsync("a"));
        Assert.False(await _store.GetBoolAsync("b"));
    }

    public void Dispose() => _conn.Dispose();
}
