using FluentAssertions;
using LTC.Persistence;
using Xunit;

namespace LTC.Tests.Persistence;

public class SettingsAndMigrationsTests
{
    [Fact]
    public void Settings_SetThenGet_RoundTrips()
    {
        using var db = LtcDatabase.OpenInMemory();
        db.Settings.Set("window.width", "1380");
        db.Settings.Get("window.width").Should().Be("1380");
    }

    [Fact]
    public void Settings_SetTwice_Updates()
    {
        using var db = LtcDatabase.OpenInMemory();
        db.Settings.Set("k", "v1");
        db.Settings.Set("k", "v2");
        db.Settings.Get("k").Should().Be("v2");
    }

    [Fact]
    public void Settings_Delete()
    {
        using var db = LtcDatabase.OpenInMemory();
        db.Settings.Set("k", "v");
        db.Settings.Delete("k");
        db.Settings.Get("k").Should().BeNull();
    }

    [Fact]
    public void Settings_GetAll()
    {
        using var db = LtcDatabase.OpenInMemory();
        db.Settings.Set("a", "1");
        db.Settings.Set("b", "2");
        var all = db.Settings.GetAll();
        all.Should().ContainKeys("a", "b");
    }

    [Fact]
    public void Migrations_AreIdempotent()
    {
        using var db = LtcDatabase.OpenInMemory();
        // Re-apply: should not throw, should not mangle data.
        LTC.Persistence.Migrations.SchemaMigrator.Apply(db.Connection);
        LTC.Persistence.Migrations.SchemaMigrator.Apply(db.Connection);
        db.Settings.Set("alive", "yes");
        db.Settings.Get("alive").Should().Be("yes");
    }
}
