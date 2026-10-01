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
        /// <summary>Original player position before spectating, used by network sync to avoid pushing the remote player.</summary>
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

        /// <summary>
        /// Exit spectator mode and restore the local player to active state.
        /// <paramref name="restorePosition"/> false: keep the body where it is (caller
        /// sends it home, as vanilla <c>onDeath</c> does) but still restore the
        /// invisible / ignoreMe flags saved on entry.
        /// </summary>
        public void ExitAndRespawn(bool restorePosition = true)
        {
            if (_spectateTargetIndex < 0) return;

            var player = Player.Instance;
            if (player != null)
            {
                if (restorePosition)
                    RestorePlayerPosition(player);
                else
                {
                    player.invisible = _savedPlayerInvisible;
                    player.ignoreMe = _savedPlayerIgnoreMe;
                }
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

            // Local flags only: leaving spectate must not wipe the other peers' death
            // bookkeeping. Set PreventSpectator after the reset (the reset clears it).
            DeathStateTracker.ResetLocal();
            DeathStateTracker.PreventSpectator = true;

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

            // Local flags only: leaving spectate must not wipe the other peers' death
            // bookkeeping. Set PreventSpectator after the reset (the reset clears it).
            DeathStateTracker.ResetLocal();
            DeathStateTracker.PreventSpectator = true;

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

            if (!CanUseSpectateKey())
                return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected)
                return;

            // Prefer alive proxies, ordered by player ID for a stable F4 cycle.
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
                    // Night/dream death spectators should not auto-respawn via F4 wrap;
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

        /// <summary>
        /// F4 is a menu-less hotkey, so it needs the same state guards as the F3 manual-save
        /// window plus the states where moving the body under a peer would corrupt a
        /// transition: dialogue, dream entry/switch, cutscene.
        /// </summary>
        private static bool CanUseSpectateKey()
        {
            if (Core.mainMenu || Core.loadingGame || Core.forbidInputs || Core.EnteringDream)
                return false;
            // F4 typed into chat / the F2 menu must not start spectating.
            if (UiInputLock.IsHeld)
                return false;

            Player player = Player.Instance;
            if (player == null || player.inDialogue)
                return false;

            Controller ctrl = Singleton<Controller>.Instance;
            if (ctrl != null && ctrl.playingCutscene)
                return false;

            Dreams dreams = Singleton<Dreams>.Instance;
            if (dreams != null && (dreams.switchingDream || dreams.wantToDream || dreams.dreamPrepared))
                return false;

            return true;
        }

        private void SyncProxyVision()
        {
            if (_proxyVision == null)
                return;

            var player = Player.Instance;
            // Dead local player has FOV lights off (switchVisibilty); copying them kills the cone.
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
            // Cone + ambient circle every frame (HideLocalExtraVision / bad copy can drop circle).
            _proxyVision.EnsureVisionCircle();
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
    }
}
