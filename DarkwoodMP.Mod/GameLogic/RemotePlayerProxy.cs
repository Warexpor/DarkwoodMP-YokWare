using UnityEngine;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Lightweight identity + control flags on a remote player clone (Horde proxy spine).
/// Full Horde anim controller ports later; tags the GO, freeze (dreams), and corpse state.
/// </summary>
public sealed class RemotePlayerProxy : MonoBehaviour
{
    /// <summary>Stable network PlayerId this visual represents.</summary>
    public int PlayerId { get; set; } = -1;

    public string DisplayName { get; set; } = "Player";

    /// <summary>
    /// When true, <see cref="PlayerSync"/> skips driving this transform
    /// (dream load / freeze windows / corpse).
    /// </summary>
    public bool FreezePosition { get; set; }

    public bool IsDead { get; private set; }

    public bool RemoteInvisible { get; set; }
    public bool RemoteIgnoreMe { get; set; }
    public bool RemotePoisoned { get; set; }
    public bool RemoteBleeding { get; set; }

    /// <summary>Last applied network pose (for other systems).</summary>
    public Vector3 LastNetworkPos { get; set; }

    public float LastNetworkRotY { get; set; }

    public void ApplyNetworkHint(Vector3 pos, float rotY)
    {
        LastNetworkPos = pos;
        LastNetworkRotY = rotY;
    }

    /// <summary>
    /// Partner died: stop being a living hittable clone (Horde HandlePlayerDied).
    /// </summary>
    public void ApplyDeathState(Vector3? deathPos = null)
    {
        IsDead = true;
        FreezePosition = true;
        if (deathPos.HasValue)
        {
            transform.position = deathPos.Value;
            LastNetworkPos = deathPos.Value;
        }

        // CharBase alive flag if present (clone may keep disabled Player but CharBase)
        try
        {
            var cb = GetComponent<CharBase>() ?? GetComponentInChildren<CharBase>();
            if (cb != null)
            {
                cb.alive = false;
                cb.isActive = false;
            }
        }
        catch { /* type may differ */ }

        foreach (var col in GetComponentsInChildren<Collider>(true))
        {
            if (col != null) col.enabled = false;
        }

        // Soft visual: darken / leave body in place (anim death clip is Phase 4b)
        foreach (var r in GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            try
            {
                foreach (var mat in r.materials)
                {
                    if (mat != null && mat.HasProperty("_Color"))
                        mat.color = Color.Lerp(mat.color, Color.black, 0.45f);
                }
            }
            catch { /* material instance may fail */ }
        }
    }

    /// <summary>Partner respawned / rejoined — allow pose drive again.</summary>
    public void ClearDeathState()
    {
        IsDead = false;
        FreezePosition = false;
        foreach (var col in GetComponentsInChildren<Collider>(true))
        {
            if (col != null) col.enabled = true;
        }
    }
}
