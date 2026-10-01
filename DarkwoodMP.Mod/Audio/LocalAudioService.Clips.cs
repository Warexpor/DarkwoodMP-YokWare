using System;
using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Audio
{
    public static partial class LocalAudioService
    {
        private static readonly Dictionary<string, AudioClip> _clipCache =
            new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Ids that resolved to nothing, with the time to retry. A miss falls back to
        /// <c>Resources.FindObjectsOfTypeAll</c> (full asset scan), so repeated misses for the
        /// same id must not rescan every call; the short TTL lets late-loaded clips still resolve.
        /// </summary>
        private static readonly Dictionary<string, float> _missUntil =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        private const float MissRetrySec = 10f;

        /// <summary>Resolve an AudioToolkit id (or clip name) to a single AudioClip.</summary>
        public static AudioClip ResolveClip(string audioID, int depth = 0)
        {
            if (depth > 5 || string.IsNullOrEmpty(audioID))
                return null;

            if (_clipCache.TryGetValue(audioID, out AudioClip cached) && cached != null)
                return cached;

            if (depth == 0 && _missUntil.TryGetValue(audioID, out float retryAt))
            {
                if (Time.unscaledTime < retryAt)
                    return null;
                _missUntil.Remove(audioID);
            }

            AudioClip found = null;
            AudioItem item = AudioController.GetAudioItem(audioID);
            if (item != null && item.subItems != null && item.subItems.Length > 0)
            {
                // Prefer first real clip; recurse into Item-type subitems.
                for (int i = 0; i < item.subItems.Length; i++)
                {
                    var sub = item.subItems[i];
                    if (sub == null) continue;

                    if (sub.SubItemType == AudioSubItemType.Clip && sub.Clip != null)
                    {
                        found = sub.Clip;
                        break;
                    }

                    if (sub.SubItemType == AudioSubItemType.Item && !string.IsNullOrEmpty(sub.ItemModeAudioID))
                    {
                        found = ResolveClip(sub.ItemModeAudioID, depth + 1);
                        if (found != null)
                            break;
                    }
                }
            }

            if (found == null)
            {
                AudioClip[] allClips = Resources.FindObjectsOfTypeAll<AudioClip>();
                for (int i = 0; i < allClips.Length; i++)
                {
                    if (allClips[i] != null && allClips[i].name == audioID)
                    {
                        found = allClips[i];
                        break;
                    }
                }
            }

            if (found != null)
            {
                _clipCache[audioID] = found;
                _missUntil.Remove(audioID);
            }
            else if (depth == 0)
            {
                _missUntil[audioID] = Time.unscaledTime + MissRetrySec;
            }

            return found;
        }

        /// <summary>Volume scale from AudioItem + first subitem (matches typical Play path).</summary>
        public static float GetItemVolumeScale(string audioID)
        {
            AudioItem item = AudioController.GetAudioItem(audioID);
            if (item == null)
                return 1f;

            float vol = item.Volume;
            if (item.subItems != null && item.subItems.Length > 0 && item.subItems[0] != null)
                vol *= item.subItems[0].Volume;
            return vol;
        }

        public static void ClearClipCache()
        {
            _clipCache.Clear();
            _missUntil.Clear();
        }
    }
}
