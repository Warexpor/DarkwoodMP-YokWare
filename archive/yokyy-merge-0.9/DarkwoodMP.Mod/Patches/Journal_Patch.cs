using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Syncs journal content. Verified game API (via IL):
/// - Journal.addJournalEntry(string type, bool noPopup): plain journal entries
///   (dialogue, dreams).
/// - Inventory.addJournalItem(string type, bool, bool): item-triggered journal
///   content - it resolves the item from the ItemsDatabase and runs
///   QuestItemReference/JournalNoteReference/KeyReference.pickup() before
///   (conditionally) calling addJournalEntry. Notes and quest items ONLY work
///   through this path, so it must be replayed as a whole on the remote side.
/// </summary>
public sealed class Journal_Patch : IPatch
{
    private static readonly HashSet<string> _sent = new();
    // Session journal for late-join bulk (channel + type), order-stable for re-apply
    private static readonly List<(string channel, string type)> _sessionJournal = new();

    // addJournalItem internally calls addJournalEntry and the reference
    // pickups; while inside, those postfixes must stay silent so only the
    // richer journalitem message goes out
    public static bool InAddJournalItem { get; private set; }

    /// <summary>Snapshot for join bulk (flags-style session store).</summary>
    public static List<(string channel, string type)> GetSessionJournal()
    {
        lock (_sessionJournal)
            return new List<(string, string)>(_sessionJournal);
    }

    /// <summary>Record a journal event for digest/join (local send or remote apply).</summary>
    public static void RecordSession(string channel, string type)
    {
        if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(type)) return;
        lock (_sessionJournal)
        {
            if (!_sent.Add(type)) return; // one wire identity per type (same as live de-dupe)
            _sessionJournal.Add((channel, type));
        }
    }

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        _sent.Clear(); // fresh session: allow re-sync (e.g. different save/world)
        lock (_sessionJournal) _sessionJournal.Clear();
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (target.DeclaringType!.Name == "Inventory")
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(Journal_Patch).GetMethod(nameof(ItemPrefix), statics)!),
                postfix: new HarmonyMethod(typeof(Journal_Patch).GetMethod(nameof(ItemPostfix), statics)!));
        }
        else
        {
            baseHarmony.Patch(target,
                postfix: new HarmonyMethod(typeof(Journal_Patch).GetMethod(nameof(EntryPostfix), statics)!));
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Journal", "addJournalEntry");
        yield return ("Inventory", "addJournalItem");
    }

    public static void EntryPostfix(string type)
    {
        if (InAddJournalItem) return;
        Send("journal", type);
    }

    public static void ItemPrefix() => InAddJournalItem = true;

    public static void ItemPostfix(string type)
    {
        InAddJournalItem = false;
        Send("journalitem", type);
    }

    private static void Send(string channel, string type)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (string.IsNullOrEmpty(type)) return;

            // RecordSession also enforces once-per-type
            var before = _sent.Count;
            RecordSession(channel, type);
            if (_sent.Count == before) return; // already recorded this session

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            ModLogger.Msg($"[Journal_Patch] Ironbark JournalSync {channel} '{type}'");
            byte kind = channel == "journalitem" ? JournalSyncPacket.KindItem : JournalSyncPacket.KindEntry;
            network.SendReliable(new JournalSyncPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                Kind = kind,
                Payload = type,
                RefClass = ""
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Journal_Patch] {ex.Message}");
        }
    }
}
