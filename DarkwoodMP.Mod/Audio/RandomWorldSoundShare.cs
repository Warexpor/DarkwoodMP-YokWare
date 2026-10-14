using System;
using System.Reflection;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// The game's random world stingers (<c>RandomWorldSounds</c>: house_noises indoors,
    /// forest_noises outdoors, the far musician and the big pig once a day) heard by every player
    /// near them. Vanilla rolls one every 50 to 150 s for its one player, 250 to 530 units away,
    /// and plays it with no parent object, which the sound forwarding skips on purpose (ambient
    /// beds would flood the net). Each player rolled its own: one heard a noise behind the house
    /// and the player next to it heard nothing. The player whose game rolls one now sends it
    /// (PlayerAudio, a world sound at its spot); a peer in earshot plays it there and counts it
    /// as its own roll: the once-a-day ones are used up, and its own timer starts over, so players
    /// together get about vanilla's pace rather than one stinger per player.
    /// </summary>
    internal static class RandomWorldSoundShare
    {
        private static int[] _counts; // process-scoped: scratch (prefix to postfix of one roll step)

        internal static int[] PlayedCounts(RandomWorldSounds rws)
        {
            if (rws == null || rws.randomSounds == null)
                return null;
            // One step's snapshot, read back by its postfix before the next step: kept, not made per step.
            int n = rws.randomSounds.Count;
            if (_counts == null || _counts.Length != n)
                _counts = new int[n];
            int[] counts = _counts;
            for (int i = 0; i < counts.Length; i++)
                counts[i] = rws.randomSounds[i] != null ? rws.randomSounds[i].timesPlayedToday : 0;
            return counts;
        }

        /// <summary>Local roll step ran: when a stinger's count went up, it played; send it.</summary>
        internal static void OnLocalRoll(RandomWorldSounds rws, int[] before)
        {
            if (rws == null || rws.randomSounds == null || before == null
                || before.Length != rws.randomSounds.Count)
                return;
            string id = null;
            for (int i = 0; i < before.Length; i++)
            {
                RandomWorldSounds.RandomSound s = rws.randomSounds[i];
                if (s != null && s.timesPlayedToday > before[i])
                {
                    id = s.name;
                    break;
                }
            }
            AudioObject ao = rws.audioObj;
            if (id == null || ao == null)
                return;
            if (!NetGuard.Connected(out LanNetworkManager net))
                return;
            // Where it was played: AlignToObject (keeps it beside this player) has not moved it yet.
            Vector3 pos = ao.transform.position;
            net.SendPlayerAudio(new PlayerAudioMessage
            {
                SoundId = id,
                Volume = 1f,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                StickToSender = false
            });
            ModRuntime.LegacyInfo("[RandomWorldSound] TX " + id + " at " + pos);
        }

        /// <summary>
        /// A peer's world sound passed the hear gate here. When it is one of the game's random
        /// stingers it is handled here (true): played only when this player's own roll could have
        /// picked it right now (vanilla <c>waitToPlayRandomSound</c> / <c>getEligibleSound</c>:
        /// not underground, not dreaming or in the credits, indoors for house_noises and outdoors
        /// for forest_noises, requirements met, under its daily cap), and then played the vanilla
        /// way: the game's own falloff, following this player at the offset it arrived at. Played
        /// at a fixed spot, it faded as the listener walked off (steps dying away out of nowhere),
        /// and a player outdoors heard the house footsteps a peer indoors rolled. A played one
        /// counts as this player's roll.
        /// </summary>
        internal static bool TryPlayPeerStinger(string audioID, Vector3 pos, float volume)
        {
            RandomWorldSounds rws = Singleton<RandomWorldSounds>.Instance;
            if (rws == null || rws.randomSounds == null || string.IsNullOrEmpty(audioID))
                return false;
            RandomWorldSounds.RandomSound hit = null;
            foreach (RandomWorldSounds.RandomSound s in rws.randomSounds)
            {
                if (s != null && s.name == audioID)
                {
                    hit = s;
                    break;
                }
            }
            if (hit == null)
                return false;
            Player p = Player.Instance;
            if (p == null || p._transform == null || !CouldRollNow(p, hit))
            {
                ModRuntime.LegacyInfo("[RandomWorldSound] RX " + audioID + " dropped (not rollable here now)");
                return true;
            }
            hit.timesPlayedToday++;
            bool prevNet = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            AudioObject ao;
            try { ao = AudioController.Play(audioID, pos, null, Mathf.Clamp01(volume)); }
            finally { TraverseHack.SetExplicitFlag(prevNet); }
            if (ao != null)
            {
                rws.audioObj = ao;
                AlignToObject align = ao.gameObject.AddComponent<AlignToObject>();
                if (align != null)
                    align.transformToAlign = p._transform;
            }
            // Vanilla's init: the wait for the next stinger starts over.
            if (rws.isActiveAndEnabled)
                rws.init();
            ModRuntime.LegacyInfo("[RandomWorldSound] RX " + audioID + " (own timer restarted)");
            return true;
        }

        private static bool CouldRollNow(Player p, RandomWorldSounds.RandomSound s)
        {
            if (p.whereAmI != null && p.whereAmI.bigLocation != null && p.whereAmI.bigLocation.isUnderground)
                return false;
            Dreams dreams = Singleton<Dreams>.Instance;
            if (dreams != null && dreams.dreaming)
                return false;
            Controller ctrl = Singleton<Controller>.Instance;
            if (ctrl != null && ctrl.inCredits)
                return false;
            if (s.disabled || !(p.isInside ? s.canBeInside : s.canBeOutside))
                return false;
            if (s.maxTimesPerDay != 0 && s.timesPlayedToday >= s.maxTimesPerDay)
                return false;
            return s.requirementsMet();
        }
    }

    /// <summary>The roll loop (<c>RandomWorldSounds.waitToPlayRandomSound</c>): a step that played a stinger sends it.</summary>
    [HarmonyPatch]
    internal static class RandomWorldSoundRollPatch
    {
        private static bool Prepare() => TargetMethod() != null;

        private static MethodBase TargetMethod()
        {
            MethodInfo outer = AccessTools.Method(typeof(RandomWorldSounds), "waitToPlayRandomSound");
            return outer != null ? AccessTools.EnumeratorMoveNext(outer) : null;
        }

        private static void Prefix(out int[] __state)
        {
            __state = RandomWorldSoundShare.PlayedCounts(Singleton<RandomWorldSounds>.Instance);
        }

        private static void Postfix(int[] __state)
        {
            try
            {
                RandomWorldSoundShare.OnLocalRoll(Singleton<RandomWorldSounds>.Instance, __state);
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Audio, "[RandomWorldSound] send failed: " + ex.Message);
            }
        }
    }
}
