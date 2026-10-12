using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// The hideout oven (vanilla <c>ExperienceMachine</c>) is lit or put out. Only a real change
    /// of <c>isOn</c> outside a load is sent: vanilla <c>Start</c> calls <c>disable()</c> on every
    /// unlit oven and <c>enable()</c> in story scenes, and sending those made a peer's world load
    /// put out the oven the party had lit (and with it the hideout's shadow ward).
    /// </summary>
    internal static class HideoutOvenSync
    {
        internal static bool ShouldSend(bool wasOn, ExperienceMachine oven)
        {
            if (oven == null || oven.isOn == wasOn)
                return false;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return false;
            if (LanNetworkManager.IsApplyingRemoteState || Core.loadingGame)
                return false;
            var ol = Singleton<OutsideLocations>.Instance;
            return ol == null || !ol.loading;
        }

        internal static void Send(ExperienceMachine oven)
        {
            Vector3 p = oven.transform.position;
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);
            ModRuntime.Network.SendHideoutUpgrade(new HideoutUpgradeMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                IsOn = oven.isOn
            });
            ModRuntime.LegacyInfo($"[HideoutUpgrade] {(oven.isOn ? "enable" : "disable")} at {key}");
        }
    }

    /// <summary>
    /// Each player's home is their own lit oven (vanilla <c>Player.experienceMachine</c>, also the
    /// respawn home). Vanilla puts out your previous oven when you move home, and that took the
    /// home (and the shadow ward) away from whoever else still lived there. An oven now goes out
    /// only when nobody calls it home any more. Homes travel with each player's effect sync.
    /// </summary>
    internal static class OvenHomes
    {
        private const float SameOvenSq = 1f;

        /// <summary>
        /// The local player's lit home oven is out (the host's world save, which holds the host's
        /// home): light it again next frame, outside any apply scope, so peers hear it. Only for a
        /// home this player already lit: an oven never examined stays unlit, as in vanilla.
        /// </summary>
        internal static void RelightOwnHomeNextFrame()
        {
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null)
                return;
            ctrl.waitFramesAndRun(() =>
            {
                Player p = Player.Instance;
                ExperienceMachine home = p != null ? p.experienceMachine : null;
                if (home != null && !home.isOn)
                    home.enable();
            }, 1);
        }

        /// <summary>
        /// A peer moved home from <paramref name="from"/> to <paramref name="to"/>. A player who
        /// lived in the oven it moved out of moves with it: the party had one home, and the one
        /// who never talked to the new oven woke up after a night death alone in the old hideout.
        /// A player living elsewhere keeps its own home. Runs next frame, outside the apply scope,
        /// so an old oven nobody lives in any more goes out on every peer.
        /// </summary>
        private static bool _movedHome; // reset-in: ConsumeMovedHome (read on the next effect send)
        private static bool _following; // process-scoped: call-scoped, a follow is not a move of its own

        /// <summary>The local player moved home itself (vanilla setExperienceMachine from a live home).</summary>
        internal static void NoteLocalMove()
        {
            if (!_following)
                _movedHome = true;
        }

        internal static bool ConsumeMovedHome()
        {
            bool moved = _movedHome;
            _movedHome = false;
            return moved;
        }

        internal static void FollowPeerMove(Vector3 from, Vector3 to, bool moverInStory)
        {
            if (moverInStory || Sync.PersonalPrologue.LocalInPrologue)
                return;
            if (!LivesAt(Player.Instance, from))
                return;
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null)
                return;
            ctrl.waitFramesAndRun(() =>
            {
                Player p = Player.Instance;
                if (!LivesAt(p, from))
                    return;
                ExperienceMachine next = Sync.WorldQueryHelper.FindNearest<ExperienceMachine>(to, 2f);
                if (next == null || next == p.experienceMachine)
                {
                    ModRuntime.Log?.LogWarning("[HideoutUpgrade] party moved home but no oven found at " + to);
                    return;
                }
                _following = true;
                try { p.setExperienceMachine(next, doEnable: true); }
                finally { _following = false; }
                ModLog.Event(LogCat.World, "[HideoutUpgrade] party moved home — following to " + next.transform.position);
            }, 1);
        }

        private static bool LivesAt(Player p, Vector3 oven)
            => p != null && p.experienceMachine != null
                && (p.experienceMachine.transform.position - oven).sqrMagnitude <= SameOvenSq;

        /// <summary>Another player (not <paramref name="exceptPlayerId"/>) has this oven as home.</summary>
        internal static bool IsOtherPlayersHome(ExperienceMachine oven, int exceptPlayerId)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (oven == null || net == null || !net.IsConnected)
                return false;
            Vector3 p = oven.transform.position;
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null || proxy.PlayerId == exceptPlayerId || !proxy.RemoteHomeOven.HasValue)
                    continue;
                if ((proxy.RemoteHomeOven.Value - p).sqrMagnitude <= SameOvenSq)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The local player moved from a home it had to another oven (the oven talk). A first home
    /// (fresh character, prologue) is not a move. Tells <see cref="OvenHomes"/> so the next
    /// effect sync says so and the players who lived in the old hideout follow.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.setExperienceMachine))]
    public static class OvenMoveHomeNotePatch
    {
        // First: OvenMoveHomePatch's prefix may skip the original, and the home it held must be read before.
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Player __instance, out ExperienceMachine __state)
            => __state = __instance != null ? __instance.experienceMachine : null;

        private static void Postfix(Player __instance, ExperienceMachine __state)
        {
            if (__instance == null || __instance != Player.Instance || __state == null)
                return;
            if (__instance.experienceMachine != null && __instance.experienceMachine != __state)
                OvenHomes.NoteLocalMove();
        }
    }

    /// <summary>Moving home keeps the old oven lit while another player lives there.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.setExperienceMachine))]
    public static class OvenMoveHomePatch
    {
        private static bool Prefix(Player __instance, ExperienceMachine machine, bool doEnable)
        {
            if (machine == null || __instance.experienceMachine == null || __instance.experienceMachine == machine)
                return true;
            if (!OvenHomes.IsOtherPlayersHome(__instance.experienceMachine, exceptPlayerId: -1))
                return true;
            ModRuntime.LegacyInfo("[HideoutUpgrade] moving home — old oven stays lit (another player's home)");
            if (doEnable)
                machine.enable();
            else
                machine.setAsDefaultExpMachine();
            return false;
        }
    }

    /// <summary>
    /// Lighting an oven unlocks its hideout for the porter (<c>player_unlockedHideout_2/3</c>, set
    /// in <c>enable()</c>). A client lights it itself when its dialogue closes, and that close runs
    /// inside the last board, where shared flags wait for the host's replay of the board. The host
    /// does not replay the lighting (it is the speaker's own move; the host lights its copy from
    /// the oven message, a remote apply that does not fan flags out), so the speaker never got the
    /// unlock: desync check "player_unlockedHideout_3: host=1 client=none". The speaker's own
    /// lighting writes it, and its flag sync carries it to the others.
    /// </summary>
    [HarmonyPatch(typeof(ExperienceMachine), "enable")]
    public static class OvenEnableWritesUnlockPatch
    {
        private static void Prefix(out int __state) => __state = Sync.DialogClientWorldDefer.Suspend();

        private static void Finalizer(int __state) => Sync.DialogClientWorldDefer.Resume(__state);
    }

    [HarmonyPatch(typeof(ExperienceMachine), "enable")]
    public static class HideoutUpgradeEnablePatch
    {
        private static void Prefix(ExperienceMachine __instance, out bool __state)
        {
            __state = __instance != null && __instance.isOn;
        }

        private static void Postfix(ExperienceMachine __instance, bool __state)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

            // A peer's lit oven hums here too, as a lit oven does in vanilla.
            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            if (HideoutOvenSync.ShouldSend(__state, __instance))
                HideoutOvenSync.Send(__instance);
        }
    }

    [HarmonyPatch(typeof(ExperienceMachine), "disable")]
    public static class HideoutUpgradeDisablePatch
    {
        private static void Prefix(ExperienceMachine __instance, out bool __state)
        {
            __state = __instance != null && __instance.isOn;
        }

        private static void Postfix(ExperienceMachine __instance, bool __state)
        {
            if (HideoutOvenSync.ShouldSend(__state, __instance))
                HideoutOvenSync.Send(__instance);
        }
    }
}
