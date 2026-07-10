using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.Patches;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Keeps the story state consistent between players.
///
/// Dreams: the host is authoritative. When the host's game picks a dream
/// (Dreams.prepareDream), the preset is broadcast; when the client's game next
/// starts a dream, Dream_Patch overrides the arguments with the host's choice,
/// so both players experience the same dream sequence.
///
/// Flags: Darkwood stores all story decisions (dream outcomes, dialogue choices,
/// world events) in the Flags singleton. Every setFlag on either side is
/// replicated. Karma needs no extra sync - Controller.startBeforeDay derives
/// karmaPoints from flags (verified in IL).
/// </summary>
public class StorySync
{
    private readonly NetworkLayer _network;

    private Type _flagsType;
    private MethodInfo _setFlagBool;
    private MethodInfo _setFlagInt;

    private string _pendingDreamPreset;
    private int _pendingDreamId;
    private bool _hasPendingDream;

    public StorySync(NetworkLayer network)
    {
        _network = network;
    }

    // ------------------------------------------------------------------
    // Dreams
    // ------------------------------------------------------------------

    public void OnRemoteDream(string presetName, int dreamId)
    {
        _pendingDreamPreset = presetName;
        _pendingDreamId = dreamId;
        _hasPendingDream = true;
        ModLogger.Msg($"[StorySync] Host dream received: '{presetName}' (id {dreamId})");
    }

    /// <summary>Consume the host's dream choice (used once, then cleared).</summary>
    public bool TryConsumePendingDream(out string presetName, out int dreamId)
    {
        presetName = _pendingDreamPreset;
        dreamId = _pendingDreamId;
        if (!_hasPendingDream) return false;
        _hasPendingDream = false;
        return true;
    }

    // ------------------------------------------------------------------
    // Flags
    // ------------------------------------------------------------------

    public void ApplyRemoteFlag(string flagName, bool isInt, bool boolValue, int intValue)
    {
        // Defense in depth: never apply per-player transient flags,
        // even if an older mod version sent them
        if (Patches.Flags_Patch.IsLocalOnly(flagName)) return;

        // Applied remote flags count into the convergence digest exactly like
        // local sends - if sync works, both machines' stores match (v0.6)
        Patches.Flags_Patch.RecordForDigest(flagName, isInt, isInt ? intValue : (boolValue ? 1 : 0));

        if (!ResolveFlagsApi()) return;

        var flags = UnityEngine.Object.FindObjectOfType(_flagsType);
        if (flags == null)
        {
            QueuePendingFlag(flagName, isInt, boolValue, intValue);
            return;
        }

        RemoteApply.Active = true;
        try
        {
            if (isInt)
                _setFlagInt.Invoke(flags, new object[] { flagName, intValue });
            else
                _setFlagBool.Invoke(flags, new object[] { flagName, boolValue });
            if (NetworkLayer.VerboseLogging)
                ModLogger.Msg($"[StorySync] Applied remote flag '{flagName}' = {(isInt ? intValue.ToString() : boolValue.ToString())}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[StorySync] Failed to apply flag '{flagName}': {ex.Message}");
        }
        finally
        {
            RemoteApply.Active = false;
        }
    }

    // ------------------------------------------------------------------
    // Journal
    // ------------------------------------------------------------------

    /// <summary>
    /// Replay a full item-triggered journal addition (notes, quest items,
    /// keys). Inventory.addJournalItem resolves the item from the database
    /// and runs the pickup side effects - addJournalEntry alone is not enough.
    /// </summary>
    public void ApplyRemoteJournalItem(string itemType)
    {
        Journal_Patch.RecordSession("journalitem", itemType);

        var inventoryType = GameTypes.GetType("Inventory");
        var playerType = GameTypes.GetType("Player");
        if (inventoryType == null || playerType == null) return;

        // The method lives on Inventory but only touches singletons internally;
        // the player's own inventory is a safe instance to invoke it on
        var playerTransform = DarkwoodMP.DependencyInjection.ServiceLocator
            .Resolve<PlayerSync>()?.LocalPlayerTransform;
        var player = playerTransform != null ? playerTransform.GetComponent(playerType) : null;
        var inventory = player != null
            ? playerType.GetField("Inventory", BindingFlags.Public | BindingFlags.Instance)?.GetValue(player)
            : UnityEngine.Object.FindObjectOfType(inventoryType);
        if (inventory == null) return;

        var method = inventoryType.GetMethod("addJournalItem",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) return;

        RemoteApply.Active = true;
        RemoteApply.AllowInventoryGrant = true; // quest items/keys go to both players
        try
        {
            // showImmediately=false, noPopup=false: show the popup, don't force-open
            method.Invoke(inventory, new object[] { itemType, false, false });
            ModLogger.Msg($"[StorySync] Journal item '{itemType}' added from remote");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[StorySync] Failed to add journal item '{itemType}': {ex.Message}");
        }
        finally
        {
            RemoteApply.Active = false;
            RemoteApply.AllowInventoryGrant = false;
        }
    }

