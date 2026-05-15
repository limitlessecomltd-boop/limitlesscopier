using FluentAssertions;
using LTC.Core.Models;
using LTC.Persistence;
using LTC.Persistence.Encryption;
using Xunit;

namespace LTC.Tests.Persistence;

public class AccountRepositoryTests
{
    [Fact]
    public void EmptyDb_ReturnsEmptyList()
    {
        using var db = LtcDatabase.OpenInMemory();
        db.Accounts.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void Upsert_ThenGetAll_RoundTripsAllFields()
    {
        using var db = LtcDatabase.OpenInMemory();
        var acc = new Account
        {
            DisplayName = "FTMO Challenge",
            Login = 5005878,
            Password = "s3cret-password",
            Server = "ftmo.com",
            Port = 443,
            Role = AccountRole.Master,
            BrokerLabel = "FTMO",
            Enabled = true,
        };

        db.Accounts.Upsert(acc);

        var loaded = db.Accounts.GetAll().Single();
        loaded.Id.Should().Be(acc.Id);
        loaded.DisplayName.Should().Be("FTMO Challenge");
        loaded.Login.Should().Be(5005878u);
        loaded.Password.Should().Be("s3cret-password");
        loaded.Server.Should().Be("ftmo.com");
        loaded.Port.Should().Be(443);
        loaded.Role.Should().Be(AccountRole.Master);
        loaded.BrokerLabel.Should().Be("FTMO");
        loaded.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Upsert_TwiceWithSameId_Updates()
    {
        using var db = LtcDatabase.OpenInMemory();
        var acc = new Account { DisplayName = "Original", Login = 1, Server = "x" };
        db.Accounts.Upsert(acc);

        acc.DisplayName = "Renamed";
        acc.Enabled = false;
        db.Accounts.Upsert(acc);

        var all = db.Accounts.GetAll();
        all.Should().ContainSingle();
        all[0].DisplayName.Should().Be("Renamed");
        all[0].Enabled.Should().BeFalse();
    }

    [Fact]
    public void GetById_ReturnsNullForUnknownId()
    {
        using var db = LtcDatabase.OpenInMemory();
        db.Accounts.GetById(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void GetById_ReturnsAccount()
    {
        using var db = LtcDatabase.OpenInMemory();
        var acc = new Account { DisplayName = "X", Login = 99, Server = "y" };
        db.Accounts.Upsert(acc);

        var loaded = db.Accounts.GetById(acc.Id);
        loaded.Should().NotBeNull();
        loaded!.Login.Should().Be(99u);
    }

    [Fact]
    public void Delete_RemovesAccount()
    {
        using var db = LtcDatabase.OpenInMemory();
        var acc = new Account { DisplayName = "X", Login = 1, Server = "y" };
        db.Accounts.Upsert(acc);
        db.Accounts.Delete(acc.Id);
        db.Accounts.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void PasswordIsProtectedThroughTheStorageLayer()
    {
        // Use a "fake DPAPI" that wraps the value with markers — verifies the protector
        // is consulted on both write and read paths.
        var protector = new MarkerProtector();
        using var db = LtcDatabase.OpenInMemory(protector);

        db.Accounts.Upsert(new Account { DisplayName = "X", Login = 1,
            Password = "hello", Server = "y" });
        var loaded = db.Accounts.GetAll().Single();

        loaded.Password.Should().Be("hello");
        protector.Protected.Should().Contain("hello");
        protector.Unprotected.Should().Contain("[ENC:hello]");
    }

    private sealed class MarkerProtector : ICredentialProtector
    {
        public List<string> Protected { get; } = new();
        public List<string> Unprotected { get; } = new();
        public string Protect(string s) { Protected.Add(s); return $"[ENC:{s}]"; }
        public string Unprotect(string s)
        {
            Unprotected.Add(s);
            return s.StartsWith("[ENC:") && s.EndsWith("]")
                ? s.Substring(5, s.Length - 6)
                : s;
        }
    }
}
