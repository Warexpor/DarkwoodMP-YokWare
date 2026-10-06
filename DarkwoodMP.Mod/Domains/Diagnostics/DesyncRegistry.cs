using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Scene objects the desync check reads near a player, registered as they wake. A scene-wide
    /// FindObjectsOfType costs tens of milliseconds; the check runs every few seconds on every
    /// machine, so it walks these sets instead. Objects in a chunk that never woke are not in
    /// them, and nothing near a player is in such a chunk.
    /// </summary>
    internal static class DesyncRegistry<T> where T : Component
    {
        private static readonly HashSet<T> _set = new HashSet<T>(); // process-scoped: scene objects outlive a session; destroyed ones pruned in Snapshot
        private static readonly List<T> _dead = new List<T>(); // process-scoped: scratch

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
