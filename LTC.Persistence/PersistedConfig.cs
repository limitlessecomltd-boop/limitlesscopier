using System.Text.Json;
using LTC.Core.Models;

namespace LTC.Persistence;

/// <summary>
/// Convenience facade over the LtcDatabase repositories. Loads the engine's full
/// persisted state in one call (accounts + links) and saves changes back.
/// </summary>
public sealed class PersistedConfig
{
    private readonly LtcDatabase _db;

    public PersistedConfig(LtcDatabase db) { _db = db; }

    /// <summary>Load all persisted accounts and links.</summary>
    public PersistedSnapshot LoadAll()
    {
        var accounts = _db.Accounts.GetAll();
        var links = _db.Links.GetAll();
        return new PersistedSnapshot(accounts, links);
    }

    public void SaveAccount(Account account) => _db.Accounts.Upsert(account);
    public void DeleteAccount(Guid id)
    {
        // Cascading FK will remove dependent links; we still delete explicitly to be safe
        // in case the FK pragma was somehow off.
        var deps = _db.Links.GetAll().Where(l => l.MasterAccountId == id || l.SlaveAccountId == id);
        foreach (var l in deps) _db.Links.Delete(l.Id);
        _db.Accounts.Delete(id);
    }

    public void SaveLink(CopyLink link) => _db.Links.Upsert(link);
    public void DeleteLink(Guid linkId) => _db.Links.Delete(linkId);

    // -----------------------------------------------------------------
    // Daily-anchor persistence
    //
    // We piggyback on the existing app_settings key/value store so we
    // don't need a schema migration. One row per account, key shape
    // "anchor:<login>", value is JSON-serialized PropDailyAnchor.
    //
    // Why persist:
    //   - If the user closes the app at 14:00 having lost $300 today
    //     and reopens at 16:00, we want today's anchor (the equity at
    //     this morning's 00:00 UTC reset) to come back from disk
    //     instead of getting reset to "current equity at 16:00" — which
    //     would hide the $300 already lost.
    //   - The recorder calls SaveAnchor() on first-ever record AND on
    //     each reset rollover. Mid-day HWM updates are NOT persisted
    //     (would hammer SQLite); HWM gets re-discovered on the next
    //     write event.
    // -----------------------------------------------------------------

    private const string AnchorKeyPrefix = "anchor:";

    public void SaveAnchor(PropDailyAnchor anchor)
    {
        var key = AnchorKeyPrefix + anchor.AccountLogin;
        var value = JsonSerializer.Serialize(anchor);
        _db.Settings.Set(key, value);
    }

    public IReadOnlyList<PropDailyAnchor> LoadAllAnchors()
    {
        var result = new List<PropDailyAnchor>();
        foreach (var kvp in _db.Settings.GetAll())
        {
            if (!kvp.Key.StartsWith(AnchorKeyPrefix, StringComparison.Ordinal)) continue;
            try
            {
                var parsed = JsonSerializer.Deserialize<PropDailyAnchor>(kvp.Value);
                if (parsed is not null) result.Add(parsed);
            }
            catch
            {
                // Don't crash app start on a corrupt anchor row — the recorder
                // will retro-anchor cleanly on next equity update.
            }
        }
        return result;
    }
}

public sealed record PersistedSnapshot(IReadOnlyList<Account> Accounts, IReadOnlyList<CopyLink> Links);
