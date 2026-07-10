using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Lock state sync (v0.4). Door open/close was synced since v0.1, but the
/// LOCK state never was - one player unlocking a door with a key, lockpick or
/// padlock combination left it locked for the other. Verified IL:
///
/// - Locked.unlock() just sets locked=false; keys (tryToOpenWithKey) and
///   lockpicks (tryToLockpick) both route through it -> one postfix covers
///   every player-driven unlock.
/// - Door.unlock() writes Locked.locked=false DIRECTLY (no Locked.unlock
///   call) - the event-driven unlock path needs its own postfix.
/// - Door.lockMe(keyType) adds/configures the Locked component - events lock
///   doors mid-game, replicate it.
/// - Padlock.unlock(manually) fires the padlock's own event triggers (that is
///   what opens whatever it guards) - replayed with manually:true inside
///   RemoteApply so those triggers run remotely too; the "success" message
///   doubles as a co-op notification.
/// </summary>
public sealed class Lock_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var name = target.DeclaringType!.Name switch
        {
            "Locked" => nameof(LockedUnlockPostfix),
            "Padlock" => nameof(PadlockUnlockPostfix),
            _ => target.Name == "lockMe" ? nameof(DoorLockPostfix) : nameof(DoorUnlockPostfix)
        };
        baseHarmony.Patch(target, postfix: new HarmonyMethod(typeof(Lock_Patch).GetMethod(name, statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Locked", "unlock");
        yield return ("Door", "unlock");
        yield return ("Door", "lockMe");
        yield return ("Padlock", "unlock");
    }

    public static void LockedUnlockPostfix(object __instance)
    {
        if (__instance is Locked locked)
            Send(WorldLockPacket.KindUnlock, GameIds.ForComponent(locked));
    }

    public static void DoorUnlockPostfix(object __instance)
    {
        if (__instance is Door door)
            Send(WorldLockPacket.KindUnlock, GameIds.ForComponent(door));
    }

    // Parameter name matches Door.lockMe(string keyType)
    public static void DoorLockPostfix(object __instance, string keyType)
    {
        if (__instance is Door door)
            Send(WorldLockPacket.KindDoorLock, GameIds.ForComponent(door), keyType ?? "");
    }

    public static void PadlockUnlockPostfix(object __instance)
    {
        if (__instance is Padlock padlock)
            Send(WorldLockPacket.KindPadUnlock, GameIds.ForComponent(padlock));
    }

    private static void Send(byte kind, string objectId, string keyType = "")
    {
        try
        {
            if (RemoteApply.Active) return;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            // Session history feeds the join snapshot (v0.5)
            var lockSync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<LockSync>();
            if (lockSync != null)
            {
                if (kind == WorldLockPacket.KindUnlock)
                    lockSync.RecordUnlock(objectId);
                else if (kind == WorldLockPacket.KindPadUnlock)
                    lockSync.RecordPadlockUnlock(objectId);
                else if (kind == WorldLockPacket.KindDoorLock)
                    lockSync.RecordDoorLock(objectId, keyType);
            }

            network.SendReliable(new WorldLockPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                Kind = kind,
                ObjectId = objectId ?? "",
                KeyType = keyType ?? ""
            });
            ModLogger.Msg($"[Lock_Patch] kind={kind} id={objectId}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Lock_Patch] {ex.Message}");
        }
    }
}
