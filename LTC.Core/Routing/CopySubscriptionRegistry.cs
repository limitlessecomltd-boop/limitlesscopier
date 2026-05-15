using System.Collections.Immutable;
using LTC.Core.Models;

namespace LTC.Core.Routing;

/// <summary>
/// Holds the active set of CopyLinks indexed for very fast fan-out by master account id.
/// Mutations (add/remove link) replace the underlying immutable map with a new version,
/// so readers on the hot path never take a lock.
/// </summary>
public sealed class CopySubscriptionRegistry
{
    private ImmutableDictionary<Guid, ImmutableArray<CopyLink>> _byMaster
        = ImmutableDictionary<Guid, ImmutableArray<CopyLink>>.Empty;

    /// <summary>O(1) lookup of all links for a given master, with no locking.</summary>
    public ImmutableArray<CopyLink> LinksForMaster(Guid masterAccountId)
    {
        return _byMaster.TryGetValue(masterAccountId, out var arr) ? arr : ImmutableArray<CopyLink>.Empty;
    }

    /// <summary>Replace the entire link set. Atomic from any reader's perspective.</summary>
    public void ReplaceAll(IEnumerable<CopyLink> links)
    {
        var builder = ImmutableDictionary.CreateBuilder<Guid, ImmutableArray<CopyLink>>();
        foreach (var grp in links.Where(l => l.Enabled).GroupBy(l => l.MasterAccountId))
            builder[grp.Key] = grp.ToImmutableArray();
        Volatile.Write(ref _byMaster, builder.ToImmutable());
    }

    /// <summary>Add or update a single link.</summary>
    public void Upsert(CopyLink link)
    {
        ImmutableDictionary<Guid, ImmutableArray<CopyLink>> oldDict, newDict;
        do
        {
            oldDict = Volatile.Read(ref _byMaster);
            var existing = oldDict.TryGetValue(link.MasterAccountId, out var arr) ? arr : ImmutableArray<CopyLink>.Empty;
            var replaced = ImmutableArray.CreateBuilder<CopyLink>();
            bool found = false;
            foreach (var l in existing)
            {
                if (l.Id == link.Id) { replaced.Add(link); found = true; }
                else replaced.Add(l);
            }
            if (!found) replaced.Add(link);
            newDict = oldDict.SetItem(link.MasterAccountId, replaced.ToImmutable());
        } while (Interlocked.CompareExchange(ref _byMaster, newDict, oldDict) != oldDict);
    }

    /// <summary>Remove a link by id. No-op if not present.</summary>
    public void Remove(Guid linkId)
    {
        ImmutableDictionary<Guid, ImmutableArray<CopyLink>> oldDict, newDict;
        do
        {
            oldDict = Volatile.Read(ref _byMaster);
            var builder = oldDict.ToBuilder();
            bool changed = false;
            foreach (var key in oldDict.Keys.ToArray())
            {
                var arr = oldDict[key];
                var filtered = arr.Where(l => l.Id != linkId).ToImmutableArray();
                if (filtered.Length != arr.Length)
                {
                    if (filtered.IsEmpty) builder.Remove(key);
                    else builder[key] = filtered;
                    changed = true;
                }
            }
            if (!changed) return;
            newDict = builder.ToImmutable();
        } while (Interlocked.CompareExchange(ref _byMaster, newDict, oldDict) != oldDict);
    }
}
