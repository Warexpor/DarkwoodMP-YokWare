using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Scene objects the desync check reads near a player, registered as they wake. A scene-wide
    /// FindObjectsOfType costs tens of milliseconds; the check runs every few seconds on every
    /// machine, so it walks these sets instead. The check looks around one focus player, and on
    /// the other machines that spot can sit in a chunk that never woke there (the host never went
    /// near the camp a client is looting: "Containers camp_fire_doctor_camp…: host=&lt;none&gt;").
    /// So the first snapshot after each scene load also takes every object of the type, inactive
    /// ones included, once.
    /// </summary>
    internal static class DesyncRegistry<T> where T : Component
    {
        private static readonly HashSet<T> _set = new HashSet<T>(); // process-scoped: scene objects outlive a session; destroyed ones pruned in Snapshot
        private static readonly List<T> _dead = new List<T>(); // process-scoped: scratch
        private static bool _seeded; // process-scoped: cleared on every scene load

        static DesyncRegistry()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => _seeded = false;
        }

        internal static void Add(T c)
        {
            if (c != null)
                _set.Add(c);
        }

        /// <summary>The live members (destroyed ones dropped) copied into <paramref name="into"/>.</summary>
        internal static void Snapshot(List<T> into)
        {
            into.Clear();
            _dead.Clear();
            if (!_seeded)
            {
                _seeded = true;
                T[] found = Object.FindObjectsOfType<T>(true);
                for (int i = 0; i < found.Length; i++)
                    _set.Add(found[i]);
            }
            foreach (T c in _set)
            {
                if (c == null)
                    _dead.Add(c);
                else
                    into.Add(c);
            }
            for (int i = 0; i < _dead.Count; i++)
                _set.Remove(_dead[i]);
            _dead.Clear();
        }
    }

    [HarmonyPatch(typeof(Item), "Awake")]
    internal static class DesyncItemAwakePatch
    {
        private static void Postfix(Item __instance) => DesyncRegistry<Item>.Add(__instance);
    }

    [HarmonyPatch(typeof(Inventory), "Start")]
    internal static class DesyncInventoryStartPatch
    {
        private static void Postfix(Inventory __instance) => DesyncRegistry<Inventory>.Add(__instance);
    }

    [HarmonyPatch(typeof(Trigger), "Awake")]
    internal static class DesyncTriggerAwakePatch
    {
        private static void Postfix(Trigger __instance) => DesyncRegistry<Trigger>.Add(__instance);
    }

    [HarmonyPatch(typeof(Burn), "Start")]
    internal static class DesyncBurnStartPatch
    {
        private static void Postfix(Burn __instance) => DesyncRegistry<Burn>.Add(__instance);
    }

    [HarmonyPatch(typeof(NPC), "Awake")]
    internal static class DesyncNpcAwakePatch
    {
        private static void Postfix(NPC __instance) => DesyncRegistry<NPC>.Add(__instance);
    }
}
