using System;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Patches;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Cosmetic firearm sync (receive side). Ported from the BepInEx mod's
/// WeaponFire/BulletFX patches, adapted to this mod's distributed model.
///
/// The DAMAGE half of ranged combat is ALREADY handled here:
///  - firearm hits on enemies go through Character.getHit (CharDamage_Patch),
///  - firearm hits on remote-player clones are recast + forwarded by
///    PvpHit_Patch.BulletPostfix.
/// What was missing is the VISUALS: the partner never saw your muzzle flash or
/// where your bullets landed. This module replays those FX from the broadcasts
/// sent by WeaponFire_Patch (muzzle) and BulletFX_Patch (impact/blood splats).
///
/// No friendly-fire is applied here on purpose: the shooter's PvpHit_Patch
/// already forwards ranged hits on clones, so applying it again on receive
/// would double the damage.
/// </summary>
public class RangedSync
{
    /// <summary>A partner fired a firearm - replay muzzle flash/particles on their clone.</summary>
    public void OnRemoteFired(int playerId, string itemType, float aimY)
    {
        try
        {
            var proxy = NetworkManager.Instance?.GetRemotePlayer(playerId);
            if (proxy == null || string.IsNullOrEmpty(itemType)) return;

            var itemDef = Singleton<ItemsDatabase>.Instance != null
                ? Singleton<ItemsDatabase>.Instance.getItem(itemType, false)
                : null;
            if (itemDef == null || !itemDef.isFirearm) return;

            var proxyT = proxy.transform;
            var muzzlePos = proxyT.position
                + proxyT.up * itemDef.muzzleOffset.y
                + proxyT.right * itemDef.muzzleOffset.x;
            var muzzleRot = Quaternion.Euler(90f, aimY, 0f);

            RemoteApply.Active = true;
            try
            {
                if (itemDef.muzzlePrefab != null && !string.IsNullOrEmpty(itemDef.muzzlePrefab.name))
                    Core.AddPooledPrefab("FX", itemDef.muzzlePrefab.name, muzzlePos, muzzleRot);
                if (itemDef.muzzleParticles != null && !string.IsNullOrEmpty(itemDef.muzzleParticles.name))
                    Core.AddPooledPrefab("FX", itemDef.muzzleParticles.name, muzzlePos, muzzleRot);
                if (!itemDef.noMuzzleFlash)
                    Core.AddPrefab("FX/Muzzle/PistolFlash", proxyT.position + proxyT.up, muzzleRot, null, true);
            }
            finally
            {
                RemoteApply.Active = false;
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[RangedSync] muzzle replay failed: {ex.Message}");
        }
    }

    /// <summary>A partner's bullet hit something - replay the impact/blood FX here.</summary>
    public void OnRemoteBulletFx(string pool, string prefab, Vector3 pos, Vector3 rotEuler)
    {
        if (string.IsNullOrEmpty(prefab)) return;
        try
        {
            var rot = Quaternion.Euler(rotEuler.x, rotEuler.y, rotEuler.z);
            RemoteApply.Active = true;
            try
            {
                if (string.IsNullOrEmpty(pool))
                    Core.AddPrefab(prefab, pos, rot, null);
                else
                    Core.AddPooledPrefab(pool, prefab, pos, rot);
            }
            finally
            {
                RemoteApply.Active = false;
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[RangedSync] bullet FX replay failed: {ex.Message}");
        }
    }
}
