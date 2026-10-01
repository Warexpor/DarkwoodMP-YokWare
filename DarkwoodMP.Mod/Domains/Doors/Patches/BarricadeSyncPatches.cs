using System.Collections.Generic;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Shared helper methods for barricade event synchronization (build,
    /// damage, destroy) between host and clients.
    /// </summary>
    internal static class BarricadeSyncHelpers
    {
        // B4: while getHit is running, destroyBarricade() is called inside vanilla
        // before GetHit Postfix — suppress destroy patch send; GetHit owns the event.
        // Nesting-aware depth: door A getHit can nest into door/window B; a single id
        // would clear A's suppress early and double-send destroyBarricade.
        private static readonly Dictionary<int, int> _getHitDepth = new Dictionary<int, int>(8);

        /// <summary>
        /// Session latches for door/window boards removed mid-session. Late-join bulk
        /// only scanned currently-barricaded sites, so soft-reconnect / AlreadyInWorld
        /// peers kept stale boards after host night defense tore them down.
        /// Key = "x_y_z|isWindow". Cap keeps N-peer night chaff bounded.
        /// </summary>
        internal const int MaxRemovedBoards = 128;
        private static readonly Dictionary<string, byte> _removedBoards =
            new Dictionary<string, byte>(64);

        public static void Reset()
        {
            ClientWorldMeleeRedirectHelper.Reset();
            _getHitDepth.Clear();
            ClearRemovedBoards();
        }

        internal static void ClearRemovedBoards() => _removedBoards.Clear();

        internal static string RemovedBoardKey(Vector3 key, byte isWindow)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return key.x.ToString("F1", inv) + "_" + key.y.ToString("F1", inv) + "_"
                + key.z.ToString("F1", inv) + "|" + isWindow;
        }

        internal static void NoteBoardRemoved(Vector3 key, byte isWindow)
        {
            if (isWindow > 1) return;
            string id = RemovedBoardKey(key, isWindow);
            if (_removedBoards.ContainsKey(id)) return;
            if (_removedBoards.Count >= MaxRemovedBoards)
            {
                // Drop oldest insertion order (Dictionary preserves order on netstandard/modern).
                string first = null;
                foreach (var k in _removedBoards.Keys) { first = k; break; }
                if (first != null) _removedBoards.Remove(first);
            }
            _removedBoards[id] = isWindow;
        }

        internal static void NoteBoardBuilt(Vector3 key, byte isWindow)
        {
            if (isWindow > 1) return;
            _removedBoards.Remove(RemovedBoardKey(key, isWindow));
        }

        /// <summary>Host late-join: Destroyed for boards removed this session (door=0 / window=1).</summary>
        internal static int SendRemovedBoardsTo(LanNetworkManager net, int targetPlayerId, byte isWindow, int maxSend)
        {
            if (net == null || net.Role != NetworkRole.Host || maxSend <= 0) return 0;
            if (_removedBoards.Count == 0) return 0;
            int sent = 0;
            foreach (var kv in _removedBoards)
            {
                if (sent >= maxSend) break;
                if (kv.Value != isWindow) continue;
                // id = "x_y_z|w"
                string id = kv.Key;
                int pipe = id.LastIndexOf('|');
                if (pipe <= 0) continue;
                string[] parts = id.Substring(0, pipe).Split('_');
                if (parts.Length != 3) continue;
                if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float x)) continue;
                if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float y)) continue;
                if (!float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float z)) continue;
                var msg = new BarricadeEventMessage
                {
                    PosX = x, PosY = y, PosZ = z,
                    IsWindow = isWindow,
                    Action = BarricadeAction.Destroyed,
                    Health = 0,
                    PlayerBarricade = false,
                    MainHealth = -1,
                    DamageAmount = -1
                };
                net.SendBulkOrAll(NetMessageType.BarricadeEvent, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }
            if (sent > 0)
                ModRuntime.LegacyInfo($"[BulkSync] Barricade removed-boards isWindow={isWindow} → p{targetPlayerId}: {sent}");
            return sent;
        }

        internal static void BeginGetHit(int instanceId)
        {
            int d;
            _getHitDepth.TryGetValue(instanceId, out d);
            _getHitDepth[instanceId] = d + 1;
        }

        internal static void EndGetHit(int instanceId)
        {
            int d;
            if (!_getHitDepth.TryGetValue(instanceId, out d)) return;
            if (d <= 1) _getHitDepth.Remove(instanceId);
            else _getHitDepth[instanceId] = d - 1;
        }

        internal static bool IsInsideGetHit(int instanceId) => _getHitDepth.ContainsKey(instanceId);

        internal static void SendBarricadeEvent(Vector3 pos, byte targetType, BarricadeAction action, int health, bool playerBarricade, int mainHealth = -1, int damageAmount = -1, Vector3? attackerPos = null)
        {
            if (LanNetworkManager.ProcessingBarricadeEvent) { if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] suppressed (processing)"); return; }
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            Vector3 key = new Vector3((float)System.Math.Round(pos.x, 1), (float)System.Math.Round(pos.y, 1), (float)System.Math.Round(pos.z, 1));
            var msg = new BarricadeEventMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                IsWindow = targetType,
                Action = action,
                Health = health,
                PlayerBarricade = playerBarricade,
                MainHealth = mainHealth,
                DamageAmount = damageAmount,
                HasAttackerPos = attackerPos.HasValue,
                AttackerPosX = attackerPos?.x ?? 0f,
                AttackerPosY = attackerPos?.y ?? 0f,
                AttackerPosZ = attackerPos?.z ?? 0f
            };
            if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] SEND type={targetType} act={action} hp={health} pos={key}");
            // Host latch: Destroyed boards must survive soft-reconnect (bulk scan misses them).
            if (action == BarricadeAction.Destroyed && targetType <= 1)
                NoteBoardRemoved(key, targetType);
            else if (action == BarricadeAction.Built && targetType <= 1)
                NoteBoardBuilt(key, targetType);
            var net = ModRuntime.Network;
            if (net != null)
                net.Broadcast(NetMessageType.BarricadeEvent, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>
    /// Syncs barricade build event AND door restoration (re-build a destroyed
    /// door from empty doorway) on doors to remote clients.
    /// </summary>
    [HarmonyPatch(typeof(Door), "barricade", new[] { typeof(bool) })]
    public static class DoorBarricadePatch
    {
        // __state (not a static map): nested/re-entrant barricade calls cannot overwrite it.
        [HarmonyPrefix]
        private static void Prefix(Door __instance, out bool __state)
        {
            __state = __instance.destroyed;
        }

        private static void Postfix(Door __instance, object[] __args, bool __state)
        {
            bool wasDestroyed = __state;

            bool byPlayer = (bool)__args[0];
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;

            bool justRestored = wasDestroyed && !__instance.destroyed;

            // Send event if barricaded (normal barricade build) OR if the door
            // was just restored from destroyed state (no barricade planks).
            if (__instance.barricaded || justRestored)
            {
                BarricadeSyncHelpers.SendBarricadeEvent(
                    __instance.transform.position, 0, BarricadeAction.Built,
                    __instance.barricadeHealth, byPlayer);
            }
        }
    }

    /// <summary>
    /// Syncs barricade destruction on doors to remote clients.
    /// </summary>
    [HarmonyPatch(typeof(Door), "destroyBarricade", new[] { typeof(bool) })]
    public static class DoorDestroyBarricadePatch
    {
        private static void Postfix(Door __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            // B4: destroyBarricade called from inside getHit — GetHit Postfix sends instead
            if (BarricadeSyncHelpers.IsInsideGetHit(__instance.GetInstanceID()))
                return;
            BarricadeSyncHelpers.SendBarricadeEvent(__instance.transform.position, 0, BarricadeAction.Destroyed, 0, false);
        }
    }

    /// <summary>
    /// Syncs barricade build event on windows to remote clients.
    /// </summary>
    [HarmonyPatch(typeof(Window), "barricade", new[] { typeof(int), typeof(bool) })]
    public static class WindowBarricadePatch
    {
        private static void Postfix(Window __instance, object[] __args)
        {
            bool byPlayer = (bool)__args[1];

            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (__instance.barricaded)
            {
                BarricadeSyncHelpers.SendBarricadeEvent(__instance.transform.position, 1, BarricadeAction.Built, __instance.barricadeHealth, byPlayer);
            }
        }
    }

    /// <summary>
    /// Syncs barricade destruction on windows to remote clients.
    /// </summary>
    [HarmonyPatch(typeof(Window), "destroyBarricade", new[] { typeof(bool) })]
    public static class WindowDestroyBarricadePatch
    {
        private static void Postfix(Window __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (BarricadeSyncHelpers.IsInsideGetHit(__instance.GetInstanceID()))
                return;
            BarricadeSyncHelpers.SendBarricadeEvent(__instance.transform.position, 1, BarricadeAction.Destroyed, 0, false);
        }
    }

    /// <summary>
    /// Syncs ALL door damage (barricade and main health) to the remote peer.
    /// Uses Prefix to capture pre-damage barricade state because vanilla
    /// Door.getHit calls destroyBarricade() before Postfix, resetting barricaded.
    /// </summary>
    [HarmonyPatch(typeof(Door), "getHit", new[] { typeof(int), typeof(Transform), typeof(bool), typeof(bool) })]
    public static class DoorGetHitPatch
    {
        // Pre-hit barricade state travels in __state: a nested getHit on the same door (or a
        // Finalizer of an inner call) cannot clear or overwrite what the outer Postfix reads.
        private struct State
        {
            public bool WasBarricaded;
            public int BarricadeHealthBefore;
            public bool PlayerBarricadeBefore;
        }

        [HarmonyPriority(Priority.Last)]
        [HarmonyPrefix]
        private static void Prefix(Door __instance, out State __state)
        {
            BarricadeSyncHelpers.BeginGetHit(__instance.GetInstanceID());
            __state = new State
            {
                WasBarricaded = __instance.barricaded,
                BarricadeHealthBefore = __instance.barricadeHealth,
                PlayerBarricadeBefore = __instance.playerBarricade
            };
        }

        [HarmonyPriority(Priority.Last)]
        [HarmonyPostfix]
        private static void Postfix(Door __instance, object[] __args, State __state)
        {
            if (ClientRandomEventGate.PlayingHostLocationEvent
                && !(__args.Length > 1 && __args[1] is Transform doorAtk
                    && Player.Instance != null
                    && (doorAtk == Player.Instance.transform || doorAtk.IsChildOf(Player.Instance.transform))))
                return;

            bool wasBarricaded = __state.WasBarricaded;
            int barricadeHealthBefore = __state.BarricadeHealthBefore;
            bool playerBarricadeBefore = __state.PlayerBarricadeBefore;
            // EndGetHit runs in the Finalizer (covers throw before/during Postfix).

            int damage = (int)__args[0];

            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;

            // If the client redirected this hit (local player attacking a remote
            // world object), the original getHit was skipped and barricadeHealth
            // is stale. Don't send a barricade event — the MeleeWorldHit handler
            // on the host will apply damage.
            if (__args.Length > 1 && __args[1] is Transform atk && ClientWorldMeleeRedirectHelper.ShouldRedirect(atk))
                return;

            // Capture attacker position for door-swing physics sync.
            // Vanilla Door.getHit applies bodyRB.AddForce when the door is open,
            // not barricaded, and not destroyed. We relay the attacker position
            // so HandleBarricadeEvent can apply the same force on the receiver.
            Vector3? attackerPos = null;
            if (__args.Length > 1 && __args[1] is Transform at && at != null)
                attackerPos = at.position;

            if (wasBarricaded)
            {
                // If health dropped to 0 (now not barricaded), it was destroyed
                bool wasDestroyed = barricadeHealthBefore > 0 && !__instance.barricaded;
                BarricadeSyncHelpers.SendBarricadeEvent(
                    __instance.transform.position, 0,
                    wasDestroyed ? BarricadeAction.Destroyed : BarricadeAction.Damaged,
                    __instance.barricadeHealth, playerBarricadeBefore,
                    __instance.destroyed ? -1 : __instance.health,
                    damage, attackerPos);
            }
            else
            {
                BarricadeSyncHelpers.SendBarricadeEvent(
                    __instance.transform.position, 0,
                    __instance.destroyed ? BarricadeAction.Destroyed : BarricadeAction.Damaged,
                    0, false, __instance.health, damage, attackerPos);
            }
        }

        // Finalizer (not Postfix): getHit throw after Prefix BeginGetHit leaves
        // IsInsideGetHit sticky → destroyBarricade sync suppressed forever.
        [HarmonyPriority(Priority.Last)]
        [HarmonyFinalizer]
        private static void Finalizer(Door __instance)
        {
            if (__instance == null) return;
            BarricadeSyncHelpers.EndGetHit(__instance.GetInstanceID());
        }
    }

    /// <summary>
    /// Syncs barricade damage/health changes on windows to remote clients
    /// after getHit is called (including destruction when health reaches zero).
    /// Uses Prefix because vanilla Window.getHit calls destroyBarricade() before Postfix.
    /// </summary>
    [HarmonyPatch(typeof(Window), "getHit", new[] { typeof(int), typeof(Transform), typeof(bool) })]
    public static class WindowGetHitPatch
    {
        // Pre-hit barricade state travels in __state (nested getHit safe, nothing static to leak).
        private struct State
        {
            public bool WasBarricaded;
            public int BarricadeHealthBefore;
            public bool PlayerBarricadeBefore;
        }

        [HarmonyPriority(Priority.Last)]
        [HarmonyPrefix]
        private static void Prefix(Window __instance, out State __state)
        {
            int id = __instance.GetInstanceID();
            BarricadeSyncHelpers.BeginGetHit(id);
            __state = new State
            {
                WasBarricaded = __instance.barricaded,
                BarricadeHealthBefore = __instance.barricadeHealth,
                PlayerBarricadeBefore = __instance.playerBarricade
            };
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[Barr_Win] Prefix id={id} barricaded={__instance.barricaded} hp={__instance.barricadeHealth}");
        }

        [HarmonyPriority(Priority.Last)]
        [HarmonyPostfix]
        private static void Postfix(Window __instance, object[] __args, State __state)
        {
            if (ClientRandomEventGate.PlayingHostLocationEvent
                && !(__args.Length > 1 && __args[1] is Transform winAtk
                    && Player.Instance != null
                    && (winAtk == Player.Instance.transform || winAtk.IsChildOf(Player.Instance.transform))))
                return;

            int damage = (int)__args[0];
            int id = __instance.GetInstanceID();

            bool wasBarricaded = __state.WasBarricaded;
            int barricadeHealthBefore = __state.BarricadeHealthBefore;
            bool playerBarricadeBefore = __state.PlayerBarricadeBefore;
            // EndGetHit runs in the Finalizer (covers throw before/during Postfix).

            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (!wasBarricaded)
                return;

            // If the client redirected this hit, original getHit was skipped
            // and barricadeHealth is stale. Don't send a barricade event.
            if (__args.Length > 1 && __args[1] is Transform atk && ClientWorldMeleeRedirectHelper.ShouldRedirect(atk))
                return;

            // If health dropped to 0 (now not barricaded), it was destroyed
            bool wasDestroyed = barricadeHealthBefore > 0 && !__instance.barricaded;

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[Barr_Win] SEND wasBarricaded={wasBarricaded} hpBefore={barricadeHealthBefore} hpNow={__instance.barricadeHealth} wasDestroyed={wasDestroyed}");
            BarricadeSyncHelpers.SendBarricadeEvent(
                __instance.transform.position, 1,
                wasDestroyed ? BarricadeAction.Destroyed : BarricadeAction.Damaged,
                __instance.barricadeHealth, playerBarricadeBefore, damageAmount: damage);
        }

        // Finalizer (not Postfix): getHit throw after Prefix BeginGetHit leaves
        // IsInsideGetHit sticky → destroyBarricade sync suppressed forever.
        [HarmonyPriority(Priority.Last)]
        [HarmonyFinalizer]
        private static void Finalizer(Window __instance)
        {
            if (__instance == null) return;
            BarricadeSyncHelpers.EndGetHit(__instance.GetInstanceID());
        }
    }

    /// <summary>
    /// Syncs damage to destructible world items (wardrobes, furniture, etc.).
    /// Captures the position in a Prefix because Item.getHit → die() may move
    /// the transform before the Postfix runs.
    /// </summary>
    [HarmonyPatch(typeof(Item), "getHit", new[] { typeof(int), typeof(Transform), typeof(bool) })]
    public static class ItemGetHitPatch
    {
        // __state (not a static map): a nested getHit on the same item cannot overwrite it and
        // nothing accumulates offline.
        [HarmonyPriority(Priority.Last)]
        [HarmonyPrefix]
        private static void Prefix(Item __instance, out Vector3 __state)
        {
            __state = __instance.transform.position;
        }

        [HarmonyPriority(Priority.Last)]
        [HarmonyPostfix]
        private static void Postfix(Item __instance, object[] __args, Vector3 __state)
        {
            int damage = (int)__args[0];

            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            // Match Door/Window: allow host MeleeWorldHit apply (IsApplyingRemoteState)
            // to fan BarricadeEvent. Loop stop is ProcessingBarricadeEvent only.
            if (LanNetworkManager.ProcessingBarricadeEvent) return;
            if (!__instance.destructible) return;

            // If the client redirected this hit, original getHit was skipped
            // and health is stale. Don't send a barricade event.
            if (__args.Length > 1 && __args[1] is Transform atk && ClientWorldMeleeRedirectHelper.ShouldRedirect(atk))
                return;

            Vector3 pos = __state;
            bool destroyed = __instance.destroyed;
            int health = __instance.health;

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[World] {__instance.name} dmg={damage} health={health} destroyed={destroyed} pos={pos}");

            BarricadeSyncHelpers.SendBarricadeEvent(
                pos, 2,
                destroyed ? BarricadeAction.Destroyed : BarricadeAction.Damaged,
                health, false, damageAmount: damage);
        }
    }
}
