using LTC.Persistence.Encryption;
using LTC.Persistence.Migrations;
using LTC.Persistence.Repositories;
using Microsoft.Data.Sqlite;

namespace LTC.Persistence;

/// <summary>
/// Owns the SQLite connection lifecycle and the repository instances that share it.
/// Disposable; close at app shutdown.
/// </summary>
public sealed class LtcDatabase : IDisposable
{
    public SqliteConnection Connection { get; }
    public AccountRepository Accounts { get; }
    public CopyLinkRepository Links { get; }
    public SettingsRepository Settings { get; }

    public LtcDatabase(string dbPath, ICredentialProtector protector)
    {
        // Ensure the directory exists if the user supplied a path with directories.
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        Connection = new SqliteConnection($"Data Source={dbPath}");
        Connection.Open();

        SchemaMigrator.Apply(Connection);

        Accounts = new AccountRepository(Connection, protector);
        Links    = new CopyLinkRepository(Connection);
        Settings = new SettingsRepository(Connection);
    }

    /// <summary>Open an in-memory database for tests.</summary>
    public static LtcDatabase OpenInMemory(ICredentialProtector? protector = null)
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        SchemaMigrator.Apply(conn);
        return new LtcDatabase(conn, protector ?? new NoOpCredentialProtector());
    }

    private LtcDatabase(SqliteConnection openConnection, ICredentialProtector protector)
    {
        Connection = openConnection;
        Accounts = new AccountRepository(Connection, protector);
        Links    = new CopyLinkRepository(Connection);
        Settings = new SettingsRepository(Connection);
    }

    /// <summary>Default DB path: %LOCALAPPDATA%\LimitlessTradeCopier\ltc.db</summary>
    public static string DefaultPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "LimitlessTradeCopier", "ltc.db");
    }

    public void Dispose()
    {
        try { Connection.Close(); } catch { /* tolerate */ }
        Connection.Dispose();
    }
}
