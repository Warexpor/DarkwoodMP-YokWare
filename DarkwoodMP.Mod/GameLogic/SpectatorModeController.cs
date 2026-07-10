using System.Collections.Generic;
using System.Linq;
using DarkwoodMP.Network;
using UnityEngine;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Full co-op spectator (Horde SpectatorModeController port).
/// Night/dream death + optional F4 cycle. Peers see NetworkPositionOverride, not cam body.
/// </summary>
public sealed class SpectatorModeController : MonoBehaviour
{
    private bool _wasNoClip;
    private Transform _followTarget;
    private Transform _audioListener;
    private Vector3 _savedAudioListenerPosition;
    private Quaternion _savedAudioListenerRotation;
    private Vector3 _savedPlayerPosition;
    private bool _savedPlayerInvisible;
    private bool _savedPlayerIgnoreMe;
    private int _spectateTargetIndex = -1;

    public static SpectatorModeController Instance { get; private set; }

    public bool IsSpectating => _spectateTargetIndex >= 0;
    public Vector3? FollowTargetPosition => _followTarget != null ? (Vector3?)_followTarget.position : null;
    /// <summary>Pose peers should see while we spectate (death/corpse pos).</summary>
    public Vector3? NetworkPositionOverride => _spectateTargetIndex >= 0 ? (Vector3?)_savedPlayerPosition : null;

    public static void EnsureExists()
    {
        if (Instance != null) return;
        var go = new GameObject("YokWare_SpectatorMode");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<SpectatorModeController>();
    }

    private void Awake()
    {
        Instance = this;
    }

    public void ForceEnter(Transform target)
    {
        if (target == null) return;
        if (_spectateTargetIndex >= 0)
            ForceExitKeepDeathHold();

        var player = Player.Instance;
        var cam = Singleton<CamMain>.Instance;
        if (player == null || cam == null) return;

        EnterSpectate(target, player, cam);
        _spectateTargetIndex = 0;
    }

    public void ExitAndRespawn()
    {
        if (_spectateTargetIndex < 0) return;
        var player = Player.Instance;
        var cam = Singleton<CamMain>.Instance;
        if (player != null && cam != null)
            ExitSpectate(player, cam, restorePosition: true);
        else
            ResetState();
        DeathStateTracker.ClearMorningRepSkip();
        ModLogger.Msg("[Spectate] ExitAndRespawn");
    }

    /// <summary>Dream end already teleported the player — do not restore saved pos.</summary>
    public void ExitWithoutPositionRestore()
    {
        if (_spectateTargetIndex < 0) return;
        _spectateTargetIndex = -1;
        _followTarget = null;

        var player = Player.Instance;
        if (player != null)
        {
            var cam = Singleton<CamMain>.Instance;
            if (cam != null) cam.followTarget = player.transform;
            try { if (player.immobilised) player.stopImmobilise(); } catch { }
            player.invulnerable = false;
            player.noClipMode = false;
            try { player.switchVisibilty(true); } catch { }
            ShowLocalExtraVision(player);
            try
            {
                player.invisible = _savedPlayerInvisible;
                player.ignoreMe = _savedPlayerIgnoreMe;
            }
            catch { }
            RestoreAudioListener(player);
        }
        try
        {
            if (Singleton<global::UI>.Instance != null)
                Singleton<global::UI>.Instance.showVisibleUI();
        }
        catch { }
        ModLogger.Msg("[Spectate] ExitWithoutPositionRestore");
    }

    public void ForceExit()
    {
        bool hold = (DeathStateTracker.LocalNightDeath && !DeathStateTracker.AllDeadAtNight)
            || FinalDreamsceneManager.IsLocalDead;
        if (hold)
        {
            ForceExitKeepDeathHold();
            return;
        }
        ExitAndRespawn();
    }

    private void ForceExitKeepDeathHold()
    {
        // Clear cam follow only if needed — keep death spectate state by re-entering later
        _spectateTargetIndex = -1;
        _followTarget = null;
    }

    private void Update()
    {
        if (_spectateTargetIndex >= 0)
        {
            if (_followTarget == null || _followTarget.gameObject == null)
            {
                bool hold = DeathStateTracker.LocalNightDeath || FinalDreamsceneManager.IsLocalDead;
                if (hold && TryRetargetLivingProxy()) return;
                if (hold) return;
                ForceExit();
                return;
            }

            var proxy = _followTarget.GetComponentInParent<RemotePlayerProxy>();
            if (proxy != null && proxy.IsDead
                && (DeathStateTracker.LocalNightDeath || FinalDreamsceneManager.IsLocalDead))
            {
                if (TryRetargetLivingProxy()) return;
            }

            SyncAudioListener();
            SyncPlayerPosition();
        }

        if (!Input.GetKeyDown(KeyCode.F4)) return;
        var manager = NetworkManager.Instance;
        if (manager == null || !manager.IsConnected) return;

        var targets = GetSpectateTargets();
        if (targets.Count == 0) return;

        if (_spectateTargetIndex < 0)
        {
            ForceEnter(targets[0]);
            _spectateTargetIndex = 0;
        }
        else
        {
            _spectateTargetIndex++;
            if (_spectateTargetIndex >= targets.Count)
            {
                if (DeathStateTracker.LocalNightDeath || FinalDreamsceneManager.IsLocalDead)
                {
                    _spectateTargetIndex = 0;
                    SwitchToTarget(targets[0]);
                    return;
                }
                ExitAndRespawn();
                return;
            }
            SwitchToTarget(targets[_spectateTargetIndex]);
        }
    }

