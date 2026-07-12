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
    /// Open-door swing force is still applied locally (predictive) so the door moves
    /// on the striker's machine — host getHit owns damage + authority force.
    /// </summary>
    internal static class ClientWorldMeleeRedirectHelper
    {
        // B5: local combat FX already played on redirect; suppress apply-side hit FX briefly.
        private static readonly Dictionary<string, float> _fxSuppressUntil = new Dictionary<string, float>(16);
        // Local open-door swing already applied — skip network BarricadeEvent re-force.
        private static readonly Dictionary<string, float> _swingSuppressUntil = new Dictionary<string, float>(16);
        private const float FxSuppressSeconds = 2f;
        private const float SwingSuppressSeconds = 1.5f;
        private const float OpenDoorHitForce = -50000f;

        internal static void Reset()
        {
            _fxSuppressUntil.Clear();
            _swingSuppressUntil.Clear();
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

        internal static void RegisterLocalDoorSwing(Vector3 pos)
        {
            string key = MakeKey(0, pos);
            _swingSuppressUntil[key] = Time.unscaledTime + SwingSuppressSeconds;
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

        /// <summary>True if local open-door swing was already predicted (skip network re-force).</summary>
        internal static bool ShouldSuppressDoorSwingForce(Vector3 pos)
        {
            string key = MakeKey(0, pos);
            if (!_swingSuppressUntil.TryGetValue(key, out float until))
                return false;
            if (Time.unscaledTime > until)
            {
                _swingSuppressUntil.Remove(key);
                return false;
            }
            _swingSuppressUntil.Remove(key);
            return true;
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

        /// <summary>
        /// Vanilla Door.getHit defers bodyRB.AddForce by 2 frames when the door is open.
        /// Redirect skips getHit entirely — re-apply that swing so client sees the door move.
        /// </summary>
        internal static void ApplyOpenDoorSwingPredictive(Door door, Transform attacker)
        {
            if (door == null || attacker == null)
                return;
            if (!door.opened || door.destroyed || door.barricaded)
                return;

            RegisterLocalDoorSwing(door.transform.position);

            Rigidbody bodyRB = Traverse.Create(door).Field("bodyRB").GetValue<Rigidbody>();
            if (bodyRB == null)
                return;

            // Keep non-kinematic like an opened door so force sticks.
            if (bodyRB.isKinematic)
                bodyRB.isKinematic = false;

            Transform doorT = door.transform;
            Controller ctrl = Singleton<Controller>.Instance;
            if (ctrl != null)
            {
                ctrl.waitFramesAndRun(delegate
                {
                    if (door == null || doorT == null || attacker == null)
                        return;
                    if (!door.opened || door.destroyed || door.barricaded)
                        return;
                    Rigidbody rb = Traverse.Create(door).Field("bodyRB").GetValue<Rigidbody>();
                    if (rb == null)
                        return;
                    if (rb.isKinematic)
                        rb.isKinematic = false;
                    Vector3 vector = attacker.position - doorT.position;
                    if (vector.sqrMagnitude < 0.0001f)
                        return;
                    rb.AddForce(vector.normalized * OpenDoorHitForce);
                }, 2);
            }
            else
            {
                Vector3 vector = attacker.position - doorT.position;
                if (vector.sqrMagnitude >= 0.0001f)
                    bodyRB.AddForce(vector.normalized * OpenDoorHitForce);
            }
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
            // Open door swing: host still owns HP; client needs local force or the door never moves.
            ClientWorldMeleeRedirectHelper.ApplyOpenDoorSwingPredictive(__instance, attacker);
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
            // Mirror vanilla Item.getHit FX (redirect skips getHit entirely).
            // Without hitParticlePrefabObject the client never sees wood chips / debris.
            if (__instance.hitParticlePrefabObject != null)
            {
                Core.AddPrefab(__instance.hitParticlePrefabObject, __instance.transform.position,
                    Quaternion.Euler(90f, 0f, 0f), null);
            }
            string hitSound = !string.IsNullOrEmpty(__instance.hitSound)
                ? __instance.hitSound
                : "woodenObject_hit";
            AudioController.Play(hitSound, __instance.transform.position);
            if (Player.Instance != null)
                Singleton<UI>.Instance.enemyHealthBar.show(__instance.gameObject);
            return false;
        }
    }
}