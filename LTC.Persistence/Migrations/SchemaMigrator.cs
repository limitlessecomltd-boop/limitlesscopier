using Microsoft.Data.Sqlite;

namespace LTC.Persistence.Migrations;

/// <summary>
/// Runs schema migrations against a SQLite database. Idempotent — calling
/// <see cref="Apply"/> on an already-current database is a no-op.
/// </summary>
public static class SchemaMigrator
{
    /// <summary>The current schema version. Bump when adding a migration below.</summary>
    public const int CurrentVersion = 3;

    /// <summary>Apply all pending migrations to the open connection.</summary>
    public static void Apply(SqliteConnection connection)
    {
        // Pragmas: reasonable defaults for a desktop app where we own the file.
        ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL;");
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");

        // Read the user_version pragma — this is our migration counter.
        int version = GetUserVersion(connection);

        if (version < 1) ApplyV1(connection);
        if (version < 2) ApplyV2(connection);
        if (version < 3) ApplyV3(connection);

        // Final: write the new version
        if (version < CurrentVersion)
            ExecuteNonQuery(connection, $"PRAGMA user_version = {CurrentVersion};");
    }

    private static int GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var v = cmd.ExecuteScalar();
        return v is null ? 0 : Convert.ToInt32(v);
    }

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // -------- Migration v1: initial schema --------
    private static void ApplyV1(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();

        ExecuteNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS accounts (
                id                  TEXT PRIMARY KEY NOT NULL,
                display_name        TEXT NOT NULL,
                login               INTEGER NOT NULL,
                password_protected  TEXT NOT NULL,            -- DPAPI-encrypted, base64
                server              TEXT NOT NULL,
                port                INTEGER NOT NULL DEFAULT 443,
                role                TEXT NOT NULL,             -- 'Master' | 'Slave'
                broker_label        TEXT NULL,
                enabled             INTEGER NOT NULL DEFAULT 1,
                created_at          TEXT NOT NULL,             -- ISO 8601 UTC
                last_connected_at   TEXT NULL
            );
        """);

        ExecuteNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS copy_links (
                id                   TEXT PRIMARY KEY NOT NULL,
                master_account_id    TEXT NOT NULL,
                slave_account_id     TEXT NOT NULL,
                enabled              INTEGER NOT NULL DEFAULT 1,
                lot_sizing_mode      TEXT NOT NULL,            -- enum string
                lot_sizing_value     REAL NOT NULL,
                lot_sizing_min       REAL NOT NULL DEFAULT 0,
                lot_sizing_max       REAL NOT NULL DEFAULT 0,
                reverse_copy         INTEGER NOT NULL DEFAULT 0,
                copy_pending         INTEGER NOT NULL DEFAULT 1,
                copy_sl_tp           INTEGER NOT NULL DEFAULT 1,
                copy_modifications   INTEGER NOT NULL DEFAULT 1,
                max_slippage_points  INTEGER NOT NULL DEFAULT 100,
                filter_json          TEXT NULL,                -- serialized CopyFilter
                symbol_map_json      TEXT NULL,                -- serialized Dictionary<string,string>
                FOREIGN KEY (master_account_id) REFERENCES accounts(id) ON DELETE CASCADE,
                FOREIGN KEY (slave_account_id)  REFERENCES accounts(id) ON DELETE CASCADE
            );
        """);

        ExecuteNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS app_settings (
                key   TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL
            );
        """);

        ExecuteNonQuery(conn, """
            CREATE INDEX IF NOT EXISTS idx_copy_links_master
              ON copy_links(master_account_id);
        """);

        tx.Commit();
    }

    // -------- Migration v2: symbol_prefix / symbol_suffix on accounts --------
    private static void ApplyV2(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        // SQLite ALTER TABLE ADD COLUMN — safe and idempotent because v2 is
        // applied only when user_version < 2.
        ExecuteNonQuery(conn,
            "ALTER TABLE accounts ADD COLUMN symbol_prefix TEXT NOT NULL DEFAULT '';");
        ExecuteNonQuery(conn,
            "ALTER TABLE accounts ADD COLUMN symbol_suffix TEXT NOT NULL DEFAULT '';");
        tx.Commit();
    }

    // -------- Migration v3: account kind + prop firm config --------
    //
    // Adds:
    //   - accounts.kind                 ('Personal' | 'PropChallenge' | 'PropFunded')
    //   - accounts.prop_config_json     (JSON-serialized PropFirmConfig, NULL for Personal)
    //
    // We chose a JSON column instead of normalizing the prop config into
    // a separate table because:
    //   1) The shape of PropFirmConfig will likely grow (we may add
    //      news-blackout windows, consistency rules, etc.) and JSON
    //      avoids a schema migration every time;
    //   2) It's always loaded together with its parent account row, so
    //      there's no query benefit to splitting it out;
    //   3) The total payload is small (few hundred bytes per account).
    //
    // The trade-off: SQLite can't enforce structural constraints on the
    // JSON, so the repository layer must validate it on read. We accept
    // that — corruption here is a recoverable "show the trader the
    // AddAccount dialog with defaults and let them re-enter" situation,
    // not data loss.
    private static void ApplyV3(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        ExecuteNonQuery(conn,
            "ALTER TABLE accounts ADD COLUMN kind TEXT NOT NULL DEFAULT 'Personal';");
        ExecuteNonQuery(conn,
            "ALTER TABLE accounts ADD COLUMN prop_config_json TEXT NULL;");
        tx.Commit();
    }
}
