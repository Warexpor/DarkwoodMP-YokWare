using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Barricade + item-damage handlers composed for 0.8.</summary>
    internal sealed partial class BarricadeNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal const int MaxPendingBarricadeEvents = 64;
        private readonly List<BarricadeEventMessage> _pendingBarricadeEvents = new List<BarricadeEventMessage>();

        internal BarricadeNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearPendingBarricades()
        {
            _pendingBarricadeEvents.Clear();
            BarricadeSyncHelpers.ClearRemovedBoards();
        }

        internal void HandleBarricadeEvent(BarricadeEventMessage msg)
        {
            // Host: latch peer-sourced board tear-downs for late-join (local Send already notes).
            if (_net.Role == NetworkRole.Host && msg.IsWindow <= 1)
            {
                Vector3 key = new Vector3(
                    (float)System.Math.Round(msg.PosX, 1),
                    (float)System.Math.Round(msg.PosY, 1),
                    (float)System.Math.Round(msg.PosZ, 1));
                if (msg.Action == BarricadeAction.Destroyed)
                    BarricadeSyncHelpers.NoteBoardRemoved(key, msg.IsWindow);
                else if (msg.Action == BarricadeAction.Built)
                    BarricadeSyncHelpers.NoteBoardBuilt(key, msg.IsWindow);
            }
            ApplyBarricadeEvent(msg, queueIfMissing: true);
        }

        internal void ApplyBarricadeEvent(BarricadeEventMessage msg, bool queueIfMissing)
        {
            if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] HANDLE type={msg.IsWindow} act={msg.Action} hp={msg.Health} pos=({msg.PosX:F1},{msg.PosY:F1},{msg.PosZ:F1}) mainHp={msg.MainHealth}");
            LanNetworkManager._processingBarricadeEvent = true;
            try
            {
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);

                if (msg.IsWindow == 2)
                {
                    // Match door/window: queue until the pad/chunk wakes (fresh
                    // OutsideLocations.spawnLocation). HandleItemDamageEvent alone
                    // dropped Destroyed crates when the Item was not spawned yet.
                    if (WorldQueryHelper.FindDestructibleItemXz(pos, 25f) == null)
                    {
                        if (queueIfMissing)
                            QueuePendingBarricade(msg);
                        else if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo($"[Barr] item not found at {pos}");
                        return;
                    }
                    HandleItemDamageEvent(pos, msg);
                    return;
                }

                if (msg.IsWindow == 0)
                {
                    Door door = WorldQueryHelper.FindDoorByPos(pos);
                    if (door == null)
                    {
                        if (queueIfMissing)
                            QueuePendingBarricade(msg);
                        else if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo($"[Barr] door not found at {pos}");
                        return;
                    }
                    if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] found door={door.name} barricaded={door.barricaded} hp={door.barricadeHealth}");

                    if (msg.Action == BarricadeAction.Built)
                    {
                        door.playerBarricade = msg.PlayerBarricade;
                        // Health == 0 means the door was restored from destroyed
                        // (player built a new door in an empty doorway, no barricade).
                        if (msg.Health <= 0 && door.destroyed)
                        {
                            door.unDestroy();
                            if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] door restored from destroyed");
                        }
                        else
                        {
                            // Normal barricade build. setToBarricaded -> setBarricadeState.
                            // If door was destroyed, first call only restores (unDestroy),
                            // second call applies the barricade.
                            door.setToBarricaded();
                            if (!door.barricaded)
                                door.setToBarricaded();
                            if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] door barricade applied (destroyed={door.destroyed} barricaded={door.barricaded})");
                        }
                        MaybeAlertHostRemoteHammer(msg, door.transform.position);
                    }
                    else
                    {
                        // Bulk/state snapshots use DamageAmount < 0 (no combat hit FX).
                        // Client redirect registers a short suppress so striker does not double FX.
                        bool playCombatFx = msg.DamageAmount >= 0
                            && !DWMPHorde.Patches.ClientWorldMeleeRedirectHelper.ShouldSuppressApplyFx(0, pos);

                        // Apply barricade state changes
                        if (msg.Action == BarricadeAction.Destroyed)
                        {
                            if (door.barricaded)
                                door.destroyBarricade(silent: true);
                            if (playCombatFx)
                                AudioController.Play("woodenObject_destroy", door.body?.position ?? pos);
                            if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] door destroyed");
                            MaybeAlertHostRemoteHammer(msg, door.transform.position);
                        }
                        else if (msg.Action == BarricadeAction.Damaged)
                        {
                            if (door.barricaded)
                            {
                                door.barricadeHealth = msg.Health;
                                if (door.barricadeHealth <= 0)
                                    door.destroyBarricade(silent: true);
                            }
                            if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] door damaged hp={msg.Health}");
                        }

                        // Apply main health changes + optional FX
                        if (msg.MainHealth >= 0)
                        {
                            if (msg.MainHealth <= 0 && !door.destroyed)
                            {
                                door.destroyDoor();
                                if (playCombatFx)
                                    AudioController.Play("woodenObject_destroy", door.body?.position ?? pos);
                                if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] door main destroyed");
                            }
                            else
                            {
                                Traverse.Create(door).Field("health").SetValue(msg.MainHealth);
                                if (playCombatFx && door.body != null)
                                {
                                    Core.AddPrefab("particles/door_hit_melee", door.body.position,
                                        Quaternion.Euler(90f, 0f, 0f), null, worldSpace: true);
                                    AudioController.Play("woodenObject_hit", door.body);
                                }
                            }
                        }

                        // Apply door-swing physics when an open, unbarricaded door is melee-hit.
                        // Mirrors vanilla Door.getHit(): bodyRB.AddForce(vector.normalized * -50000f)
                        // using the attacker position captured in DoorGetHitPatch.
                        // Skip if this client already predicted the swing (melee redirect path).
                        if (msg.HasAttackerPos && door.opened && !door.destroyed && !door.barricaded
                            && !DWMPHorde.Patches.ClientWorldMeleeRedirectHelper.ShouldSuppressDoorSwingForce(pos))
                        {
                            Vector3 doorPos = door.body != null ? door.body.position : door.transform.position;
                            Vector3 forceDir = (new Vector3(msg.AttackerPosX, msg.AttackerPosY, msg.AttackerPosZ) - doorPos).normalized * -50000f;
                            Rigidbody doorRB = Traverse.Create(door).Field("bodyRB").GetValue<Rigidbody>();
                            if (doorRB != null)
                            {
                                if (doorRB.isKinematic)
                                    doorRB.isKinematic = false;
                                doorRB.AddForce(forceDir);
                            }
                        }
                    }
                }
                else
                {
                    // Window (type 1)
                    Window window = WorldQueryHelper.FindWindowByPos(pos);
                    if (window == null)
                    {
                        if (queueIfMissing)
                            QueuePendingBarricade(msg);
                        else if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo($"[Barr] window not found at {pos}");
                        return;
                    }
                    if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] found window={window.name} barricaded={window.barricaded} hp={window.barricadeHealth}");

                    // B3: vanilla setBarricadeState / destroyBarricade for graph tags + sprites
                    if (msg.Action == BarricadeAction.Built)
                    {
                        window.barricadeState = 3;
                        // destHealth 0 => full max in vanilla; join bulk sends actual HP (>0 when boarded).
                        // byPlayer=false avoids gainSaturation / construction side effects on remote apply.
                        int destHp = msg.Health > 0 ? msg.Health : 0;
                        window.setBarricadeState(destHp, byPlayer: false);
                        window.playerBarricade = msg.PlayerBarricade;
                        if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] window barricade via setBarricadeState hp={destHp}");
                        MaybeAlertHostRemoteHammer(msg, window.transform.position);
                    }
                    else if (msg.Action == BarricadeAction.Destroyed)
                    {
                        if (window.barricaded)
                            window.destroyBarricade(silent: true);
                        if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] window destroyBarricade");
                        MaybeAlertHostRemoteHammer(msg, window.transform.position);
                    }
                    else if (msg.Action == BarricadeAction.Damaged)
                    {
                        if (msg.Health <= 0)
                        {
                            if (window.barricaded)
                                window.destroyBarricade(silent: true);
                        }
                        else if (window.barricaded)
                        {
                            window.barricadeHealth = msg.Health;
                        }
                        if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] window damaged/destroyed hp={msg.Health}");
                    }
                }
            }
            finally { LanNetworkManager._processingBarricadeEvent = false; }
        }

        /// <summary>
        /// Host: mirror vanilla HammerWork <c>alertInArea(player, 500f)</c> when
        /// remote barricade apply skips the anim. Prefer sender proxy; else object.
        /// </summary>
        private void MaybeAlertHostRemoteHammer(BarricadeEventMessage msg, Vector3 fallbackPos)
        {
            if (_net.Role != NetworkRole.Host || msg.IsWindow > 1) return;
            // Built+byPlayer = plank; Destroyed+DamageAmount&lt;0 = dismantle (not combat).
            bool hammerNoise = (msg.Action == BarricadeAction.Built && msg.PlayerBarricade)
                || (msg.Action == BarricadeAction.Destroyed && msg.DamageAmount < 0);
            if (!hammerNoise) return;

            Vector3 alertPos = fallbackPos;
            int sender = _net.CurrentReceivePlayerId;
            if (sender > 0)
            {
                RemotePlayerProxy proxy = _net.GetProxy(sender);
                if (proxy != null && proxy.transform != null)
                    alertPos = proxy.transform.position;
            }
            Character.alertInArea(alertPos, 500f, dangerousSound: false, 1f);
        }

        internal void QueuePendingBarricade(BarricadeEventMessage msg)
        {
            // Replace same rounded position + type
            for (int i = _pendingBarricadeEvents.Count - 1; i >= 0; i--)
            {
                var p = _pendingBarricadeEvents[i];
                if (p.IsWindow == msg.IsWindow
                    && Mathf.Abs(p.PosX - msg.PosX) < 0.15f
                    && Mathf.Abs(p.PosY - msg.PosY) < 0.15f
                    && Mathf.Abs(p.PosZ - msg.PosZ) < 0.15f)
                    _pendingBarricadeEvents.RemoveAt(i);
            }
            if (_pendingBarricadeEvents.Count >= MaxPendingBarricadeEvents)
                _pendingBarricadeEvents.RemoveAt(0);
            _pendingBarricadeEvents.Add(msg);
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[Barr] queued event type={msg.IsWindow} act={msg.Action}");
        }

        internal void TryFlushPendingBarricadeEvents()
        {
            if (_pendingBarricadeEvents.Count == 0) return;
            for (int i = _pendingBarricadeEvents.Count - 1; i >= 0; i--)
            {
                var msg = _pendingBarricadeEvents[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                bool found = msg.IsWindow == 0
                    ? WorldQueryHelper.FindDoorByPos(pos) != null
                    : msg.IsWindow == 1
                        ? WorldQueryHelper.FindWindowByPos(pos) != null
                        : msg.IsWindow == 2
                            && WorldQueryHelper.FindDestructibleItemXz(pos, 25f) != null;
                if (!found) continue;
                _pendingBarricadeEvents.RemoveAt(i);
                ApplyBarricadeEvent(msg, queueIfMissing: false);
            }
        }
    }
}
