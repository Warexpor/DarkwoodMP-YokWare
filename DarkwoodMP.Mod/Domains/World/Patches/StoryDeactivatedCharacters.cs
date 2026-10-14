using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Creatures a story step switched off (vanilla <c>setMeActive(false)</c>, the GameEvents
    /// "setActive" step). The host wakes a creature near a remote player that its own culling
    /// switched off (it simulates the world round every player), and that wake took these too:
    /// the dream bunker's talking door, switched off by its leave-door event when the talk closes,
    /// came straight back on the host and went on streaming to every client, standing in the
    /// opened doorway. A story step switching it on again takes it off this list.
    /// </summary>
    internal static class StoryDeactivatedCharacters
    {
        private static readonly HashSet<int> _off = new HashSet<int>(); // process-scoped: instance ids, cleared on session stop

        internal static bool Contains(GameObject go) => go != null && _off.Contains(go.GetInstanceID());

        internal static void Note(GameObject go, bool active)
        {
            if (go == null || go.GetComponent<Character>() == null)
                return;
            if (active)
                _off.Remove(go.GetInstanceID());
            else
                _off.Add(go.GetInstanceID());
        }

        internal static void Clear() => _off.Clear();
    }

    [HarmonyPatch(typeof(Helpers), nameof(Helpers.setMeActive))]
    public static class StoryDeactivatedCharactersPatch
    {
        private static void Postfix(GameObject target, bool activeModifier)
            => StoryDeactivatedCharacters.Note(target, activeModifier);
    }
}
