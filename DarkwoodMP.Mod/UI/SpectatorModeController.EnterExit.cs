using System.Collections.Generic;
using System.Linq;
using DWMPHorde;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Spectator
{
    public sealed partial class SpectatorModeController : MonoBehaviour
    {

        private void StartSpectate(Transform remoteTransform)
        {
            var player = Player.Instance;
            if (player == null) return;

            var cam = Singleton<CamMain>.Instance;
            if (cam == null) return;

            EnterSpectate(remoteTransform, player, cam);
        }

        private void SwitchToTarget(Transform newTarget)
        {
            _followTarget = newTarget;

            var cam = Singleton<CamMain>.Instance;
            if (cam != null)
                cam.followTarget = newTarget;

            // Listen + cull from new target immediately
            SyncAudioListener();
            RefreshWorldGridAt(newTarget.position);

            // Teleport player to new target
            var player = Player.Instance;
            if (player != null)
            {
                Vector3 tpPos = newTarget.position;
                tpPos.y = player._transform.position.y;
                player._transform.position = tpPos;
                if (player.Rigidbody != null)
                    player.Rigidbody.position = tpPos;
            }

            // Update vision controller for new proxy
            if (_proxyVision != null)
            {
                _proxyVision.SetAllVisionDisabled();
                _proxyVision = null;
            }
            var proxyGo = newTarget.gameObject;
            _proxyVision = PlayerVisionController.From(proxyGo);
            if (_proxyVision != null)
            {
                bool localFovLive = player != null && player.alive
                    && !DeathStateTracker.LocalNightDeath
                    && player.FOVLogic != null
                    && player.FOVLogic.gameObject.activeInHierarchy;
                if (localFovLive)
                {
                    _proxyVision.SetVisionConeEnabled(true);
                    _proxyVision.SyncFovConeFrom(player);
                    _proxyVision.SetFlashlightEnabled(PlayerVisionController.IsFlashlightActiveOn(player));
                }
                else
                {
                    _proxyVision.ApplySpectatorConeDefaults();
                }
                _proxyVision.EnsureVisionCircle();
            }
        }

        private void EnterSpectate(Transform remoteTransform, Player player, CamMain cam)
        {
            _followTarget = remoteTransform;
            _wasNoClip = player.noClipMode;

            cam.followTarget = remoteTransform;

            player.switchVisibilty(false);
            HideLocalExtraVision(player);

            // Teleport player to target so game logic (audio triggers, AI proximity) uses correct position
            _savedPlayerPosition = player._transform.position;
            _savedPlayerInvisible = player.invisible;
            _savedPlayerIgnoreMe = player.ignoreMe;
            player.invisible = true;
            player.ignoreMe = true;
            Vector3 tpPos = remoteTransform.position;
            tpPos.y = player._transform.position.y;
            player._transform.position = tpPos;
            if (player.Rigidbody != null)
                player.Rigidbody.position = tpPos;

            // Move AudioListener to follow target so world SFX volume matches camera (5.3).
            BindAudioListener(player, remoteTransform.position);

            var proxyGo = remoteTransform.gameObject;
            _proxyVision = PlayerVisionController.From(proxyGo);
            if (_proxyVision != null)
            {
                // Dead/night-death local FOV is inactive; SyncFovConeFrom would leave circle-only.
                if (player.alive && !DeathStateTracker.LocalNightDeath
                    && player.FOVLogic != null && player.FOVLogic.gameObject.activeInHierarchy)
                {
                    _proxyVision.SetVisionConeEnabled(true);
                    _proxyVision.SyncFovConeFrom(player);
                    _proxyVision.SetFlashlightEnabled(PlayerVisionController.IsFlashlightActiveOn(player));
                }
                else
                {
                    _proxyVision.ApplySpectatorConeDefaults();
                }
                _proxyVision.EnsureVisionCircle();
            }

            // Mute corpse/get-up SFX on the local body while cam follows a peer.
            MuteLocalPlayerAudio(player, mute: true);

            player.immobilise();
            player.invulnerable = true;
            player.noClipMode = true;

            // Load WorldGrid / cullables around the spectated peer immediately (5.3).
            RefreshWorldGridAt(remoteTransform.position);

            if (Singleton<UI>.Instance != null)
                Singleton<UI>.Instance.hideVisibleUI();

            ModRuntime.LegacyInfo("[Spectate] Entered spectator mode");
        }

        private void BindAudioListener(Player player, Vector3 worldPos)
        {
            _audioListener = player.transform.Find("AudioListener");
            if (_audioListener == null)
            {
                AudioListener al = player.GetComponentInChildren<AudioListener>(true);
                if (al != null)
                    _audioListener = al.transform;
            }
            if (_audioListener == null)
                return;

            _savedAudioListenerPosition = _audioListener.position;
            _savedAudioListenerRotation = _audioListener.rotation;
            _audioListener.SetParent(null);
            _audioListener.position = worldPos;
        }

        /// <summary>
        /// Local body is teleported under the spectated peer; corpse get-up / CharacterSounds
        /// would play at the camera. Disable while spectating.
        /// </summary>
        private static void MuteLocalPlayerAudio(Player player, bool mute)
        {
            if (player == null) return;
            try
            {
                CharacterSounds cs = player.GetComponent<CharacterSounds>();
                if (cs != null)
                {
                    if (mute)
                    {
                        try { cs.destroySounds(); } catch { /* ok */ }
                        cs.enabled = false;
                    }
                    else
                    {
                        cs.enabled = true;
                    }
                }
                foreach (var src in player.GetComponentsInChildren<AudioSource>(true))
                {
                    if (src == null) continue;
                    if (mute)
                    {
                        src.Stop();
                        src.mute = true;
                    }
                    else
                    {
                        src.mute = false;
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.LegacyInfo($"[Spectate] MuteLocalPlayerAudio: {ex.Message}");
            }
        }

        private static void RefreshWorldGridAt(Vector3 pos)
        {
            try
            {
                var wg = Singleton<WorldGrid>.Instance;
                if (wg != null)
                    wg.refreshPosition(pos, instant: true, force: true);
            }
            catch
            {
                // World not ready
            }
        }

        private void ExitSpectate(Player player, CamMain cam)
        {
            _spectateTargetIndex = -1;
            _followTarget = null;

            cam.followTarget = null;

            if (_proxyVision != null)
            {
                _proxyVision.SetAllVisionDisabled();
                _proxyVision = null;
            }

            RestorePlayerPosition(player);
            RestoreAudioListener(player);
            player.stopImmobilise();
            player.switchVisibilty(true);
            ShowLocalExtraVision(player);
            player.invulnerable = false;
            player.noClipMode = _wasNoClip;

            if (Singleton<UI>.Instance != null)
                Singleton<UI>.Instance.showVisibleUI();

            ModRuntime.LegacyInfo("[Spectate] Exited spectator mode");
        }

        public void ForceExit()
        {
            bool holdNightDeath = DeathStateTracker.LocalNightDeath && !DeathStateTracker.AllDeadAtNight;

            _spectateTargetIndex = -1;
            _followTarget = null;

            var player = Player.Instance;
            if (player != null)
            {
                var cam = Singleton<CamMain>.Instance;
                if (cam != null)
                    cam.followTarget = player.transform;

                if (player.immobilised)
                    player.stopImmobilise();

                player.invulnerable = false;
                player.noClipMode = false;

                if (player.alive)
                {
                    player.switchVisibilty(true);
                    ShowLocalExtraVision(player);
                }

                RestorePlayerPosition(player);
                RestoreAudioListener(player);
                // EnterSpectate muted the local body; every other exit path unmutes it.
                MuteLocalPlayerAudio(player, mute: false);
            }

            if (_proxyVision != null)
            {
                _proxyVision.SetAllVisionDisabled();
                _proxyVision = null;
            }

            if (Singleton<UI>.Instance != null)
                Singleton<UI>.Instance.showVisibleUI();

            if (holdNightDeath)
            {
                // Keep LocalNightDeath: the host morning release (startDay) frees this peer.
                DeathStateTracker.PreventSpectator = false;
                ModRuntime.LegacyInfo("[Spectate] Force exited but holding night-death state");
                return;
            }

            DeathStateTracker.ResetLocal();
            DeathStateTracker.PreventSpectator = true;

            ModRuntime.LegacyInfo("[Spectate] Force exited (follow target lost)");
        }

        /// <summary>Switch camera to the lowest-PlayerId living remote proxy.</summary>
        private bool TryRetargetLivingProxy()
        {
            if (!NetGuard.Connected(out var net)) return false;

            var living = net.GetAllProxies()
                .Where(p => p != null && p.GetComponent<CharBase>()?.alive != false)
                .OrderBy(p => p.PlayerId)
                .ToList();
            if (living.Count == 0) return false;

            Transform t = living[0].transform;
            if (_followTarget == t) return true;

            if (_spectateTargetIndex < 0)
                ForceEnter(t);
            else
                SwitchToTarget(t);

            _spectateTargetIndex = 0;
            ModRuntime.LegacyInfo($"[Spectate] Retargeted to living player {living[0].PlayerId}");
            return true;
        }

        private void RestorePlayerPosition(Player player)
        {
            player.invisible = _savedPlayerInvisible;
            player.ignoreMe = _savedPlayerIgnoreMe;
            player._transform.position = _savedPlayerPosition;
            if (player.Rigidbody != null)
                player.Rigidbody.position = _savedPlayerPosition;
        }

        private void RestoreAudioListener(Player player)
        {
            if (_audioListener != null)
            {
                if (player != null)
                {
                    // Re-parent under player and reset local pose (don't leave world offset stuck).
                    _audioListener.SetParent(player.transform, false);
                    _audioListener.localPosition = Vector3.zero;
                    _audioListener.localRotation = Quaternion.identity;
                }
                else
                {
                    _audioListener.position = _savedAudioListenerPosition;
                    _audioListener.rotation = _savedAudioListenerRotation;
                }
            }
            _audioListener = null;
        }
    }
}
