using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// The Wolfman's hideout visit is a host body (<see cref="WolfDespawnClientPatch"/>): a client
    /// never despawns him itself, the host's despawn reaches it as an entity despawn. A save written
    /// while he stood in the hideout holds him, though, and every machine loads him from it. The
    /// host's morning on that load sends him away before any client has joined, so he is never
    /// streamed and no despawn reaches the clients: they kept a Wolfman of their own at the hideout,
    /// idle and talkable, that the host does not have. A client now drops a hideout Wolfman the host
    /// has never driven while the host's <c>wolf_inPlayerHideout</c> says he is not there.
    /// </summary>
    internal static class WolfVisitorGhostSweep
    {
        private const string WolfName = "Wolfman_att";
        /// <summary>A visit the host has just started may reach the flag after the body; wait it out.</summary>
        private const float GhostSeconds = 10f;

        private static readonly Dictionary<Character, float> _since = new Dictionary<Character, float>(); // process-scoped: keyed by body, dead keys pruned each sweep
        private static readonly List<Character> _drop = new List<Character>(); // process-scoped: scratch
        private static float _next; // process-scoped: throttle

        internal static void TickClient(LanNetworkManager net)
        {
            if (net == null || net.Role != NetworkRole.Client || Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 2f;
            Flags flags = Singleton<Flags>.Instance;
            if (flags == null || flags.isFlagTrue("wolf_inPlayerHideout"))
            {
                _since.Clear();
                return;
            }

            float now = Time.unscaledTime;
            int n = CharacterTracker.CopyAll(out Character[] chars);
            _drop.Clear();
            foreach (Character c in _since.Keys)
                if (c == null)
                    _drop.Add(c);
            for (int i = 0; i < _drop.Count; i++)
                _since.Remove(_drop[i]);
            for (int i = 0; i < n; i++)
            {
                Character c = chars[i];
                if (c == null || !c.alive || c.name != WolfName || CharacterTracker.TryGetStableId(c, out _))
                    continue;
                Location loc = c.GetComponentInParent<Location>();
                if (loc == null || !loc.playerBase)
                    continue;
                if (!_since.TryGetValue(c, out float t))
                {
                    _since[c] = now;
                    continue;
                }
                if (now - t < GhostSeconds)
                    continue;
                _since.Remove(c);
                if (loc.wolf == c.gameObject)
                    loc.wolf = null;
                ModRuntime.LegacyInfo($"[Wolf] dropped the save's hideout Wolfman at {c.transform.position} in '{loc.name}': the host has none there");
                Object.Destroy(c.gameObject);
            }
        }
    }
}
