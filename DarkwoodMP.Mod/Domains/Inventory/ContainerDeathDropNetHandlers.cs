using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Container open / corpse death-drop state request (host snapshot to requester).
    /// </summary>
    internal sealed class ContainerDeathDropNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal ContainerDeathDropNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        /// <summary>
        /// Client->Host: client requests the current state of a container inventory.
        /// Host captures all non-empty slots and sends ContainerStateSync.
        /// </summary>
        internal void HandleContainerStateRequest(ContainerStateRequestMessage msg)
        {
            if (_net.Role != NetworkRole.Host) return;

            ModRuntime.LegacyInfo($"[Container] HandleContainerStateRequest: hash={msg.TargetEntityHash} pos=({msg.PosX:F1},{msg.PosY:F1},{msg.PosZ:F1})");

            // Try exact entity hash lookup first
            Inventory inv = null;
            if (msg.TargetEntityHash > 0)
            {
                Character c = CharacterTracker.FindByStableId((short)msg.TargetEntityHash);
                if (c != null)
                {
                    InvItemClass held = HarmonyLib.Traverse.Create(c).Field("currentItem").GetValue<InvItemClass>();
                    inv = c.GetComponent<Inventory>();
                    if (inv == null)
                        ModRuntime.LegacyInfo($"[Container] entity hash lookup found '{c.name}' but no Inventory component");
                    else
                        ModRuntime.LegacyInfo($"[Container] entity hash lookup OK: '{c.name}' invType={inv.invType} slots={inv.slots.Count}");
                }
                else
                {
                    ModRuntime.LegacyInfo($"[Container] entity hash {msg.TargetEntityHash} not found, falling back to position");
                }
            }

            if (inv == null)
            {
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                inv = WorldQueryHelper.FindInventoryByPos(pos);
                if (inv == null)
                {
                    // Final fallback: scan dead Characters near the position. A dead
                    // body may have a disabled collider or the stable ID may not match.
                    Character[] allChars = WorldQueryHelper.GetCachedSceneComponents<Character>();
                    Character closestDead = null;
                    float closestDeadDist = 10f;
                    foreach (Character c in allChars)
                    {
                        if (c == null || c.alive) continue;
                        if (c.GetComponent<Inventory>() == null) continue;
                        float d = Vector3.Distance(c.transform.position, pos);
                        if (d < closestDeadDist)
                        {
                            closestDeadDist = d;
                            closestDead = c;
                        }
                    }
                    if (closestDead != null)
                    {
                        inv = closestDead.GetComponent<Inventory>();
                        ModRuntime.LegacyInfo($"[Container] HandleContainerStateRequest: found dead '{closestDead.name}' at {closestDeadDist:F1}m from request pos");
                    }
                    else
                    {
                        ModRuntime.Log?.LogWarning($"[Container] HandleContainerStateRequest: no inventory at ({msg.PosX:F1},{msg.PosY:F1},{msg.PosZ:F1}) hash={msg.TargetEntityHash}");
                        return;
                    }
                }
                else
                {
                    ModRuntime.LegacyInfo($"[Container] HandleContainerStateRequest: found by pos: '{inv.name}' invType={inv.invType} slots={inv.slots.Count}");
                }
            }

            // Count non-empty slots
            int count = 0;
            for (int i = 0; i < inv.slots.Count; i++)
            {
                if (!InvItemClass.isNull(inv.slots[i].invItem))
                    count++;
            }

            ModRuntime.LegacyInfo($"[Container] HandleContainerStateRequest: responding with {count} items");

            short entityHash = 0;
            Character ownerChar = inv.GetComponent<Character>();
            if (ownerChar != null)
                entityHash = CharacterTracker.GetStableId(ownerChar);

            var sync = new ContainerStateSyncMessage
            {
                PosX = msg.PosX,
                PosY = msg.PosY,
                PosZ = msg.PosZ,
                EntityHash = entityHash,
                SlotCount = count,
                Slots = new SlotStateEntry[count]
            };
            int idx = 0;
            for (int i = 0; i < inv.slots.Count; i++)
            {
                var slot = inv.slots[i];
                if (!InvItemClass.isNull(slot.invItem))
                {
                    InvItemClass dit = slot.invItem;
                    bool isRecipe = dit.isRecipe;
                    sync.Slots[idx++] = new SlotStateEntry
                    {
                        SlotIndex = (byte)i,
                        ItemType = isRecipe ? dit.recipeFor : dit.type,
                        Amount = dit.amount,
                        Durability = dit.durability,
                        Ammo = dit.ammo,
                        IsRecipe = isRecipe,
                        Upgrades = Sync.InvItemUpgradeWire.CollectNames(dit),
                        ShouldBeActive = dit.shouldBeActive
                    };
                }
            }
            // Full snapshot only to the requester. Broadcast would wipe other
            // clients' mid-loot views (3+ and dual-open races).
            int requester = _net.CurrentReceivePlayerId;
            if (requester > 0)
            {
                _net.SendToPlayer(requester, NetMessageType.ContainerStateSync,
                    w => sync.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
            else
            {
                _net.Broadcast(NetMessageType.ContainerStateSync, w => sync.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            }

            // Shared world: mark container searched for everyone (hover text).
            Item openItem = inv.GetComponent<Item>();
            if (openItem != null && !openItem.searched)
            {
                openItem.searched = true;
                Character oc = inv.GetComponent<Character>();
                if (oc != null) oc.searched = true;
                _net.Broadcast(NetMessageType.ContainerItem, w => new ContainerItemMessage
                {
                    PosX = msg.PosX,
                    PosY = msg.PosY,
                    PosZ = msg.PosZ,
                    Action = ContainerAction.Searched,
                    SlotIndex = 0,
                    ItemType = "",
                    Amount = 0,
                    Durability = 0,
                    Ammo = 0
                }.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
        }
    }
}
