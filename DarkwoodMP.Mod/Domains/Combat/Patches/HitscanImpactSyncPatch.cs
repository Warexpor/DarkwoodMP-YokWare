using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

/// <summary>
/// Forwards hitscan raycast hit FX (bullet_hit_1, Shotsplat) and proxy damage to the
/// other peer. Only runs when the local player fires a weapon, ensuring both clients
/// see blood FX and the proxy receives damage from the remote player at range.
/// </summary>
namespace DWMPHorde.Patches
{
    /// <summary>
    /// Marks the window of the local player's vanilla Player.spawnBullet. The hitscan Raycast
    /// postfix below is global (Physics.Raycast, 5-arg overload, hitscan mask) and the host's own
    /// predator proxy-aggro scan uses the same overload+mask, so it only acts inside this scope.
    /// If the detour did not fire during the call (the JIT can inline the Raycast call site, so
    /// the detour silently never runs), cast the shot ray here so proxy FF / blood still work.
    /// </summary>
    [HarmonyPatch(typeof(Player), "spawnBullet", typeof(float))]
    internal static class HitscanSpawnBulletScopePatch
    {
        /// <summary>Main-thread nesting depth of spawnBullet (plain static: read on every raycast).</summary>
        internal static int Depth; // process-scoped: call-scoped, unwound by Finalizer
        /// <summary>The Raycast detour saw a hitscan-mask ray during the current spawnBullet.</summary>
        internal static bool SawHitscanRaycast; // process-scoped: call-scoped, cleared per call
        private static bool _fallbackLogged; // process-scoped: one log line per process
        private const float FallbackRange = 1200f;

        private static void Prefix()
        {
            if (Depth++ == 0)
                SawHitscanRaycast = false;
        }

        // Finalizer (not Postfix): spawnBullet can throw and must not leave the scope open.
        private static void Finalizer(Player __instance, System.Exception __exception)
        {
            if (Depth > 0) Depth--;
            if (Depth != 0) return;
            bool saw = SawHitscanRaycast;
            SawHitscanRaycast = false;
            if (saw || __exception != null) return;
            TryFallbackShot(__instance);
        }

        private static void TryFallbackShot(Player player)
        {
            if (player == null || player != Player.Instance) return;
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected) return;
            if (InvItemClass.isNull(player.currentItem) || player.currentItem.baseClass == null) return;
            // Projectile weapons hit through FastProjectile / Bullet.onCollide, not a ray.
            if (player.currentItem.baseClass.item != null) return;

            if (!_fallbackLogged)
            {
                _fallbackLogged = true;
                ModLog.Warn(LogCat.Combat, "Hitscan Raycast detour did not fire inside spawnBullet"
                    + " (inlined call site?): casting the shot ray from the scope patch instead.");
            }

