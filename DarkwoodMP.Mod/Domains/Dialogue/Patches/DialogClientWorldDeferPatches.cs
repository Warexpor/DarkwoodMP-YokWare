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

    /// <summary>
    /// A dialogue trip (the Wolf's lift to the Doctor's house and back: transportToOutsideLoc /
    /// returnToWorld) carries the speaker, as a door into a location does. The speaking client runs
    /// it itself; the host replaying that client's board must not go. It used to be the other way
    /// round: the client's trip was deferred to the host, whose replay then took the host there.
    /// </summary>
    internal static class DialogPeerTrip
    {
        private static int _depth;

        internal static bool Active => _depth > 0;

        internal static void Begin() => _depth++;

        internal static void End()
        {
            if (_depth > 0)
                _depth--;
        }

        internal static void Reset() => _depth = 0;

        /// <summary>The board vanilla is about to display moves its speaker.</summary>
        internal static bool BoardMovesSpeaker(DialogueWindow dw)
        {
            CharacterDialogue.Dialogue d = dw != null ? dw.currentDialogue : null;
            if (d == null || d.boards == null)
                return false;
            int next;
            try { next = Traverse.Create(dw).Field("currentBoard").GetValue<int>() + 1; }
            catch { return false; }
            if (next < 0 || next >= d.boards.Count || d.boards[next] == null || d.boards[next].outcomes == null)
                return false;
            foreach (CharacterDialogue.Dialogue.Board.Outcome o in d.boards[next].outcomes)
            {
                if (o == null)
                    continue;
                if (o.type == CharacterDialogue.Dialogue.Board.Outcome.Type.returnToWorld
                    || (o.type == CharacterDialogue.Dialogue.Board.Outcome.Type.transportToOutsideLoc
                        && !string.IsNullOrEmpty(o.Value)))
                    return true;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(OutsideLocations), "prepareLocation")]
    public static class DialogPeerTripPrepareLocationPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix() => !DialogPeerTrip.Active;
    }

    [HarmonyPatch(typeof(OutsideLocations), "returnToWorld")]
    public static class DialogPeerTripReturnToWorldPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix() => !DialogPeerTrip.Active;
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
