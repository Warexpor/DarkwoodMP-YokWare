using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Players
{
    /// <summary>
    /// A player who dies at night or in a dream is out until morning (or the dream's end),
    /// spectating; their own body is moved under the player they watch and their stand-in on
    /// the other machines was parked under the ground, so the body simply vanished. They now
    /// leave a body where they fell, on every machine including their own: a copy of the player
    /// lying on the last frame of the death clip, with no collider (players and creatures walk
    /// over it). After a night death that body is also the death bag: vanilla still drops the bag
    /// on the spot, and while the body lies there the bag's sprite is hidden under it, so
    /// pointing at the body loots the bag the usual way. A dream death drops no bag (the player
    /// keeps their kit), so that body is only a body. At release (morning, dream end) the body
    /// goes and whatever is left in the bag shows as a bag again. A day death is untouched: the
    /// bag and vanilla's respawn at home, no body. A one-life death by day, which is also held
    /// until morning, counts as a day death here.
    /// </summary>
    internal static class PlayerCorpses
    {
        private sealed class Corpse
        {
            public GameObject Body;
            public bool Dream;
            public Vector3 Pos;
            public DeathDrop Bag;
            public float BagSearchUntil;
            public float NextBagSearch;
            public readonly List<Renderer> HiddenBag = new List<Renderer>();
            public readonly List<Renderer> HiddenProxy = new List<Renderer>();
        }

        private static readonly Dictionary<int, Corpse> _corpses = new Dictionary<int, Corpse>(); // process-scoped: emptied on session stop and scene change
        private static readonly List<int> _scratch = new List<int>(); // process-scoped: scratch
        private static float _nextTick; // process-scoped: throttle

        /// <summary>A bag is dropped 3.5 s into vanilla's death sequence (later on a peer that gets it over the net).</summary>
        private const float BagSearchSeconds = 30f;
        private const float BagRadius = 15f;
        private const float TickInterval = 0.25f;

        internal static void Tick(LanNetworkManager net)
        {
            if (Time.unscaledTime < _nextTick)
                return;
            _nextTick = Time.unscaledTime + TickInterval;

            if (net == null || !net.IsConnected)
            {
                ClearAll();
                return;
            }
            if (!LanNetworkManager.CanSpawnRemoteProxies() || Player.Instance == null)
                return;

            int local = net.LocalPlayerId;

            // Release first: whoever is no longer down (or whose body went with the scene).
            _scratch.Clear();
            foreach (KeyValuePair<int, Corpse> kv in _corpses)
                if (kv.Value.Body == null || !StillDown(kv.Key, local, kv.Value.Dream))
                    _scratch.Add(kv.Key);
            for (int i = 0; i < _scratch.Count; i++)
                Release(_scratch[i]);

            // Bodies for whoever is down and has none yet.
            if (DeathStateTracker.LocalNightDeath && DeathStateTracker.LocalDiedInNightWindow)
                Ensure(local, DeathStateTracker.LocalDeathPosition, dream: false, null);
            else if (FinalDreamsceneManager.IsLocalDead)
                Ensure(local, FinalDreamsceneManager.LocalDeathPosition, dream: true, null);

            _scratch.Clear();
            _scratch.AddRange(DeathStateTracker.RemoteNightDeadIds);
            for (int i = 0; i < _scratch.Count; i++)
            {
                int id = _scratch[i];
                if (DeathStateTracker.RemoteDiedInNightWindow(id)
                    && DeathStateTracker.TryGetRemoteDeathPosition(id, out Vector3 pos))
                    Ensure(id, pos, dream: false, net.GetProxy(id));
            }
            _scratch.Clear();
            _scratch.AddRange(FinalDreamsceneManager.RemoteDeadIds);
            for (int i = 0; i < _scratch.Count; i++)
            {
                int id = _scratch[i];
                // The dead dreamer stops sending its position, so its stand-in stays where it fell.
                RemotePlayerProxy proxy = net.GetProxy(id);
                if (proxy != null && FinalDreamsceneManager.IsRemoteDeadInDream(id))
                    Ensure(id, proxy.transform.position, dream: true, proxy);
            }

            foreach (KeyValuePair<int, Corpse> kv in _corpses)
            {
                Corpse c = kv.Value;
                if (kv.Key != local)
                    HideRenderers(net.GetProxy(kv.Key) != null ? net.GetProxy(kv.Key).gameObject : null, c.HiddenProxy);
                if (c.Dream)
                    continue;
                if (c.Bag == null && Time.unscaledTime < c.BagSearchUntil && Time.unscaledTime >= c.NextBagSearch)
                {
                    c.NextBagSearch = Time.unscaledTime + 1f;
                    c.Bag = FindBag(c.Pos);
                }
                if (c.Bag != null)
                    HideRenderers(c.Bag.gameObject, c.HiddenBag);
            }
        }

        private static bool StillDown(int id, int local, bool dream)
        {
            if (dream)
                return id == local ? FinalDreamsceneManager.IsLocalDead : FinalDreamsceneManager.IsRemoteDeadInDream(id);
            return id == local
                ? DeathStateTracker.LocalNightDeath
                : DeathStateTracker.IsRemoteNightDead(id);
        }

        private static void Ensure(int id, Vector3 pos, bool dream, RemotePlayerProxy proxy)
        {
            if (_corpses.ContainsKey(id) || pos == Vector3.zero)
                return;
            Player source = Player.Instance;
            GameObject body = PlayerProxyBuilder.CreatePlayerClone(
                source, "RemotePlayerCorpse_p" + id, Vector3.zero, PlayerCloneKind.Remote, null);
            if (body == null)
                return;
            foreach (Collider col in body.GetComponentsInChildren<Collider>(true))
                col.enabled = false;
            body.transform.position = pos;
            // Face the way the player faced when it fell (the local body has not been moved yet:
            // the spectator takes it away a few seconds into the death sequence).
            Transform facing = proxy != null ? proxy.transform : source.transform;
            body.transform.rotation = facing.rotation;
            // The clone woke this frame: its animator's Start (still to come) would put the
            // default Idle clip over the death clip played here and loop it, a body standing up.
            foreach (tk2dSpriteAnimator anim in body.GetComponentsInChildren<tk2dSpriteAnimator>(true))
                anim.playAutomatically = false;
            body.GetComponent<SecondPlayerAnimController>()?.PlayDeathClip("Death1");

            _corpses[id] = new Corpse
            {
                Body = body,
                Dream = dream,
                Pos = pos,
                BagSearchUntil = Time.unscaledTime + BagSearchSeconds,
            };
            ModRuntime.LegacyInfo($"[Corpse] body of p{id} left at {pos} ({(dream ? "dream" : "night")})");
        }

        private static void Release(int id)
        {
            if (!_corpses.TryGetValue(id, out Corpse c))
                return;
            _corpses.Remove(id);
            if (c.Body != null)
                Object.Destroy(c.Body);
            ShowRenderers(c.HiddenBag);
            ShowRenderers(c.HiddenProxy);
            ModRuntime.LegacyInfo($"[Corpse] body of p{id} removed" + (c.Bag != null ? " (its bag shows again)" : ""));
        }

        /// <summary>One line per body (pilot <c>corpses</c>).</summary>
        internal static List<string> Describe()
        {
            var lines = new List<string>();
            foreach (KeyValuePair<int, Corpse> kv in _corpses)
            {
                Corpse c = kv.Value;
                Vector3 p = c.Body != null ? c.Body.transform.position : c.Pos;
                lines.Add("p" + kv.Key + " " + (c.Dream ? "dream" : "night") + " body@" + Mathf.RoundToInt(p.x) + "," + Mathf.RoundToInt(p.z)
                    + " shown=" + (c.Body != null && c.Body.activeInHierarchy)
                    + " solidColliders=" + SolidColliders(c.Body)
                    + " bag=" + (c.Bag != null ? "hidden(" + c.HiddenBag.Count + " renderers)" : "none")
                    + " proxyHidden=" + c.HiddenProxy.Count
                    + " " + Look(c.Body));
            }
            if (lines.Count == 0)
                lines.Add("no bodies");
            return lines;
        }

        private static string Look(GameObject body)
        {
            if (body == null)
                return "look=-";
            int on = 0, all = 0;
            foreach (Renderer r in body.GetComponentsInChildren<Renderer>(true))
            {
                all++;
                if (r.enabled && r.gameObject.activeInHierarchy)
                    on++;
            }
            tk2dSpriteAnimator torso = body.GetComponent<tk2dSpriteAnimator>();
            tk2dBaseSprite sprite = body.GetComponent<tk2dBaseSprite>();
            return "renderers=" + on + "/" + all
                + " clip=" + (torso != null && torso.CurrentClip != null ? torso.CurrentClip.name + "@" + torso.CurrentFrame : "-")
                + " sprite=" + (sprite != null && sprite.CurrentSprite != null ? sprite.CurrentSprite.name : "-")
                + " y=" + Mathf.RoundToInt(body.transform.position.y);
        }

        private static int SolidColliders(GameObject body)
        {
            int n = 0;
            if (body != null)
                foreach (Collider col in body.GetComponentsInChildren<Collider>(true))
                    if (col.enabled)
                        n++;
            return n;
        }

        internal static void ClearAll()
        {
            if (_corpses.Count == 0)
                return;
            _scratch.Clear();
            _scratch.AddRange(_corpses.Keys);
            for (int i = 0; i < _scratch.Count; i++)
                Release(_scratch[i]);
        }

        /// <summary>The death bag dropped at the body (the one not already under another body).</summary>
        private static DeathDrop FindBag(Vector3 pos)
        {
            DeathDrop best = null;
            float bestDist = BagRadius;
            DeathDrop[] all = Object.FindObjectsOfType<DeathDrop>();
            for (int i = 0; i < all.Length; i++)
            {
                DeathDrop dd = all[i];
                if (dd == null || Claimed(dd))
                    continue;
                Vector3 p = dd.transform.position;
                float dx = p.x - pos.x, dz = p.z - pos.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = dd;
                }
            }
            return best;
        }

        private static bool Claimed(DeathDrop dd)
        {
            foreach (Corpse c in _corpses.Values)
                if (c.Bag == dd)
                    return true;
            return false;
        }

        /// <summary>Keep every renderer under <paramref name="root"/> off (other code may turn one back on).</summary>
        private static void HideRenderers(GameObject root, List<Renderer> hidden)
        {
            if (root == null)
                return;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.enabled)
                    continue;
                r.enabled = false;
                if (!hidden.Contains(r))
                    hidden.Add(r);
            }
        }

        private static void ShowRenderers(List<Renderer> hidden)
        {
            for (int i = 0; i < hidden.Count; i++)
                if (hidden[i] != null)
                    hidden[i].enabled = true;
            hidden.Clear();
        }
    }
}
