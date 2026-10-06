using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Morning survival reward is per-player. Vanilla <c>startAfterNight(!isAfterDeath)</c>
    /// gives no reward after a night death; the host's own death mark is consumed here
    /// (the morning release resets death state one tick earlier, at startDay).
    /// Live ReputationSync ignores isNightTrader, so host and client trader standing
    /// never overwrite each other.
    /// </summary>
    [HarmonyPatch(typeof(Controller), "startAfterNight")]
    public static class MorningRepPatch
    {
        private static void Prefix(Controller __instance, ref bool withReward)
        {
            if (!DeathStateTracker.ConsumeMorningRewardSkip(__instance.day))
                return;

            ModRuntime.LegacyInfo("[MorningRep] Player died at night — no morning reward");
            // Same effect as vanilla isAfterDeath: skips the trader +rep block and the
            // saturation gain, while spawn / FX / freeze still run.
            withReward = false;
        }
    }

    /// <summary>
    /// <c>startAfterNight</c> runs on the host only. Every other surviving peer standing in
    /// the same hideout gets its own copy of the reward exactly once per morning
    /// (<see cref="MorningRewardMessage"/>). Peers that were night-dead at dawn get none,
    /// matching vanilla.
    /// </summary>
    [HarmonyPatch(typeof(Controller), "startAfterNight")]
    public static class MorningRewardFanOutPatch
    {
        private static int _rewardedDay = -1;

        /// <summary>startDay always precedes startAfterNight; re-arm the once-per-morning guard.</summary>
        internal static void ArmForNewMorning() => _rewardedDay = -1;

        /// <summary>Session end: a reloaded save may replay the same morning.</summary>
        internal static void Reset() => _rewardedDay = -1;

        private static void Postfix(Controller __instance)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            int day = __instance.day;
            if (_rewardedDay == day)
                return;
            _rewardedDay = day;

            try
            {
                HostAwayMorning.TryRun(net, __instance);
                FanOut(net, __instance, day);
            }
            finally
            {
                DeathStateTracker.ClearMorningDeadMarks();
            }
        }

        private static void FanOut(LanNetworkManager net, Controller ctrl, int day)
        {
            // Vanilla sets isAfterNight only when the host stands in its hideout; otherwise
            // HostAwayMorning ran it on the hideouts peers are in.
            if (!ctrl.isAfterNight)
                return;
            var flags = Singleton<Flags>.Instance;
            var scenarios = Singleton<NightScenarios>.Instance;
            bool showHelp = scenarios != null && scenarios.scenarioId == 4
                && Core.currentProfile != null && !Core.currentProfile.skippedPrologue;

            foreach (RemotePlayerProxy proxy in new List<RemotePlayerProxy>(net.GetAllProxies()))
            {
                if (proxy == null)
                    continue;
                int id = proxy.PlayerId;
                if (id <= 0 || DeathStateTracker.WasNightDeadAtMorning(id, day))
                    continue;
                CharBase cb = proxy.CachedCharBase;
                if (cb != null && !cb.alive)
                    continue;
                // Each player is rewarded by the trader of the hideout it greets the morning in.
                Location loc = Location.getAtPos(proxy.transform.position);
                if (loc == null || !HostAwayMorning.HadMorning(loc))
                    continue;

                NPC trader = loc.trader != null ? loc.trader.GetComponent<NPC>() : null;
                bool traderRep = trader != null && flags != null && !flags.isFlagTrue("talkingTree_burnt");
                int repGain = traderRep ? MorningRewardTable.ReputationFor(loc.hideoutId, loc.chapterId) : 0;
                string traderName = traderRep ? trader.name : string.Empty;
                var msg = new MorningRewardMessage
                {
                    Day = day,
                    TraderName = traderName,
                    Reputation = repGain,
                    Saturation = 10f,
                    ShowTraderHelp = showHelp
                };
                net.SendToPlayer(id, NetMessageType.MorningReward, w => msg.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                ModRuntime.LegacyInfo($"[MorningRep] reward → p{id}: {traderName} +{repGain} at {loc.name}");
            }
        }
    }

    /// <summary>
    /// Vanilla <c>startAfterNight</c> runs the morning on the hideout the local player stands in
    /// and does nothing anywhere else ("Location not found for morning event"). With the shared
    /// clock and several players, players greet the morning in different hideouts, or the host
    /// is out in the forest: only the host's hideout (or nobody's) got a morning. Every hideout a
    /// living player stands in now gets the world half of vanilla's morning: the end-of-night
    /// effect, its lights, its trader (the Wolfman's visit goes to one hideout only, the first,
    /// as he is one man), the night's creatures cleared. The host gets no reward or end-of-night
    /// screen effect where it is not home; with the host away the morning freeze is the clock alone.
    /// </summary>
    internal static class HostAwayMorning
    {
        /// <summary>Hideouts this morning ran on beside the host's own vanilla one.</summary>
        private static readonly List<Location> _extra = new List<Location>(4); // reset-in: Reset
        private static Location _hostHome; // reset-in: Reset
        private static Controller _frozeClock; // reset-in: ReleaseClock (called from Reset)

        /// <summary>This hideout had a morning (the host's own, or one run here).</summary>
        internal static bool HadMorning(Location loc)
            => loc != null && (loc == _hostHome || _extra.Contains(loc));

        internal static void TryRun(LanNetworkManager net, Controller ctrl)
        {
            _extra.Clear();
            Player host = Player.Instance;
            Location hostLoc = host != null && host.whereAmI != null ? host.whereAmI.bigLocation : null;
            // Vanilla ran it where the host stands when that is a hideout (isAfterNight set).
            _hostHome = ctrl.isAfterNight && hostLoc != null && hostLoc.playerBase ? hostLoc : null;
            bool wolfPlaced = _hostHome != null;

            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null)
                    continue;
                CharBase cb = proxy.CachedCharBase;
                if (cb != null && !cb.alive)
                    continue;
                Location loc = Location.getAtPos(proxy.transform.position);
                if (loc == null || !loc.playerBase || HadMorning(loc))
                    continue;
                RunOn(ctrl, loc, mayGetWolf: !wolfPlaced);
                wolfPlaced = true;
                _extra.Add(loc);
            }
        }

        private static void RunOn(Controller ctrl, Location loc, bool mayGetWolf)
        {
            ctrl.isAfterNight = true;
            Core.AddPrefab("FX/efekt_konca_nocy", loc.transform.position, Quaternion.identity, null);
            // Same wolf / trader choice as vanilla Controller.startAfterNight.
            var flags = Singleton<Flags>.Instance;
            var wg = Singleton<WorldGenerator>.Instance;
            bool wolf = mayGetWolf && !flags.isFlagTrue("wolf_killed")
                && (flags.isFlagTrue("wolf_inPlayerHideout")
                    || (!flags.isFlagTrue("wolf_cameToPlayerHideout")
                        && !flags.isFlagTrue("wolf_shownOpeningDialogue")
                        && (wg == null || wg.chapterID <= 1)));
            if (wolf)
            {
                loc.spawnWolf();
            }
            else
            {
                if (mayGetWolf)
                    loc.despawnWolf();
                if (!flags.isFlagTrue("talkingTree_burnt"))
                    loc.spawnTrader();
            }
            ctrl.gaveAfterNightRewards = true;
            loc.enableAllLights();
            loc.removeCharacters();
            // Vanilla freezes through the timeFreeze effect on the player at home; the host is not
            // at this one.
            if (_hostHome == null && ctrl.DoUpdateTime)
            {
                ctrl.DoUpdateTime = false;
                _frozeClock = ctrl;
            }
            ModRuntime.LegacyInfo($"[DayNight] morning at peer hideout '{loc.name}'");
        }

        /// <summary>Host endAfterNight: vanilla despawns the trader where the host stands, not at these.</summary>
        internal static void OnEnded(bool byKillingTrader)
        {
            for (int i = 0; i < _extra.Count; i++)
            {
                Location loc = _extra[i];
                if (loc != null && loc.trader != null && !byKillingTrader)
                    Object.Destroy(loc.trader);
            }
            _extra.Clear();
            _hostHome = null;
            ReleaseClock();
        }

        /// <summary>Session end: hand back a clock this morning stopped.</summary>
        internal static void Reset()
        {
            _extra.Clear();
            _hostHome = null;
            ReleaseClock();
        }

        private static void ReleaseClock()
        {
            Controller ctrl = _frozeClock;
            _frozeClock = null;
            if (ctrl != null)
                ctrl.DoUpdateTime = true;
        }
    }

    /// <summary>Vanilla startAfterNight trader standing table (Controller ~1173-1195).</summary>
    public static class MorningRewardTable
    {
        public static int ReputationFor(int hideoutId, int chapterId)
        {
            if (hideoutId == 1)
            {
                if (chapterId == 1) return 100;
                if (chapterId == 2) return 250;
                return 0;
            }
            if (hideoutId == 2) return 150;
            if (hideoutId == 3) return 200;
            return 0;
        }
    }

    /// <summary>
    /// Host morning edge: release every night-dead peer. Runs on <c>startDay</c> so the
    /// host's own <c>startAfterNight</c> one minute later already finds its player at home.
    /// </summary>
    [HarmonyPatch(typeof(Controller), "startDay")]
    public static class MorningNightDeathReleasePatch
    {
        private static void Postfix()
        {
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;
            MorningRewardFanOutPatch.ArmForNewMorning();
            DeathStateTracker.HostReleaseNightDeadAtMorning("startDay");
        }
    }
}
