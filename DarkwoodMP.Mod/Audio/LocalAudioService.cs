using System;
using System.Collections.Generic;
using DWMPHorde.Spectator;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// Shared multiplayer audio helpers: listen position, distance culling,
    /// clip resolution, and send-side rate limiting.
    /// </summary>
    public static class LocalAudioService
    {
        // Peer SFX cull + Unity spatial falloff (player footsteps/guns/equip, entity, MOS).
        // 500 was tight for hideout↔yard; +30% so peers stay audible a bit farther.
        public const float DefaultMaxAudioDistance = 650f;
        public const float DefaultMinSpatialDistance = 30f;
        public const float DefaultMaxSpatialDistance = 650f;
        public const float ForwardMinIntervalSec = 0.08f;

        private static readonly Dictionary<string, float> _lastForwardTime =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, AudioClip> _clipCache =
            new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Where the local player is listening from (spectator target when spectating).
        /// </summary>
        public static Vector3 GetListenPosition()
        {
            var spec = SpectatorModeController.Instance;
            if (spec != null && spec.IsSpectating && spec.FollowTargetPosition.HasValue)
                return spec.FollowTargetPosition.Value;

            Player local = Player.Instance;
            if (local != null)
            {
                if (local._transform != null)
                    return local._transform.position;
                return local.transform.position;
            }

            if (Camera.main != null)
                return Camera.main.transform.position;

            return Vector3.zero;
        }

        public static float DistanceToListener(Vector3 worldPosition)
        {
            return Vector3.Distance(GetListenPosition(), worldPosition);
        }

        public static bool IsNearListener(Vector3 worldPosition, float maxDistance = DefaultMaxAudioDistance)
        {
            return DistanceToListener(worldPosition) <= maxDistance;
        }

        /// <summary>
        /// True if the local listener OR any remote proxy is within range.
        /// Host send-side cull for EntitySound: do not drop SFX when only a client is near the mob (3+).
        /// </summary>
        public static bool IsNearAnyListener(Vector3 worldPosition, float maxDistance = DefaultMaxAudioDistance)
        {
            if (IsNearListener(worldPosition, maxDistance))
                return true;

            var net = ModRuntime.Network as Networking.LanNetworkManager;
            if (net == null || !net.IsConnected)
                return false;

            float maxSq = maxDistance * maxDistance;
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null) continue;
                Vector3 p = proxy.transform.position;
                float dx = p.x - worldPosition.x;
                float dy = p.y - worldPosition.y;
                float dz = p.z - worldPosition.z;
                if (dx * dx + dy * dy + dz * dz <= maxSq)
                    return true;
            }
            return false;
        }

        public static bool IsNearListener(Component targetComponent, float maxDistance = DefaultMaxAudioDistance)
        {
            if (targetComponent == null) return false;
            return IsNearListener(targetComponent.transform.position, maxDistance);
        }

        /// <summary>Legacy name — distance to listen position (spectator-aware).</summary>
        public static float DistanceToLocalPlayer(Vector3 worldPosition) => DistanceToListener(worldPosition);

        /// <summary>Legacy name — near listen position (spectator-aware).</summary>
        public static bool IsNearLocalPlayer(Vector3 worldPosition, float maxDistance = DefaultMaxAudioDistance)
            => IsNearListener(worldPosition, maxDistance);

        public static bool IsNearLocalPlayer(Component targetComponent, float maxDistance = DefaultMaxAudioDistance)
            => IsNearListener(targetComponent, maxDistance);

        /// <summary>
        /// Rate-limit outbound audio forwards per sound id (stops never call this).
        /// </summary>
        public static bool TryAllowForward(string soundId)
        {
            if (string.IsNullOrEmpty(soundId))
                return false;

            float now = Time.unscaledTime;
            if (_lastForwardTime.TryGetValue(soundId, out float last) && now - last < ForwardMinIntervalSec)
                return false;

            _lastForwardTime[soundId] = now;
            return true;
        }

        public static void ResetRateLimits()
        {
            _lastForwardTime.Clear();
        }

        /// <summary>Drop resolved clip cache on session end (frees stale AudioClip refs).</summary>
        public static void ResetClipCache()
        {
            _clipCache.Clear();
        }

        /// <summary>
        /// Vanilla <c>Player.getHit</c> plays these parentless (2D SP feedback).
        /// Peers must hear them as spatial SFX at the victim proxy — never as a
        /// second local hit on their own character.
        /// </summary>
        public static bool IsPlayerHitFeedbackSound(string audioID)
        {
            if (string.IsNullOrEmpty(audioID))
                return false;
            if (string.Equals(audioID, "player_melee_hit", StringComparison.OrdinalIgnoreCase))
                return true;
            // Blocked hit (stamina absorbed)
            if (string.Equals(audioID, "door_hit_metal", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(audioID, "shadow_hit", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        /// <summary>
        /// Intentional no-parent <c>Play(string)</c> sounds that should still reach peers
        /// (e.g. molotov lighting). Everything else is local-only.
        /// </summary>
        public static bool IsAllowlistedNoParentSound(string audioID)
        {
            if (string.IsNullOrEmpty(audioID))
                return false;

            // Fixed co-op presence one-shots (vanilla often uses parentless Play(string)).
            // Note: UI_selectItem (hotbar slot click) is intentionally NOT shared —
            // peers still hear get/hide when the item is actually pulled out.
            if (IsPlayerHitFeedbackSound(audioID))
                return true;
            if (string.Equals(audioID, "door_locked", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(audioID, "close_drawer", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(audioID, "open_drawer", StringComparison.OrdinalIgnoreCase))
                return true;

            // Deliberate player-held one-shots without a parent Transform.
            if (audioID.IndexOf("molotov", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (audioID.IndexOf("lighter", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (audioID.IndexOf("aimSound", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (audioID.IndexOf("aim_sound", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (audioID.IndexOf("zippo", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (audioID.IndexOf("flare_ignite", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Match held item's configured SFX (attack/reload/empty/aim/get/hide/activate…).
            if (IsCurrentItemActionSound(audioID))
                return true;

            // Weapon/item equip while switching (getSound / hideSound are Play(string) only).
            if (IsPlayerItemSwitchSoundContext())
                return true;

            return false;
        }

        /// <summary>
        /// True if audioID matches a non-empty SFX field on the local player's current item.
        /// Covers parentless Play(attackSound/reloadSound/…) for all weapons without a name list.
        /// </summary>
        public static bool IsCurrentItemActionSound(string audioID)
        {
            if (string.IsNullOrEmpty(audioID))
                return false;

            Player p = Player.Instance;
            if (p == null || InvItemClass.isNull(p.currentItem) || p.currentItem.baseClass == null)
                return false;

            InvItem b = p.currentItem.baseClass;
            return IdEquals(audioID, b.attackSound)
                || IdEquals(audioID, b.reloadSound)
                || IdEquals(audioID, b.emptyClipSound)
                || IdEquals(audioID, b.aimSound)
                || IdEquals(audioID, b.aimReturnSound)
                || IdEquals(audioID, b.getSound)
                || IdEquals(audioID, b.hideSound)
                || IdEquals(audioID, b.activateSound)
                || IdEquals(audioID, b.deactivateSound);
        }

        private static bool IdEquals(string audioID, string field)
        {
            return !string.IsNullOrEmpty(field)
                && string.Equals(audioID, field, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True while the local player is mid hotbar/equip anim — getSound/hideSound
        /// play via parentless Play(string) and should reach peers.
        /// </summary>
        public static bool IsPlayerItemSwitchSoundContext()
        {
            Player p = Player.Instance;
            if (p == null) return false;
            return p.switchingItem || p.hidingItem;
        }

        /// <param name="suppressFootsteps">
        /// When true (player-origin sounds), block footstep IDs — remotes use
        /// HandleProxyFootstep. When false (enemy/world), footsteps are network-audible.
        /// </param>
        public static bool IsPersonalOrUiSound(string audioID, bool suppressFootsteps = true)
        {
            if (string.IsNullOrEmpty(audioID))
                return true;
            // UI_selectItem and other UI_* stay local (hotbar click / menus).
            if (audioID.StartsWith("UI_", StringComparison.OrdinalIgnoreCase))
                return true;
            if (suppressFootsteps && audioID.IndexOf("foot", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (audioID.IndexOf("walk_clothes", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (audioID.IndexOf("player_low_health", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (audioID.IndexOf("player_tired", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        /// <summary>
        /// World ambients/loops that vanilla parents to <see cref="Player"/> only so the
        /// listener carries them (SoundArea onlyOneInstance, RandomWorldSounds global,
        /// forest outside beds). Must stay local — networking them makes the remote
        /// player hear forest ambients from the host proxy position.
        /// </summary>
        public static bool IsWorldAmbientLocalOnly(string audioID)
        {
            if (string.IsNullOrEmpty(audioID))
                return true;

            if (audioID.IndexOf("ambient", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (audioID.IndexOf("ambience", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (audioID.IndexOf("outside", StringComparison.OrdinalIgnoreCase) >= 0
                && audioID.IndexOf("sound", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            try
            {
                AudioItem item = AudioController.GetAudioItem(audioID);
                if (item == null)
                    return false;

                // Forest / day-night beds are tagged isOutsideSound and parented to Player.
                if (item.isOutsideSound)
                    return true;

                // Continuous loops parented to Player are almost always world/area ambience
                // (fire, banshee, forest). One-shots (attacks, hits) use DoNotLoop.
                if (item.Loop != AudioItem.LoopMode.DoNotLoop)
                    return true;

                if (item.category != null && !string.IsNullOrEmpty(item.category.Name))
                {
                    string cat = item.category.Name;
                    if (cat.IndexOf("ambient", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                    if (cat.IndexOf("ambi", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                    if (cat.IndexOf("music", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch
            {
                // Audio system not ready — do not block (fail open for real player SFX).
            }

            return false;
        }

        /// <summary>Resolve an AudioToolkit id (or clip name) to a single AudioClip.</summary>
        public static AudioClip ResolveClip(string audioID, int depth = 0)
        {
            if (depth > 5 || string.IsNullOrEmpty(audioID))
                return null;

            if (_clipCache.TryGetValue(audioID, out AudioClip cached) && cached != null)
                return cached;

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
                _clipCache[audioID] = found;

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
        }
    }
}
