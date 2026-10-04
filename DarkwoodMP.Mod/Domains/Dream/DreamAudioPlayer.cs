using DWMPHorde.Audio;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Sync
{
    internal static class DreamAudioPlayer
    {
        /// <summary>
        /// Play a host dream one-shot through the game's AudioController, the way the host
        /// played it: the item's own clip pick, volume, rolloff and the Sound volume slider
        /// (a bare AudioSource skipped the mixer, always used the first clip and needed a
        /// hand-rolled clip lookup that missed many ids).
        /// </summary>
        public static void PlayForwardedAudio(DreamAudioMessage msg)
        {
            if (string.IsNullOrEmpty(msg.AudioID)) return;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            if (pos != Vector3.zero
                && !LocalAudioService.IsNearListener(pos, LocalAudioService.DefaultMaxAudioDistance))
                return;

            float vol = msg.Volume <= 0f ? 1f : Mathf.Clamp01(msg.Volume);
            // Our replay of a host sound: the AudioController forward patches must not send it on.
            bool prevNet = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                AudioObject ao = AudioController.Play(msg.AudioID, pos, null, vol);
                if (ao != null && msg.Pitch > 0f && !Mathf.Approximately(msg.Pitch, 1f)
                    && ao.primaryAudioSource != null)
                    ao.primaryAudioSource.pitch *= msg.Pitch;
            }
            finally { TraverseHack.SetExplicitFlag(prevNet); }
        }
    }
}
