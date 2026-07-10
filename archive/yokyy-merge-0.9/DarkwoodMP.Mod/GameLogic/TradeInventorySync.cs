using System;
using System.Collections.Generic;
using System.Text;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using UnityEngine;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Absolute trader stock model C (Horde TradeInventorySync).
/// Keyed by NPC.name; success-only live path; join bulk; pending if NPC unloaded.
/// </summary>
public static class TradeInventorySync
{
    private static readonly Dictionary<string, string> _pending = new Dictionary<string, string>();

    public static void Reset() => _pending.Clear();

    public static void BroadcastNpcInventory(NPC npc)
    {
        if (npc == null || string.IsNullOrEmpty(npc.name) || npc.inventory == null) return;
        var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
        var manager = NetworkManager.Instance;
        if (network == null || manager == null || !network.IsConnected) return;

        var payload = EncodeInventory(npc.inventory);
        network.SendReliableCritical(new TradeInventoryPacket
        {
            PlayerId = manager.LocalPlayerId >= 0 ? manager.LocalPlayerId : 0,
            NpcName = Sanitize(npc.name),
            StockCsv = payload
        });
        ModLogger.Msg($"[TradeSync] Ironbark TradeInventory '{npc.name}'");
    }

    public static void Handle(string npcName, string payload)
    {
        if (string.IsNullOrEmpty(npcName)) return;
        var npc = FindNpcByName(npcName);
        if (npc == null)
        {
            _pending[npcName] = payload ?? "";
            ModLogger.Msg($"[TradeSync] pending stock for '{npcName}'");
            return;
        }
        ApplyToNpc(npc, payload);
        _pending.Remove(npcName);
    }

    public static void FlushPending()
    {
        if (_pending.Count == 0) return;
        List<string> done = null;
        foreach (var kvp in _pending)
        {
            var npc = FindNpcByName(kvp.Key);
            if (npc == null) continue;
            ApplyToNpc(npc, kvp.Value);
            (done ??= new List<string>()).Add(kvp.Key);
        }
        if (done != null)
            foreach (var k in done) _pending.Remove(k);
    }

    public static void ApplyToNpc(NPC npc, string payload)
    {
        if (npc?.inventory == null) return;
        var inv = npc.inventory;
        RemoteApply.Active = true;
        try
        {
            inv.clear();
            if (!string.IsNullOrEmpty(payload))
            {
                foreach (var entry in payload.Split(';'))
                {
                    if (string.IsNullOrEmpty(entry)) continue;
                    var comma = entry.LastIndexOf(',');
                    if (comma <= 0) continue;
                    var type = entry.Substring(0, comma);
                    if (!int.TryParse(entry.Substring(comma + 1), out var amount) || amount <= 0) continue;
                    try { inv.addItemType(type, amount); } catch { }
                }
            }
            try { inv.refreshReputation(); } catch { }
            try
            {
                var dw = Singleton<global::UI>.Instance?.dialogueWindow;
                if (dw != null && dw.opened && dw.npc == npc)
                {
                    inv.refreshIcons();
                    dw.exchangeTrader?.refreshIcons();
                }
            }
            catch { }
        }
        finally
        {
            RemoteApply.Active = false;
        }
        ModLogger.Msg($"[TradeSync] applied absolute stock '{npc.name}'");
    }

    public static void CollectSnapshot(List<Packet> into)
    {
        if (into == null) return;
        try
        {
            var pid = NetworkManager.Instance?.LocalPlayerId ?? 0;
            foreach (var npc in UnityEngine.Object.FindObjectsOfType<NPC>())
            {
                if (npc == null || !npc.trader || npc.inventory == null) continue;
                if (string.IsNullOrEmpty(npc.name)) continue;
                into.Add(new TradeInventoryPacket
                {
                    PlayerId = pid,
                    NpcName = Sanitize(npc.name),
                    StockCsv = EncodeInventory(npc.inventory)
                });
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[TradeSync] CollectSnapshot: {ex.Message}");
        }
    }

    public static bool IsTraderInventory(Component inventory)
    {
        if (inventory == null) return false;
        try
        {
            var npc = inventory.GetComponentInParent<NPC>();
            return npc != null && npc.trader;
        }
        catch { return false; }
    }

    public static NPC FindNpcByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var npc in UnityEngine.Object.FindObjectsOfType<NPC>())
            if (npc != null && npc.name == name) return npc;
        return null;
    }

    private static string EncodeInventory(Inventory inv)
    {
        var totals = new Dictionary<string, int>();
        try
        {
            var items = inv.getAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                if (InvItemClass.isNull(items[i])) continue;
                var type = items[i].type;
                if (string.IsNullOrEmpty(type)) continue;
                if (totals.ContainsKey(type)) totals[type] += items[i].amount;
                else totals[type] = items[i].amount;
            }
        }
        catch { }
        var sb = new StringBuilder();
        foreach (var kv in totals)
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append(kv.Key).Append(',').Append(kv.Value);
        }
        return sb.ToString();
    }

    private static string Sanitize(string s) => (s ?? "").Replace(":", "_");
}
