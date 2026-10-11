using System;
using System.Reflection;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
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
        internal static int[] PlayedCounts(RandomWorldSounds rws)
        {
            if (rws == null || rws.randomSounds == null)
                return null;
            var counts = new int[rws.randomSounds.Count];
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
        /// A peer's world sound is about to play here (it passed the hear gate). When it is one of
        /// the game's random stingers, it counts as this player's roll.
        /// </summary>
        internal static void OnPeerSoundHeard(string audioID)
        {
            RandomWorldSounds rws = Singleton<RandomWorldSounds>.Instance;
            if (rws == null || rws.randomSounds == null || string.IsNullOrEmpty(audioID))
                return;
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
                return;
            hit.timesPlayedToday++;
            // Vanilla's init: the wait for the next stinger starts over.
            if (rws.isActiveAndEnabled)
                rws.init();
            ModRuntime.LegacyInfo("[RandomWorldSound] RX " + audioID + " (own timer restarted)");
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