            Transform t = player.transform;
            if (Physics.Raycast(t.position, t.up, out RaycastHit hit, FallbackRange, GameplayConstants.HitscanLayerMask))
                HitscanImpactSyncPatch.HandleHit(hit);
        }
    }

    // Parked: do not retarget global Physics.Raycast to spawnBullet — that would fan out
    // to every raycast in the game. Proxy hitscan FF stays in this Postfix on the hitscan mask,
    // gated on HitscanSpawnBulletScopePatch.Depth so only the local player's shot counts and
    // every other raycast in the game pays a single int compare.
    [HarmonyPatch]
    public static class HitscanImpactSyncPatch
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(Physics), "Raycast", new[] { typeof(Vector3), typeof(Vector3), typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int) });

        private static void Postfix(bool __result, RaycastHit hitInfo, int layerMask)
        {
            if (HitscanSpawnBulletScopePatch.Depth == 0) return;
            if (layerMask != GameplayConstants.HitscanLayerMask) return;
            HitscanSpawnBulletScopePatch.SawHitscanRaycast = true;
            if (!__result) return;
            HandleHit(hitInfo);
        }

        /// <summary>Proxy-hit handling for one local hitscan ray (detour or scope fallback).</summary>
        internal static void HandleHit(RaycastHit hitInfo)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            // Projectile sweep (incl. stalled pellets) uses the same layer mask —
            // damage for those is Bullet.onCollide → ProxyDamagePatch, never HitscanFF.
            if (TraverseHack.IsInsideFastProjectileRaycast) return;

            Player player = Player.Instance;
            if (player == null) return;
            if (InvItemClass.isNull(player.currentItem) || player.currentItem.baseClass == null) return;

            Collider collider = hitInfo.collider;
            if (collider == null) return;

            Vector3 hitPoint = hitInfo.point;

            RemotePlayerProxy proxy = collider.GetComponentInParent<RemotePlayerProxy>();
            if (proxy != null)
            {
                // Physical projectile weapons (shotgun pellets, etc.) also Raycast with this
                // layer mask via FastProjectile. Their damage is Bullet.onCollide → CharBase
                // → ProxyDamagePatch. Only pure hitscan weapons need damage here.
                bool hitscanWeapon = player.currentItem.baseClass.item == null;
                if (!hitscanWeapon)
                    return;

                // Vanilla spawnBullet hitscan only damages Character components.
                // Remote proxies are CharBase-only (no Character), so getHit never runs
                // and ProxyDamagePatch never fires. Send FF damage here instead.
                if (!SessionSettings.FriendlyFireEnabled)
                    return;

                CharBase proxyCB = proxy.CachedCharBase;
                if (proxyCB != null && !proxyCB.alive)
                    return;
                if (DeathStateTracker.IsRemoteNightDead(proxy.PlayerId))
                    return;

                // Match vanilla spawnBullet → getHit(baseClass.damage) plus upgrade mods
                // (melee uses getModdedDamage; firearms hitscan did not, but upgrades still apply).
                int baseDmg = player.currentItem.baseClass.damage;
                int dmg = Mathf.Max(1, player.currentItem.getModdedDamage(baseDmg));
                if (!ProxyCombatRelay.TryConsumeSafetyNet(net.LocalPlayerId, proxy.PlayerId))
                    return;
                Vector3 atkPos = player.transform.position;
                bool inWater = proxyCB != null && proxyCB.inWater;

                if (net.Role == NetworkRole.Host)
                {
                    net.SendToPlayer(proxy.PlayerId, NetMessageType.DamagePlayer, w =>
                        new DamagePlayerMessage
                        {
                            Damage = dmg,
                            AttackerPosX = atkPos.x,
                            AttackerPosY = atkPos.y,
                            AttackerPosZ = atkPos.z,
                            ShowRedScreen = true,
                            NormalHit = true,
                            CanInterrupt = true
                        }.Serialize(w), DeliveryMethod.ReliableOrdered);
                    ModRuntime.LegacyInfo($"[HitscanFF] host hit proxy {proxy.PlayerId} dmg={dmg}");
                }
                else
                {
                    net.Send(NetMessageType.FriendlyFire, w =>
                        new FriendlyFireMessage
                        {
                            Damage = dmg,
                            AttackerPosX = atkPos.x,
                            AttackerPosY = atkPos.y,
                            AttackerPosZ = atkPos.z,
                            AttackerPlayerId = net.LocalPlayerId,
                            VictimPlayerId = proxy.PlayerId
                        }.Serialize(w), DeliveryMethod.ReliableOrdered);
                    ModRuntime.LegacyInfo($"[HitscanFF] client hit proxy {proxy.PlayerId} dmg={dmg}");
                }

                float yRot = player.transform.eulerAngles.y;
                string bloodPrefab = inWater ? "FX/Bloodsplats/Shotsplat" : "FX/Bloodsplats/Shotsplat_stay";
                TraverseHack.ApplyingFromNetwork = true;
                try
                {
                    Core.AddPrefab(bloodPrefab, hitPoint,
                        Quaternion.Euler(90f, yRot + Random.Range(-20f, 20f), 0f), null);
                }
                finally { TraverseHack.ApplyingFromNetwork = false; }

                // Play spatialized bullet impact sound at the hit point so the
                // shooter hears the direction the proxy was hit from.
                AudioController.Play("bullet_hit_1", hitPoint);

                // Send BulletImpact so the other peer sees the same blood
                net.Send(NetMessageType.BulletImpact, w => new BulletImpactMessage
                {
                    PrefabName = bloodPrefab,
                    PoolName = "",
                    PosX = hitPoint.x,
                    PosY = hitPoint.y,
                    PosZ = hitPoint.z,
                    RotX = 90f,
                    RotY = yRot + Random.Range(-40f, 40f),
                    RotZ = 0f
                }.Serialize(w), DeliveryMethod.ReliableOrdered);

                return;
            }

            // Wall/entity FX is handled by HitscanImpactForwardPatch (Prefix on Core.AddPooledPrefab).
            // This Postfix only handles proxy hits.
        }
    }
}