    /// <summary>
    /// Replay an atomic journal-reference pickup (note read from the world,
    /// quest item, key). The pickup methods only use the JournalDatabase and
    /// singletons, so invoking them on a temporary component works regardless
    /// of whether the original world object is loaded here.
    /// </summary>
    public void ApplyRemoteJournalRef(string className, string id)
    {
        var componentType = GameTypes.GetType(className);
        if (componentType == null)
        {
            ModLogger.Warning($"[StorySync] Unknown journal ref type '{className}'");
            return;
        }

        GameObject temp = null;
        RemoteApply.Active = true;
        RemoteApply.AllowInventoryGrant = true; // quest items/keys go to both players
        try
        {
            temp = new GameObject("DWMP_JournalRef");
            var component = temp.AddComponent(componentType);

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var idFieldName = className == "JournalNoteReference" ? "noteName" : "type";
            componentType.GetField(idFieldName, flags)?.SetValue(component, id);
            // Never let the temp pickup destroy anything or skip the journal
            componentType.GetField("dontDestroy", flags)?.SetValue(component, true);

            var pickup = componentType.GetMethod("pickup", flags);
            if (pickup == null)
            {
                ModLogger.Warning($"[StorySync] {className}.pickup not found");
                return;
            }

            // showImmediately=false, noPopup=false: player sees the popup
            pickup.Invoke(component, new object[] { false, false });
            ModLogger.Msg($"[StorySync] Applied remote {className} '{id}'");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[StorySync] Failed to apply {className} '{id}': {ex.Message}");
        }
        finally
        {
            RemoteApply.Active = false;
            RemoteApply.AllowInventoryGrant = false;
            if (temp != null)
                UnityEngine.Object.Destroy(temp);
        }
    }

    public void ApplyRemoteJournal(string entryType)
    {
        Journal_Patch.RecordSession("journal", entryType);

        var journalType = GameTypes.GetType("Journal");
        if (journalType == null) return;

        var journal = UnityEngine.Object.FindObjectOfType(journalType);
        if (journal == null) return;

        var method = journalType.GetMethod("addJournalEntry",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) return;

        RemoteApply.Active = true;
        try
        {
            // noPopup=false: the receiving player should see the new entry too
            method.Invoke(journal, new object[] { entryType, false });
            ModLogger.Msg($"[StorySync] Journal entry '{entryType}' added from remote");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[StorySync] Failed to add journal entry '{entryType}': {ex.Message}");
        }
        finally
        {
            RemoteApply.Active = false;
        }
    }

    public void Reset()
    {
        _hasPendingDream = false;
        _pendingFlags.Clear();
    }

    // Flags that arrived before Flags.Instance existed (menu / load)
    private readonly List<(string name, bool isInt, int value)> _pendingFlags = new();
    private float _lastFlagPendingRetry;

    /// <summary>
    /// Join bulk: session flags + journal entries as typed FlagDelta/JournalSync for one peer
    /// (Horde FlagBulk / JournalBulk under Yok wire).
    /// </summary>
    public void CollectSnapshot(List<Packet> into, int targetPlayerId)
    {
        if (into == null) return;
        var localId = NetworkManager.Instance != null && NetworkManager.Instance.LocalPlayerId >= 0
            ? NetworkManager.Instance.LocalPlayerId
            : 0;

        foreach (var (name, isInt, value) in Flags_Patch.GetSessionFlags())
        {
            into.Add(new FlagDeltaPacket
            {
                PlayerId = localId,
                IsInt = isInt,
                FlagName = name,
                Value = isInt ? value : (value != 0 ? 1 : 0)
            });
        }

        foreach (var (channel, type) in Journal_Patch.GetSessionJournal())
        {
            byte kind = channel == "journalitem" ? JournalSyncPacket.KindItem
                : channel == "journalref" ? JournalSyncPacket.KindRef
                : JournalSyncPacket.KindEntry;
            into.Add(new JournalSyncPacket
            {
                PlayerId = localId,
                Kind = kind,
                Payload = type,
                RefClass = ""
            });
        }

        if (into.Count > 0)
            ModLogger.Msg($"[JoinBulk] StorySync: flags+journal packets for player {targetPlayerId}");
    }

    /// <summary>Queue flag apply when Flags singleton not ready yet.</summary>
    public void QueuePendingFlag(string flagName, bool isInt, bool boolValue, int intValue)
    {
        _pendingFlags.Add((flagName, isInt, isInt ? intValue : (boolValue ? 1 : 0)));
    }

    public void OnUpdate()
    {
        if (_pendingFlags.Count == 0) return;
        if (Time.time - _lastFlagPendingRetry < 2f) return;
        _lastFlagPendingRetry = Time.time;
        if (!ResolveFlagsApi()) return;
        var flagsObj = UnityEngine.Object.FindObjectOfType(_flagsType);
        if (flagsObj == null) return;

        for (var i = _pendingFlags.Count - 1; i >= 0; i--)
        {
            var f = _pendingFlags[i];
            try
            {
                RemoteApply.Active = true;
                if (f.isInt)
                    _setFlagInt?.Invoke(flagsObj, new object[] { f.name, f.value });
                else
                    _setFlagBool?.Invoke(flagsObj, new object[] { f.name, f.value != 0 });
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[StorySync] Pending flag '{f.name}': {ex.Message}");
            }
            finally
            {
                RemoteApply.Active = false;
            }
            _pendingFlags.RemoveAt(i);
        }
    }

    private bool ResolveFlagsApi()
    {
        if (_flagsType != null) return _setFlagBool != null && _setFlagInt != null;

        _flagsType = GameTypes.GetType("Flags");
        if (_flagsType == null) return false;

        var flagsBinding = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var m in _flagsType.GetMethods(flagsBinding))
        {
            if (m.Name != "setFlag") continue;
            var ps = m.GetParameters();
            if (ps.Length != 2) continue;
            if (ps[1].ParameterType == typeof(bool)) _setFlagBool = m;
            else if (ps[1].ParameterType == typeof(int)) _setFlagInt = m;
        }
        return _setFlagBool != null && _setFlagInt != null;
    }
}
