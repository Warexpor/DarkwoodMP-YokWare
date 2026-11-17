using System.Collections;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>Tracks local contact for the tower view and the two repeatable Road Home routes.</summary>
    internal sealed class LocalAreaPresence : MonoBehaviour
    {
        private EventTriggers _volume;
        private int _activation;
        private static LocalAreaPresence _firingEnter; // process-scoped: restored after each coroutine step

        internal static bool IsPersonalArea(EventTriggers volume)
            => volume.name == "goToRoadToHome_oneChance" || volume.name == "goToRoadToHome_treeVillage"
                || IsTowerView(volume);

        private static bool IsTowerView(EventTriggers volume)
        {
            switch (volume.name)
            {
                case "area_spot_med_undergroundCh2Entrance_01":
                case "area_tween_color_med_undergroundCh2Entrance_01":
                case "area_tween_color_med_undergroundCh2Entrance_02":
                    return true;
                case "area_signs_01":
                    return volume.transform.parent != null
                        && volume.transform.parent.name == "event_radioTower_enter";
                default:
                    return false;
            }
        }

        private static bool InCoopWorld => ModRuntime.Network != null
            && ModRuntime.Network.Role != NetworkRole.Offline;

        internal static bool OwnsContact(EventTriggers volume, Collider other)
            => InCoopWorld && volume != null && volume.reactsToPlayer && other != null
                && Player.Instance != null && other.gameObject == Player.Instance.gameObject
                && IsPersonalArea(volume);

        private static bool CanEnter(EventTriggers volume)
            => volume.isActiveAndEnabled && EventTriggersAuth.CanFireTriggers(volume)
                && Singleton<Controller>.Instance != null && Singleton<OutsideLocations>.Instance?.loading != true;

        internal static void TryEnter(EventTriggers volume)
        {
            if (volume.entered > volume.exited || !CanEnter(volume))
                return;
            volume.entered = volume.exited + 1;
            volume.fireEventTrigger(EventTrigger.Type.area);
        }

        // Unity can report enter just once while a remote pad load is blocking local triggers.
        private void OnTriggerStay(Collider other)
        {
            if (OwnsContact(_volume, other))
                TryEnter(_volume);
        }

        private IEnumerator WhilePresent(IEnumerator original, bool triggerStep)
        {
            int activation = _activation;
            int entry = _volume.entered;
            while (_volume != null && _volume.isActiveAndEnabled && activation == _activation
                && entry == _volume.entered && entry > _volume.exited)
            {
                if (triggerStep && !CanEnter(_volume))
                {
                    yield return null;
                    continue;
                }
                bool more;
                LocalAreaPresence previous = _firingEnter;
                if (triggerStep)
                    _firingEnter = this;
                try { more = original.MoveNext(); }
                finally { _firingEnter = previous; }
                if (!more)
                    yield break;
                yield return original.Current;
            }
        }

        [HarmonyPatch(typeof(EventTriggers), "OnEnable")]
        private static class AttachPatch
        {
            private static void Postfix(EventTriggers __instance)
            {
                if (!Application.isPlaying || !IsPersonalArea(__instance))
                    return;
                var presence = __instance.GetComponent<LocalAreaPresence>();
                if (presence == null)
                    presence = __instance.gameObject.AddComponent<LocalAreaPresence>();
                presence._volume = __instance;
            }
        }

        [HarmonyPatch(typeof(EventTriggers), "OnDisable")]
        private static class DisablePatch
        {
            private static void Prefix(EventTriggers __instance)
            {
                if (!IsPersonalArea(__instance))
                    return;
                var presence = __instance.GetComponent<LocalAreaPresence>();
                if (presence != null)
                    presence._activation++;

                // A disabled trigger receives no physics exit. Restore the scene's authored view.
                if (InCoopWorld && __instance.entered > __instance.exited && IsTowerView(__instance)
                    && Singleton<Controller>.Instance != null && EventTriggersAuth.CanFireTriggers(__instance))
                    __instance.fireEventTriggerExit(EventTrigger.Type.area);
            }
        }

        [HarmonyPatch(typeof(EventTrigger), nameof(EventTrigger.fire))]
        private static class PendingEnterPatch
        {
            private static void Postfix(EventTrigger.Type TriggerType, EventTriggers thisEventTriggers,
                bool force, ref IEnumerator __result)
            {
                if (!InCoopWorld || force || TriggerType != EventTrigger.Type.area || __result == null
                    || thisEventTriggers == null || !IsPersonalArea(thisEventTriggers))
                    return;
                var presence = thisEventTriggers.GetComponent<LocalAreaPresence>();
                if (presence != null)
                    __result = presence.WhilePresent(__result, triggerStep: true);
            }
        }

        [HarmonyPatch(typeof(GameEvent), nameof(GameEvent.fire))]
        private static class PendingViewPatch
        {
            private static void Postfix(ref IEnumerator __result)
            {
                // GameEvent yields again after EventTrigger has fired. An exit between those
                // two waits must not let an old darkness tween replace the exit's tween.
                LocalAreaPresence presence = _firingEnter;
                if (__result != null && presence != null && IsTowerView(presence._volume))
                    __result = presence.WhilePresent(__result, triggerStep: false);
            }
        }
    }
}
