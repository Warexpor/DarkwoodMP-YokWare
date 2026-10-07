using DWMPHorde.Logging;
using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A joiner's prologue pad arrives before its opening, the reverse of a new game. In a new
    /// game the pad comes up during world generation, its arrival's <c>hideScreen</c> runs, and
    /// only then <c>tweenLoading</c> puts up the black screen for the title and the movie. Here the
    /// arrival's <c>hideScreen</c> (about a second after the pad is in) landed on the opening
    /// <see cref="PrologueIntro"/> had just begun. It faded the black screen out and turned it off,
    /// unlocked input and showed the cursor. With the world camera off for the movie (as in a new
    /// game), nothing cleared the frame: the UI's noise overlay piled up into white noise behind
    /// the "PROLOGUE" title, with the HUD on top. Until the joiner wakes, the arrival keeps
    /// only its audio step; vanilla <c>activatePlayer</c> uncovers the screen, as in a new game.
    /// </summary>
    [HarmonyPatch(typeof(OutsideLocations), "hideScreen")]
    public static class PrologueJoinerArrivalScreenPatch
    {
        private static bool Prefix()
        {
            if (!PersonalPrologue.JoinerBeforeWake)
                return true;
            AudioController.UnpauseAll(1f);
            ModLog.Event(LogCat.Session, "[Prologue] pad arrival: screen stays dark for the opening");
            return false;
        }
    }

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
    /// The host's home oven is not a new player's. Runs right after the dream state is loaded: a
    /// save the host made mid-prologue would resume the host's prologue dream at load (SaveManager
    /// wantToDream); the joiner starts its own instead. Nor are the host's per-player flags (help
    /// popups seen, first oven talk, its night), loaded with the save's flags just before.
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
            SetNewGameHome(Player.Instance);
            DWMPHorde.Networking.ClientStateBackup.ResetPlayerFlagsForNewCharacter();
            return false;
        }

        /// <summary>
        /// What vanilla's skipped <c>loadValues2</c> does for a player who never examined an oven
        /// (<c>setExperienceMachine(home, doEnable: false)</c>), with a new game's home: the
        /// hideout's default oven (<c>isDefaultExpMachine</c>). <c>setAsDefaultExpMachine</c> lights
        /// it (light, hum, smoke) and gives it the lit portrait the oven's first dialogue needs. Left
        /// to the oven's own <c>Start</c>, a joiner met it unlit and got the unlit greeting.
        /// </summary>
        private static void SetNewGameHome(Player p)
        {
            WorldGenerator wg = Singleton<WorldGenerator>.Instance;
            if (p == null || wg == null || wg.playerBase == null)
                return;
            ExperienceMachine home = null;
            foreach (ExperienceMachine em in wg.playerBase.GetComponentsInChildren<ExperienceMachine>(true))
            {
                if (em != null && em.isDefaultExpMachine)
                {
                    home = em;
                    break;
                }
            }
            if (home == null)
            {
                ModLog.Warn(LogCat.Session, "[Prologue] no default oven in the hideout for the fresh character");
                return;
            }
            string was = p.experienceMachine != null ? p.experienceMachine.name + " at " + p.experienceMachine.transform.position : "none";
            // Not the host's home: setExperienceMachine would put that one out.
            p.experienceMachine = null;
            p.setExperienceMachine(home, doEnable: false);
            ModLog.Event(LogCat.Session, "[Prologue] fresh character's home oven: " + home.transform.position
                + " lit=" + (home.light2D != null && home.light2D.activeSelf) + " (was " + was + ")");
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
