using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Client co-op: wrap displayNextBoard so world outcomes are host-only.
    /// Clears dialogue-triggered wantToDream so DreamStartRequest is not raced
    /// with DialogOutcome host prepare.
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), "displayNextBoard")]
    public static class DialogClientWorldDeferBoardPatch
    {
        private static void Prefix(DialogueWindow __instance, out bool __state)
        {
            __state = false;
            if (__instance == null) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return;
            if (!DialogApplyPolicy.ShouldDeferWorldOnClient(true, true, false))
                return;

            DialogClientWorldDefer.Begin();
            __state = true;
        }

        private static void Postfix(DialogueWindow __instance, bool __state)
        {
            if (!__state) return;
            try
            {
                // Host will start dialogue dreams via DialogOutcome → displayDialogue.
                if (__instance != null)
                    __instance.dreamToStart = null;
                var dreams = Dreams.Instance;
                if (dreams != null && dreams.wantToDream && !dreams.dreaming && !dreams.dreamPrepared)
                {
                    // Only clear if we are not already mid host-driven prepare.
                    if (!DreamSession.IsActive)
                        dreams.wantToDream = false;
                }
            }
            finally
            {
                DialogClientWorldDefer.End();
            }
        }
    }

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
}
