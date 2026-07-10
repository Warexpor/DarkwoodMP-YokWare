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
/// Syncs the atomic journal-content mutations. Verified via IL/caller scan:
/// examining world objects (e.g. the "road home" picture) calls
/// JournalNoteReference.pickup DIRECTLY (Item.activate / InvSlot paths),
/// bypassing Inventory.addJournalItem entirely. Patching the three reference
/// components catches every journal source regardless of UI path:
///   QuestItemReference.pickup (field `type`)
///   JournalNoteReference.pickup (field `noteName`)
///   KeyReference.pickup (field `type`)
/// </summary>
public sealed class JournalRef_Patch : IPatch
{
    private static readonly HashSet<string> _sent = new();

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        _sent.Clear(); // fresh session: allow re-sync (e.g. different save/world)
        var postfix = new HarmonyMethod(typeof(JournalRef_Patch).GetMethod(nameof(Postfix), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!);
        baseHarmony.Patch(target, postfix: postfix);
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("QuestItemReference", "pickup");
        yield return ("JournalNoteReference", "pickup");
        yield return ("KeyReference", "pickup");
    }

    public static void Postfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            // Inventory.addJournalItem fires these internally and is synced
            // as a whole via the journalitem channel
            if (Journal_Patch.InAddJournalItem) return;

            var componentType = __instance.GetType();
            var className = componentType.Name;
            var idFieldName = className == "JournalNoteReference" ? "noteName" : "type";
            var idField = componentType.GetField(idFieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (idField?.GetValue(__instance) is not string id || string.IsNullOrEmpty(id)) return;

            if (!_sent.Add($"{className}:{id}")) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            ModLogger.Msg($"[JournalRef_Patch] Ironbark JournalSync ref {className} '{id}'");
            network.SendReliable(new JournalSyncPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                Kind = JournalSyncPacket.KindRef,
                Payload = id,
                RefClass = className
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[JournalRef_Patch] {ex.Message}");
        }
    }
}
