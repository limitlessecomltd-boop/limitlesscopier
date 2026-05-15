using System.Text.Json;
using Dapper;
using LTC.Core.Models;
using Microsoft.Data.Sqlite;

namespace LTC.Persistence.Repositories;

/// <summary>
/// CRUD for CopyLink records. The CopyFilter object and SymbolMapOverrides
/// dictionary are serialized as JSON columns to keep the schema simple and
/// allow filter evolution without migrations.
/// </summary>
public sealed class CopyLinkRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly SqliteConnection _conn;

    public CopyLinkRepository(SqliteConnection conn) { _conn = conn; }

    public IReadOnlyList<CopyLink> GetAll()
    {
        var rows = _conn.Query<LinkRow>("SELECT * FROM copy_links").ToList();
        return rows.Select(ToDomain).ToList();
    }

    public IReadOnlyList<CopyLink> GetByMaster(Guid masterAccountId)
    {
        var rows = _conn.Query<LinkRow>(
            "SELECT * FROM copy_links WHERE master_account_id = @M",
            new { M = masterAccountId.ToString() }).ToList();
        return rows.Select(ToDomain).ToList();
    }

    public void Upsert(CopyLink link)
    {
        _conn.Execute("""
            INSERT INTO copy_links
              (id, master_account_id, slave_account_id, enabled,
               lot_sizing_mode, lot_sizing_value, lot_sizing_min, lot_sizing_max,
               reverse_copy, copy_pending, copy_sl_tp, copy_modifications, max_slippage_points,
               filter_json, symbol_map_json)
            VALUES
              (@Id, @Master, @Slave, @Enabled,
               @LotMode, @LotValue, @LotMin, @LotMax,
               @Rev, @Pend, @SlTp, @Mod, @Slip,
               @Filter, @SymMap)
            ON CONFLICT(id) DO UPDATE SET
              master_account_id   = excluded.master_account_id,
              slave_account_id    = excluded.slave_account_id,
              enabled             = excluded.enabled,
              lot_sizing_mode     = excluded.lot_sizing_mode,
              lot_sizing_value    = excluded.lot_sizing_value,
              lot_sizing_min      = excluded.lot_sizing_min,
              lot_sizing_max      = excluded.lot_sizing_max,
              reverse_copy        = excluded.reverse_copy,
              copy_pending        = excluded.copy_pending,
              copy_sl_tp          = excluded.copy_sl_tp,
              copy_modifications  = excluded.copy_modifications,
              max_slippage_points = excluded.max_slippage_points,
              filter_json         = excluded.filter_json,
              symbol_map_json     = excluded.symbol_map_json;
        """, new
        {
            Id = link.Id.ToString(),
            Master = link.MasterAccountId.ToString(),
            Slave  = link.SlaveAccountId.ToString(),
            Enabled = link.Enabled ? 1 : 0,
            LotMode = link.LotSizing.Mode.ToString(),
            LotValue = link.LotSizing.Value,
            LotMin = link.LotSizing.MinLot,
            LotMax = link.LotSizing.MaxLot,
            Rev = link.ReverseCopy ? 1 : 0,
            Pend = link.CopyPending ? 1 : 0,
            SlTp = link.CopySLTP ? 1 : 0,
            Mod = link.CopyModifications ? 1 : 0,
            Slip = (long)link.MaxSlippagePoints,
            Filter = JsonSerializer.Serialize(link.Filter, JsonOpts),
            SymMap = link.SymbolMapOverrides is { Count: > 0 }
                ? JsonSerializer.Serialize(link.SymbolMapOverrides, JsonOpts)
                : null,
        });
    }

    public void Delete(Guid linkId)
    {
        _conn.Execute("DELETE FROM copy_links WHERE id = @Id",
            new { Id = linkId.ToString() });
    }

    private CopyLink ToDomain(LinkRow row)
    {
        // Filter
        var filter = new CopyFilter();
        if (!string.IsNullOrEmpty(row.filter_json))
        {
            // We deserialize into a transport DTO and copy fields back so that the
            // HashSets keep their OrdinalIgnoreCase comparer (JSON would otherwise give
            // us default case-sensitive sets).
            var dto = JsonSerializer.Deserialize<CopyFilter>(row.filter_json, JsonOpts);
            if (dto is not null)
            {
                if (dto.SymbolWhitelist is not null)
                    foreach (var s in dto.SymbolWhitelist) filter.SymbolWhitelist.Add(s);
                if (dto.SymbolBlacklist is not null)
                    foreach (var s in dto.SymbolBlacklist) filter.SymbolBlacklist.Add(s);
                filter.Direction = dto.Direction;
                filter.MaxLotPerTrade = dto.MaxLotPerTrade;
                filter.DailyLossLimit = dto.DailyLossLimit;
            }
        }

        var symMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(row.symbol_map_json))
        {
            var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(
                row.symbol_map_json, JsonOpts);
            if (deserialized is not null)
                foreach (var kvp in deserialized) symMap[kvp.Key] = kvp.Value;
        }

        return new CopyLink
        {
            Id = Guid.Parse(row.id),
            MasterAccountId = Guid.Parse(row.master_account_id),
            SlaveAccountId  = Guid.Parse(row.slave_account_id),
            Enabled = row.enabled != 0,
            LotSizing = new LotSizingConfig
            {
                Mode = Enum.TryParse<LotSizingMode>(row.lot_sizing_mode, true, out var m)
                    ? m : LotSizingMode.Multiplier,
                Value = row.lot_sizing_value,
                MinLot = row.lot_sizing_min,
                MaxLot = row.lot_sizing_max,
            },
            ReverseCopy = row.reverse_copy != 0,
            CopyPending = row.copy_pending != 0,
            CopySLTP = row.copy_sl_tp != 0,
            CopyModifications = row.copy_modifications != 0,
            MaxSlippagePoints = (ulong)row.max_slippage_points,
            Filter = filter,
            SymbolMapOverrides = symMap,
        };
    }

    private sealed class LinkRow
    {
        public string id { get; set; } = "";
        public string master_account_id { get; set; } = "";
        public string slave_account_id { get; set; } = "";
        public int enabled { get; set; }
        public string lot_sizing_mode { get; set; } = "Multiplier";
        public double lot_sizing_value { get; set; }
        public double lot_sizing_min { get; set; }
        public double lot_sizing_max { get; set; }
        public int reverse_copy { get; set; }
        public int copy_pending { get; set; }
        public int copy_sl_tp { get; set; }
        public int copy_modifications { get; set; }
        public long max_slippage_points { get; set; }
        public string? filter_json { get; set; }
        public string? symbol_map_json { get; set; }
    }
}
