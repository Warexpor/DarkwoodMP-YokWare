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
        private static int _getHitInstanceId;

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
            DoorBarricadePatch.ClearSessionState();
            DoorGetHitPatch.ClearSessionState();
            WindowGetHitPatch.ClearSessionState();
            ItemGetHitPatch.ClearSessionState();
            ClientWorldMeleeRedirectHelper.Reset();
            _getHitInstanceId = 0;
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
                ModRuntime.LegacyInfo("[BulkSync] Barricade removed-boards isWindow="
                    + isWindow + " → p" + targetPlayerId + ": " + sent);
            return sent;
        }

        internal static void BeginGetHit(int instanceId) => _getHitInstanceId = instanceId;

        internal static void EndGetHit(int instanceId)
        {
            if (_getHitInstanceId == instanceId)
                _getHitInstanceId = 0;
        }

        internal static bool IsInsideGetHit(int instanceId) => _getHitInstanceId == instanceId;

        internal static void SendBarricadeEvent(Vector3 pos, byte targetType, BarricadeAction action, int health, bool playerBarricade, int mainHealth = -1, int damageAmount = -1, Vector3? attackerPos = null)
        {
            if (LanNetworkManager._processingBarricadeEvent) { if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] suppressed (processing)"); return; }
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
            var net = LanNetworkManager.Instance;
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
        private static readonly Dictionary<int, bool> _wasDestroyed = new Dictionary<int, bool>();

        internal static void ClearSessionState() => _wasDestroyed.Clear();

        [HarmonyPrefix]
        private static void Prefix(Door __instance)
        {
            _wasDestroyed[__instance.GetInstanceID()] = __instance.destroyed;
        }

        private static void Postfix(Door __instance, object[] __args)
        {
            int id = __instance.GetInstanceID();
            bool wasDestroyed;
            if (!_wasDestroyed.TryGetValue(id, out wasDestroyed))
                wasDestroyed = false;
            _wasDestroyed.Remove(id);

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
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Door), "getHit", new[] { typeof(int), typeof(Transform), typeof(bool), typeof(bool) })]
    public static class DoorGetHitPatch
    {
        private static readonly Dictionary<int, bool> _wasBarricaded = new Dictionary<int, bool>(16);
        private static readonly Dictionary<int, int> _barricadeHealthBefore = new Dictionary<int, int>(16);
        private static readonly Dictionary<int, bool> _playerBarricadeBefore = new Dictionary<int, bool>(16);

        internal static void ClearSessionState()
        {
            _wasBarricaded.Clear();
            _barricadeHealthBefore.Clear();
            _playerBarricadeBefore.Clear();
        }

        [HarmonyPrefix]
        private static void Prefix(Door __instance)
        {
            int id = __instance.GetInstanceID();
            BarricadeSyncHelpers.BeginGetHit(id);
            _wasBarricaded[id] = __instance.barricaded;
            _barricadeHealthBefore[id] = __instance.barricadeHealth;
            _playerBarricadeBefore[id] = __instance.playerBarricade;
        }

        [HarmonyPostfix]
        private static void Postfix(Door __instance, object[] __args)
        {
            if (ClientRandomEventGate.PlayingHostLocationEvent
                && !(__args.Length > 1 && __args[1] is Transform doorAtk
                    && Player.Instance != null
                    && (doorAtk == Player.Instance.transform || doorAtk.IsChildOf(Player.Instance.transform))))
            {
                BarricadeSyncHelpers.EndGetHit(__instance.GetInstanceID());
                return;
            }
            int id = __instance.GetInstanceID();
            bool wasBarricaded;
            int barricadeHealthBefore;
            bool playerBarricadeBefore;
            if (!_wasBarricaded.TryGetValue(id, out wasBarricaded))
                wasBarricaded = false;
            if (!_barricadeHealthBefore.TryGetValue(id, out barricadeHealthBefore))
                barricadeHealthBefore = 0;
            if (!_playerBarricadeBefore.TryGetValue(id, out playerBarricadeBefore))
                playerBarricadeBefore = false;
            _wasBarricaded.Remove(id);
            _barricadeHealthBefore.Remove(id);
            _playerBarricadeBefore.Remove(id);

            int damage = (int)__args[0];

            try
            {
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
            finally
            {
                BarricadeSyncHelpers.EndGetHit(id);
            }
        }
    }

    /// <summary>
    /// Syncs barricade damage/health changes on windows to remote clients
    /// after getHit is called (including destruction when health reaches zero).
    /// Uses Prefix because vanilla Window.getHit calls destroyBarricade() before Postfix.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Window), "getHit", new[] { typeof(int), typeof(Transform), typeof(bool) })]
    public static class WindowGetHitPatch
    {
        private static readonly Dictionary<int, bool> _wasBarricaded = new Dictionary<int, bool>(16);
        private static readonly Dictionary<int, int> _barricadeHealthBefore = new Dictionary<int, int>(16);
        private static readonly Dictionary<int, bool> _playerBarricadeBefore = new Dictionary<int, bool>(16);

        internal static void ClearSessionState()
        {
            _wasBarricaded.Clear();
            _barricadeHealthBefore.Clear();
            _playerBarricadeBefore.Clear();
        }

        [HarmonyPrefix]
        private static void Prefix(Window __instance)
        {
            int id = __instance.GetInstanceID();
            BarricadeSyncHelpers.BeginGetHit(id);
            _wasBarricaded[id] = __instance.barricaded;
            _barricadeHealthBefore[id] = __instance.barricadeHealth;
            _playerBarricadeBefore[id] = __instance.playerBarricade;
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[Barr_Win] Prefix id={id} barricaded={__instance.barricaded} hp={__instance.barricadeHealth}");
        }

        [HarmonyPostfix]
        private static void Postfix(Window __instance, object[] __args)
        {
            if (ClientRandomEventGate.PlayingHostLocationEvent
                && !(__args.Length > 1 && __args[1] is Transform winAtk
                    && Player.Instance != null
                    && (winAtk == Player.Instance.transform || winAtk.IsChildOf(Player.Instance.transform))))
            {
                BarricadeSyncHelpers.EndGetHit(__instance.GetInstanceID());
                return;
            }
            int damage = (int)__args[0];
            int id = __instance.GetInstanceID();

            bool wasBarricaded;
            int barricadeHealthBefore;
            bool playerBarricadeBefore;
            if (!_wasBarricaded.TryGetValue(id, out wasBarricaded))
                wasBarricaded = false;
            if (!_barricadeHealthBefore.TryGetValue(id, out barricadeHealthBefore))
                barricadeHealthBefore = 0;
            if (!_playerBarricadeBefore.TryGetValue(id, out playerBarricadeBefore))
                playerBarricadeBefore = false;
            _wasBarricaded.Remove(id);
            _barricadeHealthBefore.Remove(id);
            _playerBarricadeBefore.Remove(id);

            try
            {
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
            finally
            {
                BarricadeSyncHelpers.EndGetHit(id);
            }
        }
    }

    /// <summary>
    /// Syncs damage to destructible world items (wardrobes, furniture, etc.).
    /// Captures the position in a Prefix because Item.getHit → die() may move
    /// the transform before the Postfix runs.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Item), "getHit", new[] { typeof(int), typeof(Transform), typeof(bool) })]
    public static class ItemGetHitPatch
    {
        private static readonly Dictionary<int, Vector3> _prePosByItem = new Dictionary<int, Vector3>(16);

        internal static void ClearSessionState() => _prePosByItem.Clear();

        [HarmonyPrefix]
        private static void Prefix(Item __instance)
        {
            _prePosByItem[__instance.GetInstanceID()] = __instance.transform.position;
        }

        [HarmonyPostfix]
        private static void Postfix(Item __instance, object[] __args)
        {
            int damage = (int)__args[0];
            int itemId = __instance.GetInstanceID();

            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (LanNetworkManager._processingBarricadeEvent) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (!__instance.destructible) return;

            // If the client redirected this hit, original getHit was skipped
            // and health is stale. Don't send a barricade event.
            if (__args.Length > 1 && __args[1] is Transform atk && ClientWorldMeleeRedirectHelper.ShouldRedirect(atk))
                return;

            if (!_prePosByItem.TryGetValue(itemId, out Vector3 pos))
                pos = __instance.transform.position;
            _prePosByItem.Remove(itemId);
            bool destroyed = __instance.destroyed;
            int health = __instance.health;

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[World] " + __instance.name + " dmg=" + damage + " health=" + health + " destroyed=" + destroyed + " pos=" + pos);

            BarricadeSyncHelpers.SendBarricadeEvent(
                pos, 2,
                destroyed ? BarricadeAction.Destroyed : BarricadeAction.Damaged,
                health, false, damageAmount: damage);
        }
    }
}
