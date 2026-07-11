using System.Collections.Generic;
using System.Linq;
using DWMPHorde;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Spectator
{
    public sealed class SpectatorModeController : MonoBehaviour
    {
        private bool _wasNoClip;
        private Transform _followTarget;
        private PlayerVisionController _proxyVision;
        private Transform _audioListener;
        private Vector3 _savedAudioListenerPosition;
        private Quaternion _savedAudioListenerRotation;
        private Vector3 _savedPlayerPosition;
        private bool _savedPlayerInvisible;
        private bool _savedPlayerIgnoreMe;
        private int _spectateTargetIndex = -1;

        public static SpectatorModeController Instance { get; private set; }
        public bool IsSpectating => _spectateTargetIndex >= 0;
        /// <summary>Position of the spectated target, used by Harmony culling patch.</summary>
        public Vector3? FollowTargetPosition => _followTarget != null ? (Vector3?)_followTarget.position : null;
        /// <summary>Original player position before spectating — used by network sync to avoid pushing the remote player.</summary>
        public Vector3? NetworkPositionOverride => _spectateTargetIndex >= 0 ? (Vector3?)_savedPlayerPosition : null;

        public static void EnsureExists()
        {
            if (Instance != null) return;
            var go = new GameObject("SpectatorModeController");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<SpectatorModeController>();
        }

        /// <summary>Programmatically enter spectator mode, following the given target transform.</summary>
        public void ForceEnter(Transform target)
        {
            if (_spectateTargetIndex >= 0)
            {
                ForceExit();
            }

            var player = Player.Instance;
            if (player == null) return;

            var cam = Singleton<CamMain>.Instance;
            if (cam == null) return;

            EnterSpectate(target, player, cam);
            _spectateTargetIndex = 0;
        }

        /// <summary>Exit spectator mode and restore the local player to active state.</summary>
        public void ExitAndRespawn()
        {
            if (_spectateTargetIndex < 0) return;

            var player = Player.Instance;
            if (player != null)
            {
                RestorePlayerPosition(player);
                player.switchVisibilty(true);
                ShowLocalExtraVision(player);
                if (player.immobilised)
                    player.stopImmobilise();
                player.invulnerable = false;
                player.noClipMode = false;
            }

            RestoreAudioListener(player);
            if (player != null)
                MuteLocalPlayerAudio(player, mute: false);

            var cam = Singleton<CamMain>.Instance;
            if (cam != null && player != null)
            {
                cam.followTarget = player.transform;
            }

            if (_proxyVision != null)
            {
                _proxyVision.SetAllVisionDisabled();
                _proxyVision = null;
            }

            if (Singleton<UI>.Instance != null)
                Singleton<UI>.Instance.showVisibleUI();

            _followTarget = null;
            _spectateTargetIndex = -1;

            DeathStateTracker.PreventSpectator = true;
            DeathStateTracker.Reset();

            ModRuntime.LegacyInfo("[Spectate] ExitAndRespawn — player restored");
        }

        /// <summary>Exit spectator mode without restoring the player position.
        /// Used when the dream ending has already teleported the player to the correct position.</summary>
        public void ExitWithoutPositionRestore()
        {
            if (_spectateTargetIndex < 0) return;
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
                player.switchVisibilty(true);
                ShowLocalExtraVision(player);
                player.invisible = _savedPlayerInvisible;
                player.ignoreMe = _savedPlayerIgnoreMe;
            }

            RestoreAudioListener(player);
            if (player != null)
                MuteLocalPlayerAudio(player, mute: false);

            if (_proxyVision != null)
            {
                _proxyVision.SetAllVisionDisabled();
                _proxyVision = null;
            }

            if (Singleton<UI>.Instance != null)
                Singleton<UI>.Instance.showVisibleUI();

            DeathStateTracker.PreventSpectator = true;
            DeathStateTracker.Reset();

            ModRuntime.LegacyInfo("[Spectate] ExitWithoutPositionRestore");
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Update()
        {
            if (_spectateTargetIndex >= 0)
            {
                if (_followTarget == null || _followTarget.gameObject == null)
                {
                    // Night / dream death: retarget another living peer; never wipe death state
                    // just because a proxy despawned (disconnect) mid-spectate.
                    bool holdDeathSpectate = DeathStateTracker.LocalNightDeath
                        || FinalDreamsceneManager.IsLocalDead;
                    if (holdDeathSpectate && TryRetargetLivingProxy())
                        return;
                    if (holdDeathSpectate)
                    {
                        // Hold: wait for all-dead / host morning / dream end resolve.
                        return;
                    }
                    ForceExit();
                    return;
                }
                // If current target died, switch to next living proxy when possible.
                var cb = _followTarget.GetComponentInParent<CharBase>();
                if (cb != null && !cb.alive
                    && (DeathStateTracker.LocalNightDeath || FinalDreamsceneManager.IsLocalDead))
                {
                    if (TryRetargetLivingProxy())
                        return;
                }
                SyncProxyVision();
                SyncAudioListener();
                SyncPlayerPosition();
            }

            if (!Input.GetKeyDown(KeyCode.F4))
                return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected)
                return;

            // Prefer alive proxies, ordered by playerId for stable F4 cycle (P2.2)
            var targets = net.GetAllProxies()
                .Where(p => p != null)
                .OrderBy(p => p.PlayerId)
                .ToList();
            var alive = targets
                .Where(p => p.GetComponent<CharBase>()?.alive != false)
                .ToList();
            if (alive.Count > 0)
                targets = alive;

            if (targets.Count == 0)
                return;

            if (_spectateTargetIndex < 0)
            {
                StartSpectate(targets[0].transform);
                _spectateTargetIndex = 0;
            }
            else
            {
                _spectateTargetIndex++;
                if (_spectateTargetIndex >= targets.Count)
                {
                    // Night/dream death spectators should not auto-respawn via F4 wrap —
                    // only exit if local is actually allowed to leave spectator.
                    if (DeathStateTracker.LocalNightDeath || FinalDreamsceneManager.IsLocalDead)
                    {
                        _spectateTargetIndex = 0;
                        SwitchToTarget(targets[0].transform);
                        return;
                    }
                    ExitAndRespawn();
                    return;
                }
                SwitchToTarget(targets[_spectateTargetIndex].transform);
            }
        }

        private void SyncProxyVision()
        {
            if (_proxyVision == null)
                return;

            var player = Player.Instance;
            // Dead local player has FOV lights off (switchVisibilty) — copying them kills the cone.
            // Use spectator defaults for cone shape; flashlight stays network-driven on the proxy.
            bool localFovLive = player != null && player.alive
                && !DeathStateTracker.LocalNightDeath
                && player.FOVLogic != null
                && player.FOVLogic.gameObject.activeInHierarchy;

            if (localFovLive)
            {
                _proxyVision.CopyFovValuesFrom(player);
                _proxyVision.SetVisionConeEnabled(true);
                // Only mirror local flashlight when F4-spectating while alive.
                _proxyVision.SetFlashlightEnabled(PlayerVisionController.IsFlashlightActiveOn(player));
            }
            else
            {
                _proxyVision.ApplySpectatorConeDefaults();
                // Leave proxy flashlight as set by continuous light sync (host's torch/flash).
            }
        }

        private void SyncAudioListener()
        {
            if (_audioListener == null || _followTarget == null) return;
            _audioListener.position = _followTarget.position;
            _audioListener.rotation = _followTarget.rotation;
        }

        private void SyncPlayerPosition()
        {
            var player = Player.Instance;
            if (player == null || _followTarget == null) return;

            Vector3 targetPos = _followTarget.position;
            Vector3 pos = player._transform.position;
            pos.x = targetPos.x;
            pos.z = targetPos.z;
            player._transform.position = pos;
            if (player.Rigidbody != null)
                player.Rigidbody.position = pos;

            // Toggle CharacterController so Unity re-evaluates trigger overlaps.
            // Without this, teleporting via direct position assignment skips
            // OnTriggerEnter/OnTriggerExit for room/area volumes, causing the
            // game to play the wrong ambient (e.g. outdoor sounds when inside).
            var cc = player.GetComponent<CharacterController>();
            if (cc != null && cc.enabled)
            {
                cc.enabled = false;
                cc.enabled = true;
            }
        }

        private static void HideLocalExtraVision(Player player)
        {
            Transform t = player.transform;
            SetActiveIfExists(t, "PlayerFOVLight", false);
            SetActiveIfExists(t, "PlayerFOVLightDot", false);
        }

        private static void ShowLocalExtraVision(Player player)
        {
            Transform t = player.transform;
            SetActiveIfExists(t, "PlayerFOVLight", true);
            SetActiveIfExists(t, "PlayerFOVLightDot", true);
        }

        private static void SetActiveIfExists(Transform root, string name, bool active)
        {
            Transform child = root.Find(name);
            if (child != null)
                child.gameObject.SetActive(active);
        }

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
                // Dead/night-death local FOV is inactive — SyncFovConeFrom would leave circle-only.
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
        /// Local body is teleported under the spectated peer — corpse get-up / CharacterSounds
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
                ModRuntime.LegacyInfo("[Spectate] MuteLocalPlayerAudio: " + ex.Message);
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
                // Keep SkipMorningRepBonus / LocalNightDeath for host morning resolution.
                DeathStateTracker.PreventSpectator = false;
                ModRuntime.LegacyInfo("[Spectate] Force exited but holding night-death state");
                return;
            }

            DeathStateTracker.PreventSpectator = true;
            DeathStateTracker.Reset();

            ModRuntime.LegacyInfo("[Spectate] Force exited (follow target lost)");
        }

        /// <summary>Switch camera to the lowest-PlayerId living remote proxy.</summary>
        private bool TryRetargetLivingProxy()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return false;

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
