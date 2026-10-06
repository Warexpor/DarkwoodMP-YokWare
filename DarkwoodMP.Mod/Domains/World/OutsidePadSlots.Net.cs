using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    internal static partial class OutsidePadSlots
    {
        private static LocationPadSlotEntry ToWire(string name, OutsidePadSlotLogic.Entry e) => new LocationPadSlotEntry
        {
            LocationName = name,
            Slot = e != null ? e.Slot : -1,
            YawDeg = e != null ? e.Yaw : 0
        };

        private static void BroadcastEntry(string name, OutsidePadSlotLogic.Entry e)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;
            var msg = new LocationPadSlotSyncMessage
            {
                NextSlot = Logic.HighWater,
                Entries = new[] { ToWire(name, e) }
            };
            net.SendToAll(NetMessageType.LocationPadSlotSync, w => msg.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Host: a client asks for the slot of a pad it is about to spawn.</summary>
        internal static void HandleRequest(LanNetworkManager net, int playerId,
            LocationPadSlotRequestMessage msg)
        {
            if (net == null || net.Role != NetworkRole.Host || playerId <= 0
                || string.IsNullOrEmpty(msg.LocationName))
                return;
            var ol = Singleton<OutsideLocations>.Instance;
            if (ol == null)
            {
                // Cannot allocate. Say so instead of staying silent: Slot -1 means "unavailable, use
                // vanilla placement", and the requester stops waiting (silence left it black-screened).
                var unavailable = new LocationPadSlotSyncMessage
                {
                    NextSlot = Logic.HighWater,
                    Entries = new[] { ToWire(msg.LocationName, null) }
                };
                net.SendToPlayer(playerId, NetMessageType.LocationPadSlotSync,
                    w => unavailable.Serialize(w), DeliveryMethod.ReliableOrdered);
                ModLog.WarnRate(LogCat.World, "padslot-host-no-ol",
                    "[PadSlot] host has no OutsideLocations — told p" + playerId
                    + " to use vanilla placement for '" + msg.LocationName + "'", 10f);
                return;
            }
            OutsidePadSlotLogic.Entry e = AllocateAuthoritative(ol, msg.LocationName, out bool isNew);
            var reply = new LocationPadSlotSyncMessage
            {
                NextSlot = Logic.HighWater,
                Entries = new[] { ToWire(msg.LocationName, e) }
            };
            net.SendToPlayer(playerId, NetMessageType.LocationPadSlotSync, w => reply.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            if (isNew)
            {
                net.SendToAllExcept(playerId, NetMessageType.LocationPadSlotSync, w => reply.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            }
        }

        /// <summary>Client: host assignments (reply, broadcast, or join snapshot).</summary>
        internal static void HandleSync(LanNetworkManager net, LocationPadSlotSyncMessage msg)
        {
            if (net == null || net.Role != NetworkRole.Client)
                return;
            var ol = Singleton<OutsideLocations>.Instance;
            if (ol == null || !Bind(ol) || msg.Entries == null)
                return; // world not loaded yet — the join snapshot / request path repopulates
            for (int i = 0; i < msg.Entries.Length; i++)
            {
                var w = msg.Entries[i];
                if (string.IsNullOrEmpty(w.LocationName))
                    continue;
                // Ignored while this machine is mid-spawn with the assignment it already holds.
                Logic.ApplyHostAssignment(w.LocationName, w.Slot, w.YawDeg);
            }
            Logic.RaiseHighWater(msg.NextSlot);
            SeedFromWorld(ol, authority: false); // marks assignments whose pad already exists here consumed
            if (ol.actualSpawnedLocationsCount < Logic.HighWater)
                ol.actualSpawnedLocationsCount = Logic.HighWater;
        }

        /// <summary>Host: hand a late joiner every live/reserved assignment.</summary>
        internal static void SendSnapshotTo(LanNetworkManager net, int playerId)
        {
            if (net == null || net.Role != NetworkRole.Host || playerId <= 0)
                return;
            var ol = Singleton<OutsideLocations>.Instance;
            if (ol == null || !Bind(ol))
                return;
            SeedFromWorld(ol, authority: true);
            var list = new List<LocationPadSlotEntry>();
            foreach (var kv in Logic.Entries)
            {
                // Stale entries (pad gone) are skipped: a fresh spawn will ask again.
                if (!Logic.ShouldSnapshot(kv.Key, kv.Value, n => IsAlive(ol, n)))
                    continue;
                list.Add(ToWire(kv.Key, kv.Value));
                if (list.Count >= 128)
                    break;
            }
            if (list.Count == 0 && Logic.HighWater == 0)
                return;
            var msg = new LocationPadSlotSyncMessage { NextSlot = Logic.HighWater, Entries = list.ToArray() };
            net.SendToPlayer(playerId, NetMessageType.LocationPadSlotSync, w => msg.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            ModLog.Event(LogCat.Session,
                "[BulkSync] LocationPadSlot x" + list.Count + " → p" + playerId);
        }
    }
}
