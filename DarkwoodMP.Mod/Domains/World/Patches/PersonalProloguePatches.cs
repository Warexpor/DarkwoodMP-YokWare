using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A joiner new to the world loads it with a new-game character, as vanilla starts the
    /// prologue: the save's player block (position, level, skills, health, upgrades, recipes,
    /// effects) is the host's character. Without this the joiner played on as a copy of the host.
    /// </summary>
    [HarmonyPatch(typeof(Player.SaveState), nameof(Player.SaveState.loadValues))]
    public static class PrologueFreshCharacterPatch
    {
        private static bool Prefix(bool onlyState)
        {
            if (onlyState || !PersonalPrologue.DecideFreshAtLoad())
                return true;
            // What vanilla loadValues still does for any load: the prologue flag off until the
            // joiner's own prologue sets it (a new-game Player has it on, and the load would take
            // the new-game branch with no prologue pad).
            Player.Instance.firstPlay = false;
            InitNewGameSkills(Player.Instance.skills);
            return false;
        }

        /// <summary>
        /// Vanilla's new-game skill setup (<c>PlayerSkills.Start</c> outside a load): every skill
        /// prefab unchosen, then <c>initialize</c>. A load leaves it to the save's
        /// <c>PlayerSkills.SaveState.loadValues</c>, which this skip does not run, so the fresh
        /// character's skills were never initialized. That also never ran <c>setfarsight</c>, which
        /// sets the camera's look distance (<c>CamMain.seeDistance</c> 5.2, Farsight 3.4): the
        /// field kept its default of 1.5 and the client's camera looked about three and a half
        /// times as far toward the cursor as the host's.
        /// </summary>
        private static void InitNewGameSkills(PlayerSkills skills)
        {
            if (skills == null)
                return;
            UnityEngine.Object[] prefabs = UnityEngine.Resources.LoadAll("Skills", typeof(UnityEngine.GameObject));
            for (int i = 0; i < prefabs.Length; i++)
            {
                PlayerSkill skill = (prefabs[i] as UnityEngine.GameObject)?.GetComponent<PlayerSkill>();
                if (skill == null)
                    continue;
                skill.chosen = false;
                skill.initialized = false;
            }
            skills.initialized = false;
            skills.initialize();
        }
    }

    /// <summary>
    /// The host's home oven is not a new player's (vanilla sets it when the player uses one). Runs
    /// right after the dream state is loaded: a save the host made mid-prologue would resume the
    /// host's prologue dream at load (SaveManager wantToDream); the joiner starts its own instead.
    /// </summary>
    [HarmonyPatch(typeof(Player.SaveState), nameof(Player.SaveState.loadValues2))]
    public static class PrologueFreshCharacterOvenPatch
    {
        private static bool Prefix()
        {
            if (!PersonalPrologue.FreshCharacterLoad)
                return true;
            Dreams d = Dreams.Instance;
            if (d != null)
                d.wantToDream = false;
            return false;
        }
    }

    /// <summary>
    /// Host: day 1 begins when everyone is out of the prologue. While anyone still plays it on a
    /// world whose first morning has not begun, the clock does not step (vanilla FixedUpdate's
    /// <c>DoUpdateTime</c> gate; <see cref="HostSharedClockPatch"/> honours it too).
    /// </summary>
    [HarmonyPatch(typeof(Controller), "FixedUpdate")]
    [HarmonyPriority(Priority.First)]
    public static class PrologueDayOneHoldPatch
    {
        private static void Prefix(Controller __instance, out bool __state)
        {
            __state = false;
            if (__instance == null)
                return;
            // Counted every step (TimeSync carries it, and the "day 1 waits" line goes out once).
            bool hold = PersonalPrologue.HoldDayOne(out _);
            if (!hold || !__instance.DoUpdateTime)
                return;
            __instance.DoUpdateTime = false;
            __state = true;
        }

        private static void Postfix(Controller __instance, bool __state)
        {
            if (__state && __instance != null)
                __instance.DoUpdateTime = true;
        }

        private static System.Exception Finalizer(Controller __instance, bool __state, System.Exception __exception)
        {
            if (__state && __instance != null)
                __instance.DoUpdateTime = true;
            return __exception;
        }
    }
}
