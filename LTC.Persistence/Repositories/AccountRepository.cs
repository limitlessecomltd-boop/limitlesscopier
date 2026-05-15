using Dapper;
using LTC.Core.Models;
using LTC.Persistence.Encryption;
using Microsoft.Data.Sqlite;

namespace LTC.Persistence.Repositories;

/// <summary>
/// CRUD for Account records. Passwords are encrypted via <see cref="ICredentialProtector"/>
/// before being written and decrypted on load.
/// </summary>
public sealed class AccountRepository
{
    private readonly SqliteConnection _conn;
    private readonly ICredentialProtector _protector;

    public AccountRepository(SqliteConnection conn, ICredentialProtector protector)
    {
        _conn = conn;
        _protector = protector;
    }

    public IReadOnlyList<Account> GetAll()
    {
        var rows = _conn.Query<AccountRow>(
            "SELECT * FROM accounts ORDER BY display_name COLLATE NOCASE").ToList();
        return rows.Select(ToDomain).ToList();
    }

    public Account? GetById(Guid id)
    {
        var row = _conn.QuerySingleOrDefault<AccountRow>(
            "SELECT * FROM accounts WHERE id = @Id",
            new { Id = id.ToString() });
        return row is null ? null : ToDomain(row);
    }

    public void Upsert(Account account)
    {
        var protectedPwd = _protector.Protect(account.Password ?? "");
        // Serialize the prop firm config to JSON. Null for personal accounts —
        // that's how the DB distinguishes (kind column also says so, but the
        // null JSON saves a few bytes and is easier to spot in db dumps).
        string? propJson = account.PropConfig is null ? null
            : System.Text.Json.JsonSerializer.Serialize(account.PropConfig);
        _conn.Execute("""
            INSERT INTO accounts
              (id, display_name, login, password_protected, server, port, role, broker_label, enabled, created_at, last_connected_at, symbol_prefix, symbol_suffix, kind, prop_config_json)
            VALUES
              (@Id, @DisplayName, @Login, @Pwd, @Server, @Port, @Role, @Broker, @Enabled, @CreatedAt, @LastConnectedAt, @Prefix, @Suffix, @Kind, @PropJson)
            ON CONFLICT(id) DO UPDATE SET
              display_name      = excluded.display_name,
              login             = excluded.login,
              password_protected= excluded.password_protected,
              server            = excluded.server,
              port              = excluded.port,
              role              = excluded.role,
              broker_label      = excluded.broker_label,
              enabled           = excluded.enabled,
              last_connected_at = excluded.last_connected_at,
              symbol_prefix     = excluded.symbol_prefix,
              symbol_suffix     = excluded.symbol_suffix,
              kind              = excluded.kind,
              prop_config_json  = excluded.prop_config_json;
        """, new
        {
            Id = account.Id.ToString(),
            account.DisplayName,
            Login = (long)account.Login,
            Pwd = protectedPwd,
            account.Server,
            account.Port,
            Role = account.Role.ToString(),
            Broker = account.BrokerLabel,
            Enabled = account.Enabled ? 1 : 0,
            CreatedAt = account.CreatedAt.ToString("O"),
            LastConnectedAt = account.LastConnectedAt?.ToString("O"),
            Prefix = account.SymbolPrefix ?? "",
            Suffix = account.SymbolSuffix ?? "",
            Kind = account.Kind.ToString(),
            PropJson = propJson,
        });
    }

    public void Delete(Guid id)
    {
        _conn.Execute("DELETE FROM accounts WHERE id = @Id", new { Id = id.ToString() });
    }

    private Account ToDomain(AccountRow row)
    {
        string password = "";
        try { password = _protector.Unprotect(row.password_protected); }
        catch { /* leave empty if decryption fails — user will have to re-enter */ }

        // Deserialize the prop firm config if present. Defensive: if the
        // JSON is corrupt we log nothing and treat the account as personal —
        // the trader will see the AddAccount dialog with defaults next time
        // they edit this account, which is the least-surprising recovery.
        PropFirmConfig? propConfig = null;
        if (!string.IsNullOrWhiteSpace(row.prop_config_json))
        {
            try
            {
                propConfig = System.Text.Json.JsonSerializer.Deserialize<PropFirmConfig>(row.prop_config_json);
            }
            catch { /* swallowed: account stays "personal" until re-edited */ }
        }

        return new Account
        {
            Id = Guid.Parse(row.id),
            DisplayName = row.display_name,
            Login = (ulong)row.login,
            Password = password,
            Server = row.server,
            Port = row.port,
            Role = Enum.TryParse<AccountRole>(row.role, true, out var r) ? r : AccountRole.Slave,
            BrokerLabel = row.broker_label,
            Enabled = row.enabled != 0,
            SymbolPrefix = row.symbol_prefix ?? "",
            SymbolSuffix = row.symbol_suffix ?? "",
            Kind = Enum.TryParse<AccountKind>(row.kind, true, out var k) ? k : AccountKind.Personal,
            PropConfig = propConfig,
            CreatedAt = DateTime.TryParse(row.created_at, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var c) ? c : DateTime.UtcNow,
            LastConnectedAt = string.IsNullOrEmpty(row.last_connected_at) ? null
                : (DateTime.TryParse(row.last_connected_at, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var lc) ? lc : null),
        };
    }

    /// <summary>Internal row type matching the table — Dapper maps by snake_case → property.</summary>
    private sealed class AccountRow
    {
        public string id { get; set; } = "";
        public string display_name { get; set; } = "";
        public long login { get; set; }
        public string password_protected { get; set; } = "";
        public string server { get; set; } = "";
        public int port { get; set; }
        public string role { get; set; } = "Slave";
        public string? broker_label { get; set; }
        public int enabled { get; set; }
        public string created_at { get; set; } = "";
        public string? last_connected_at { get; set; }
        public string? symbol_prefix { get; set; }
        public string? symbol_suffix { get; set; }
        public string? kind { get; set; }
        public string? prop_config_json { get; set; }
    }
}
