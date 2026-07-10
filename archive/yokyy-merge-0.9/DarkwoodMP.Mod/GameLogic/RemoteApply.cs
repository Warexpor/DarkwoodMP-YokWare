using UnityEngine;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Re-entrancy guard: set while applying a remote state change to the local game
/// so the Harmony patches don't send the change right back to the network.
/// Depth-counted (Horde NetworkApplyGuard style): nested Active=true / Active=false
/// pairs do not clear an outer receive scope early.
/// </summary>
public static class RemoteApply
{
    private static int _applyDepth;

    /// <summary>
    /// True while any apply scope is open. Setter pushes/pops depth so existing
    /// <c>Active = true; try/finally Active = false</c> nests correctly under
    /// <see cref="Network.NetworkApplyGuard"/>.
    /// </summary>
    public static bool Active
    {
        get => _applyDepth > 0;
        set
        {
            if (value) PushApply();
            else PopApply();
        }
    }

    public static void PushApply() => _applyDepth++;

    public static void PopApply()
    {
        if (_applyDepth > 0)
            _applyDepth--;
    }

    public static void ResetApplyDepth()
    {
        _applyDepth = 0;
        AllowInventoryGrant = false;
        BroadcastFxFromApply = false;
    }

    /// <summary>
    /// While a remote change replays, Inventory.addItemTypeToPlayer is skipped
    /// (no phantom item grants). Journal-item replays opt back in: quest items
    /// and keys SHOULD land in both players' inventories.
    /// </summary>
    public static bool AllowInventoryGrant;

    /// <summary>
    /// Opt-in override for BulletFX_Patch: set (together with Active) while
    /// the AUTHORITY applies a client-forwarded enemy hit (EnemySync.
    /// ApplyRemoteAttack). The blanket Active suppression is correct for every
    /// other apply context (bulletfx replays must not loop, mirror-side die()
    /// gore must not echo back), but the blood born from an applied remote hit
    /// is AUTHORITATIVE first-hand FX - without this flag the attacking client
    /// never saw blood for its own hits.
    /// </summary>
    public static bool BroadcastFxFromApply;

    // Objects WE spawned as mirrors of a partner's world (remote throws, flare
    // light mirrors). Their own Start/Awake hooks fire a frame later - long
    // after Active was cleared - so patches that broadcast on component birth
    // (Flare_Patch) consult this to avoid re-broadcasting a mirror back.
    private static readonly System.Collections.Generic.HashSet<int> _remoteSpawnedRoots = new();

    public static void MarkRemoteSpawned(GameObject go)
    {
        if (go == null) return;
        if (_remoteSpawnedRoots.Count > 1024) _remoteSpawnedRoots.Clear(); // bound
        _remoteSpawnedRoots.Add(go.GetInstanceID());
    }

    public static bool IsRemoteSpawned(GameObject go)
        => go != null && (_remoteSpawnedRoots.Contains(go.GetInstanceID())
            || (go.transform.root != null && _remoteSpawnedRoots.Contains(go.transform.root.gameObject.GetInstanceID())));

    /// <summary>Clear mirror-spawn tracking (session stop).</summary>
    public static void ClearRemoteSpawned() => _remoteSpawnedRoots.Clear();
}

/// <summary>
/// Builds stable cross-client IDs for world objects. Both clients run the same
/// world, so "name + rounded position" identifies the same door/lever/etc. on
/// every machine (Unity instance IDs differ per process and can't be used).
/// </summary>
public static class GameIds
{
    public static string ForComponent(Component c)
    {
        var p = c.transform.position;
        // Darkwood's ground plane is X/Z (Y is height, ~constant) - encoding
        // x,y would make all objects in a column share the same id
        return $"{c.gameObject.name}@{p.x:F0},{p.z:F0}";
    }

    /// <summary>
    /// Find the component matching a "name@x,z" id: exact id first, then the
    /// nearest same-named candidate within 10m (worlds can differ slightly).
    /// </summary>
    public static Component FindByGameId(UnityEngine.Object[] candidates, string objectId)
    {
        var at = objectId.LastIndexOf('@');
        var name = at > 0 ? objectId.Substring(0, at) : null;
        float x = 0f, z = 0f;
        var hasCoords = false;
        if (at > 0)
        {
            var coords = objectId.Substring(at + 1).Split(',');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            hasCoords = coords.Length == 2
                && float.TryParse(coords[0], System.Globalization.NumberStyles.Float, inv, out x)
                && float.TryParse(coords[1], System.Globalization.NumberStyles.Float, inv, out z);
        }

        Component best = null;
        var bestDist = 10f * 10f;
        foreach (var obj in candidates)
        {
            if (obj is not Component c) continue;
            if (ForComponent(c) == objectId) return c;

            if (!hasCoords || c.gameObject.name != name) continue;
            var p = c.transform.position;
            var dx = p.x - x;
            var dz = p.z - z;
            var d = dx * dx + dz * dz;
            if (d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }
        return best;
    }
}
