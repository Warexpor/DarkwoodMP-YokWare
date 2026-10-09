using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A dream NPC that already received co-op presence scaling. On the scene's own creature it
    /// lists the extras it brought, which follow it into its fight.
    /// </summary>
    public sealed class DreamBalanceProcessedMarker : MonoBehaviour
    {
        [System.NonSerialized] public List<Character> Extras;
    }

    /// <summary>
    /// Host-only: scale presence of allowlisted NPCs (default ChomperBlack) during dreams.
    /// Every dream black chomper is placed in its scene inactive and a story event shows it
    /// (<c>activateGameObject</c>, <c>gameObject setActive</c>, or <c>replaceCharacter</c> with a
    /// target, which activates it); none is spawned through <c>Core.AddPrefab</c>. So the extras
    /// come with the creature's first activation (<c>Character.Start</c>), the moment it enters
    /// the dream, at its spot. One placed active (dream_doctor_02) starts with the dream.
    /// Night hideout scenarios are intentionally not scaled.
    /// </summary>
    [HarmonyPatch(typeof(Character), "Start")]
    public static class NamedNpcScalePatch
    {
        private static bool _spawningExtra; // process-scoped: reentry guard, reset per session

        public static void Reset()
        {
            _spawningExtra = false;
            CoopBalance.InvalidateAllowlistCache();
        }

        private static void Postfix(Character __instance)
        {
            if (__instance == null || _spawningExtra || !__instance.alive)
                return;
            if (ModConfig.NamedNpcScaleEnabled != null && !ModConfig.NamedNpcScaleEnabled.Value)
                return;
            if (!NetGuard.ConnectedHost(out _))
                return;
            // The dream pad's creatures: the overworld has black chompers of the same name.
            if (!DreamSyncManager.IsDreamActive || !DreamSyncManager.IsAtDreamPad(__instance.transform.position))
                return;
            if (PersonalPrologue.IsOnProloguePad(__instance.transform))
                return;
            if (__instance.GetComponent<DreamBalanceProcessedMarker>() != null)
                return;

            string nameKey = CoopBalance.NormalizeNpcName(__instance.name);
            if (!CoopBalance.IsNamedNpcAllowlisted(nameKey))
                return;

            int mult = CoopBalance.GetPartyMultiplier();
            if (mult <= 1)
                return;

            var marker = __instance.gameObject.AddComponent<DreamBalanceProcessedMarker>();
            marker.Extras = SpawnExtras(__instance, ResolvePrefabPath(__instance.gameObject, nameKey), mult - 1);

            ModLog.Event(LogCat.Session,
                $"[DreamNpcScale] '{nameKey}' at {__instance.transform.position} mult={mult} extras={marker.Extras.Count}");
        }

        private static string ResolvePrefabPath(GameObject go, string shortName)
        {
            var pathComp = go.GetComponent<PrefabPathComponent>();
            if (pathComp != null && !string.IsNullOrEmpty(pathComp.Path))
                return pathComp.Path;
            return "Characters/" + shortName;
        }

        /// <summary>
        /// Extras stand around the original, in its parent: the dream pad is destroyed with them in
        /// it when the dream ends (unparented, they outlived it). They take the original's scene
        /// setup (how it reacts to the player, relentless pursuit); <see cref="OnAttack"/> gives
        /// them its fight.
        /// </summary>
        private static List<Character> SpawnExtras(Character original, string prefabPath, int count)
        {
            var extras = new List<Character>(count);
            Vector3 basePos = original.transform.position;
            Quaternion rot = original.transform.rotation;
            GameObject parent = original.transform.parent != null ? original.transform.parent.gameObject : null;

            _spawningExtra = true;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Vector3 spawnPos = SpotNear(basePos);
                    spawnPos.y = basePos.y;

                    GameObject go = Core.AddPrefab(prefabPath, spawnPos, rot, parent, worldSpace: true);
                    if (go == null)
                    {
                        ModLog.Warn(LogCat.Session, "[DreamNpcScale] AddPrefab failed for " + prefabPath);
                        continue;
                    }
                    go.AddComponent<DreamBalanceProcessedMarker>();

                    Character extra = go.GetComponent<Character>();
                    if (extra == null)
                        continue;
                    extra.aggressiveness = original.aggressiveness;
                    extra.relentlessPursuit = original.relentlessPursuit;
                    // The scene's own tuning of this creature (the grave meadow's is faster and is
                    // never cut in half): the prefab's defaults made the extras a different beast.
                    extra.chaseSpeed = original.chaseSpeed;
                    extra.canBeCutInHalf = original.canBeCutInHalf;
                    extra.isActive = true;
                    extras.Add(extra);
                }
            }
            finally
            {
                _spawningExtra = false;
            }
            return extras;
        }

        /// <summary>
        /// A walkable spot 30-90 from the original, on the same side of a wall as it (indoors when
        /// it stands indoors, as in the church ruins). Vanilla's randomPosAround with canBeInside
        /// false retries itself with a shrinking radius while the spot is indoors and never stops
        /// for a creature standing indoors: the church dream's chomper overflowed the stack and
        /// took the host's game down.
        /// </summary>
        private static Vector3 SpotNear(Vector3 basePos)
        {
            bool baseInside = Physics.Raycast(new Ray(basePos, Vector3.down), 200f, 2);
            for (int i = 0; i < 12; i++)
            {
                Vector3 v = basePos + Core.RandomOnUnitCircle3(Random.Range(30f, 90f), 0f);
                if (!baseInside && Physics.Raycast(new Ray(v, Vector3.down), 200f, 2))
                    continue;
                return OnGraph(v);
            }
            return OnGraph(basePos);
        }

        private static Vector3 OnGraph(Vector3 v)
        {
            if (AstarPath.active == null)
                return v;
            Pathfinding.GraphNode node = AstarPath.active.GetNearest(v, Singleton<Controller>.Instance.pathNoneConstraint).node;
            return node != null ? (Vector3)node.position : v;
        }

        /// <summary>
        /// Host, after an attack order: the story's attack events and activities target only the
        /// scene's creature, so its extras join its fight (each on the arbiter's pick for it).
        /// An extra already fighting (it sees the one it chases) keeps its own target. A target
        /// alone is not a fight: an extra handed a player before the order (its first look around
        /// at Start) had no path to it and stood at its spawn until someone walked up to it.
        /// </summary>
        internal static void OnAttack(Character c)
        {
            if (c == null || c.target == null)
                return;
            var marker = c.GetComponent<DreamBalanceProcessedMarker>();
            if (marker == null || marker.Extras == null)
                return;
            for (int i = 0; i < marker.Extras.Count; i++)
            {
                Character extra = marker.Extras[i];
                if (extra == null || !extra.alive
                    || (extra.target != null && extra.behaviour == Character.Behaviour.chasingTarget && extra.canSeeEnemyFar))
                    continue;
                extra.aggressiveness = c.aggressiveness;
                extra.relentlessPursuit = c.relentlessPursuit;
                extra.attackCharacter(c.target);
            }
        }
    }
}
