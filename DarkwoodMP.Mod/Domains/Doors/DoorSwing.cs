using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// A swinging door is run by one machine; the others follow it. Every game used to simulate
    /// each swing on its own (the opener's push, a blow's knock-back, a body walking into it) while
    /// the host sent its door's angle about 10 times a second and the others snapped to it. The
    /// snaps arrived late, threw the door back to an older angle while it was being pushed (it
    /// hitched and its creak restarted), and nothing was sent once the host's door slowed down,
    /// so a door hit while swinging came to rest at a different angle on each machine and stayed
    /// so until closed and opened again. A door pushed by a client's body never moved on the host.
    ///
    /// Now the machine whose player (or, on the host, creature or blast) moves an idle door runs
    /// its swing: it sends the door's angle and spin about 20 a second, and once the door has
    /// stood still for a moment, the angle it rests at. The other machines turn their door along
    /// a curve through those angles, 0.1 s behind, by setting its spin each physics step, so it
    /// moves smoothly and its creak is the vanilla one; the rest angle lands exactly. A followed
    /// door is drawn between physics steps. A player walking into a door another machine
    /// runs sends its push there, and a blow on it is sent there as a force. An open echoed from
    /// another machine opens the door without vanilla's push (the opener's swing follows); the
    /// host's knock-back for a client's blow is left to the striker's machine, which already
    /// swung it. Two machines starting the same swing at once: the lower player id keeps it.
    /// </summary>
    internal static class DoorSwing
    {
        private const float SampleInterval = 1f / 20f;
        /// <summary>A door held still while still run (a player leaning on it) is re-sent this often.</summary>
        private const float KeepAliveInterval = 0.5f;
        /// <summary>A turn smaller than this since the last sample is not news (degrees).</summary>
        private const float SampleMinTurn = 0.05f;
        /// <summary>How far behind the sender a follower plays the swing (seconds; two samples and jitter).</summary>
        private const float InterpDelay = 0.1f;
        /// <summary>A followed door off its curve (a late sample) closes the gap over this long (seconds).</summary>
        private const float CatchUpTime = 0.05f;
        /// <summary>Samples further apart than this: the door stood still in between (seconds).</summary>
        private const float StoodStillGap = SampleInterval * 1.5f;
        /// <summary>An idle door turning faster than this is moving (degrees a second).</summary>
        private const float MovingSpeed = 6f;
        /// <summary>Slower than this for <see cref="SettleTime"/>: the swing is over.</summary>
        private const float SettleSpeed = 3f;
        private const float SettleTime = 0.25f;
        /// <summary>A followed swing with no word for this long has lost its machine.</summary>
        private const float FollowLost = 1.5f;
        /// <summary>A follower that has heard nothing for this long yields to another machine's swing.</summary>
        private const float FollowStale = 0.3f;
        /// <summary>Within this of the rest angle the door is put on it (degrees).</summary>
        private const float RestSnap = 0.5f;
        private const float MaxDriveSpeed = 720f;
        private const float PushSendInterval = 1f / 15f;
        /// <summary>A push not refreshed within this has stopped.</summary>
        private const float PushHold = 0.3f;
        /// <summary>A contact not refreshed within this has ended.</summary>
        private const float TouchHold = 0.15f;
        /// <summary>Walking into it: input this close to the direction of the contact (cosine).</summary>
        private const float IntoDoorCos = 0.3f;
        private const float MinIntentSq = 0.01f;
        private const int MaxSamples = 32;
        /// <summary>Vanilla's knock-back runs two frames after the blow: the window the host leaves it to the striker.</summary>
        private const int RemoteStrikeFrames = 8;

        private enum Mode { Own, Follow, Hold }

        private struct Sample
        {
            public float T;
            public float Yaw;
            /// <summary>The sender's spin at the sample (degrees a second).</summary>
            public float Speed;
            /// <summary>The curve's spin through the sample, fixed once the playback reaches it.</summary>
            public float Slope;
            public bool HasSlope;
        }

        private sealed class Push
        {
            public Vector3 Force;
            public Vector3 Local;
            public float Until;
        }

        private struct Touch
        {
            public Door Door;
            public Vector3 Point;
            public float At;
        }

        private struct LastRest
        {
            public int Owner;
            public float T;
        }

        private sealed class Drive
        {
            public Door Door;
            public Rigidbody Rb;
            public Mode Mode;
            /// <summary>The machine running the swing (Follow), or this one (Own).</summary>
            public int OwnerId;
            // Own
            public float NextSendAt;
            public float LastSentAt = -10f;
            public float LastSentYaw;
            public float LastYaw;
            public float QuietSince = -1f;
            public readonly Dictionary<int, Push> Pushes = new Dictionary<int, Push>(2);
            // Follow / Hold
            public readonly List<Sample> Samples = new List<Sample>(MaxSamples);
            public float Offset;
            public bool HasOffset;
            public bool HasRest;
            public float RestYaw;
            public float RestT;
            public float LastRecvAt;
            public float NextPushAt;
            /// <summary>Drawn between physics steps while followed; what to put back after.</summary>
            public bool Smoothed;
            public RigidbodyInterpolation WasInterpolation;
            public int Got;
            public int Late;
            public float LateMax;
        }

        private static readonly Dictionary<int, Drive> _drives = new Dictionary<int, Drive>(8);
        /// <summary>Door id: the angle every machine agrees it rests at (the last swing's end).</summary>
        private static readonly Dictionary<int, float> _restYaw = new Dictionary<int, float>(32);
        /// <summary>Door id: the last finished swing's machine and end time; its late samples are dropped.</summary>
        private static readonly Dictionary<int, LastRest> _lastRest = new Dictionary<int, LastRest>(32);
        /// <summary>Door id: until which frame the host's knock-back on it belongs to a client's blow.</summary>
        private static readonly Dictionary<int, int> _remoteStrikeUntil = new Dictionary<int, int>(4);
        /// <summary>Door id: the local player's contact with it.</summary>
        private static readonly Dictionary<int, Touch> _touch = new Dictionary<int, Touch>(4);
        private static readonly Dictionary<int, Rigidbody> _bodies = new Dictionary<int, Rigidbody>(64);
        private static readonly List<int> _done = new List<int>(8); // process-scoped: scratch buffer, cleared before each use
        private static readonly List<int> _pushDone = new List<int>(2); // process-scoped: scratch buffer, cleared before each use
        /// <summary>The door whose vanilla open is running (its push goes through <see cref="OpenForce"/>).</summary>
        private static Door _opening; // process-scoped: call-scoped, restored by the Door.open Finalizer
        /// <summary>Something was sent since the last physics step: it leaves now, not on the transport's next wake.</summary>
        private static bool _sent;
        private static int _echoDepth; // process-scoped: call-scoped, unwound in finally

        // ------------------------------------------------------------------ hooks

        /// <summary>Runs <paramref name="open"/> as the echo of another machine's open: no vanilla push.</summary>
        internal static void RunEchoOpen(System.Action open)
        {
            _echoDepth++;
            try { open(); }
            finally { _echoDepth--; }
        }

        internal static Door BeginOpen(Door door)
        {
            Door prev = _opening;
            _opening = door;
            return prev;
        }

        internal static void EndOpen(Door prev) => _opening = prev;

        /// <summary>Vanilla <c>Door.open</c>'s push (its <c>Rigidbody.AddForce</c> calls).</summary>
        public static void OpenForce(Rigidbody rb, Vector3 force)
        {
            if (_echoDepth > 0)
                return;
            if (_opening == null)
            {
                rb.AddForce(force);
                return;
            }
            LocalForce(_opening, rb, force);
        }

        /// <summary>Vanilla <c>Door.getHit</c>'s knock-back on an open door (run two frames after the blow).</summary>
        public static void HitForce(Rigidbody rb, Vector3 force)
        {
            Door door = DoorOf(rb);
            if (door == null)
            {
                rb.AddForce(force);
                return;
            }
            // Not consumed: a shotgun's pellets are several blows, each with its own knock-back.
            if (_remoteStrikeUntil.TryGetValue(door.GetInstanceID(), out int until) && Time.frameCount <= until)
                return;
            LocalForce(door, rb, force);
        }

        /// <summary>Host: a client's blow is about to run here; its knock-back is the striker's machine's.</summary>
        internal static void NoteRemoteStrike(Door door)
        {
            if (door != null)
                _remoteStrikeUntil[door.GetInstanceID()] = Time.frameCount + RemoteStrikeFrames;
        }

        /// <summary>
        /// A force on a door from something on this machine (an open, a blow): applied here and
        /// this machine runs the swing, or sent to the machine already running it.
        /// </summary>
        internal static void LocalForce(Door door, Rigidbody rb, Vector3 force)
        {
            var net = ModRuntime.Network;
            if (door == null || net == null || !net.IsConnected || IsPersonal(door))
            {
                rb.AddForce(force);
                return;
            }
            if (_drives.TryGetValue(door.GetInstanceID(), out Drive d) && d.Mode == Mode.Follow)
            {
                SendImpulse(net, d, force);
                return;
            }
            rb.AddForce(force);
            Claim(door, rb, "force");
        }

        /// <summary>The local player's body against a door (<see cref="LocalDoorContact"/>).</summary>
        internal static void NoteTouch(Door door, Vector3 point)
        {
            if (door == null)
                return;
            _touch[door.GetInstanceID()] = new Touch { Door = door, Point = point, At = Time.fixedUnscaledTime };
        }

        /// <summary>The door's swing is run or followed here: other door state leaves its angle alone.</summary>
        internal static bool IsDriven(Door door)
            => door != null && _drives.ContainsKey(door.GetInstanceID());

        // ------------------------------------------------------------------ physics step

        internal static void FixedTick()
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected)
                return;
            Player p = Player.Instance;
            if (p != null && p.GetComponent<LocalDoorContact>() == null)
                p.gameObject.AddComponent<LocalDoorContact>();
            float now = Time.fixedUnscaledTime;

            ScanIdleDoors(net, now);

            if (_drives.Count == 0)
                return;
            _done.Clear();
            foreach (var kv in _drives)
            {
                Drive d = kv.Value;
                if (!Usable(d.Door, d.Rb))
                {
                    _done.Add(kv.Key);
                    continue;
                }
                bool finished = d.Mode == Mode.Own ? TickOwn(net, d, now) : TickFollow(net, d, now);
                if (finished)
                    _done.Add(kv.Key);
            }
            for (int i = 0; i < _done.Count; i++)
            {
                Smooth(_drives[_done[i]], false);
                _drives.Remove(_done[i]);
            }
            _done.Clear();
            if (_sent)
            {
                // The transport sends every 15 ms; a sample waiting on it reaches the follower unevenly.
                _sent = false;
                net.FlushQueuedSends();
            }
        }

        /// <summary>
        /// An idle door that moves: the local player's body against it, or on the host anything at
        /// all, makes this machine run it. On a client a door moving with no cause here (a creature
        /// copy brushing it) goes back to where every machine has it at rest.
        /// </summary>
        private static void ScanIdleDoors(LanNetworkManager net, float now)
        {
            IList<Door> all = ListTracker<Door>.GetAll();
            bool host = net.Role == NetworkRole.Host;
            for (int i = 0; i < all.Count; i++)
            {
                Door door = all[i];
                if (door == null || _drives.ContainsKey(door.GetInstanceID()))
                    continue;
                Rigidbody rb = Body(door);
                if (!Usable(door, rb) || rb.IsSleeping())
                    continue;
                if (Mathf.Abs(rb.angularVelocity.y) * Mathf.Rad2Deg < MovingSpeed)
                    continue;
                if (IsPersonal(door))
                    continue;
                if (host || Touching(door, now, out _))
                    Claim(door, rb, host && !Touching(door, now, out _) ? "moved" : "body");
                else
                    StartHold(door, rb, now);
            }
        }

        private static void Claim(Door door, Rigidbody rb, string why)
        {
            int id = door.GetInstanceID();
            if (_drives.TryGetValue(id, out Drive d))
            {
                if (d.Mode != Mode.Hold)
                    return;
            }
            else
            {
                d = new Drive { Door = door, Rb = rb };
                _drives[id] = d;
            }
            var net = ModRuntime.Network;
            d.Mode = Mode.Own;
            d.OwnerId = net != null ? net.LocalPlayerId : 0;
            d.LastYaw = Yaw(door);
            d.NextSendAt = 0f;
            d.LastSentAt = -10f;
            d.QuietSince = -1f;
            d.Samples.Clear();
            d.Pushes.Clear();
            ModLog.Event(LogCat.Physics, $"[DoorSwing] running {door.gameObject.name} at {Key(door)} here ({why})");
        }

        private static void StartHold(Door door, Rigidbody rb, float now)
        {
            int id = door.GetInstanceID();
            if (!_restYaw.TryGetValue(id, out float rest))
                return;
            var d = new Drive { Door = door, Rb = rb, Mode = Mode.Hold, RestYaw = rest, HasRest = true, LastRecvAt = now };
            _drives[id] = d;
        }

        private static bool TickOwn(LanNetworkManager net, Drive d, float now)
        {
            float yaw = Yaw(d.Door);
            float dt = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 0.02f;
            float speed = Mathf.DeltaAngle(d.LastYaw, yaw) / dt;
            d.LastYaw = yaw;
            bool pushed = ApplyPushes(d, now);
            if (Mathf.Abs(speed) < SettleSpeed && !pushed)
            {
                if (d.QuietSince < 0f)
                    d.QuietSince = now;
                else if (now - d.QuietSince >= SettleTime)
                {
                    SendSwing(net, d, DoorSwingMessage.ModeRest, yaw, 0f, now);
                    int id = d.Door.GetInstanceID();
                    _restYaw[id] = yaw;
                    _lastRest[id] = new LastRest { Owner = net.LocalPlayerId, T = now };
                    ModLog.Event(LogCat.Physics, $"[DoorSwing] {d.Door.gameObject.name} at {Key(d.Door)} came to rest at {yaw:F1}");
                    return true;
                }
            }
            else
                d.QuietSince = -1f;

            if (now >= d.NextSendAt)
            {
                d.NextSendAt = now + SampleInterval;
                if (Mathf.Abs(Mathf.DeltaAngle(d.LastSentYaw, yaw)) >= SampleMinTurn
                    || now - d.LastSentAt >= KeepAliveInterval)
                    SendSwing(net, d, DoorSwingMessage.ModeSample, yaw, speed, now);
            }
            return false;
        }

        /// <summary>Other players' pushes on a door run here, at their contact points.</summary>
        private static bool ApplyPushes(Drive d, float now)
        {
            if (d.Pushes.Count == 0)
                return false;
            bool any = false;
            _pushDone.Clear();
            foreach (var kv in d.Pushes)
            {
                Push push = kv.Value;
                if (now > push.Until)
                {
                    _pushDone.Add(kv.Key);
                    continue;
                }
                any = true;
                Vector3 point = d.Door.body.TransformPoint(push.Local);
                point.y = d.Rb.worldCenterOfMass.y;
                d.Rb.AddForceAtPosition(
                    push.Force * (PlayerMoveForcePatch.VanillaForceFactor * Time.fixedDeltaTime * PushScale(d, now)), point);
            }
            for (int i = 0; i < _pushDone.Count; i++)
                d.Pushes.Remove(_pushDone[i]);
            _pushDone.Clear();
            return any;
        }

        /// <summary>
        /// The share of a walk force another player's push gets here: half when the local player
        /// pushes too (the two turn it 1.5 times as fast), else the door's share of the damping of
        /// it and a pusher (mass times drag of each), as a lone body push moves it.
        /// </summary>
        private static float PushScale(Drive d, float now)
        {
            Player p = Player.Instance;
            if (p == null || p.Rigidbody == null)
                return 1f;
            if (PushingInto(d.Door, now, out _))
                return SharedDrag.TogetherSpeed - 1f;
            float door = d.Rb.mass * d.Rb.drag;
            float pusher = p.Rigidbody.mass * p.Rigidbody.drag;
            if (door + pusher <= 0.0001f)
                return 1f;
            return Mathf.Clamp(door / (door + pusher), 0.25f, 1f);
        }

        private static bool TickFollow(LanNetworkManager net, Drive d, float now)
        {
            if (d.Mode == Mode.Hold && Touching(d.Door, now, out _))
            {
                // The local player's body takes it: this machine runs it from here.
                Claim(d.Door, d.Rb, "body");
                return false;
            }
            if (d.Mode == Mode.Follow && !d.HasRest && now - d.LastRecvAt > FollowLost)
            {
                ModLog.Event(LogCat.Physics, $"[DoorSwing] {d.Door.gameObject.name}: p{d.OwnerId}'s swing went quiet, left where it is");
                Stop(d);
                return true;
            }

            bool final = true;
            float rate = 0f;
            float target = d.Mode == Mode.Hold ? d.RestYaw : Playback(d, now, out final, out rate);
            float delta = Mathf.DeltaAngle(BodyYaw(d), target);
            if (final && Mathf.Abs(delta) <= RestSnap)
            {
                Turn(d, delta);
                Stop(d);
                _restYaw[d.Door.GetInstanceID()] = target;
                if (d.Mode == Mode.Follow)
                    ModLog.Event(LogCat.Physics, $"[DoorSwing] {d.Door.gameObject.name} at {Key(d.Door)}: followed p{d.OwnerId} to rest at {target:F1}"
                        + $" ({d.Got} samples, {d.Late} late, worst {d.LateMax * 1000f:F0} ms)");
                return true;
            }
            float dt = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 0.02f;
            // Followed: the curve's own spin, plus what closes the gap to it. Held: back to rest in one step.
            float spin = d.Mode == Mode.Hold ? delta / dt : rate + delta / Mathf.Max(CatchUpTime, dt);
            Spin(d, Mathf.Clamp(spin, -MaxDriveSpeed, MaxDriveSpeed));

            if (d.Mode == Mode.Follow && now >= d.NextPushAt && PushingInto(d.Door, now, out Vector3 point))
            {
                d.NextPushAt = now + PushSendInterval;
                SendPush(net, d, point);
            }
            return false;
        }

        /// <summary>
        /// The followed angle and its spin (degrees a second), <see cref="InterpDelay"/> behind the
        /// sender's clock: a curve through every sample at its time. Between two samples it never
        /// leaves their range, so the door cannot swing past an angle the sender's door did not reach.
        /// </summary>
        private static float Playback(Drive d, float now, out bool final, out float rate)
        {
            final = false;
            rate = 0f;
            int n = d.Samples.Count;
            if (n == 0)
            {
                final = d.HasRest;
                return d.HasRest ? d.RestYaw : BodyYaw(d);
            }
            float t = now - d.Offset - InterpDelay;
            if (t <= d.Samples[0].T)
                return d.Samples[0].Yaw;
            Sample last = d.Samples[n - 1];
            if (t >= last.T)
            {
                final = d.HasRest && last.T >= d.RestT;
                return last.Yaw;
            }
            int i = 0;
            while (i < n - 2 && d.Samples[i + 1].T <= t)
                i++;
            if (i > 0)
                d.Samples.RemoveRange(0, i);
            float from = SlopeAt(d, 0);
            float to = SlopeAt(d, 1);
            Sample a = d.Samples[0];
            Sample b = d.Samples[1];
            float span = b.T - a.T;
            if (span <= 0.0001f)
                return b.Yaw;
            float turn = Mathf.DeltaAngle(a.Yaw, b.Yaw);
            float mean = turn / span;
            from = WithinTurn(from, mean);
            to = WithinTurn(to, mean);
            float u = Mathf.Clamp01((t - a.T) / span);
            float u2 = u * u;
            float u3 = u2 * u;
            rate = (3f * u2 - 4f * u + 1f) * from + (6f * u - 6f * u2) * mean + (3f * u2 - 2f * u) * to;
            return a.Yaw + (u3 - 2f * u2 + u) * span * from + (3f * u2 - 2f * u3) * turn + (u3 - u2) * span * to;
        }

        /// <summary>
        /// The curve's spin through a sample: the turn between its neighbours over their time when
        /// both are known, else the spin the sender measured. Fixed on first use, so the curve
        /// leaves the sample at the spin it arrived with.
        /// </summary>
        private static float SlopeAt(Drive d, int i)
        {
            Sample s = d.Samples[i];
            if (s.HasSlope)
                return s.Slope;
            float slope = s.Speed;
            if (i > 0 && i + 1 < d.Samples.Count)
            {
                Sample before = d.Samples[i - 1];
                Sample after = d.Samples[i + 1];
                float span = after.T - before.T;
                if (span > 0.0001f)
                    slope = (Mathf.DeltaAngle(before.Yaw, s.Yaw) + Mathf.DeltaAngle(s.Yaw, after.Yaw)) / span;
            }
            s.Slope = slope;
            s.HasSlope = true;
            d.Samples[i] = s;
            return slope;
        }

        /// <summary>
        /// An end spin that keeps the curve between its two samples: the way of the turn, and no
        /// more than three times its mean spin.
        /// </summary>
        private static float WithinTurn(float slope, float mean)
        {
            if (Mathf.Abs(mean) < 0.0001f || slope * mean <= 0f)
                return 0f;
            return Mathf.Abs(slope) > 3f * Mathf.Abs(mean) ? 3f * mean : slope;
        }

        /// <summary>Spin the door about its hinge at this rate (degrees a second) for the coming step.</summary>
        private static void Spin(Drive d, float degPerSec)
        {
            Vector3 w = Vector3.up * (degPerSec * Mathf.Deg2Rad);
            Vector3 r = d.Rb.worldCenterOfMass - Hinge(d.Door);
            r.y = 0f;
            d.Rb.angularVelocity = w;
            d.Rb.velocity = Vector3.Cross(w, r);
        }

        private static void Turn(Drive d, float deltaDeg) => TurnAboutHinge(d.Door, deltaDeg);

        /// <summary>A door put at another machine's resting angle: a client's door nudged here with no cause goes back to it.</summary>
        internal static void NoteAtRest(Door door)
        {
            if (door != null && door.body != null && !IsDriven(door))
                _restYaw[door.GetInstanceID()] = Yaw(door);
        }

        /// <summary>Turn an open door's body about its hinge (as vanilla's own turns: about the vertical).</summary>
        internal static void TurnAboutHinge(Door door, float deltaDeg)
        {
            if (door != null && door.body != null && Mathf.Abs(deltaDeg) > 0.0001f)
                door.body.RotateAround(Hinge(door), Vector3.up, deltaDeg);
        }

        /// <summary>
        /// A followed door is drawn between physics steps (as a player's stand-in is): its spin is
        /// set at the physics rate, which a frame shows one or two steps of at a time.
        /// </summary>
        private static void Smooth(Drive d, bool on)
        {
            if (d.Smoothed == on)
                return;
            d.Smoothed = on;
            if (d.Rb == null)
                return;
            if (on)
            {
                d.WasInterpolation = d.Rb.interpolation;
                d.Rb.interpolation = RigidbodyInterpolation.Interpolate;
            }
            else
                d.Rb.interpolation = d.WasInterpolation;
        }

        private static void Stop(Drive d)
        {
            if (d.Rb == null || d.Rb.isKinematic)
                return;
            d.Rb.velocity = Vector3.zero;
            d.Rb.angularVelocity = Vector3.zero;
        }

        private static Vector3 Hinge(Door door)
        {
            HingeJoint h = door.body.GetComponent<HingeJoint>();
            return h != null ? door.body.TransformPoint(h.anchor) : door.body.position;
        }

        // ------------------------------------------------------------------ network

        private static void SendSwing(LanNetworkManager net, Drive d, byte mode, float yaw, float speed, float now)
        {
            Vector3 key = Key(d.Door);
            var msg = new DoorSwingMessage
            {
                Mode = mode,
                SenderId = net.LocalPlayerId,
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                Time = now,
                Yaw = yaw,
                Speed = speed
            };
            net.BroadcastHot(NetMessageType.DoorSwing, w => msg.Serialize(w),
                mode == DoorSwingMessage.ModeRest ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
            d.LastSentAt = now;
            d.LastSentYaw = yaw;
            _sent = true;
        }

        private static void SendPush(LanNetworkManager net, Drive d, Vector3 contact)
        {
            Vector3 intent = PlayerMoveForcePatch.Intent;
            Vector3 local = d.Door.body.InverseTransformPoint(contact);
            Vector3 key = Key(d.Door);
            var msg = new DoorSwingMessage
            {
                Mode = DoorSwingMessage.ModePush,
                SenderId = net.LocalPlayerId,
                TargetId = d.OwnerId,
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                ForceX = intent.x,
                ForceZ = intent.z,
                LocalX = local.x,
                LocalY = local.y,
                LocalZ = local.z
            };
            net.BroadcastHot(NetMessageType.DoorSwing, w => msg.Serialize(w), DeliveryMethod.Unreliable);
            _sent = true;
        }

        private static void SendImpulse(LanNetworkManager net, Drive d, Vector3 force)
        {
            Vector3 key = Key(d.Door);
            var msg = new DoorSwingMessage
            {
                Mode = DoorSwingMessage.ModeImpulse,
                SenderId = net.LocalPlayerId,
                TargetId = d.OwnerId,
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                ForceX = force.x,
                ForceZ = force.z
            };
            net.BroadcastHot(NetMessageType.DoorSwing, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            _sent = true;
            ModLog.Event(LogCat.Physics, $"[DoorSwing] blow on {d.Door.gameObject.name} sent to p{d.OwnerId}, who runs it");
        }

        /// <summary>Inbound <see cref="DoorSwingMessage"/> (host-stamped sender).</summary>
        internal static void Handle(DoorSwingMessage m)
        {
            var net = ModRuntime.Network;
            if (net == null || m.SenderId == net.LocalPlayerId)
                return;
            if ((m.Mode == DoorSwingMessage.ModePush || m.Mode == DoorSwingMessage.ModeImpulse)
                && m.TargetId != net.LocalPlayerId)
                return;
            Door door = WorldQueryHelper.FindDoorByPos(new Vector3(m.PosX, m.PosY, m.PosZ));
            Rigidbody rb = Body(door);
            if (!Usable(door, rb))
                return;
            float now = Time.unscaledTime;
            switch (m.Mode)
            {
                case DoorSwingMessage.ModeSample:
                case DoorSwingMessage.ModeRest:
                    OnRemoteSwing(net, door, rb, m, now);
                    break;
                case DoorSwingMessage.ModePush:
                case DoorSwingMessage.ModeImpulse:
                    OnRemoteForce(door, rb, m, now);
                    break;
            }
        }

        private static void OnRemoteSwing(LanNetworkManager net, Door door, Rigidbody rb, DoorSwingMessage m, float now)
        {
            int id = door.GetInstanceID();
            if (_lastRest.TryGetValue(id, out LastRest lr) && lr.Owner == m.SenderId && m.Time <= lr.T)
                return; // late sample of a swing that has already ended
            if (_drives.TryGetValue(id, out Drive d))
            {
                if (d.Mode == Mode.Own)
                {
                    // Both machines started this swing: the lower player id keeps it.
                    if (m.SenderId > net.LocalPlayerId)
                        return;
                    ToFollow(d, m.SenderId, now, "yields to", true);
                }
                else if (d.Mode == Mode.Hold)
                    ToFollow(d, m.SenderId, now, "follows");
                else if (d.OwnerId != m.SenderId)
                {
                    if (m.SenderId > d.OwnerId && now - d.LastRecvAt <= FollowStale)
                        return;
                    ToFollow(d, m.SenderId, now, "follows");
                }
            }
            else
            {
                d = new Drive { Door = door, Rb = rb };
                _drives[id] = d;
                ToFollow(d, m.SenderId, now, "follows");
            }

            int n = d.Samples.Count;
            if (n > 0 && m.Time <= d.Samples[n - 1].T)
                return;
            float offset = now - m.Time;
            if (!d.HasOffset || offset < d.Offset)
            {
                d.Offset = offset;
                d.HasOffset = true;
            }
            d.Got++;
            if (n > 0)
            {
                Sample prev = d.Samples[n - 1];
                float gap = m.Time - prev.T;
                if (gap <= StoodStillGap)
                {
                    // The playback has already passed the sample before this one: it waited there.
                    float behind = now - d.Offset - InterpDelay - prev.T;
                    if (behind > 0f)
                    {
                        d.Late++;
                        if (behind > d.LateMax)
                            d.LateMax = behind;
                    }
                }
                else if (m.Mode == DoorSwingMessage.ModeSample)
                {
                    // The sender skips samples while its door stands still: the door stood at the
                    // last angle until one sample before this one, it did not drift here all the while.
                    AddSample(d, new Sample { T = m.Time - SampleInterval, Yaw = prev.Yaw, HasSlope = true });
                }
            }
            AddSample(d, new Sample { T = m.Time, Yaw = m.Yaw, Speed = m.Speed });
            d.LastRecvAt = now;
            if (m.Mode == DoorSwingMessage.ModeRest)
            {
                d.HasRest = true;
                d.RestYaw = m.Yaw;
                d.RestT = m.Time;
                _lastRest[id] = new LastRest { Owner = m.SenderId, T = m.Time };
            }
        }

        private static void AddSample(Drive d, Sample s)
        {
            if (d.Samples.Count >= MaxSamples)
                d.Samples.RemoveAt(0);
            d.Samples.Add(s);
        }

        private static void ToFollow(Drive d, int owner, float now, string verb, bool wasOwn = false)
        {
            d.Mode = Mode.Follow;
            d.OwnerId = owner;
            d.Samples.Clear();
            d.HasOffset = false;
            d.HasRest = false;
            d.Pushes.Clear();
            d.LastRecvAt = now;
            d.NextPushAt = 0f;
            d.Got = 0;
            d.Late = 0;
            d.LateMax = 0f;
            Smooth(d, true);
            ModLog.Event(LogCat.Physics, $"[DoorSwing] {d.Door.gameObject.name} at {Key(d.Door)}: {verb} p{owner}'s swing"
                + (wasOwn ? " (both started it; lower id keeps it)" : ""));
        }

        private static void OnRemoteForce(Door door, Rigidbody rb, DoorSwingMessage m, float now)
        {
            int id = door.GetInstanceID();
            if (!_drives.TryGetValue(id, out Drive d))
            {
                // Sent while this machine's swing was ending: the door takes it and this machine runs it again.
                Claim(door, rb, m.Mode == DoorSwingMessage.ModeImpulse ? "blow from p" + m.SenderId : "push from p" + m.SenderId);
                if (!_drives.TryGetValue(id, out d))
                    return;
            }
            if (d.Mode != Mode.Own)
                return;
            if (m.Mode == DoorSwingMessage.ModeImpulse)
            {
                rb.AddForce(new Vector3(m.ForceX, 0f, m.ForceZ));
                d.QuietSince = -1f;
                return;
            }
            if (!d.Pushes.TryGetValue(m.SenderId, out Push push))
            {
                push = new Push();
                d.Pushes[m.SenderId] = push;
            }
            push.Force = new Vector3(m.ForceX, 0f, m.ForceZ);
            push.Local = new Vector3(m.LocalX, m.LocalY, m.LocalZ);
            push.Until = now + PushHold;
        }

        // ------------------------------------------------------------------ helpers

        private static bool Touching(Door door, float now, out Vector3 point)
        {
            point = default;
            if (door == null || !_touch.TryGetValue(door.GetInstanceID(), out Touch t) || t.Door != door)
                return false;
            point = t.Point;
            return now - t.At <= TouchHold;
        }

        /// <summary>The local player walks into the door where it touches it.</summary>
        private static bool PushingInto(Door door, float now, out Vector3 point)
        {
            if (!Touching(door, now, out point))
                return false;
            Player p = Player.Instance;
            if (p == null)
                return false;
            Vector3 intent = PlayerMoveForcePatch.Intent;
            intent.y = 0f;
            if (intent.sqrMagnitude < MinIntentSq)
                return false;
            Vector3 to = point - p.transform.position;
            to.y = 0f;
            return to.sqrMagnitude > 0.0001f && Vector3.Dot(intent.normalized, to.normalized) >= IntoDoorCos;
        }

        private static bool Usable(Door door, Rigidbody rb)
            => door != null && rb != null && door.body != null && door.opened && !door.destroyed
                && !door.barricaded && !rb.isKinematic && door.gameObject.activeInHierarchy;

        /// <summary>The host's own prologue pad: its doors exist on its machine only.</summary>
        private static bool IsPersonal(Door door) => PersonalPrologue.IsOnProloguePad(door.transform);

        private static Rigidbody Body(Door door)
        {
            if (door == null || door.body == null)
                return null;
            int id = door.GetInstanceID();
            if (_bodies.TryGetValue(id, out Rigidbody rb) && rb != null)
                return rb;
            rb = door.body.GetComponent<Rigidbody>();
            _bodies[id] = rb;
            return rb;
        }

        private static Door DoorOf(Rigidbody rb)
        {
            if (rb == null)
                return null;
            DoorBody db = rb.GetComponent<DoorBody>();
            return db != null ? db.door : null;
        }

        /// <summary>The door's angle about the vertical: its body's hinge axis is vertical, so its right axis stays level.</summary>
        private static float Yaw(Door door)
        {
            Vector3 r = door.body.right;
            return Mathf.Atan2(r.x, r.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// A driven door's angle in the physics step: a door drawn between steps has its transform
        /// at the drawn pose, its body at the stepped one.
        /// </summary>
        private static float BodyYaw(Drive d)
        {
            if (!d.Smoothed)
                return Yaw(d.Door);
            Vector3 r = d.Rb.rotation * Vector3.right;
            return Mathf.Atan2(r.x, r.z) * Mathf.Rad2Deg;
        }

        private static Vector3 Key(Door door) => WorldPos.Key(door.transform.position);

        // ------------------------------------------------------------------ lifecycle

        /// <summary>A player left: swings its machine ran stay where they are, its pushes end.</summary>
        internal static void OnPeerLeft(int playerId)
        {
            _done.Clear();
            foreach (var kv in _drives)
            {
                Drive d = kv.Value;
                d.Pushes.Remove(playerId);
                if (d.Mode == Mode.Follow && d.OwnerId == playerId)
                {
                    if (d.Rb != null && d.Door != null)
                        Stop(d);
                    Smooth(d, false);
                    _done.Add(kv.Key);
                }
            }
            for (int i = 0; i < _done.Count; i++)
                _drives.Remove(_done[i]);
            _done.Clear();
        }

        internal static void Reset()
        {
            foreach (var kv in _drives)
                Smooth(kv.Value, false);
            _sent = false;
            _drives.Clear();
            _restYaw.Clear();
            _lastRest.Clear();
            _remoteStrikeUntil.Clear();
            _touch.Clear();
            _bodies.Clear();
            _done.Clear();
            _echoDepth = 0;
            _opening = null;
        }
    }

    /// <summary>The local player's contacts with door bodies (vanilla keeps none for doors).</summary>
    internal sealed class LocalDoorContact : MonoBehaviour
    {
        private void OnCollisionEnter(Collision c) => Note(c);

        private void OnCollisionStay(Collision c) => Note(c);

        private void Note(Collision c)
        {
            Collider other = c != null ? c.collider : null;
            if (other == null)
                return;
            DoorBody db = other.GetComponentInParent<DoorBody>();
            if (db == null || db.door == null)
                return;
            Vector3 point = c.contactCount > 0 ? c.GetContact(0).point : other.ClosestPoint(transform.position);
            DoorSwing.NoteTouch(db.door, point);
        }
    }
}
