using Dapper;
using Microsoft.Data.Sqlite;

namespace LTC.Persistence.Repositories;

/// <summary>
/// Tiny key/value store for app-level settings (window size, last-used config, etc).
/// Values are strings; callers serialize/parse as needed.
/// </summary>
public sealed class SettingsRepository
{
    private readonly SqliteConnection _conn;

    public SettingsRepository(SqliteConnection conn) { _conn = conn; }

    public string? Get(string key)
        => _conn.QuerySingleOrDefault<string>(
            "SELECT value FROM app_settings WHERE key = @K",
            new { K = key });

    public void Set(string key, string value)
    {
        _conn.Execute("""
            INSERT INTO app_settings (key, value) VALUES (@K, @V)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
        """, new { K = key, V = value });
    }

    public void Delete(string key)
        => _conn.Execute("DELETE FROM app_settings WHERE key = @K", new { K = key });

    public IReadOnlyDictionary<string, string> GetAll()
    {
        var rows = _conn.Query<(string Key, string Value)>(
            "SELECT key, value FROM app_settings").ToList();
        return rows.ToDictionary(r => r.Key, r => r.Value);
    }
}
