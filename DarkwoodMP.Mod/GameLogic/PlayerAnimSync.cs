using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.Patches;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Syncs player animation state. Darkwood's Player has two tk2d animators:
/// `torsoAnimator` and `legsAnimator`. The held weapon is NOT part of the clip
/// name - the game swaps the torso animator's LIBRARY (a tk2dSpriteAnimation
/// asset per item, verified in Player.switchToItem IL). So both the clip name
/// and the library name must be synced to show weapons on remote players.
/// </summary>
public class PlayerAnimSync
{
    private readonly NetworkLayer _network;

    private const float CheckInterval = 0.05f; // 20Hz change detection
    /// <summary>Unreliable stream self-heal: re-broadcast last state even if unchanged.</summary>
    private const float ResendInterval = 0.75f;
    private float _lastCheck;
    private float _lastResend;
    private string _lastTorso = "";
    private string _lastLegs = "";
    private string _lastTorsoLib = "";
    private string _lastLegsLib = "";
    private bool _lastTorsoPlaying = true;
    private bool _lastLegsPlaying = true;
    private float _lastTorsoFps;
    private float _lastLegsFps;
    private bool _hasSentOnce;

    private Type _playerType;
    private FieldInfo _torsoField;
    private FieldInfo _legsField;
    private Component _localPlayer;

    private readonly Dictionary<int, RemoteAnimators> _remote = new();

    // Library assets by name, discovered among loaded objects
    private Dictionary<string, tk2dSpriteAnimation> _libraries;
    private float _lastLibraryScan = float.MinValue;
    private const float LibraryRescanCooldown = 5f;

    public PlayerAnimSync(NetworkLayer network)
    {
        _network = network;
    }

    public void OnUpdate()
    {
        if (!ResolveApi()) return;

        // Every frame: smooth the remote legs toward their target direction
        InterpolateRemoteLegs();
        ProcessPreloadQueue();

        if (Time.time - _lastCheck < CheckInterval) return;
        _lastCheck = Time.time;

        if (_localPlayer == null)
        {
            var t = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<PlayerSync>()?.LocalPlayerTransform;
            if (t == null) return;
            _localPlayer = t.GetComponent(_playerType);
            if (_localPlayer == null) return;
        }

        // Retry every tick until it succeeds - ItemsDatabase may not exist the
        // instant the local player resolves (a one-shot call would never retry).
        PreloadLibraries();

        var torsoAnimator = _torsoField.GetValue(_localPlayer) as tk2dSpriteAnimator;
        var legsAnimator = _legsField.GetValue(_localPlayer) as tk2dSpriteAnimator;

        var torso = GetClipName(torsoAnimator);
        var legs = GetClipName(legsAnimator);
        var torsoLib = GetLibraryName(torsoAnimator);
        var legsLib = GetLibraryName(legsAnimator);
        // The game pauses/resumes and re-speeds the legs animator rather than
        // switching clips, so those states are part of the change detection
        var torsoPlaying = IsAnimatorPlaying(torsoAnimator);
        var legsPlaying = IsAnimatorPlaying(legsAnimator);
        var torsoFps = QuantizeFps(GetFps(torsoAnimator));
        var legsFps = QuantizeFps(GetFps(legsAnimator));

        var changed = torso != _lastTorso || legs != _lastLegs
            || torsoLib != _lastTorsoLib || legsLib != _lastLegsLib
            || torsoPlaying != _lastTorsoPlaying || legsPlaying != _lastLegsPlaying
            || !Mathf.Approximately(torsoFps, _lastTorsoFps)
            || !Mathf.Approximately(legsFps, _lastLegsFps);
        var dueResend = _hasSentOnce && Time.time - _lastResend >= ResendInterval;
        if (!changed && !dueResend) return;

        _lastTorso = torso;
        _lastLegs = legs;
        _lastTorsoLib = torsoLib;
        _lastLegsLib = legsLib;
        _lastTorsoPlaying = torsoPlaying;
        _lastLegsPlaying = legsPlaying;
        _lastTorsoFps = torsoFps;
        _lastLegsFps = legsFps;
        _lastResend = Time.time;
        _hasSentOnce = true;

        var localId = NetworkManager.Instance != null && NetworkManager.Instance.LocalPlayerId >= 0
            ? NetworkManager.Instance.LocalPlayerId
            : Math.Max(_network.LocalClientId, 0);

        // Unreliable stream; periodic resend heals lost walk pause/fps packets
        _network.Broadcast(new PlayerAnimPacket
        {
            PlayerId = localId,
            Torso = torso,
            Legs = legs,
            TorsoLib = torsoLib,
            LegsLib = legsLib,
            TorsoPlaying = torsoPlaying,
            LegsPlaying = legsPlaying,
            TorsoFps = torsoFps,
            LegsFps = legsFps
        }, reliable: false);

        if (NetworkLayer.VerboseLogging && changed)
            ModLogger.VerboseMsg("Anim",
                $"send legs='{legs}' playing={legsPlaying} fps={legsFps:F1} torso='{torso}'");
    }

