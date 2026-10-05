using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Another player's E-drag, played back on this peer. The dragger's machine owns the body
    /// while it drags (vanilla hinges it to that player); every observer, the host included,
    /// holds its copy kinematic and shows the dragger's poses on a timeline a short delay behind
    /// the dragger's clock (<see cref="DragSyncMessage.SendTime"/>): position linear between
    /// samples, rotation slerped (shortest way round, no Euler wrap), the delay riding out the
    /// stream's measured send interval and arrival jitter like the creature timeline. A STOP
    /// carries the pose the drag ended on; the copy plays out to it, then turns physical again.
    /// Before this, each packet teleported the body on arrival (steps at the packet rate, jitter
    /// with every late packet) and the host's copy stayed dynamic, so collisions turned and
    /// shoved it between packets.
    /// </summary>
    internal static class RemoteDragTimeline
    {
        private struct Sample
        {
            public float T;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        private sealed class Track
        {
            public Item Item;
            public Transform Tf;
            public Rigidbody Rb;
            public string Name;
            public int Sender;
            public readonly Sample[] S = new Sample[Capacity];
            public int Count;
            public float Interval = DefaultInterval;
            /// <summary>Render delay in use (-1: not started).</summary>
            public float Delay = -1f;
            public bool Ending;
            public float EndedLocal;
            public float LastSampleLocal;
            public float StartedLocal;
            public int Samples;
            public int Late;

            public void Push(Sample x)
            {
                if (Count == Capacity)
                {
                    for (int i = 1; i < Capacity; i++)
                        S[i - 1] = S[i];
                    Count--;
                }
                S[Count++] = x;
            }
        }

        private sealed class SenderClock
        {
            public readonly HostClockEstimator Clock = new HostClockEstimator();
            public readonly ArrivalJitter Jitter = new ArrivalJitter();
        }

        private const int Capacity = 16;
        /// <summary>The dragger sends with PlayerState (~30 Hz) until an interval is measured.</summary>
        private const float DefaultInterval = LanNetworkManager.SendInterval;
        private const float IntervalGain = 0.15f;
        /// <summary>Sample gaps longer than this (a pause, a lost burst) do not feed the interval estimate.</summary>
        private const float MaxIntervalSample = 0.25f;
        private const float DelaySafety = 0.01f;
        private const float DelayMin = 0.04f;
        private const float DelayMax = 0.3f;
        /// <summary>Delay change per second: growing (late packets) quickly, shrinking slowly (playback runs 5% fast).</summary>
        private const float DelayGrowPerSec = 0.25f;
        private const float DelayShrinkPerSec = 0.05f;
        /// <summary>A grabbed body sends every tick, standing still too: this long silent, its dragger is gone.</summary>
        private const float SilentReleaseSec = 2f;
        /// <summary>A STOP's final pose is reached within the delay; this is the bound if the clock jumped.</summary>
        private const float EndPlayoutMaxSec = 1f;
        private const float StatsEverySec = 2f;

        private static readonly Dictionary<int, Track> _tracks = new Dictionary<int, Track>(4);
        private static readonly Dictionary<int, SenderClock> _clocks = new Dictionary<int, SenderClock>(4);
        private static readonly List<int> _keys = new List<int>(4); // process-scoped: scratch buffer, cleared before each use
        private static readonly List<int> _done = new List<int>(4); // process-scoped: scratch buffer, cleared before each use
        private static double _epoch = -1; // process-scoped: clock origin, only differences are used
        private static float _nextStatsAt;

        private static readonly AccessTools.FieldRef<Item, Vector3> ShadowPosition =
            AccessTools.FieldRefAccess<Item, Vector3>("shadowPosition");

        /// <summary>This peer's drag clock: seconds of real time since its first use.</summary>
        internal static float LocalNow()
        {
            double now = Time.unscaledTimeAsDouble;
            if (_epoch < 0)
                _epoch = now;
            return (float)(now - _epoch);
        }

        /// <summary>
        /// Sender stamp for the dragged body's current pose. A body without rigidbody
        /// interpolation shows its last physics step, which is up to one fixed step older than
        /// this frame; stamping it at the frame would put that step's jitter into every sample.
        /// </summary>
        internal static float StampFor(Rigidbody rb)
        {
            float now = LocalNow();
            if (rb != null && rb.interpolation == RigidbodyInterpolation.None && Time.timeScale > 0f)
            {
                float age = Time.time - Time.fixedTime;
                if (age > 0f && age < Time.fixedDeltaTime * 2f)
                    now -= age / Time.timeScale;
            }
            return now;
        }

        /// <summary>
        /// The copy already playing <paramref name="sender"/>'s drag of <paramref name="name"/>.
        /// It is shown a delay behind the newest sample, so a search near the sample's position
        /// could miss it (or take a same-named neighbour) while the drag is fast.
        /// </summary>
        internal static Item TrackedItem(string name, int sender)
        {
            if (string.IsNullOrEmpty(name) || _tracks.Count == 0)
                return null;
            Track tr = FindTrack(name, sender);
            return tr != null && tr.Item != null ? tr.Item : null;
        }

        /// <summary>A grab sample from <paramref name="msg"/> for <paramref name="item"/> (the observer copy).</summary>
        internal static void AddSample(Item item, DragSyncMessage msg)
        {
            if (item == null)
                return;
            float local = LocalNow();
            SenderClock clock = ClockFor(msg.ClaimedByPlayerId);
            bool hadClock = clock.Clock.HasEstimate;
            clock.Clock.AddSample(msg.SendTime, local);
            float lateness = clock.Clock.ToHost(local) - msg.SendTime;
            if (hadClock)
                clock.Jitter.Add(lateness);

            int id = item.GetInstanceID();
            Track tr;
            if (!_tracks.TryGetValue(id, out tr) || tr.Item != item || tr.Sender != msg.ClaimedByPlayerId)
            {
                tr = new Track
                {
                    Item = item,
                    Tf = item.transform,
                    Rb = item.GetComponent<Rigidbody>(),
                    Name = item.gameObject.name,
                    Sender = msg.ClaimedByPlayerId,
                    StartedLocal = local
                };
                // The pose it rests in here, one interval before the first sample: the body glides
                // into the drag instead of jumping to it.
                tr.Push(new Sample
                {
                    T = msg.SendTime - DefaultInterval,
                    Pos = tr.Tf.position,
                    Rot = tr.Tf.rotation
                });
                _tracks[id] = tr;
                ModRuntime.LegacyInfo($"[DragTimeline] start {tr.Name} sender=p{tr.Sender} at {tr.Tf.position}");
            }

            tr.Ending = false;
            tr.LastSampleLocal = local;
            Sample last = tr.S[tr.Count - 1];
            if (msg.SendTime <= last.T)
            {
                // Late unreliable sample after a newer reliable one (or a duplicate).
                tr.Late++;
                HoldKinematic(tr);
                return;
            }
            float gap = msg.SendTime - last.T;
            if (tr.Samples > 0 && gap < MaxIntervalSample)
                tr.Interval += (gap - tr.Interval) * IntervalGain;
            tr.Push(new Sample
            {
                T = msg.SendTime,
                Pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ),
                Rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ)
            });
            tr.Samples++;
            HoldKinematic(tr);
        }

        /// <summary>
        /// The dragger's STOP. With its final pose the matching copy plays out to that pose and is
        /// released when the timeline reaches it (true). Without a pose (host releasing a gone
        /// dragger) or with no copy playing, false: the caller releases at once.
        /// </summary>
        internal static bool End(DragSyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.ObjectName))
                return false;
            Track tr = FindTrack(msg.ObjectName, msg.ClaimedByPlayerId);
            if (tr == null)
                return false;
            if (!msg.HasPose)
            {
                int key = KeyOf(tr);
                Finish(tr, localHold: false);
                _tracks.Remove(key);
                return false;
            }
            float local = LocalNow();
            SenderClock clock = ClockFor(tr.Sender);
            clock.Clock.AddSample(msg.SendTime, local);
            Sample last = tr.S[tr.Count - 1];
            if (msg.SendTime > last.T)
            {
                tr.Push(new Sample
                {
                    T = msg.SendTime,
                    Pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ),
                    Rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ)
                });
            }
            tr.Ending = true;
            tr.EndedLocal = local;
            return true;
        }

        /// <summary>Stop playing <paramref name="item"/> back: the local player took hold of it.</summary>
        internal static void Drop(Item item)
        {
            if (item == null)
                return;
            int id = item.GetInstanceID();
            if (_tracks.TryGetValue(id, out Track tr))
            {
                Finish(tr, localHold: true);
                _tracks.Remove(id);
            }
        }

        /// <summary>Release every copy played back for <paramref name="name"/> now (dragger disconnected).</summary>
        internal static void DropByName(string name)
        {
            if (string.IsNullOrEmpty(name) || _tracks.Count == 0)
                return;
            _done.Clear();
            foreach (var kv in _tracks)
            {
                if (string.Equals(kv.Value.Name, name, System.StringComparison.OrdinalIgnoreCase))
                    _done.Add(kv.Key);
            }
            for (int i = 0; i < _done.Count; i++)
            {
                Finish(_tracks[_done[i]], localHold: false);
                _tracks.Remove(_done[i]);
            }
            _done.Clear();
        }

        /// <summary>Per frame (LateUpdate, after vanilla Update): pose every played-back body.</summary>
        internal static void Tick()
        {
            if (_tracks.Count == 0)
                return;
            float local = LocalNow();
            float dt = Time.unscaledDeltaTime;
            bool stats = local >= _nextStatsAt;
            if (stats)
                _nextStatsAt = local + StatsEverySec;

            _keys.Clear();
            _keys.AddRange(_tracks.Keys);
            _done.Clear();
            for (int k = 0; k < _keys.Count; k++)
            {
                int key = _keys[k];
                Track tr = _tracks[key];
                if (tr.Item == null || tr.Tf == null)
                {
                    // Destroyed (a spawned stand-in cleaned up on STOP, a chunk unloading).
                    _done.Add(key);
                    continue;
                }
                Player lp = Player.Instance;
                if (tr.Item.beingDragged || (lp != null && lp.itemBeingDragged == tr.Item))
                {
                    // The local player took hold of it: its hinge drives the body now.
                    Finish(tr, localHold: true);
                    _done.Add(key);
                    continue;
                }

                SenderClock clock = ClockFor(tr.Sender);
                float target = tr.Interval + clock.Jitter.Margin + DelaySafety;
                if (target < DelayMin) target = DelayMin;
                else if (target > DelayMax) target = DelayMax;
                if (tr.Delay < 0f)
                    tr.Delay = target;
                else if (target > tr.Delay)
                    tr.Delay = Mathf.Min(target, tr.Delay + DelayGrowPerSec * dt);
                else
                    tr.Delay = Mathf.Max(target, tr.Delay - DelayShrinkPerSec * dt);

                float t = clock.Clock.ToHost(local) - tr.Delay;
                bool pastNewest = SampleAt(tr, t, out Vector3 pos, out Quaternion rot);
                Apply(tr, pos, rot);

                if (stats)
                {
                    ModRuntime.LegacyInfo(
                        $"[DragTimeline] {tr.Name} p{tr.Sender} samples={tr.Samples} late={tr.Late}"
                        + $" interval={(tr.Interval * 1000f):F0}ms delay={(tr.Delay * 1000f):F0}ms"
                        + $" margin={(clock.Jitter.Margin * 1000f):F0}ms behind={((clock.Clock.ToHost(local) - tr.S[tr.Count - 1].T) * 1000f):F0}ms"
                        + $" yaw={rot.eulerAngles.y:F1}{(tr.Ending ? " ending" : "")}");
                }

                if (tr.Ending)
                {
                    if (pastNewest || local - tr.EndedLocal > EndPlayoutMaxSec)
                    {
                        if (!pastNewest)
                        {
                            Sample fin = tr.S[tr.Count - 1];
                            Apply(tr, fin.Pos, fin.Rot);
                        }
                        Finish(tr, localHold: false);
                        _done.Add(key);
                    }
                }
                else if (local - tr.LastSampleLocal > SilentReleaseSec)
                {
                    ModRuntime.LegacyInfo($"[DragTimeline] {tr.Name} p{tr.Sender} silent {SilentReleaseSec:F0}s — released");
                    Finish(tr, localHold: false);
                    _done.Add(key);
                }
            }
            for (int i = 0; i < _done.Count; i++)
                _tracks.Remove(_done[i]);
            _done.Clear();
        }

        internal static void Reset()
        {
            foreach (var kv in _tracks)
                Finish(kv.Value, localHold: false);
            _tracks.Clear();
            _clocks.Clear();
            _keys.Clear();
            _done.Clear();
            _nextStatsAt = 0f;
        }

        /// <summary>Pose at sender time <paramref name="t"/>; true once <paramref name="t"/> is at or past the newest sample (held there).</summary>
        private static bool SampleAt(Track tr, float t, out Vector3 pos, out Quaternion rot)
        {
            Sample first = tr.S[0];
            Sample newest = tr.S[tr.Count - 1];
            if (t >= newest.T)
            {
                pos = newest.Pos;
                rot = newest.Rot;
                return true;
            }
            if (t <= first.T)
            {
                pos = first.Pos;
                rot = first.Rot;
                return false;
            }
            for (int i = tr.Count - 1; i > 0; i--)
            {
                Sample a = tr.S[i - 1];
                if (t < a.T)
                    continue;
                Sample b = tr.S[i];
                float span = b.T - a.T;
                float u = span > 0f ? (t - a.T) / span : 1f;
                pos = Vector3.Lerp(a.Pos, b.Pos, u);
                rot = Quaternion.Slerp(a.Rot, b.Rot, u);
                return false;
            }
            pos = first.Pos;
            rot = first.Rot;
            return false;
        }

        private static void Apply(Track tr, Vector3 pos, Quaternion rot)
        {
            // The transform, not Rigidbody.position: a kinematic body's rb pose reaches the
            // transform only at the next physics step, so the drawn body would move at the
            // physics rate, not every frame. Physics picks the transform up before it steps.
            tr.Tf.SetPositionAndRotation(pos, rot);
            // Vanilla Item.Update places the shadow from the body earlier in this frame.
            GameObject shadow = tr.Item.shadow;
            if (shadow != null)
                shadow.transform.position = pos + ShadowPosition(tr.Item);
        }

        private static void HoldKinematic(Track tr)
        {
            Rigidbody rb = tr.Rb;
            if (rb == null)
                return;
            if (!rb.isKinematic)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }
        }

        /// <summary>
        /// Playback over: the body is physical again — free, or driven by the local player's
        /// hinge when <paramref name="localHold"/> (the local player grabbed it) — and
        /// PhysicsState may track it again.
        /// </summary>
        private static void Finish(Track tr, bool localHold)
        {
            var net = ModRuntime.Network;
            if (tr.Item != null)
            {
                int id = tr.Item.GetInstanceID();
                if (tr.Rb != null && tr.Rb.isKinematic)
                    tr.Rb.isKinematic = false;
                if (net != null && net.PlayerInteractHandlers != null)
                {
                    var h = net.PlayerInteractHandlers;
                    h.RemoteDragItemIds.Remove(id);
                    // The name set blocks a local grab of any same-named body while a peer drags
                    // one; it goes once neither a claim nor another played-back copy holds it.
                    if (!h.DragClaims.ContainsKey(tr.Name) && !OtherTrackNamed(tr))
                        h.RemoteDragItemNames.Remove(tr.Name);
                }
            }
            ModRuntime.LegacyInfo(
                $"[DragTimeline] end {tr.Name} p{tr.Sender} samples={tr.Samples} late={tr.Late}"
                + $" {(LocalNow() - tr.StartedLocal):F1}s{(localHold ? " (local hold)" : "")}");
        }

        private static bool OtherTrackNamed(Track tr)
        {
            foreach (var kv in _tracks)
            {
                if (kv.Value != tr && string.Equals(kv.Value.Name, tr.Name, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static Track FindTrack(string name, int sender)
        {
            foreach (var kv in _tracks)
            {
                Track tr = kv.Value;
                if (tr.Sender == sender && string.Equals(tr.Name, name, System.StringComparison.OrdinalIgnoreCase))
                    return tr;
            }
            return null;
        }

        private static int KeyOf(Track tr)
        {
            foreach (var kv in _tracks)
            {
                if (kv.Value == tr)
                    return kv.Key;
            }
            return 0;
        }

        private static SenderClock ClockFor(int sender)
        {
            if (!_clocks.TryGetValue(sender, out SenderClock c))
            {
                c = new SenderClock();
                _clocks[sender] = c;
            }
            return c;
        }
    }
}
