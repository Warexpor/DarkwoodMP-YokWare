using System;
using System.Collections.Generic;
using System.Reflection;
using DWMPHorde;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using PathologicalGames;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host: on a shadow of a peer's Shadows wave. The shadow runs vanilla <c>ShadowCreature</c>
    /// (appear every 2-6 s at its closing distance, the Float clip's light check and three swipes,
    /// death at day or when the wave is removed), measured from the owner's body instead of
    /// <c>Player.Instance</c> (<see cref="PeerShadows"/>). This component only ends the shadow when
    /// its owner is gone, dead or held dead for the night, as vanilla's player-death hook does.
    /// </summary>
    public class ProxyShadowController : MonoBehaviour
    {
        /// <summary>The owner's stand-in. Set by the spawn capture before Start.</summary>
        public Transform TargetProxy { get; set; }
        public int OwnerPlayerId;

        private ShadowCreature _shadow;

        internal bool OwnerGone()
        {
            if (TargetProxy == null)
                return true;
            RemotePlayerProxy owner = TargetProxy.GetComponent<RemotePlayerProxy>();
            if (owner == null)
                return true;
            CharBase cb = owner.CachedCharBase;
            return (cb != null && !cb.alive) || DeathStateTracker.IsRemoteNightDead(owner.PlayerId);
        }

        private void Update()
        {
            if (_shadow == null)
                _shadow = GetComponent<ShadowCreature>();
            if (_shadow == null || !OwnerGone())
                return;
            enabled = false;
            PeerShadows.Die(_shadow);
        }
    }

    /// <summary>
    /// Vanilla <c>ShadowCreature</c> for a peer's wave, with the owner's body as "the player".
    /// Vanilla reads <c>Player.Instance</c> in Start (closing distance, death hook), appear (where it
    /// shows up, the owner's light), canSpawnHere and spawnMeleeSensor (the owner standing in light
    /// is never struck), and die counts down the cursed player's own wave. The host's body and its
    /// wave counters stay out of a peer's wave. The swipe's sensor hits the owner's stand-in and
    /// reaches the owner as a shadow hit (host proxy-hit relay).
    /// </summary>
    internal static class PeerShadows
    {
        private static readonly MethodInfo DieMethod = AccessTools.Method(typeof(ShadowCreature), "die");
        private static readonly MethodInfo ListenerMethod = AccessTools.Method(typeof(ShadowCreature), "animationTriggerListener");

        /// <summary>Shadows of each peer's wave still counted (vanilla spawnedShadowsAmount, per owner).</summary>
        private static readonly Dictionary<int, int> _waveLeft = new Dictionary<int, int>(); // reset-in: Reset

        internal static void Reset() => _waveLeft.Clear();

        /// <summary>A peer's wave starts: 8 shadows, as vanilla tryToSpawnShadow counts.</summary>
        internal static void BeginWave(int ownerPlayerId) => _waveLeft[ownerPlayerId] = 8;

        internal static ProxyShadowController Of(ShadowCreature sc)
            => sc != null ? sc.GetComponent<ProxyShadowController>() : null;

        internal static void Die(ShadowCreature sc)
        {
            if (sc != null && DieMethod != null)
                DieMethod.Invoke(sc, null);
        }

        /// <summary>Vanilla Start, around the owner.</summary>
        internal static void Start(ShadowCreature sc, ProxyShadowController ctrl)
        {
            Transform owner = ctrl.TargetProxy;
            if (owner != null)
                sc.distanceToPlayer = Core.trueDistance(sc.transform.position, owner.position);
            var anim = sc.GetComponent<tk2dSpriteAnimator>();
            if (anim != null && ListenerMethod != null)
                anim.AnimationEventTriggered = (Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip, int>)Delegate.Combine(
                    anim.AnimationEventTriggered,
                    Delegate.CreateDelegate(typeof(Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip, int>), sc, ListenerMethod));
            // Vanilla also hooks the local player's death; the owner's is watched by the controller.
            Controller ctl = Singleton<Controller>.Instance;
            if (ctl != null && DieMethod != null)
                ctl.onDayStart = (controllerDelegate)Delegate.Combine(ctl.onDayStart,
                    Delegate.CreateDelegate(typeof(controllerDelegate), sc, DieMethod));
            var spawner = Singleton<CharacterSpawner>.Instance;
            if (spawner != null && !spawner.spawnedCharacters.Contains(sc.gameObject))
                spawner.spawnedCharacters.Add(sc.gameObject);
            sc.appear();
        }

        /// <summary>Vanilla appear, around the owner.</summary>
        internal static void Appear(ShadowCreature sc, ProxyShadowController ctrl)
        {
            var spawner = Singleton<CharacterSpawner>.Instance;
            RemotePlayerProxy owner = ctrl.TargetProxy != null ? ctrl.TargetProxy.GetComponent<RemotePlayerProxy>() : null;
            if (sc.immortal && owner != null && owner.RemoteHasShadowWard)
            {
                Die(sc);
                return;
            }
            if (!sc.immortal && spawner != null && spawner.shadowsPaused)
                return;
            if (Core.isDay() || ctrl.OwnerGone() || (!sc.immortal && spawner != null && spawner.shadowsRemove))
            {
                Die(sc);
                return;
            }
            sc.dead = false;
            sc.alreadyHitPlayer = false;
            Vector3 center = ctrl.TargetProxy.position;
            // Vanilla retries 75 further out until the spot is dark (its distance is clamped to
            // 1500 by Update only afterwards); 40 steps reach 3000 beyond the start.
            for (int i = 0; i < 40; i++)
            {
                sc.transform.position = center + Core.RandomOnUnitCircle3(sc.distanceToPlayer, 0f);
                sc.transform.rotation = Quaternion.Euler(90f, UnityEngine.Random.Range(0, 360), 0f);
                if (CanSpawnHere(sc, center))
                {
                    AudioController.Play("shadow_spawn", sc.transform.position);
                    sc.GetComponent<tk2dSpriteAnimator>().Play("Float");
                    return;
                }
                sc.distanceToPlayer += 75f;
            }
        }

        /// <summary>Vanilla canSpawnHere with the owner at <paramref name="ownerPos"/>.</summary>
        private static bool CanSpawnHere(ShadowCreature sc, Vector3 ownerPos)
        {
            if (sc.immortal)
                return true;
            if (Core.trueDistance(sc.transform.position, ownerPos) < 100f && !Core.isInLight(ownerPos, mustBeWalkable: false))
                return true;
            int row = 0, col = 0;
            const int n = 4;
            float x = sc.GetComponent<tk2dSprite>().GetBounds().size.x;
            Vector3 corner = sc.transform.position - new Vector3(x / 2f + 150f, 0f, x / 2f + 50f);
            for (int i = 0; i < n * n; i++)
            {
                if (Core.isInLight((corner + new Vector3(col * 100f, 0f, row * 100f))
                        .rotateAround(sc.transform.position, sc.transform.rotation.eulerAngles.y), mustBeWalkable: false))
                    return false;
                row++;
                if (row == n)
                {
                    row = 0;
                    col++;
                }
            }
            return true;
        }

        /// <summary>
        /// Vanilla spawnMeleeSensor: no swipe at an owner standing in light (a lit path node under
        /// it, or a light area / shadow-protecting item it reports).
        /// </summary>
        internal static void Swipe(ShadowCreature sc, ProxyShadowController ctrl, int id)
        {
            if (sc.alreadyHitPlayer || ctrl.TargetProxy == null)
                return;
            RemotePlayerProxy owner = ctrl.TargetProxy.GetComponent<RemotePlayerProxy>();
            if (owner != null && ModRuntime.Network != null && ModRuntime.Network.IsRemotePlayerHasLightProtection(owner.PlayerId))
                return;
            if (Core.isInLight(ctrl.TargetProxy.position, mustBeWalkable: true))
                return;
            MeleeSensor sensor = PoolManager.Pools["Sensors"].Spawn(PoolManager.Pools["Sensors"].prefabs["MeleeSensor_shadow"],
                sc.transform.position, Quaternion.Euler(90f, sc.transform.eulerAngles.y, 0f)).GetComponent<MeleeSensor>();
            switch (id)
            {
                case 1:
                    sensor.GetComponent<BoxCollider>().center = new Vector3(45f, -110f, 0f);
                    break;
                case 2:
                    sensor.GetComponent<BoxCollider>().center = new Vector3(0f, 0f, 0f);
                    break;
                case 3:
                    sensor.GetComponent<BoxCollider>().center = new Vector3(-20f, 100f, 0f);
                    break;
            }
            sensor.attackerTransform = sc.transform;
            sensor.type = MeleeSensor.MeleeSensorType.character;
            sensor.damage = sc.damage;
            sensor.onHit = (meleeSensorDelegate)Delegate.Combine(sensor.onHit, (meleeSensorDelegate)(() => sc.alreadyHitPlayer = true));
        }

        /// <summary>Vanilla die, counting down the owner's wave instead of the host's.</summary>
        internal static void DieOwned(ShadowCreature sc, ProxyShadowController ctrl)
        {
            var info = sc.GetComponent<ShadowSyncInfo>();
            var net = ModRuntime.Network;
            if (info != null && net != null)
                net.WorldSendHandlers.UnregisterShadow(info.ShadowId);
            if (sc.gameObject != null)
                UnityEngine.Object.Destroy(sc.gameObject);
            int owner = ctrl.OwnerPlayerId;
            if (!_waveLeft.TryGetValue(owner, out int left))
                return;
            left--;
            if (left > 0)
            {
                _waveLeft[owner] = left;
                return;
            }
            // The owner's endShadows (vanilla, when the wave's count runs out).
            _waveLeft.Remove(owner);
            if (net != null && net.Role == NetworkRole.Host && net.IsConnected)
                net.SendShadowEvent(new ShadowEventMessage { End = true, OwnerId = (short)owner });
        }
    }

    [HarmonyPatch(typeof(ShadowCreature), "Start")]
    public static class PeerShadowStartPatch
    {
        private static bool Prefix(ShadowCreature __instance)
        {
            ProxyShadowController ctrl = PeerShadows.Of(__instance);
            if (ctrl == null)
                return true;
            PeerShadows.Start(__instance, ctrl);
            return false;
        }
    }

    [HarmonyPatch(typeof(ShadowCreature), "appear")]
    public static class PeerShadowAppearPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(ShadowCreature __instance)
        {
            ProxyShadowController ctrl = PeerShadows.Of(__instance);
            if (ctrl == null)
                return true;
            PeerShadows.Appear(__instance, ctrl);
            return false;
        }

        /// <summary>
        /// Host: every shadow's appearance (vanilla's or a peer's) reaches the other players as a
        /// re-spawn of that shadow, so their copies show up where it does and play its Float clip.
        /// </summary>
        private static void Postfix(ShadowCreature __instance)
        {
            if (__instance == null || __instance.dead || !NetGuard.Host(out var net) || !net.IsConnected)
                return;
            var info = __instance.GetComponent<ShadowSyncInfo>();
            // Gone already (it died in this appear): a re-spawn would bring its copies back.
            if (info == null || !net.Shadows.Tracked.ContainsKey(info.ShadowId))
                return;
            Vector3 pos = __instance.transform.position;
            if ((pos - info.LastAnnounced).sqrMagnitude < 1f)
                return;
            info.LastAnnounced = pos;
            net.SendShadowSpawn(new ShadowSpawnMessage
            {
                ShadowId = info.ShadowId,
                ShadowType = info.ShadowType,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                RotY = __instance.transform.rotation.eulerAngles.y,
                DistanceToPlayer = __instance.distanceToPlayer,
                Flags = 0
            });
        }
    }

    [HarmonyPatch(typeof(ShadowCreature), "die")]
    public static class PeerShadowDiePatch
    {
        private static bool Prefix(ShadowCreature __instance)
        {
            ProxyShadowController ctrl = PeerShadows.Of(__instance);
            if (ctrl == null)
                return true;
            PeerShadows.DieOwned(__instance, ctrl);
            return false;
        }
    }

    /// <summary>
    /// Vanilla <c>shadowLightsTurnOff</c> (the Shadows night scene) flickers the generator lights of
    /// the hideout the player stands in, leaving one on. Run for a peer's scene on the host, it
    /// flickered the host's hideout: it now does the scene player's.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.shadowLightsTurnOff))]
    public static class PeerShadowLightsTurnOffPatch
    {
        private static bool Prefix(Player __instance)
        {
            if (!NetGuard.Host(out _))
                return true;
            Transform actor = DWMPHorde.Sync.GeFireActorContext.ActorBody();
            if (actor == null || actor == __instance.transform || actor == __instance._transform)
                return true;
            if (Singleton<CharacterSpawner>.Instance.shadowsRemove || Core.isDay())
                return false;
            Location loc = Location.getAtPos(actor.position);
            if (loc != null && loc.bigLocation != null)
                loc = loc.bigLocation;
            Generator generator = loc != null ? loc.generator : null;
            if (generator == null)
                return false;
            var lights = new List<Item>(generator.powerItems);
            lights.Remove(generator.GetComponent<Item>());
            if (lights.Count <= 0)
                return false;
            var candidates = new List<Item>(lights);
            for (int i = candidates.Count - 1; i >= 0; i--)
                if (candidates[i] != null && candidates[i].dontTurnOnByShadowsEvent)
                    candidates.RemoveAt(i);
            if (candidates.Count == 0)
                return false;
            Item keep = candidates[UnityEngine.Random.Range(0, candidates.Count)];
            for (int i = 0; i < lights.Count; i++)
            {
                keep.turnOn();
                lights[i].enable();
                lights[i].itemLight.flicker_short(8f);
            }
            lights.Remove(keep);
            Singleton<Controller>.Instance.Invoke(delegate
            {
                for (int j = 0; j < lights.Count; j++)
                    if (lights[j] != null)
                        lights[j].empDisable(30f);
                if (keep != null)
                    keep.enable();
            }, 8f, timeScaleDependent: true);
            return false;
        }
    }
}