    public void OnPlayerAnim(PlayerAnimPacket packet)
    {
        var manager = NetworkManager.Instance;
        if (manager == null || packet.PlayerId == manager.LocalPlayerId) return;
        if (!ResolveApi()) return;

        if (!_remote.TryGetValue(packet.PlayerId, out var animators)
            || animators.Torso == null || animators.Legs == null)
        {
            // Keep retrying (throttled) until BOTH animators are resolved -
            // the legs animator needs special handling (see ResolveRemoteAnimators)
            if (Time.time - _lastResolve < 1f && animators != null)
            {
                // use what we have so far
            }
            else
            {
                _lastResolve = Time.time;
                var resolved = ResolveRemoteAnimators(packet.PlayerId);
                if (resolved != null)
                {
                    // Preserve the legs direction target from position packets
                    if (animators != null && animators.HasLegsTarget)
                    {
                        resolved.LegsTarget = animators.LegsTarget;
                        resolved.HasLegsTarget = true;
                    }
                    animators = resolved;
                    _remote[packet.PlayerId] = animators;
                }
            }
            if (animators == null) return;
        }

        // Library first (held weapon), then clip + play state + speed.
        // Remember the desired libraries so InterpolateRemoteLegs keeps retrying
        // until they finish preloading.
        animators.DesiredTorsoLib = packet.TorsoLib ?? "";
        animators.DesiredLegsLib = packet.LegsLib ?? "";

        if (!string.IsNullOrEmpty(packet.TorsoLib) && _loggedLibs.Add(packet.TorsoLib))
            ModLogger.Msg($"[PlayerAnimSync] remote weapon lib '{packet.TorsoLib}' | indexed={_libraries?.Count ?? 0} torsoResolved={animators.Torso != null}");
        ApplyLibrary(animators.Torso, packet.TorsoLib);
        ApplyLibrary(animators.Legs, packet.LegsLib);
        ApplyClipState(animators.Torso, packet.Torso, packet.TorsoPlaying, packet.TorsoFps);
        ApplyClipState(animators.Legs, packet.Legs, packet.LegsPlaying, packet.LegsFps);

        if (NetworkLayer.VerboseLogging)
            ModLogger.VerboseMsg("Anim",
                $"recv p{packet.PlayerId} legs='{packet.Legs}' playing={packet.LegsPlaying} fps={packet.LegsFps:F1}");
    }

    public void Reset()
    {
        _remote.Clear();
        _localPlayer = null;
        _preloaded = false;
        _preloadQueue = null;
        _itemsDb = null;
        _lastTorso = "";
        _lastLegs = "";
        _lastTorsoLib = "";
        _lastLegsLib = "";
        _hasSentOnce = false;
        _lastResend = 0f;
    }

    /// <summary>Forget cached animators when a remote player object is recreated.</summary>
    public void OnRemotePlayerRemoved(int playerId)
    {
        _remote.Remove(playerId);
    }

    // ------------------------------------------------------------------
    // Legs rotation (movement direction - independent of the body rotation)
    // ------------------------------------------------------------------

    /// <summary>World rotation of the local player's legs object.</summary>
    public Quaternion GetLocalLegsRotation()
    {
        if (_localPlayer == null || _legsField == null) return Quaternion.identity;
        var legs = _legsField.GetValue(_localPlayer) as tk2dSpriteAnimator;
        return legs != null ? legs.transform.rotation : Quaternion.identity;
    }

    /// <summary>Target legs direction for a remote player (from position packets).</summary>
    public void SetRemoteLegsRotation(int playerId, Quaternion rotation)
    {
        if (!_remote.TryGetValue(playerId, out var animators))
        {
            animators = new RemoteAnimators();
            _remote[playerId] = animators;
        }
        animators.LegsTarget = rotation;
        animators.HasLegsTarget = true;
    }

