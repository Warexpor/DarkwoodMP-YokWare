using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    public static partial class WorldPhysicsSyncService
    {
        public static void PlayExplosionSound(Vector3 pos, string soundId, string objectName = null, Explodes target = null)
        {
            string id = ResolveExplosionSoundId(soundId, objectName, target);
            if (string.IsNullOrEmpty(id))
                return;

            // Distance cull: as far as the game lets this explosion carry.
            if (!LocalAudioService.IsNearListenerPeerBand(pos, LocalAudioService.AudibleRange(id)))
                return;

            bool prev = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                // Positional 3D play with no parent, matching vanilla explode().
                // Fully 3D from its first moment (PeerSpatialPlay).
                Action<AudioSource> spatial = src => src.spatialBlend = 1f;
                AudioObject ao = PeerSpatialPlay.Play(() => AudioController.Play(id, pos, null, 1f), spatial);
                if (ao == null && id == "mushroom_explode_01")
                {
                    // Alternate clip id seen in decompiled assets.
                    ao = PeerSpatialPlay.Play(() => AudioController.Play("expObj_mushroom_01", pos, null, 1f), spatial);
                    if (ao != null) id = "expObj_mushroom_01";
                }

                ModRuntime.LegacyInfo("[ExplosionSound] play '" + id + "' at " + pos
                    + (ao != null ? " ok" : " (AudioController returned null)"));
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ExplosionSound] failed: " + ex.Message);
            }
            finally
            {
                TraverseHack.SetExplicitFlag(prev);
            }
        }

        /// <summary>
        /// A name match is only trusted this close to the reported position. A bare
        /// GameObject.Find(name) returned the first same-named barrel anywhere (including the
        /// overworld twin of a dream-pad barrel), so the wrong barrel was destroyed and the host
        /// could run a real explode() on an unrelated one.
        /// </summary>
        private const float ExplodesNameMatchMaxDist = 8f;

        /// <summary>
        /// Position-first Explodes lookup for a named explosion: nearest same-named Explodes within
        /// <see cref="ExplodesNameMatchMaxDist"/> of <paramref name="pos"/>, on the same side
        /// (dream pad vs overworld) as the explosion. Never a scene-wide name search.
        /// </summary>
        private static Explodes ResolveExplodesByNameNear(string objectName, Vector3 pos)
        {
            if (string.IsNullOrEmpty(objectName)) return null;
            Explodes e = WorldQueryHelper.FindNearestByName<Explodes>(
                pos, objectName, ExplodesNameMatchMaxDist);
            if (e == null) return null;

            return IsOnSameWorldSide(pos, e.transform) ? e : null;
        }

        /// <summary>
        /// True when <paramref name="target"/> is on the same side (dream pad vs overworld) as
        /// <paramref name="pos"/>. The pad is a clone of overworld locations, so a same-named object
        /// on the other side must never be picked for a position reported on this side.
        /// </summary>
        internal static bool IsOnSameWorldSide(Vector3 pos, Transform target)
        {
            if (target == null) return false;
            Transform pad = DreamSyncManager.GetDreamLocationTransform();
            bool posOnPad = pad != null && Vector3.Distance(pos, pad.position) <= DreamPadRadius;
            bool targetOnPad = pad != null
                && (target.IsChildOf(pad)
                    || Vector3.Distance(target.position, pad.position) <= DreamPadRadius);
            return posOnPad == targetOnPad;
        }

        private const float DreamPadRadius = 250f;

        /// <summary>
        /// The Explodes at that spot with that name, else a same-named one close by (the host moved
        /// or rolled it). Any Explodes within reach used to do: a client's molotov landing by a
        /// barrel set the barrel off on the host (whose blast is the only one that hurts) and
        /// blew up the barrel's copy on the peers.
        /// </summary>
        private static Explodes FindExplodesAt(Vector3 pos, string objectName)
        {
            int nearbyN = OverlapNear(pos, 1.5f);
            Explodes unnamed = null;
            string want = DialogOutcomeCloseNetHandlers.StripCloneSuffix(objectName ?? "");
            for (int i = 0; i < nearbyN; i++)
            {
                if (_overlap3D[i] == null) continue;
                Explodes expl = _overlap3D[i].GetComponentInParent<Explodes>();
                if (expl == null) continue;
                if (string.IsNullOrEmpty(want)
                    || string.Equals(DialogOutcomeCloseNetHandlers.StripCloneSuffix(expl.name), want, StringComparison.Ordinal))
                    return expl;
                if (unnamed == null)
                    unnamed = expl;
            }
            Explodes named = ResolveExplodesByNameNear(objectName, pos);
            if (named != null)
                return named;
            return string.IsNullOrEmpty(want) ? unnamed : null;
        }

        public static void TriggerExplosion(Vector3 pos, string objectName, bool flaming = false, string soundId = null)
        {
            Explodes target = FindExplodesAt(pos, objectName);

            if (target != null)
            {
                ModRuntime.LegacyInfo($"[ExplosionTrigger] activating {target.name} at {pos} flaming={flaming}");
                _suppressBroadcast = true;
                try
                {
                    if (flaming)
                        target.onActivate(true);
                    else
                        target.onActivate();
                }
                finally { _suppressBroadcast = false; }
                // explode() plays explodeSound when effect is set. If effect is null,
                // Vanilla skips sound, so play the message or fallback audio.
                if (target.effect == null)
                    PlayExplosionSound(pos, soundId, objectName, target);
                // Proxy damage handled by ExplosionFriendlyFirePatch.Postfix on Explodes.explode()
            }
            else
            {
                ModLog.WarnRate(LogCat.Physics, "explosion-no-target:" + objectName,
                    "[ExplosionTrigger] no Explodes found at " + pos + " name=" + objectName);
                // Object may already be destroyed or out of range; still play the host's boom.
                PlayExplosionSound(pos, soundId, objectName, null);
            }
        }

        /// <summary>
        /// Client-side explosion VFX without damage: mirrors vanilla onActivate visual half
        /// (spawnObjects + explosionPrefab + sound + optional destroy), never explode().
        /// Host ExplosionSpawnObject remains fallback when no local Explodes exists.
        /// </summary>
        public static void SpawnExplosionVisual(Vector3 pos, string objectName, string prefabName, string soundId)
        {
            Explodes target = FindExplodesAt(pos, objectName);

            // Sound is independent of visual success. Play first so already-activated or
            // destroyed mushrooms still boom on peers.
            PlayExplosionSound(pos, soundId, objectName, target);

            if (target != null)
            {
                // Don't re-spawn VFX if already activated on this side
                try
                {
                    bool activated = (bool)Traverse.Create(target).Field("activated").GetValue();
                    if (activated)
                    {
                        ModRuntime.LegacyInfo($"[ExplosionVisual] {target.name} already activated, VFX skip (sound already played)");
                        return;
                    }
                }
                catch { if (ModRuntime.VerboseLogging) ModRuntime.Log?.LogWarning("[WPSS] caught exception"); }

                try
                {
                    // Vanilla order: activated first so re-entry / ExplosionSpawnObject won't
                    // run a second full onActivate path against this component.
                    Traverse.Create(target).Field("activated").SetValue(true);

                    // 1) Secondary debris/FX (mushroom white spawnObject)
                    if (target.spawnObject != null)
                    {
                        Traverse.Create(target).Method("spawnObjects").GetValue();
                        ModRuntime.LegacyInfo($"[ExplosionVisual] spawnObjects() for {target.name} at {pos}");
                    }

                    // 2) Main boom VFX
                    if (target.explosionPrefab != null)
                    {
                        Core.AddPrefab(target.explosionPrefab, pos, Quaternion.Euler(90f, 0f, 0f), null, worldSpace: true);
                        ModRuntime.LegacyInfo($"[ExplosionVisual] spawned local prefab {target.explosionPrefab.name} at {pos}");
                    }

                    // Dedupe host ExplosionSpawnObject for the secondaries we just spawned
                    DWMPHorde.Patches.ExplosionSpawnFlagTracker.NoteLocalExplodeFx(pos);

                    // 3) Match vanilla destroy without calling explode() (no double damage)
                    if (target.destroyOnExplode && target.gameObject != null)
                        target.gameObject.DestroyMe();
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[ExplosionVisual] failed: " + ex.Message);
                }

                return;
            }

            // Fallback: no local Explodes (already destroyed / never loaded).
            ModRuntime.LegacyInfo($"[ExplosionVisual] no local Explodes for \"{objectName}\", using message data");

            if (!string.IsNullOrEmpty(prefabName))
            {
                try
                {
                    string[] prefixes = { "", "Items/", "FX/", "Environment/", "Particles/", "Dummies/", "Fire/", "Weapons/" };
                    UnityEngine.Object prefab = null;
                    for (int i = 0; i < prefixes.Length; i++)
                    {
                        prefab = Resources.Load("Prefabs/" + prefixes[i] + prefabName);
                        if (prefab != null) break;
                    }
                    // Also try particles/mushroom_explode style paths from ResourceManager.
                    if (prefab == null)
                        prefab = Resources.Load("Prefabs/Particles/" + prefabName);
                    if (prefab != null)
                    {
                        Core.AddPrefab(prefab, pos, Quaternion.Euler(90f, 0f, 0f), null, worldSpace: true);
                        ModRuntime.LegacyInfo($"[ExplosionVisual] fallback prefab {prefabName} at {pos}");
                    }
                    else
                    {
                        Core.AddPrefab(prefabName, pos, Quaternion.Euler(90f, 0f, 0f), null, worldSpace: true);
                    }
                    DWMPHorde.Patches.ExplosionSpawnFlagTracker.NoteLocalExplodeFx(pos);
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[ExplosionVisual] fallback prefab failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Spawns a "Items/GasolineTrail" prefab at the given world position.
        /// Used when the remote peer reports a gasoline pour. Nest-safe apply flag
        /// (IgniteGasAtPos may call this while already applying).
        /// </summary>
        public static void SpawnGasTrail(Vector3 pos)
        {
            // Dedupe: slightly wider than host scatter step to avoid double puddles under jitter.
            if (FindFlammableLiquidNear(pos, 1.15f) != null || HasLiquidInScene(pos, 1.15f, null))
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[GasTrail] skip spawn — liquid already near {pos}");
                return;
            }

            bool prev = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                // Explicit flag so client GasolineTrail Prefix allows this network apply path.
                GameObject go = Core.AddPrefab("Items/GasolineTrail", pos, Quaternion.Euler(90f, 0f, 0f), Core.ItemContainer);
                if (go != null)
                {
                    Core.addToSaveable(go, isDynamic: true);
                    ModRuntime.LegacyInfo($"[GasTrail] spawned at {pos}");
                }
                else
                {
                    ModRuntime.Log?.LogWarning("[GasTrail] Core.AddPrefab returned null at " + pos);
                }
            }
            finally { TraverseHack.SetExplicitFlag(prev); }
        }

        /// <summary>
        /// Finds a Liquid component (gasoline puddle) near the given position and ignites it.
        /// Used when the remote peer reports a gasoline ignition event.
        /// <paramref name="spawnIfMissing"/>: a client applying the host's ignite lays the trail
        /// first when packet order hid it. The host adopting a client's ignite never does: the
        /// host's world has every real puddle, so a miss there is not a puddle to invent.
        /// </summary>
        public static void IgniteGasAtPos(Vector3 pos, bool spawnIfMissing = true)
        {
            bool prev = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                Liquid liquid = FindFlammableLiquidNear(pos, 2.25f);
                if (liquid != null)
                {
                    if (!liquid.burning)
                    {
                        liquid.startBurning();
                        ModRuntime.LegacyInfo($"[GasIgnite] ignited {liquid.name} at {pos}");
                    }
                    return;
                }
                if (!spawnIfMissing)
                {
                    ModRuntime.LegacyInfo($"[GasIgnite] no flammable Liquid at {pos}, nothing lit");
                    return;
                }

                // If packet reordering hid the trail, spawn it before igniting under the apply flag.
                SpawnGasTrail(pos);
                liquid = FindFlammableLiquidNear(pos, 2.25f);
                if (liquid != null && !liquid.burning)
                {
                    liquid.startBurning();
                    ModRuntime.LegacyInfo($"[GasIgnite] spawned+ignited trail at {pos}");
                    return;
                }
                ModRuntime.Log?.LogWarning("[GasIgnite] no flammable Liquid found at " + pos + " (even after spawning)");
            }
            finally { TraverseHack.SetExplicitFlag(prev); }
        }

        /// <summary>
        /// Nearest flammable Liquid within <paramref name="radius"/> on the ground plane (XZ).
        /// A puddle is spawned at the blast height and then drops to the ground-item height on
        /// its own (GasolineTrail.init / the item's Y snap, ~36 units lower), so a 3D sphere at a
        /// reported spawn/ignite position missed it: the host then laid a new trail there and lit
        /// that. Searched along a vertical column like vanilla ThrownItem.onCollide's raycast.
        /// <paramref name="burning"/>: null any, true only lit puddles, false only unlit ones.
        /// <paramref name="name"/>: only puddles of that prefab (<see cref="NormalizeObjectName"/> form).
        /// </summary>
        private static Liquid FindFlammableLiquidNear(Vector3 pos, float radius, bool? burning = null, string name = null)
        {
            int nearbyN = Physics.OverlapCapsuleNonAlloc(
                new Vector3(pos.x, LiquidColumnBottomY, pos.z),
                new Vector3(pos.x, LiquidColumnTopY, pos.z),
                radius, _overlap3D);
            Liquid best = null;
            float bestSq = radius * radius;
            for (int i = 0; i < nearbyN; i++)
            {
                if (_overlap3D[i] == null) continue;
                Liquid liquid = _overlap3D[i].GetComponent<Liquid>();
                if (liquid == null) liquid = _overlap3D[i].GetComponentInParent<Liquid>();
                if (liquid == null || !liquid.flammable) continue;
                if (burning.HasValue && liquid.burning != burning.Value) continue;
                if (name != null && NormalizeObjectName(liquid.gameObject.name) != name)
                    continue;
                float dSq = XzDistSq(liquid.transform.position, pos);
                if (dSq <= bestSq)
                {
                    bestSq = dSq;
                    best = liquid;
                }
            }
            return best;
        }

        /// <summary>Vanilla ThrownItem.onCollide liquid raycast: from y=600 down 1000.</summary>
        private const float LiquidColumnTopY = 600f;
        private const float LiquidColumnBottomY = -400f;

        /// <summary>Late join: a puddle of this prefab already lies here (world-placed or sent twice).</summary>
        internal static bool HasFlammableLiquidAt(Vector3 pos, string prefabName, float radius)
        {
            string name = NormalizeObjectName(prefabName);
            return FindFlammableLiquidNear(pos, radius, null, name) != null || HasLiquidInScene(pos, radius, name);
        }

        /// <summary>
        /// A flammable puddle lies here, culled ones included. The physics search sees only active
        /// colliders, and a joiner's puddles away from it are culled (inactive) after the load: the
        /// late-join gas state laid a second copy on each one it had from the save.
        /// </summary>
        private static bool HasLiquidInScene(Vector3 pos, float radius, string name)
        {
            Liquid[] all = WorldQueryHelper.GetCachedSceneComponents<Liquid>();
            float rSq = radius * radius;
            for (int i = 0; i < all.Length; i++)
            {
                Liquid liquid = all[i];
                if (liquid == null || !liquid.flammable) continue;
                if (name != null && NormalizeObjectName(liquid.gameObject.name) != name) continue;
                if (XzDistSq(liquid.transform.position, pos) <= rSq) return true;
            }
            return false;
        }

        /// <summary>
        /// Host's puddle went out: put out this peer's copy, the nearest lit flammable puddle on the
        /// ground plane. The first Liquid in a 1.5 sphere used to be taken, lit or not, so a dense
        /// spill put out (or deleted, vanilla stopBurning destroys it) a neighbour that was still
        /// burning on the host, and the fire died early here.
        /// </summary>
        internal static bool StopLiquidBurningAt(Vector3 pos)
        {
            Liquid liq = FindFlammableLiquidNear(pos, LiquidStopMatchRadius, burning: true);
            if (liq == null)
                return false;
            bool prevNet = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                LiquidStopBurningMethod?.Invoke(liq, null);
            }
            finally { TraverseHack.SetExplicitFlag(prevNet); }
            return true;
        }

        private const float LiquidStopMatchRadius = 1.5f;

        private static readonly System.Reflection.MethodInfo LiquidStopBurningMethod =
            AccessTools.Method(typeof(Liquid), "stopBurning");
    }
}
