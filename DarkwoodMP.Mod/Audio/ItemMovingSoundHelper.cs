using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// Force-stops vanilla <see cref="ItemSounds"/> scrape loops and owns the
    /// single-scrape-owner bookkeeping for multiplayer body-push / drag.
    ///
    /// Ownership rule (per object name, per peer):
    /// - Local free-body push: native ItemSounds only.
    /// - Remote (client-kinematic / DragSync / PhysicsState interp): MOS only;
    ///   MarkRemoteScrape suppresses native ItemSounds.Update moving branch.
    /// </summary>
    public static class ItemMovingSoundHelper
    {
        /// <summary>
        /// Vanilla ItemSounds moving stop: <c>Stop(0.5f)</c>. Decision is immediate;
        /// only the soft tail lasts half a second.
        /// </summary>
        public const float IntentionalStopFade = 0.5f;

        /// <summary>
        /// After ForceStop, ignore scrape restarts for this long (late Unreliable
        /// body-push starts / residual DragSync / native ItemSounds re-arm).
        /// </summary>
        public const float PostStopSuppressSec = 0.45f;

        /// <summary>Player horizontal speed below this = no longer pushing.</summary>
        private const float LocalPushStopSpeed = 1f;

        /// <summary>Tiny grace so a single zero-speed frame mid-push doesn't kill scrape.</summary>
        private const float LocalPushStopGrace = 0.05f;

        private static readonly Dictionary<string, float> _suppressUntil =
            new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>Object names whose scrape is owned by MOS (remote network motion).</summary>
        private static readonly HashSet<string> _remoteScrape =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Local free-body names the player has been contacting while moving
        /// (native scrape owner). Cleared on stop.
        /// </summary>
        private static readonly HashSet<string> _localPushActive =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Soft ownership after contact ends — host PhysicsState echo must not
        /// yank the free-body for a short window between touch frames.
        /// </summary>
        private static readonly Dictionary<string, float> _localPushAuthorityUntil =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private const float LocalPushAuthorityGrace = 0.45f;

        private static float _playerSlowSince = -1f;

        /// <summary>True if scrape start should be ignored for this object name.</summary>
        public static bool IsScrapeSuppressed(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            if (!_suppressUntil.TryGetValue(objectName, out float until))
                return false;
            if (Time.unscaledTime >= until)
            {
                _suppressUntil.Remove(objectName);
                return false;
            }
            return true;
        }

        private static void ArmSuppress(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return;
            _suppressUntil[objectName] = Time.unscaledTime + PostStopSuppressSec;
        }

        /// <summary>Mark object as remote-owned scrape — native ItemSounds must not arm.</summary>
        public static void MarkRemoteScrape(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return;
            _remoteScrape.Add(objectName);
        }

        /// <summary>Clear remote scrape ownership (local physics / free body may take over).</summary>
        public static void ClearRemoteScrape(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return;
            _remoteScrape.Remove(objectName);
        }

        /// <summary>True while MOS / network path owns the scrape for this name.</summary>
        public static bool IsRemoteScrape(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            return _remoteScrape.Contains(objectName);
        }

        /// <summary>
        /// True if local player is body-pushing or E-dragging this object by name.
        /// Ignores MOS remote-ownership — host PhysicsState echo must not arm MOS
        /// (or ForceStop native) while we are the local scrape owner.
        /// </summary>
        public static bool IsLocalPushOrDragOwner(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return false;

            Player p = Player.Instance;
            if (p != null)
            {
                if (p.dragging && p.itemBeingDragged != null
                    && string.Equals(p.itemBeingDragged.gameObject.name, objectName, StringComparison.Ordinal))
                    return true;

                if (p.touchingColliders != null)
                {
                    for (int i = 0; i < p.touchingColliders.Count; i++)
                    {
                        Collider c = p.touchingColliders[i];
                        if (c == null) continue;
                        if (string.Equals(c.gameObject.name, objectName, StringComparison.Ordinal))
                            return true;
                        // Item root name can differ from collider GO name.
                        Item it = c.GetComponent<Item>() ?? c.GetComponentInParent<Item>();
                        if (it != null
                            && string.Equals(it.gameObject.name, objectName, StringComparison.Ordinal))
                            return true;
                    }
                }
            }

            if (_localPushActive.Contains(objectName))
                return true;

            if (_localPushAuthorityUntil.TryGetValue(objectName, out float until)
                && Time.unscaledTime < until)
                return true;

            return false;
        }

        /// <summary>Refresh soft free-body authority (call while local push is live).</summary>
        public static void NoteLocalPushAuthority(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return;
            _localPushAuthorityUntil[objectName] = Time.unscaledTime + LocalPushAuthorityGrace;
        }

        /// <summary>
        /// True if local player is currently contacting this object by name
        /// (CharBase.touchingColliders) and it is not remote-owned.
        /// Used to ignore body-push PlayerAudio starts for the local pusher.
        /// </summary>
        public static bool IsLocalOwnedScrape(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            if (IsRemoteScrape(objectName)) return false;
            return IsLocalPushOrDragOwner(objectName);
        }

        public static void ResetSuppress()
        {
            _suppressUntil.Clear();
            _remoteScrape.Clear();
            _localPushActive.Clear();
            _localPushAuthorityUntil.Clear();
            _playerSlowSince = -1f;
        }

        /// <summary>
        /// Local intent stop (T3): when the local player stops pushing a free body
        /// (not remote-owned), ForceStop immediately so residual vel cannot keep
        /// native ItemSounds armed for seconds.
        /// Call once per frame from the physics LateUpdate path.
        /// </summary>
        public static void TickLocalPushScrapeStop()
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected) return;

            Player p = Player.Instance;
            if (p == null) return;

            float hSpeed = 0f;
            if (p.Rigidbody != null)
            {
                Vector3 v = p.Rigidbody.velocity;
                hSpeed = new Vector3(v.x, 0f, v.z).magnitude;
            }

            bool playerMoving = hSpeed >= LocalPushStopSpeed;

            // Build current contact set of Item names (skip remote-owned).
            // Reuse _localPushActive as "still contacting this frame" via a temp swap.
            // To avoid alloc: walk contacts, mark still-active, collect stops.
            HashSet<string> stillContact = null;

            if (p.touchingColliders != null)
            {
                for (int i = 0; i < p.touchingColliders.Count; i++)
                {
                    Collider c = p.touchingColliders[i];
                    if (c == null) continue;
                    // Only items with a moving sound matter.
                    Item item = c.GetComponent<Item>() ?? c.GetComponentInParent<Item>();
                    if (item == null) continue;
                    string name = item.gameObject.name;
                    if (string.IsNullOrEmpty(name) || IsRemoteScrape(name)) continue;
                    if (IsScrapeSuppressed(name)) continue;
                    // E-drag owns scrape via DragSync/MOS — never treat as body-push stop.
                    if (item.beingDragged) continue;
                    if (p.dragging && p.itemBeingDragged == item) continue;
                    if (net is Networking.LanNetworkManager lnm
                        && (lnm._dragClaims.ContainsKey(name) || lnm._remoteDragItemNames.Contains(name)))
                        continue;

                    if (stillContact == null)
                        stillContact = new HashSet<string>(StringComparer.Ordinal);
                    stillContact.Add(name);

                    // Soft authority while touching (even standing) so host echo
                    // does not yank mid-push between walk frames.
                    NoteLocalPushAuthority(name);
                    // Contact while player is walking into it → local push ownership.
                    if (playerMoving)
                        _localPushActive.Add(name);
                }
            }

            if (_localPushActive.Count == 0)
            {
                _playerSlowSince = -1f;
                return;
            }

            // Player slowed: start grace, then ForceStop every active local push.
            if (!playerMoving)
            {
                if (_playerSlowSince < 0f)
                    _playerSlowSince = Time.unscaledTime;

                if (Time.unscaledTime - _playerSlowSince >= LocalPushStopGrace)
                {
                    List<string> toStop = null;
                    foreach (string name in _localPushActive)
                    {
                        if (toStop == null) toStop = new List<string>();
                        toStop.Add(name);
                    }
                    if (toStop != null)
                    {
                        for (int i = 0; i < toStop.Count; i++)
                            ForceStopLocalPush(toStop[i]);
                    }
                    _localPushActive.Clear();
                    _playerSlowSince = -1f;
                }
                return;
            }

            _playerSlowSince = -1f;

            // Contact lost while still moving: stop scrape for that object.
            if (stillContact == null)
            {
                List<string> lostAll = null;
                foreach (string name in _localPushActive)
                {
                    if (lostAll == null) lostAll = new List<string>();
                    lostAll.Add(name);
                }
                if (lostAll != null)
                {
                    for (int i = 0; i < lostAll.Count; i++)
                        ForceStopLocalPush(lostAll[i]);
                }
                _localPushActive.Clear();
                return;
            }

            List<string> lost = null;
            foreach (string name in _localPushActive)
            {
                if (!stillContact.Contains(name))
                {
                    if (lost == null) lost = new List<string>();
                    lost.Add(name);
                }
            }
            if (lost != null)
            {
                for (int i = 0; i < lost.Count; i++)
                {
                    ForceStopLocalPush(lost[i]);
                    _localPushActive.Remove(lost[i]);
                }
            }
        }

        private static void ForceStopLocalPush(string objectName)
        {
            if (string.IsNullOrEmpty(objectName) || IsRemoteScrape(objectName)) return;
            // Never body-push-stop a live drag claim (would sleep RB mid E-drag).
            var net = ModRuntime.Network as Networking.LanNetworkManager;
            if (net != null
                && (net._dragClaims.ContainsKey(objectName) || net._remoteDragItemNames.Contains(objectName)))
                return;
            Player p = Player.Instance;
            if (p != null && p.dragging && p.itemBeingDragged != null
                && string.Equals(p.itemBeingDragged.gameObject.name, objectName, StringComparison.Ordinal))
                return;

            ForceStopByName(objectName);

            // Host-local release: tell peers to kill MOS promptly (reliable stop),
            // so observers don't wait on Unreliable PhysicsState quiet ticks.
            if (net != null && net.Role == Networking.NetworkRole.Host)
                Networking.LanNetworkManager.NotifyBodyPushStopped(objectName);
        }

        /// <summary>
        /// Stop native movingSoundAO *now* (with vanilla-length fade), clear the field,
        /// sleep the body so ItemSounds.Update will not re-arm on residual velocity.
        /// Also stops MOS + any AudioController objects for the same ids.
        /// </summary>
        public static void ForceStop(GameObject go, float fadeSec = IntentionalStopFade)
        {
            if (go == null) return;

            string objectName = go.name;
            ArmSuppress(objectName);
            ClearRemoteScrape(objectName);
            _localPushActive.Remove(objectName);

            ItemSounds sounds = go.GetComponent<ItemSounds>();
            string soundId = MovingObjectSoundService.ResolveMovingSoundId(sounds);

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                // Kill residual motion so ItemSounds.Update does not re-arm scrape.
                // Do NOT Sleep() — that left free-bodies frozen after drag/push end
                // until something re-woke them (felt like objects "sticking" in co-op).
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            if (sounds != null)
            {
                try
                {
                    var ao = Traverse.Create(sounds).Field("movingSoundAO").GetValue<AudioObject>();
                    if (ao != null)
                    {
                        ao.Stop(fadeSec);
                        Traverse.Create(sounds).Field("movingSoundAO").SetValue(null);
                    }
                }
                catch
                {
                    // Traverse failure — fall through to StopAllVariants
                }
            }

            MovingObjectSoundService.StopAllVariants(objectName, soundId, fadeSec);

            // Grass variant may differ from primary movingSound id.
            if (sounds != null
                && !string.IsNullOrEmpty(sounds.movingSound_grass)
                && !string.Equals(sounds.movingSound_grass, soundId, System.StringComparison.OrdinalIgnoreCase))
            {
                MovingObjectSoundService.StopAllVariants(objectName, sounds.movingSound_grass, fadeSec);
            }
        }

        public static void ForceStopByName(string objectName, float fadeSec = IntentionalStopFade)
        {
            if (string.IsNullOrEmpty(objectName)) return;
            ArmSuppress(objectName);
            ClearRemoteScrape(objectName);
            _localPushActive.Remove(objectName);
            GameObject go = GameObject.Find(objectName);
            if (go != null)
                ForceStop(go, fadeSec);
            else
                MovingObjectSoundService.StopAllVariants(objectName, null, fadeSec);
        }

        /// <summary>
        /// Quiet/network stop: vanilla fade now, but do NOT arm PostStopSuppress and do
        /// NOT zero/sleep the rigidbody. Sparse PhysicsState + ForceStop thrash was the
        /// "1s scrape delay" — suppress ate the next NoteMoving after every quiet tick.
        /// Intentional ends (drag release, local push stop) still use <see cref="ForceStopByName"/>.
        /// Local pusher/dragger: only kill MOS — never touch native movingSoundAO (double-scrape / mute).
        /// </summary>
        public static void SoftStopNetwork(string objectName, float fadeSec = IntentionalStopFade)
        {
            if (string.IsNullOrEmpty(objectName)) return;

            // Local owner: kill residual MOS immediately; leave native ItemSounds alone.
            if (IsLocalPushOrDragOwner(objectName))
            {
                ClearRemoteScrape(objectName);
                MovingObjectSoundService.StopImmediate(objectName);
                return;
            }

            GameObject go = GameObject.Find(objectName);
            string soundId = null;
            if (go != null)
            {
                ItemSounds sounds = go.GetComponent<ItemSounds>();
                soundId = MovingObjectSoundService.ResolveMovingSoundId(sounds);
                if (sounds != null)
                {
                    try
                    {
                        var ao = Traverse.Create(sounds).Field("movingSoundAO").GetValue<AudioObject>();
                        if (ao != null)
                        {
                            ao.Stop(fadeSec);
                            Traverse.Create(sounds).Field("movingSoundAO").SetValue(null);
                        }
                    }
                    catch
                    {
                        // Traverse failure — MOS StopAllVariants still runs
                    }
                }
            }

            MovingObjectSoundService.StopAllVariants(objectName, soundId, fadeSec);
        }
    }
}