    private void InterpolateRemoteLegs()
    {
        if (_remote.Count == 0) return;
        var t = 1f - Mathf.Exp(-14f * Time.deltaTime);
        foreach (var kvp in _remote)
        {
            var animators = kvp.Value;

            // Retry applying the held-item library until it finishes loading
            // (ApplyLibrary is a no-op once the library is already set).
            if (!string.IsNullOrEmpty(animators.DesiredTorsoLib) && animators.Torso != null)
                ApplyLibrary(animators.Torso, animators.DesiredTorsoLib);
            if (!string.IsNullOrEmpty(animators.DesiredLegsLib) && animators.Legs != null)
                ApplyLibrary(animators.Legs, animators.DesiredLegsLib);

            if (!animators.HasLegsTarget || animators.Legs == null) continue;
            // World rotation on purpose: the legs clone is parented to the
            // remote player root, whose own rotation must not leak in
            var legsTransform = animators.Legs.transform;
            legsTransform.rotation = Quaternion.Slerp(legsTransform.rotation, animators.LegsTarget, t);
        }
    }

    private bool ResolveApi()
    {
        if (_playerType != null) return _torsoField != null && _legsField != null;
        _playerType = GameTypes.GetType("Player");
        if (_playerType == null) return false;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _torsoField = _playerType.GetField("torsoAnimator", flags);
        _legsField = _playerType.GetField("legsAnimator", flags);
        return _torsoField != null && _legsField != null;
    }

    private float _lastResolve = float.MinValue;

    private RemoteAnimators ResolveRemoteAnimators(int playerId)
    {
        var playerObj = NetworkManager.Instance?.GetRemotePlayer(playerId);
        if (playerObj == null) return null;

        var result = new RemoteAnimators();

        // Preferred: match the clone's animators by GameObject name against
        // the local player's rig (diagnosed: the cloned Player component's
        // legsAnimator field comes out null, so the fields can't be trusted)
        var localTorsoName = GetAnimatorGoName(_torsoField);
        var localLegsName = GetAnimatorGoName(_legsField);
        foreach (var animator in playerObj.GetComponentsInChildren<tk2dSpriteAnimator>(true))
        {
            if (result.Torso == null && animator.gameObject.name == localTorsoName)
                result.Torso = animator;
            else if (result.Legs == null && animator.gameObject.name == localLegsName)
                result.Legs = animator;
        }

        // Fallback: the cloned Player component's own fields
        var clonedPlayer = playerObj.GetComponentInChildren(_playerType, true);
        if (clonedPlayer != null)
        {
            result.Torso ??= _torsoField.GetValue(clonedPlayer) as tk2dSpriteAnimator;
            result.Legs ??= _legsField.GetValue(clonedPlayer) as tk2dSpriteAnimator;
        }

        // Last resort for the legs: the legs object apparently lives OUTSIDE
        // the player hierarchy, so the clone has none at all. Clone the local
        // player's legs object and attach it to the remote player.
        if (result.Legs == null)
            result.Legs = CloneLegsFromLocal(playerObj);

        ModLogger.Msg($"[PlayerAnimSync] Resolved remote {playerId}: torso={(result.Torso != null ? result.Torso.gameObject.name : "<null>")} legs={(result.Legs != null ? result.Legs.gameObject.name : "<null>")}");
        return result;
    }

    private string GetAnimatorGoName(FieldInfo field)
    {
        if (_localPlayer == null) return null;
        var animator = field.GetValue(_localPlayer) as tk2dSpriteAnimator;
        return animator != null ? animator.gameObject.name : null;
    }

    private tk2dSpriteAnimator CloneLegsFromLocal(GameObject remotePlayerObj)
    {
        try
        {
            if (_localPlayer == null) return null;
            var localLegs = _legsField.GetValue(_localPlayer) as tk2dSpriteAnimator;
            if (localLegs == null) return null;

            var localPlayerTransform = ((Component)_localPlayer).transform;
            var offset = localLegs.transform.position - localPlayerTransform.position;

            var legsClone = UnityEngine.Object.Instantiate(localLegs.gameObject);
            legsClone.name = localLegs.gameObject.name;
            legsClone.transform.SetParent(remotePlayerObj.transform, false);
            legsClone.transform.localPosition = offset;
            legsClone.transform.localRotation = Quaternion.identity;

            // Same rule as for the player clone: strip game logic, keep tk2d
            foreach (var behaviour in legsClone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                var asmName = behaviour.GetType().Assembly.GetName().Name;
                var isGameScript = asmName == "Assembly-CSharp" || asmName == "Assembly-CSharp-firstpass";
                if (isGameScript && !behaviour.GetType().Name.StartsWith("tk2d"))
                    behaviour.enabled = false;
            }

            ModLogger.Msg("[PlayerAnimSync] Legs object was outside the player hierarchy - cloned it onto the remote player");
            return legsClone.GetComponent<tk2dSpriteAnimator>();
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[PlayerAnimSync] Failed to clone legs: {ex.Message}");
            return null;
        }
    }

