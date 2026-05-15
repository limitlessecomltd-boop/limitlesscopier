using System.Text.Json;
using System.Text.Json.Serialization;
using LTC.Core;
using LTC.Core.Models;
using LTC.Persistence;
using LTC.Persistence.Encryption;
using Microsoft.Extensions.Logging;

namespace LTC.ConsoleTest;

/// <summary>
/// Console runner. Two modes:
///   1. Default — reads accounts.json from disk (gitignored), drives the engine.
///   2. --persist — loads from the SQLite database at %LOCALAPPDATA%\LimitlessTradeCopier\ltc.db.
///                  Use --import accounts.json to seed the DB from a JSON file.
/// </summary>
internal class Program
{
    static async Task<int> Main(string[] args)
    {
        bool persist = args.Contains("--persist");
        bool verbose = args.Contains("--verbose") || args.Contains("-v");
        string? importFrom = ArgValue(args, "--import");

        using var loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));
        var logger = loggerFactory.CreateLogger("LTC");

        // Decide source of accounts and links
        IReadOnlyList<Account> accounts;
        IReadOnlyList<CopyLink> links;
        LtcDatabase? db = null;

        if (persist)
        {
            // SQLite path. Use DPAPI on Windows; tests use NoOp.
            ICredentialProtector protector = OperatingSystem.IsWindows()
                ? new DpapiCredentialProtector()
                : new NoOpCredentialProtector();

            var dbPath = LtcDatabase.DefaultPath();
            logger.LogInformation("Persistence: SQLite at {Path}", dbPath);
            db = new LtcDatabase(dbPath, protector);

            if (!string.IsNullOrEmpty(importFrom))
            {
                if (!File.Exists(importFrom))
                {
                    Console.WriteLine($"Import file not found: {importFrom}");
                    return 2;
                }
                Console.WriteLine($"Importing from {importFrom} into the database...");
                var (impAccounts, impLinks) = LoadFromJson(importFrom, logger);
                foreach (var a in impAccounts) db.Accounts.Upsert(a);
                foreach (var l in impLinks) db.Links.Upsert(l);
                Console.WriteLine($"Imported {impAccounts.Count} accounts and {impLinks.Count} links.");
            }

            var snap = new PersistedConfig(db).LoadAll();
            accounts = snap.Accounts;
            links = snap.Links;
            if (accounts.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine("Database is empty. Run with --import accounts.json first, or add accounts via the UI.");
                Console.WriteLine();
                db.Dispose();
                return 1;
            }
        }
        else
        {
            // JSON path
            string configPath = args.FirstOrDefault(a => !a.StartsWith("-"))
                ?? Environment.GetEnvironmentVariable("LTC_CONFIG")
                ?? Path.Combine(AppContext.BaseDirectory, "accounts.json");

            if (!File.Exists(configPath))
            {
                WriteSampleConfig(configPath);
                Console.WriteLine();
                Console.WriteLine($"Wrote sample config to: {configPath}");
                Console.WriteLine("Edit it with real credentials, then re-run.");
                Console.WriteLine();
                Console.WriteLine("WARNING: this file contains plaintext passwords. Do NOT commit it.");
                return 1;
            }

            (accounts, links) = LoadFromJson(configPath, logger);
        }

        // Build and run the engine
        await using var engine = new CopierEngine(logger);
        foreach (var a in accounts) engine.AddAccount(a);
        foreach (var l in links) engine.AddLink(l);

        engine.Activity.EntryChanged += (_, e) =>
        {
            string lat = e.InternalLatencyMicros > 0
                ? $" {e.InternalLatencyMicros / 1000.0:F2}ms"
                : "";
            string status = e.Status switch
            {
                ActivityStatus.Success  => "OK",
                ActivityStatus.Failed   => "FAIL",
                ActivityStatus.Skipped  => "SKIP",
                ActivityStatus.InFlight => "...",
                _                       => "?"
            };
            string ms = e.MasterAccountLabel ?? "?";
            string sl = e.SlaveAccountLabel ?? "?";
            string sym = e.Symbol ?? "";
            string ot = e.OrderType ?? "";
            string err = e.ErrorMessage is null ? "" : $"  err={e.ErrorMessage}";
            Console.WriteLine($"[{e.TimestampUtc:HH:mm:ss.fff}] {e.Kind,-10} {status,-4} {ms} → {sl}  {sym} {ot} {e.Volume:F2}{lat}{err}");
        };

        Console.WriteLine();
        Console.WriteLine($"=== LTC Engine running ({accounts.Count} accounts, {links.Count} links). Press Q to quit. ===");
        Console.WriteLine();

        while (true)
        {
            if (Console.KeyAvailable)
            {
                var k = Console.ReadKey(intercept: true);
                if (k.Key == ConsoleKey.Q) break;
                if (k.Key == ConsoleKey.S) PrintStatus(engine);
            }
            await Task.Delay(100);
        }

        Console.WriteLine();
        Console.WriteLine("Shutting down...");
        db?.Dispose();
        return 0;
    }

    // -------- Helpers --------
    private static string? ArgValue(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx < args.Length - 1 ? args[idx + 1] : null;
    }

    private static (IReadOnlyList<Account>, IReadOnlyList<CopyLink>) LoadFromJson(string path, ILogger logger)
    {
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<JsonConfig>(json, JsonOpts)!;
        var accountByKey = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in config.Accounts)
        {
            var account = new Account
            {
                DisplayName = a.Name,
                Login = a.Login,
                Password = a.Password,
                Server = a.Server,
                Port = a.Port,
                Role = Enum.TryParse<AccountRole>(a.Role, true, out var r) ? r : AccountRole.Slave,
                BrokerLabel = a.Broker,
            };
            accountByKey[a.Key] = account;
        }

        var links = new List<CopyLink>();
        foreach (var l in config.Links ?? new List<JsonLink>())
        {
            if (!accountByKey.TryGetValue(l.Master, out var master) ||
                !accountByKey.TryGetValue(l.Slave, out var slave))
            {
                logger.LogWarning("Link references unknown account key (master={Master} slave={Slave}); skipping",
                    l.Master, l.Slave);
                continue;
            }
            links.Add(new CopyLink
            {
                MasterAccountId = master.Id,
                SlaveAccountId = slave.Id,
                LotSizing = new LotSizingConfig
                {
                    Mode = Enum.TryParse<LotSizingMode>(l.LotMode, true, out var m) ? m : LotSizingMode.Multiplier,
                    Value = l.LotValue,
                    MinLot = l.MinLot,
                    MaxLot = l.MaxLot,
                },
                ReverseCopy = l.Reverse,
                CopyPending = l.CopyPending,
                CopySLTP = l.CopySLTP,
            });
        }

        return (accountByKey.Values.ToList(), links);
    }

    private static void PrintStatus(CopierEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("=== STATUS ===");
        foreach (var c in engine.Connections.Connections)
        {
            Console.WriteLine($"  {c.Account.DisplayName,-25} {c.Status,-15} login={c.Account.Login} symbols={c.AvailableSymbols.Count}");
        }
        Console.WriteLine();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static void WriteSampleConfig(string path)
    {
        var sample = new JsonConfig
        {
            Accounts = new List<JsonAccount>
            {
                new() { Key = "master1", Name = "FTMO Challenge", Role = "Master",
                        Login = 5005878, Password = "REPLACE_ME",
                        Server = "access.metatrader5.com", Port = 443,
                        Broker = "IC Markets" },
                new() { Key = "slave1", Name = "Live A", Role = "Slave",
                        Login = 16053, Password = "REPLACE_ME",
                        Server = "157.175.246.82", Port = 443,
                        Broker = "Exness" },
            },
            Links = new List<JsonLink>
            {
                new() { Master = "master1", Slave = "slave1",
                        LotMode = "Multiplier", LotValue = 0.5,
                        CopyPending = true, CopySLTP = true,
                        MinLot = 0.01, MaxLot = 100.0 }
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(sample, JsonOpts));
    }

    // -------- JSON config types --------
    private sealed class JsonConfig
    {
        public bool Verbose { get; set; }
        public List<JsonAccount> Accounts { get; set; } = new();
        public List<JsonLink>? Links { get; set; }
    }
    private sealed class JsonAccount
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Role { get; set; } = "Slave";
        public ulong Login { get; set; }
        public string Password { get; set; } = "";
        public string Server { get; set; } = "";
        public int Port { get; set; } = 443;
        public string? Broker { get; set; }
    }
    private sealed class JsonLink
    {
        public string Master { get; set; } = "";
        public string Slave { get; set; } = "";
        public string LotMode { get; set; } = "Multiplier";
        public double LotValue { get; set; } = 1.0;
        public double MinLot { get; set; } = 0.01;
        public double MaxLot { get; set; } = 100.0;
        public bool Reverse { get; set; } = false;
        public bool CopyPending { get; set; } = true;
        public bool CopySLTP { get; set; } = true;
    }
}
