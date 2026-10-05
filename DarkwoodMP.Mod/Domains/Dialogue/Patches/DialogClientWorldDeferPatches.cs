using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;

namespace DWMPHorde.Patches
{
    // Client world-defer gates. The defer scope itself is opened and closed around
    // displayNextBoard by DialogDisplayNextBoardPatch (Prefix Begin / Finalizer End).

    [HarmonyPatch(typeof(Flags), "setFlag", typeof(string), typeof(bool))]
    public static class DialogDeferFlagBoolPatch
    {
        // Vanilla signature: setFlag(string flagName, bool activeModifier).
        private static bool Prefix(string flagName) => PerPlayerFlagGate.ShouldWrite(flagName);
    }

    [HarmonyPatch(typeof(Flags), "setFlag", typeof(string), typeof(int))]
    public static class DialogDeferFlagIntPatch
    {
        private static bool Prefix(string flagName) => PerPlayerFlagGate.ShouldWrite(flagName);
    }

    /// <summary>
    /// Who writes a flag. A shared flag from a client's dialogue board waits for the host's replay
    /// (<see cref="DialogClientWorldDefer"/>). A per-player flag (<see cref="PerPlayerFlagPolicy"/>,
    /// e.g. the oven's <c>player_firstOvenInteraction</c> outcome) is the speaker's own: the
    /// speaking client writes it, and the host running a peer's dialogue, trigger or event does
    /// not take it into its own flags.
    /// </summary>
    internal static class PerPlayerFlagGate
    {
        internal static bool ShouldWrite(string flagName)
        {
            if (PerPlayerFlagPolicy.IsPerPlayer(flagName))
                return !GameEventPersonalActorPatch.ShouldSuppressPersonal();
            return !DialogClientWorldDefer.Active;
        }
    }

    [HarmonyPatch(typeof(Events), "fireWorldEvent")]
    public static class DialogDeferFireWorldEventPatch
    {
        private static bool Prefix()
        {
            if (!DialogClientWorldDefer.Active)
                return true;
            return false;
        }
    }

    /// <summary>Dialogue transport outcomes must not start location load on client.</summary>
    [HarmonyPatch(typeof(OutsideLocations), "prepareLocation")]
    public static class DialogDeferPrepareLocationPatch
    {
        private static bool Prefix()
        {
            if (!DialogClientWorldDefer.Active)
                return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(OutsideLocations), "returnToWorld")]
    public static class DialogDeferReturnToWorldPatch
    {
        private static bool Prefix()
        {
            if (!DialogClientWorldDefer.Active)
                return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Map), "showElement", typeof(string))]
    public static class DialogDeferMarkOnMapPatch
    {
        private static bool Prefix()
        {
            if (!DialogClientWorldDefer.Active)
                return true;
            return false;
        }
    }
}
