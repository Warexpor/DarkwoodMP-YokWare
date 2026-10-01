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
        private static bool Prefix()
        {
            if (!DialogClientWorldDefer.Active)
                return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Flags), "setFlag", typeof(string), typeof(int))]
    public static class DialogDeferFlagIntPatch
    {
        private static bool Prefix()
        {
            if (!DialogClientWorldDefer.Active)
                return true;
            return false;
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
