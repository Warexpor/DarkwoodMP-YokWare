using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Syncs thrown items (flares, molotovs, rocks, ...). Verified in IL:
/// Player.throwItem does NOT spawn a drop - it detaches the already
/// instantiated Player.heldItem, sets ThrownItem.thrown + landTarget and lets
/// physics fly it. So the projectile must be captured in a prefix (heldItem is
/// gone from the field afterwards) and both the spawn AND the landing target
/// are broadcast from the postfix. The remote side spawns the same item type
/// and replays the landing (ThrownItem.thrown=true near landTarget), which
/// runs the game's own landing effects - a lit molotov explodes via
/// Explodes.onActivate(flaming), a flare ignites.
/// </summary>
public sealed class Throw_Patch : IPatch
{
    private static GameObject _heldBeforeThrow;
    private static string _typeBeforeThrow;
    private static Vector3 _posBeforeThrow;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            prefix: new HarmonyMethod(typeof(Throw_Patch).GetMethod(nameof(Prefix), statics)!),
            postfix: new HarmonyMethod(typeof(Throw_Patch).GetMethod(nameof(Postfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Player", "throwItem");
    }

    public static void Prefix(object __instance)
    {
        _heldBeforeThrow = null;
        _typeBeforeThrow = null;
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Player player) return;
            if (player.transform.root.name.StartsWith("RemotePlayer_")) return;

            _heldBeforeThrow = player.heldItem;
            _typeBeforeThrow = !InvItemClass.isNull(player.currentItem) ? player.currentItem.type : null;
            _posBeforeThrow = player.transform.position;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Throw_Patch] {ex.Message}");
        }
    }

    public static void Postfix()
    {
        try
        {
            var held = _heldBeforeThrow;
            var itemType = _typeBeforeThrow;
            _heldBeforeThrow = null;
            _typeBeforeThrow = null;

            if (RemoteApply.Active) return;
            if (held == null || string.IsNullOrEmpty(itemType)) return;

            var thrownItem = held.GetComponent<ThrownItem>();
            if (thrownItem == null || !thrownItem.thrown) return; // wasn't actually thrown

            // Flares are synced separately (Flare_Patch -> flare light). Routing
            // them through the generic drop path spawned an inert, unpickable,
            // unlit copy on the partner, so skip them here.
            if (held.GetComponent<Flare>() != null) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            var itemSync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<ItemSync>();
            if (network == null || !network.IsConnected || itemSync == null) return;

            var target = thrownItem.landTarget;
            var syncId = itemSync.RegisterLocalDrop(held.transform);
            itemSync.RecordThrow(syncId, held.transform, itemType, target);

            // Self-contained throw event (v0.7): the receiver spawns the REAL
            // armed projectile prefab (InvItem.item, with its ThrownItem) and
            // replays the landing - so a gas bomb bursts into its cloud and a
            // molotov explodes, instead of the old drop path's dead pickupable.
            // The lit state travels along so a burning molotov lands burning.
            // The throw ORIGIN rides along too: the receiver flies the
            // projectile from there with real physics, so it lands (and
            // explodes) when the thrower's bottle does - not instantly.
            var origin = _posBeforeThrow;
            network.SendReliable(new ThrownArmedPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                SyncId = syncId,
                ItemType = itemType ?? "",
                Flaming = thrownItem.flaming,
                Tx = target.x, Ty = target.y, Tz = target.z,
                Ox = origin.x, Oy = origin.y, Oz = origin.z
            });
            ModLogger.Msg($"[Throw_Patch] Threw '{itemType}' -> {target:F1} (flaming={thrownItem.flaming})");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Throw_Patch] {ex.Message}");
        }
    }
}