    private void ApplyLibrary(tk2dSpriteAnimator animator, string libraryName)
    {
        if (animator == null || string.IsNullOrEmpty(libraryName)) return;
        try
        {
            if (animator.Library != null && animator.Library.name == libraryName) return;

            var library = FindLibrary(libraryName);
            if (library == null) return;
            animator.Library = library;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[PlayerAnimSync] Failed to set library '{libraryName}': {ex.Message}");
        }
    }

    // Names that a full rescan could not find - retried with a long backoff.
    // Without this, one missing library caused a Resources.FindObjectsOfTypeAll
    // full-heap scan every 5 seconds (a periodic stutter).
    private readonly Dictionary<string, float> _failedLibraries = new();
    private const float FailedLibraryRetry = 60f;

    private bool _preloaded;
    private readonly HashSet<string> _loggedLibs = new();
    private float _lastDbWarn;
    private Queue<string> _preloadQueue;
    private ItemsDatabase _itemsDb;
    private const int PreloadPerFrame = 6;

    /// <summary>
    /// Queue every holdable ITEM's animation library for preload, so a remote
    /// player holding a weapon you have never equipped still renders with the
    /// right sprites - no more "hold the item first before the other sees it".
    /// Scoped to items the player can hold (not every animation in the game) and
    /// SPREAD over frames (ProcessPreloadQueue) so weak hardware never hitches.
    /// </summary>
    private void PreloadLibraries()
    {
        if (_preloaded) return;
        _preloaded = true;
        try
        {
            _itemsDb = UnityEngine.Object.FindObjectOfType<ItemsDatabase>();
            if (_itemsDb == null)
            {
                _preloaded = false;
                if (Time.time - _lastDbWarn > 5f) { _lastDbWarn = Time.time; ModLogger.Warning("[PlayerAnimSync] ItemsDatabase not found yet - preload waiting"); }
                return;
            }

            // Use itemsDict (the live lookup getItem uses) - itemsKeys is a
            // serialization helper that is EMPTY at runtime (gave "Preloading 0").
            var dictField = typeof(ItemsDatabase).GetField("itemsDict",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (dictField?.GetValue(_itemsDb) is not System.Collections.IDictionary dict || dict.Count == 0)
            {
                // Not populated yet (populateDict runs at Awake) - retry next tick.
                _preloaded = false;
                if (Time.time - _lastDbWarn > 5f) { _lastDbWarn = Time.time; ModLogger.Warning("[PlayerAnimSync] itemsDict empty - preload waiting for items"); }
                return;
            }

            _preloadQueue = new Queue<string>();
            foreach (var k in dict.Keys)
                if (k is string name && !string.IsNullOrEmpty(name)) _preloadQueue.Enqueue(name);
            _libraries ??= new Dictionary<string, tk2dSpriteAnimation>();
            ModLogger.Msg($"[PlayerAnimSync] Preloading {_preloadQueue.Count} item animation libraries (spread over frames)");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[PlayerAnimSync] Preload setup failed: {ex.Message}");
            _preloaded = false;
        }
    }

    /// <summary>Load a few item libraries per frame so there is no single hitch.</summary>
    private void ProcessPreloadQueue()
    {
        if (_preloadQueue == null || _preloadQueue.Count == 0 || _itemsDb == null) return;
        _libraries ??= new Dictionary<string, tk2dSpriteAnimation>();

        for (var i = 0; i < PreloadPerFrame && _preloadQueue.Count > 0; i++)
        {
            var name = _preloadQueue.Dequeue();
            try
            {
                var item = _itemsDb.getItem(name, false);
                var lib = item != null ? item.aniLibrary : null;
                if (string.IsNullOrEmpty(lib)) continue;
                // Key by the loaded ASSET's name, not the aniLibrary string - the
                // sender broadcasts animator.Library.name (the asset name), and
                // FindLibrary looks up by that. They can differ (e.g. a path), which
                // left weapons invisible.
                if (Resources.Load(lib, typeof(tk2dSpriteAnimation)) is tk2dSpriteAnimation asset
                    && asset != null && !_libraries.ContainsKey(asset.name))
                    _libraries[asset.name] = asset;
            }
            catch { /* one bad item shouldn't stop the preload */ }
        }

        if (_preloadQueue.Count == 0)
        {
            _failedLibraries.Clear();
            ModLogger.Msg($"[PlayerAnimSync] Item library preload complete ({_libraries.Count} libraries)");
        }
    }

    private tk2dSpriteAnimation FindLibrary(string name)
    {
        if (_libraries != null && _libraries.TryGetValue(name, out var cached) && cached != null)
            return cached;

        if (_failedLibraries.TryGetValue(name, out var nextRetry) && Time.time < nextRetry)
            return null;

        // Unknown library: rescan loaded assets (throttled). Weapon libraries
        // are loaded once anyone in the game session has used them.
        if (Time.time - _lastLibraryScan < LibraryRescanCooldown)
            return null;
        _lastLibraryScan = Time.time;

        _libraries = new Dictionary<string, tk2dSpriteAnimation>();
        foreach (var lib in Resources.FindObjectsOfTypeAll<tk2dSpriteAnimation>())
        {
            if (lib != null && !_libraries.ContainsKey(lib.name))
                _libraries[lib.name] = lib;
        }
        ModLogger.Msg($"[PlayerAnimSync] Indexed {_libraries.Count} animation libraries");

        _libraries.TryGetValue(name, out var found);
        if (found == null)
        {
            _failedLibraries[name] = Time.time + FailedLibraryRetry;
            ModLogger.Warning($"[PlayerAnimSync] Animation library '{name}' not loaded locally");
        }
        else
        {
            _failedLibraries.Remove(name);
        }
        return found;
    }

    private static string GetClipName(tk2dSpriteAnimator animator)
    {
        try
        {
            return animator != null && animator.CurrentClip != null ? animator.CurrentClip.name : "";
        }
        catch
        {
            return "";
        }
    }

    private static string GetLibraryName(tk2dSpriteAnimator animator)
    {
        try
        {
            return animator != null && animator.Library != null ? animator.Library.name : "";
        }
        catch
        {
            return "";
        }
    }

    private static void ApplyClipState(tk2dSpriteAnimator animator, string clipName, bool playing, float fps)
    {
        if (animator == null || string.IsNullOrEmpty(clipName)) return;
        try
        {
            var current = animator.CurrentClip;
            if (current == null || current.name != clipName)
            {
                animator.Play(clipName);
            }

            // Per-animator fps override; NEVER write clip.fps - shared clip asset
            if (fps > 0f)
                animator.ClipFps = fps;
            else if (animator.ClipFps > 0f)
                animator.ClipFps = 0f; // clear stale override so clip default applies

            if (playing)
            {
                if (!animator.Playing)
                    animator.Play(clipName);
                if (animator.Paused)
                    animator.Resume(); // Resume, not Play: no frame-0 restart pop
            }
            else
            {
                // Walk idle is often Pause on same clip — always honor remote pause
                if (animator.Playing && !animator.Paused)
                    animator.Pause();
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[PlayerAnimSync] Failed to apply clip '{clipName}': {ex.Message}");
        }
    }

    private static bool IsAnimatorPlaying(tk2dSpriteAnimator animator)
    {
        // Darkwood stops the legs via Pause() - tk2d keeps `Playing` true
        // while paused, so the paused state is the one that matters
        try { return animator != null && animator.Playing && !animator.Paused; }
        catch { return true; }
    }

    private static float GetFps(tk2dSpriteAnimator animator)
    {
        // The game sets speed on the clip asset (setLegsFPS -> clip.fps),
        // not on the animator override - read whichever is active
        try
        {
            if (animator == null) return 0f;
            if (animator.ClipFps > 0f) return animator.ClipFps;
            return animator.CurrentClip != null ? animator.CurrentClip.fps : 0f;
        }
        catch { return 0f; }
    }

    /// <summary>Quantize to 0.25 steps so continuous speed changes don't spam packets.</summary>
    private static float QuantizeFps(float fps)
    {
        return Mathf.Round(fps * 4f) / 4f;
    }

    private class RemoteAnimators
    {
        public tk2dSpriteAnimator Torso;
        public tk2dSpriteAnimator Legs;
        public Quaternion LegsTarget = Quaternion.identity;
        public bool HasLegsTarget;
        // Latest library the sender wants shown - re-applied each frame until the
        // library finishes (pre)loading, so a weapon held before its library was
        // available still appears (fixes "guns not visible from the start").
        public string DesiredTorsoLib = "";
        public string DesiredLegsLib = "";
    }
}