    private List<Transform> GetSpectateTargets()
    {
        var manager = NetworkManager.Instance;
        var list = new List<(int id, Transform t)>();
        if (manager == null) return new List<Transform>();
        foreach (var kvp in manager.RemotePlayers)
        {
            if (kvp.Value == null) continue;
            var proxy = kvp.Value.GetComponent<RemotePlayerProxy>();
            if (proxy != null && proxy.IsDead) continue;
            list.Add((kvp.Key, kvp.Value.transform));
        }
        return list.OrderBy(x => x.id).Select(x => x.t).ToList();
    }

    private bool TryRetargetLivingProxy()
    {
        var targets = GetSpectateTargets();
        if (targets.Count == 0) return false;
        _spectateTargetIndex = 0;
        SwitchToTarget(targets[0]);
        return true;
    }

    private void SwitchToTarget(Transform newTarget)
    {
        if (newTarget == null) return;
        _followTarget = newTarget;
        var cam = Singleton<CamMain>.Instance;
        if (cam != null) cam.followTarget = newTarget;
        SyncAudioListener();
        RefreshWorldGridAt(newTarget.position);

        var player = Player.Instance;
        if (player != null)
        {
            Vector3 tpPos = newTarget.position;
            tpPos.y = player.transform.position.y;
            player.transform.position = tpPos;
            if (player.Rigidbody != null) player.Rigidbody.position = tpPos;
        }
    }

    private void EnterSpectate(Transform remoteTransform, Player player, CamMain cam)
    {
        _followTarget = remoteTransform;
        _wasNoClip = player.noClipMode;
        cam.followTarget = remoteTransform;

        try { player.switchVisibilty(false); } catch { }
        HideLocalExtraVision(player);

        _savedPlayerPosition = player.transform.position;
        try
        {
            _savedPlayerInvisible = player.invisible;
            _savedPlayerIgnoreMe = player.ignoreMe;
            player.invisible = true;
            player.ignoreMe = true;
        }
        catch { }

        Vector3 tpPos = remoteTransform.position;
        tpPos.y = player.transform.position.y;
        player.transform.position = tpPos;
        if (player.Rigidbody != null) player.Rigidbody.position = tpPos;

        BindAudioListener(player, remoteTransform.position);
        try { player.immobilise(); } catch { }
        player.invulnerable = true;
        player.noClipMode = true;

        RefreshWorldGridAt(remoteTransform.position);
        try
        {
            if (Singleton<global::UI>.Instance != null)
                Singleton<global::UI>.Instance.hideVisibleUI();
        }
        catch { }
        ModLogger.Msg("[Spectate] Entered spectator mode");
    }

    private void ExitSpectate(Player player, CamMain cam, bool restorePosition)
    {
        _spectateTargetIndex = -1;
        _followTarget = null;
        cam.followTarget = player.transform;

        if (restorePosition)
            RestorePlayerPosition(player);
        RestoreAudioListener(player);
        try { if (player.immobilised) player.stopImmobilise(); } catch { }
        try { player.switchVisibilty(true); } catch { }
        ShowLocalExtraVision(player);
        player.invulnerable = false;
        player.noClipMode = _wasNoClip;
        try
        {
            player.invisible = _savedPlayerInvisible;
            player.ignoreMe = _savedPlayerIgnoreMe;
        }
        catch { }
        try
        {
            if (Singleton<global::UI>.Instance != null)
                Singleton<global::UI>.Instance.showVisibleUI();
        }
        catch { }
    }

    private void RestorePlayerPosition(Player player)
    {
        if (player == null) return;
        player.transform.position = _savedPlayerPosition;
        if (player.Rigidbody != null) player.Rigidbody.position = _savedPlayerPosition;
        RefreshWorldGridAt(_savedPlayerPosition);
    }

    private void BindAudioListener(Player player, Vector3 worldPos)
    {
        _audioListener = player.transform.Find("AudioListener");
        if (_audioListener == null)
        {
            var al = player.GetComponentInChildren<AudioListener>(true);
            if (al != null) _audioListener = al.transform;
        }
        if (_audioListener == null) return;
        _savedAudioListenerPosition = _audioListener.position;
        _savedAudioListenerRotation = _audioListener.rotation;
        _audioListener.SetParent(null);
        _audioListener.position = worldPos;
    }

    private void RestoreAudioListener(Player player)
    {
        if (_audioListener == null || player == null) return;
        try
        {
            _audioListener.SetParent(player.transform);
            _audioListener.position = _savedAudioListenerPosition;
            _audioListener.rotation = _savedAudioListenerRotation;
        }
        catch { }
        _audioListener = null;
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
        Vector3 pos = player.transform.position;
        pos.x = targetPos.x;
        pos.z = targetPos.z;
        player.transform.position = pos;
        if (player.Rigidbody != null) player.Rigidbody.position = pos;

        var cc = player.GetComponent<CharacterController>();
        if (cc != null && cc.enabled)
        {
            cc.enabled = false;
            cc.enabled = true;
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
        catch { }
    }

    private static void HideLocalExtraVision(Player player)
    {
        SetActiveIfExists(player.transform, "PlayerFOVLight", false);
        SetActiveIfExists(player.transform, "PlayerFOVLightDot", false);
    }

    private static void ShowLocalExtraVision(Player player)
    {
        SetActiveIfExists(player.transform, "PlayerFOVLight", true);
        SetActiveIfExists(player.transform, "PlayerFOVLightDot", true);
    }

    private static void SetActiveIfExists(Transform root, string name, bool active)
    {
        var child = root.Find(name);
        if (child != null) child.gameObject.SetActive(active);
    }

    private void ResetState()
    {
        _spectateTargetIndex = -1;
        _followTarget = null;
    }

    public static void ResetAll()
    {
        if (Instance != null && Instance.IsSpectating)
            Instance.ExitWithoutPositionRestore();
    }
}
