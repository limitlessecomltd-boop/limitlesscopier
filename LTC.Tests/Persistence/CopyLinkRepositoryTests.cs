using FluentAssertions;
using LTC.Core.Models;
using LTC.Persistence;
using Xunit;

namespace LTC.Tests.Persistence;

public class CopyLinkRepositoryTests
{
    private static (Account master, Account slave) SeedAccounts(LtcDatabase db)
    {
        var m = new Account { DisplayName = "M", Login = 1, Server = "x", Role = AccountRole.Master };
        var s = new Account { DisplayName = "S", Login = 2, Server = "y", Role = AccountRole.Slave };
        db.Accounts.Upsert(m);
        db.Accounts.Upsert(s);
        return (m, s);
    }

    [Fact]
    public void Upsert_RoundTripsAllFields()
    {
        using var db = LtcDatabase.OpenInMemory();
        var (m, s) = SeedAccounts(db);

        var link = new CopyLink
        {
            MasterAccountId = m.Id,
            SlaveAccountId = s.Id,
            Enabled = true,
            LotSizing = new LotSizingConfig
            {
                Mode = LotSizingMode.RiskPercent,
                Value = 1.5,
                MinLot = 0.05,
                MaxLot = 50.0,
            },
            ReverseCopy = true,
            CopyPending = false,
            CopySLTP = true,
            CopyModifications = false,
            MaxSlippagePoints = 50,
            Filter = new CopyFilter
            {
                SymbolWhitelist = { "EURUSD", "GBPUSD" },
                SymbolBlacklist = { "BTCUSD" },
                Direction = DirectionFilter.BuyOnly,
                MaxLotPerTrade = 2.5,
                DailyLossLimit = 500,
            },
            SymbolMapOverrides = { ["XAUUSD"] = "GOLD", ["EURUSD"] = "EURUSD.m" },
        };

        db.Links.Upsert(link);

        var loaded = db.Links.GetAll().Single();
        loaded.Id.Should().Be(link.Id);
        loaded.MasterAccountId.Should().Be(m.Id);
        loaded.SlaveAccountId.Should().Be(s.Id);
        loaded.LotSizing.Mode.Should().Be(LotSizingMode.RiskPercent);
        loaded.LotSizing.Value.Should().Be(1.5);
        loaded.LotSizing.MaxLot.Should().Be(50.0);
        loaded.ReverseCopy.Should().BeTrue();
        loaded.CopyPending.Should().BeFalse();
        loaded.CopyModifications.Should().BeFalse();
        loaded.MaxSlippagePoints.Should().Be(50ul);
        loaded.Filter.SymbolWhitelist.Should().BeEquivalentTo(new[] { "EURUSD", "GBPUSD" });
        loaded.Filter.SymbolBlacklist.Should().BeEquivalentTo(new[] { "BTCUSD" });
        loaded.Filter.Direction.Should().Be(DirectionFilter.BuyOnly);
        loaded.Filter.MaxLotPerTrade.Should().Be(2.5);
        loaded.Filter.DailyLossLimit.Should().Be(500);
        loaded.SymbolMapOverrides.Should().ContainKey("XAUUSD")
            .WhoseValue.Should().Be("GOLD");
        loaded.SymbolMapOverrides.Should().ContainKey("EURUSD")
            .WhoseValue.Should().Be("EURUSD.m");
    }

    [Fact]
    public void Upsert_ReplacesExistingLinkById()
    {
        using var db = LtcDatabase.OpenInMemory();
        var (m, s) = SeedAccounts(db);
        var link = new CopyLink
        {
            MasterAccountId = m.Id, SlaveAccountId = s.Id,
            LotSizing = new LotSizingConfig { Mode = LotSizingMode.Multiplier, Value = 1.0 },
        };
        db.Links.Upsert(link);

        link.LotSizing = new LotSizingConfig { Mode = LotSizingMode.Multiplier, Value = 0.25 };
        db.Links.Upsert(link);

        var all = db.Links.GetAll();
        all.Should().ContainSingle();
        all[0].LotSizing.Value.Should().Be(0.25);
    }

    [Fact]
    public void GetByMaster_FiltersCorrectly()
    {
        using var db = LtcDatabase.OpenInMemory();
        var (m1, s1) = SeedAccounts(db);
        var m2 = new Account { DisplayName = "M2", Login = 3, Server = "z", Role = AccountRole.Master };
        db.Accounts.Upsert(m2);

        db.Links.Upsert(new CopyLink { MasterAccountId = m1.Id, SlaveAccountId = s1.Id });
        db.Links.Upsert(new CopyLink { MasterAccountId = m2.Id, SlaveAccountId = s1.Id });

        db.Links.GetByMaster(m1.Id).Should().HaveCount(1);
        db.Links.GetByMaster(m2.Id).Should().HaveCount(1);
    }

    [Fact]
    public void Delete_RemovesLink()
    {
        using var db = LtcDatabase.OpenInMemory();
        var (m, s) = SeedAccounts(db);
        var link = new CopyLink { MasterAccountId = m.Id, SlaveAccountId = s.Id };
        db.Links.Upsert(link);

        db.Links.Delete(link.Id);

        db.Links.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void DeletingAccount_AlsoRemovesItsLinks_ViaPersistedConfig()
    {
        using var db = LtcDatabase.OpenInMemory();
        var (m, s) = SeedAccounts(db);
        db.Links.Upsert(new CopyLink { MasterAccountId = m.Id, SlaveAccountId = s.Id });
        db.Links.GetAll().Should().HaveCount(1);

        var pc = new PersistedConfig(db);
        pc.DeleteAccount(m.Id);

        db.Links.GetAll().Should().BeEmpty();
        db.Accounts.GetAll().Should().HaveCount(1);  // slave still exists
    }
}
