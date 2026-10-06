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
                    Vector3 spawnPos;
                    try
                    {
                        // Outside walls, on the dream's walk graph.
                        spawnPos = Core.randomPosAround(basePos, 30f, 90f, canBeInside: false, mustBeInsideGraph: true);
                    }
                    catch
                    {
                        spawnPos = basePos + new Vector3(Random.Range(-60f, 60f), 0f, Random.Range(-60f, 60f));
                    }
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
        /// Host, after an attack order: the story's attack events and activities target only the
        /// scene's creature, so its extras join its fight (each on the arbiter's pick for it).
        /// An extra already fighting keeps its own target.
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
                if (extra == null || !extra.alive || extra.target != null)
                    continue;
                extra.aggressiveness = c.aggressiveness;
                extra.relentlessPursuit = c.relentlessPursuit;
                extra.attackCharacter(c.target);
            }
        }
    }
}
