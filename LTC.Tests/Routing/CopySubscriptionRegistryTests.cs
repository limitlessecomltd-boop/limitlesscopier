using FluentAssertions;
using LTC.Core.Models;
using LTC.Core.Routing;
using Xunit;

namespace LTC.Tests.Routing;

public class CopySubscriptionRegistryTests
{
    [Fact]
    public void EmptyRegistry_ReturnsEmptyArray()
    {
        var r = new CopySubscriptionRegistry();
        r.LinksForMaster(Guid.NewGuid()).Length.Should().Be(0);
    }

    [Fact]
    public void Upsert_AddsLink_AndIsLookupable()
    {
        var r = new CopySubscriptionRegistry();
        var master = Guid.NewGuid();
        var link = new CopyLink { MasterAccountId = master, SlaveAccountId = Guid.NewGuid() };

        r.Upsert(link);

        var arr = r.LinksForMaster(master);
        arr.Length.Should().Be(1);
        arr[0].Id.Should().Be(link.Id);
    }

    [Fact]
    public void Upsert_ExistingLink_ReplacesIt()
    {
        var r = new CopySubscriptionRegistry();
        var master = Guid.NewGuid();
        var link = new CopyLink { MasterAccountId = master, SlaveAccountId = Guid.NewGuid() };
        r.Upsert(link);

        // Same Id, different config:
        var updated = new CopyLink
        {
            Id = link.Id,
            MasterAccountId = master,
            SlaveAccountId = link.SlaveAccountId,
            ReverseCopy = true
        };
        r.Upsert(updated);

        r.LinksForMaster(master).Should().ContainSingle()
            .Which.ReverseCopy.Should().BeTrue();
    }

    [Fact]
    public void Remove_RemovesByLinkId()
    {
        var r = new CopySubscriptionRegistry();
        var master = Guid.NewGuid();
        var l1 = new CopyLink { MasterAccountId = master, SlaveAccountId = Guid.NewGuid() };
        var l2 = new CopyLink { MasterAccountId = master, SlaveAccountId = Guid.NewGuid() };
        r.Upsert(l1); r.Upsert(l2);

        r.Remove(l1.Id);

        var arr = r.LinksForMaster(master);
        arr.Length.Should().Be(1);
        arr[0].Id.Should().Be(l2.Id);
    }

    [Fact]
    public void ReplaceAll_ReplacesEverything_AndIgnoresDisabled()
    {
        var r = new CopySubscriptionRegistry();
        var master = Guid.NewGuid();
        r.Upsert(new CopyLink { MasterAccountId = master, SlaveAccountId = Guid.NewGuid() });
        r.Upsert(new CopyLink { MasterAccountId = master, SlaveAccountId = Guid.NewGuid() });

        var enabled = new CopyLink { MasterAccountId = master, SlaveAccountId = Guid.NewGuid(), Enabled = true };
        var disabled = new CopyLink { MasterAccountId = master, SlaveAccountId = Guid.NewGuid(), Enabled = false };

        r.ReplaceAll(new[] { enabled, disabled });

        var arr = r.LinksForMaster(master);
        arr.Length.Should().Be(1);
        arr[0].Id.Should().Be(enabled.Id);
    }
}
