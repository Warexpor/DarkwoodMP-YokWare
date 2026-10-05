using System.Collections.Generic;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Door / Window / Item Burn fan-out (Flame / molotov). Forwardable Broadcast.
    /// Skips CharBase / Player — those stay on EntityBurning / PlayerBurning
    /// (<c>FireSyncPatches</c>).
    /// </summary>
    internal static class WorldBurnSyncHelpers
    {
        internal const float DefaultBurnTime = 20f;

        /// <summary>
        /// Burns added under network apply — <c>Start</c> runs next frame after
        /// <see cref="NetworkApplyGuard"/> ends; consume once to avoid echo Broadcast.
        /// </summary>
        private static readonly HashSet<int> RemoteAppliedInstanceIds = new HashSet<int>();

        /// <summary>Session end: ids marked but never consumed (burn destroyed first) must not leak.</summary>
        internal static void Reset() => RemoteAppliedInstanceIds.Clear();

        internal static void MarkRemoteApplied(Burn burn)
        {
            if (burn != null)
                RemoteAppliedInstanceIds.Add(burn.GetInstanceID());
        }

        private static bool ConsumeRemoteApplied(Burn burn)
        {
            return burn != null && RemoteAppliedInstanceIds.Remove(burn.GetInstanceID());
        }

        internal static bool TryResolveWorldTarget(Burn burn, out byte targetType, out Vector3 pos)
        {
            targetType = 0;
            pos = Vector3.zero;
            if (burn == null) return false;

            // Character / Player / proxy items: existing EntityBurning / PlayerBurning.
            if (burn.GetComponent<CharBase>() != null) return false;
            if (burn.GetComponent<Player>() != null) return false;
            if (burn.GetComponent<ProxyItem>() != null) return false;

            Door door = Door.getDoorScript(burn.transform);
            if (door != null)
            {
                targetType = WorldBurnStateMessage.TargetDoor;
                pos = door.transform.position;
                return true;
            }

            Window window = burn.GetComponent<Window>();
            if (window == null)
                window = burn.GetComponentInParent<Window>();
            if (window != null)
            {
                targetType = WorldBurnStateMessage.TargetWindow;
                pos = window.transform.position;
                return true;
            }

            Item item = burn.GetComponent<Item>();
            if (item != null)
            {
                targetType = WorldBurnStateMessage.TargetItem;
                pos = item.transform.position;
                return true;
            }

            return false;
        }

        internal static float ReadRemainingTime(Burn burn)
        {
            if (burn == null) return DefaultBurnTime;
            float burnTime = burn.burnTime > 0f ? burn.burnTime : DefaultBurnTime;
            var trv = Traverse.Create(burn);
            bool burning = false;
            var burningField = trv.Field("burning");
            if (burningField.FieldExists())
                burning = burningField.GetValue<bool>();
            if (!burning)
                return burnTime;

            float started = 0f;
            var startedField = trv.Field("timeStartedBurning");
            if (startedField.FieldExists())
                started = startedField.GetValue<float>();
            if (started <= 0f)
                return burnTime;
            return Mathf.Max(0.1f, burnTime - (Time.time - started));
        }

        internal static WorldBurnStateMessage BuildMessage(Burn burn, bool burning)
        {
            TryResolveWorldTarget(burn, out byte targetType, out Vector3 p);
            var msg = new WorldBurnStateMessage
            {
                PosX = Mathf.Round(p.x * 10f) / 10f,
                PosY = Mathf.Round(p.y * 10f) / 10f,
                PosZ = Mathf.Round(p.z * 10f) / 10f,
                TargetType = targetType,
                Burning = (byte)(burning ? 1 : 0)
            };
            if (burning)
            {
                msg.HasRemainingTime = true;
                msg.RemainingTime = ReadRemainingTime(burn);
            }
            return msg;
        }

        internal static void SendState(Burn burn, bool burning, string reason)
        {
            if (burn == null) return;
            if (ConsumeRemoteApplied(burn)) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;
            if (!TryResolveWorldTarget(burn, out _, out _))
                return;
            // The host's own prologue pad exists on its machine only.
            if (Sync.PersonalPrologue.IsOnProloguePad(burn.transform))
                return;

            var msg = BuildMessage(burn, burning);
            ModRuntime.Network.Broadcast(NetMessageType.WorldBurnState,
                w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[WorldBurnSync] send {reason} type={msg.TargetType} burning={burning} " +
                $"at ({msg.PosX:F1},{msg.PosZ:F1})" +
                (msg.HasRemainingTime ? $" remain={msg.RemainingTime:F1}s" : ""));
        }
    }

    /// <summary>
    /// Flame (and any other path) AddComponent&lt;Burn&gt; → Start. Host broadcasts;
    /// client → host (Forwardable).
    /// </summary>
    [HarmonyPatch(typeof(Burn), "Start")]
    public static class WorldBurnStartSyncPatch
    {
        private static void Postfix(Burn __instance)
        {
            WorldBurnSyncHelpers.SendState(__instance, burning: true, "Start");
        }
    }

    /// <summary>
    /// Burn.stop / burnTime expiry / extinguish. Prefix so transform is still valid
    /// before <c>Destroy(this)</c>. Skips Character/Player (CharBase gate).
    /// </summary>
    [HarmonyPatch(typeof(Burn), "stop")]
    public static class WorldBurnStopSyncPatch
    {
        private static void Prefix(Burn __instance)
        {
            WorldBurnSyncHelpers.SendState(__instance, burning: false, "stop");
        }
    }
}
