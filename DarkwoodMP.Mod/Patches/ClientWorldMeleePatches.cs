using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// On the client, redirects local-player melee damage on doors, windows, and
    /// destructible items to the host instead of applying it locally.
    /// </summary>
    internal static class ClientWorldMeleeRedirectHelper
    {
        // B5: local combat FX already played on redirect; suppress apply-side hit FX briefly.
        private static readonly Dictionary<string, float> _fxSuppressUntil = new Dictionary<string, float>(16);
        private const float FxSuppressSeconds = 2f;

        internal static void Reset()
        {
            _fxSuppressUntil.Clear();
        }

        internal static string MakeKey(byte targetType, Vector3 pos)
        {
            return $"{targetType}_{(float)System.Math.Round(pos.x, 1)}_{(float)System.Math.Round(pos.y, 1)}_{(float)System.Math.Round(pos.z, 1)}";
        }

        internal static void RegisterLocalFx(byte targetType, Vector3 pos)
        {
            string key = MakeKey(targetType, pos);
            _fxSuppressUntil[key] = Time.unscaledTime + FxSuppressSeconds;
        }

        /// <summary>
        /// True if this client already played hit FX for the target (redirect path).
        /// Consumes the suppress entry so only one apply is muted.
        /// </summary>
        internal static bool ShouldSuppressApplyFx(byte targetType, Vector3 pos)
        {
            string key = MakeKey(targetType, pos);
            if (!_fxSuppressUntil.TryGetValue(key, out float until))
                return false;
            _fxSuppressUntil.Remove(key);
            return Time.unscaledTime <= until;
        }

        internal static bool ShouldRedirect(Transform attacker)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Client)
                return false;
            if (LanNetworkManager.IsApplyingRemoteState)
                return false;
            if (Player.Instance == null || attacker == null)
                return false;
            return attacker == Player.Instance.transform;
        }

        internal static void SendHit(byte targetType, Vector3 pos, int damage)
        {
            RegisterLocalFx(targetType, pos);
            Vector3 atkPos = Player.Instance.transform.position;
            var net = ModRuntime.Network as LanNetworkManager;
            net?.SendMeleeWorldHit(new MeleeWorldHitMessage
            {
                TargetType = targetType,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                AttackerPosX = atkPos.x,
                AttackerPosY = atkPos.y,
                AttackerPosZ = atkPos.z,
                Damage = damage
            });
        }
    }

    [HarmonyPatch(typeof(Door), "getHit", new[] { typeof(int), typeof(Transform), typeof(bool), typeof(bool) })]
    public static class ClientDoorMeleeRedirectPatch
    {
        private static bool Prefix(Door __instance, object[] __args)
        {
            int damage = (int)__args[0];
            Transform attacker = (Transform)__args[1];
            if (!ClientWorldMeleeRedirectHelper.ShouldRedirect(attacker))
                return true;

            ClientWorldMeleeRedirectHelper.SendHit(0, __instance.transform.position, damage);
            // Play local hit effects since getHit will be skipped
            AudioController.Play("woodenObject_hit", __instance.transform);
            Core.AddPrefab("particles/door_hit_melee", __instance.transform.position, __instance.transform.rotation, null);
            if (Core.trueDistance(__instance.transform.position, Player.Instance._transform.position) < 250f)
                Singleton<CamMain>.Instance.shake(0.3f, 5f);
            if (__instance.barricaded)
                Singleton<UI>.Instance.enemyHealthBar.show(__instance.gameObject);
            return false;
        }
    }

    [HarmonyPatch(typeof(Window), "getHit", new[] { typeof(int), typeof(Transform), typeof(bool) })]
    public static class ClientWindowMeleeRedirectPatch
    {
        private static bool Prefix(Window __instance, object[] __args)
        {
            int damage = (int)__args[0];
            Transform attacker = (Transform)__args[1];
            if (!ClientWorldMeleeRedirectHelper.ShouldRedirect(attacker))
                return true;

            ClientWorldMeleeRedirectHelper.SendHit(1, __instance.transform.position, damage);
            // Play local hit effects since getHit will be skipped
            AudioController.Play("woodenObject_hit", __instance.transform);
            Core.AddPrefab("particles/window_hit", __instance.transform.position, __instance.transform.rotation, null);
            if (Core.trueDistance(__instance.transform.position, Player.Instance._transform.position) < 250f)
                Singleton<CamMain>.Instance.shake(0.3f, 5f);
            if (__instance.barricaded)
                Singleton<UI>.Instance.enemyHealthBar.show(__instance.gameObject);
            return false;
        }
    }

    [HarmonyPatch(typeof(Item), "getHit", new[] { typeof(int), typeof(Transform), typeof(bool) })]
    public static class ClientItemMeleeRedirectPatch
    {
        private static bool Prefix(Item __instance, object[] __args)
        {
            int damage = (int)__args[0];
            Transform attacker = (Transform)__args[1];
            if (!__instance.destructible)
                return true;
            if (!ClientWorldMeleeRedirectHelper.ShouldRedirect(attacker))
                return true;

            ClientWorldMeleeRedirectHelper.SendHit(2, __instance.transform.position, damage);
            // Play local hit sound since getHit will be skipped
            AudioController.Play("woodenObject_hit", __instance.transform);
            return false;
        }
    }
}