using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using Random = UnityEngine.Random;

namespace DWMPHorde.Sync
{
    internal static partial class CosmeticRolls
    {
        // ---- creatures (entity descriptor) --------------------------------------------------------

        /// <summary>Host: the key a creature's randomizers rolled with (0: it has none).</summary>
        internal static int KeyOfCharacter(Character c)
        {
            if (c == null)
                return 0;
            GameObject go = c.gameObject;
            return _anchors.TryGetValue(go.GetInstanceID(), out AnchorRec rec) && rec.Go == go ? rec.Key : 0;
        }

        /// <summary>
        /// Client: give this machine's copy of a host creature the host's look. A copy from the save
        /// rolled where the save left the creature, a phantom where this machine first saw it;
        /// the host's copy rolled where it was born.
        /// </summary>
        internal static void ApplyCharacterKey(Character c, int hostKey)
        {
            if (c == null || hostKey == 0)
                return;
            GameObject go = c.gameObject;
            if (_anchors.TryGetValue(go.GetInstanceID(), out AnchorRec rec) && rec.Go == go && rec.Key != hostKey)
                Reroll(rec, hostKey);
        }

        // ---- props (late-join bulk) ---------------------------------------------------------------

        /// <summary>Entries per message; a mover entry is about 20 bytes plus its name.</summary>
        private const int MoversPerMessage = 200;

        /// <summary>How far a joiner's copy of a prop may stand from the host's (physics settle).</summary>
        private const float MoverMatchRadius = 4f;

        /// <summary>
        /// Host, late-join bulk: props that moved since they rolled. The joiner loaded them from the
        /// save where they stand now and rolled there; it rolls them again on the host's key.
        /// </summary>
        internal static void SendMovedTo(LanNetworkManager net, int playerId)
        {
            if (net == null || playerId <= 0)
                return;
            PruneAnchors();
            var names = new List<string>();
            var xs = new List<float>();
            var zs = new List<float>();
            var keys = new List<int>();
            foreach (AnchorRec rec in _anchors.Values)
            {
                if (rec.IsCharacter || rec.Go == null)
                    continue;
                Transform t = rec.Go.transform;
                if (PlaceKey(t, withRootPlacement: true) == rec.Key)
                    continue;
                Vector3 p = t.position;
                names.Add(CleanName(rec.Go.name));
                xs.Add(p.x);
                zs.Add(p.z);
                keys.Add(rec.Key);
            }
            for (int start = 0; start < names.Count; start += MoversPerMessage)
            {
                int n = Math.Min(MoversPerMessage, names.Count - start);
                var msg = new CosmeticStateMessage
                {
                    Kind = CosmeticStateMessage.KindMovers,
                    MoverNames = names.GetRange(start, n).ToArray(),
                    MoverX = xs.GetRange(start, n).ToArray(),
                    MoverZ = zs.GetRange(start, n).ToArray(),
                    MoverKeys = keys.GetRange(start, n).ToArray()
                };
                net.SendToPlayer(playerId, NetMessageType.CosmeticState, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
            ModLog.Event(LogCat.World, "[Cosmetic] late-join → p" + playerId + ": " + names.Count + " moved prop(s)");
        }

        /// <summary>Client: the host's keys for props that moved since they rolled.</summary>
        internal static void HandleState(LanNetworkManager net, CosmeticStateMessage msg)
        {
            if (net == null || net.Role != NetworkRole.Client)
                return;
            if (msg.Kind == CosmeticStateMessage.KindDecks)
            {
                DescriptionDeck.ApplyBulk(msg);
                return;
            }
            if (msg.Kind != CosmeticStateMessage.KindMovers || msg.MoverNames == null)
                return;
            int applied = 0, missing = 0;
            for (int i = 0; i < msg.MoverNames.Length; i++)
            {
                AnchorRec best = FindMover(msg.MoverNames[i], msg.MoverX[i], msg.MoverZ[i]);
                if (best == null)
                {
                    missing++;
                    continue;
                }
                Reroll(best, msg.MoverKeys[i]);
                applied++;
            }
            ModLog.Event(LogCat.World, "[Cosmetic] host prop keys: " + applied + " applied, " + missing + " not found");
        }

        private static AnchorRec FindMover(string name, float x, float z)
        {
            AnchorRec best = null;
            float bestSq = MoverMatchRadius * MoverMatchRadius;
            foreach (AnchorRec rec in _anchors.Values)
            {
                if (rec.IsCharacter || rec.Go == null)
                    continue;
                Vector3 p = rec.Go.transform.position;
                float dx = p.x - x, dz = p.z - z;
                float dSq = dx * dx + dz * dz;
                if (dSq > bestSq || !string.Equals(CleanName(rec.Go.name), name, StringComparison.Ordinal))
                    continue;
                bestSq = dSq;
                best = rec;
            }
            return best;
        }

        // ---- AnimationPlay ------------------------------------------------------------------------

        internal struct StreamScope
        {
            public bool Active;
            public Random.State Outer;
        }

        /// <summary>
        /// <c>AnimationPlay.init</c> runs on the object's seed (<see cref="SeedFor"/>): its clip pick,
        /// start frame and first twitch frame. Its timed behaviour is <see cref="AnimSchedule"/>'s.
        /// The live seed leaves out where a location root stands: <c>init</c> runs from
        /// <c>Start</c>, which can come before or after a live location is placed.
        /// </summary>
        internal static StreamScope EnterAnimInit(AnimationPlay ap)
        {
            var scope = new StreamScope { Active = true, Outer = Random.state };
            Random.InitState(SeedFor(ap.transform, SaltAnim,
                CosmeticKey.Salt(PlaceKey(ap.transform, withRootPlacement: false), "AnimationPlay")));
            return scope;
        }

        /// <summary>
        /// tk2d's random-frame clips pick their first frame from the global stream when they start;
        /// seeded per object and clip instead.
        /// </summary>
        internal static StreamScope EnterTk2dRandom(tk2dSpriteAnimator animator, tk2dSpriteAnimationClip clip)
        {
            if (animator == null || clip == null)
                return default;
            var scope = new StreamScope { Active = true, Outer = Random.state };
            int salt = CosmeticKey.Salt(SaltTk2dRandom, clip.name ?? "");
            Random.InitState(SeedFor(animator.transform, salt,
                CosmeticKey.Salt(PlaceKey(animator.transform, withRootPlacement: false), clip.name ?? "")));
            return scope;
        }

        internal static void ExitScope(StreamScope scope)
        {
            if (scope.Active)
                Random.state = scope.Outer;
        }

        // ---- VineSpawner --------------------------------------------------------------------------

        /// <summary>
        /// <c>VineSpawner.Start</c> rolls each vine's rotation (<c>Core.getRandomHalfRotation</c>);
        /// seeded by the spawner's place, every machine turns the vines the same way. Vines spawn
        /// live only (a load restores them with their saved rotation).
        /// </summary>
        internal static StreamScope EnterVineSpawn(VineSpawner vs)
        {
            if (vs == null || vs.spawned)
                return default;
            var scope = new StreamScope { Active = true, Outer = Random.state };
            Random.InitState(CosmeticKey.Salt(PlaceKey(vs.transform, withRootPlacement: false), "VineSpawner"));
            return scope;
        }

        internal static void ExitVineSpawn(StreamScope scope) => ExitScope(scope);
    }
}
