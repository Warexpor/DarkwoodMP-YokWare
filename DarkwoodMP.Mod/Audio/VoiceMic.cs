using System;
using System.Collections.Generic;
using System.Threading;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// This player's microphone, through Unity's own <c>Microphone</c> (any install, no Steam):
    /// the device chosen in Multiplayer > Settings > Voice (config <c>VoiceMicDevice</c>, empty
    /// for the system default), recorded at 48 kHz where the device allows, cleaned of noise
    /// (<see cref="RnNoise"/>, config <c>VoiceNoiseSuppression</c>), brought to the voice
    /// rate at the chosen mic volume (<c>VoiceMicVolume</c>) and cut into 40 ms packets
    /// (<see cref="VoiceMicChain"/>). That work runs on a thread of its own, so the game's
    /// frame only hands the recording over and collects the finished packets a frame later.
    /// It also keeps the live level for the meter on the Voice screen, and a noise gate for
    /// an always-on mic that follows the room's own noise floor.
    /// </summary>
    internal static class VoiceMic
    {
        private const int FrameSamples = VoiceCodec.PacketSamples;
        /// <summary>The gate opens this far over the room's noise floor (dB).</summary>
        private const float GateOverFloorDb = 12f;
        private const float GateMinDb = -52f;
        private const float GateHoldSec = 0.45f;
        /// <summary>Seconds the looping recording holds.</summary>
        private const int RingSec = 3;

        private static AudioClip _clip; // process-scoped: the running recording
        private static string _device; // process-scoped: device the recording runs on ("" default)
        private static string _wanted; // process-scoped: the setting the recording was started for
        private static int _rate; // process-scoped: the recording's own rate
        private static int _readPos; // process-scoped: next unread sample in the looping clip
        private static Worker _worker; // process-scoped: the running recording's processing thread
        private static readonly List<float[]> _done = new List<float[]>(8); // process-scoped: scratch
        private static bool _warnedSuppression; // process-scoped: log once
        private static float _floorDb = -60f; // process-scoped: tracked noise floor
        private static float _gateUntil; // process-scoped: gate hangover
        private static bool _warnedNoDevice; // process-scoped: log once
        private static float _nextRetry; // process-scoped: device retry

        /// <summary>The level of the latest frame, 0..1 (as sent with the packets), for the meter.</summary>
        internal static float Level { get; private set; }
        /// <summary>The latest frame's loudness in dBFS.</summary>
        internal static float Db { get; private set; } = -120f;
        /// <summary>The always-on gate is open (speech over the room's noise).</summary>
        internal static bool GateOpen => Time.unscaledTime < _gateUntil;
        internal static bool Running => _clip != null;
        /// <summary>The device the recording runs on, as Unity names it ("" when none is running).</summary>
        internal static string RunningDevice => _clip != null ? (_device ?? "") : "";

        /// <summary>The input devices Unity sees, in its order.</summary>
        internal static string[] Devices
        {
            get
            {
                try { return Microphone.devices ?? new string[0]; }
                catch (Exception ex) { ModLog.Swallowed(ex); return new string[0]; }
            }
        }

        /// <summary>Keep the mic running (or stop it) and turn what it heard into finished frames.</summary>
        internal static void Tick(bool want, List<float[]> frames)
        {
            if (!want)
            {
                Stop();
                return;
            }
            string device = ModConfig.VoiceMicDevice?.Value ?? "";
            if (_clip != null && !string.Equals(device, _wanted, StringComparison.Ordinal))
                Stop();
            if (_clip == null && !Start(device))
                return;
            if (!IsRecording())
            {
                // The device went away (unplugged): try again in a moment.
                Stop();
                return;
            }
            Pump(frames);
        }

        internal static void Stop()
        {
            if (_clip == null)
                return;
            try { Microphone.End(DeviceArg(_device)); }
            catch (Exception ex) { ModLog.Swallowed(ex); /* already gone */ }
            _clip = null;
            if (_worker != null)
                _worker.Stop();
            _worker = null;
            Level = 0f;
            Db = -120f;
        }

        private static string DeviceArg(string device) => string.IsNullOrEmpty(device) ? null : device;

        private static bool IsRecording()
        {
            try { return Microphone.IsRecording(DeviceArg(_device)); }
            catch (Exception ex) { ModLog.Swallowed(ex); return false; }
        }

        private static bool Start(string device)
        {
            if (Time.unscaledTime < _nextRetry)
                return false;
            _nextRetry = Time.unscaledTime + 3f;
            string wanted = device;
            string[] devices = Devices;
            if (devices.Length == 0)
            {
                if (!_warnedNoDevice)
                {
                    _warnedNoDevice = true;
                    ModLog.Warn(LogCat.Audio, "[Voice] no microphone found");
                }
                return false;
            }
            // A saved device that is not plugged in: the default one instead.
            if (!string.IsNullOrEmpty(device) && Array.IndexOf(devices, device) < 0)
            {
                if (NetLogThrottle.ShouldLog("voice-mic-missing", 60f, out _))
                    ModLog.Warn(LogCat.Audio, "[Voice] microphone '" + device + "' not found, using the default");
                device = "";
            }
            try
            {
                Microphone.GetDeviceCaps(DeviceArg(device), out int min, out int max);
                int rate = VoiceMicChain.ModelRate;
                if (min > 0 || max > 0)
                    rate = Mathf.Clamp(rate, min > 0 ? min : rate, max > 0 ? max : rate);
                // A looping recording of RingSec: the frame reads what came since the last one,
                // so a stall longer than that (a load, a hitch) would lose what was said.
                AudioClip clip = Microphone.Start(DeviceArg(device), true, RingSec, rate);
                if (clip == null)
                    return false;
                _clip = clip;
                _device = device;
                _wanted = wanted;
                _rate = clip.frequency > 0 ? clip.frequency : rate;
                _readPos = 0;
                _worker = new Worker(_rate);
                _warnedNoDevice = false;
                ModLog.Event(LogCat.Audio, "[Voice] microphone '" + (string.IsNullOrEmpty(device) ? devices[0] + "' (default)" : device + "'")
                    + " at " + _rate + " Hz");
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Audio, "[Voice] microphone start failed: " + ex.Message);
                return false;
            }
        }

        private static void Pump(List<float[]> frames)
        {
            int pos = Microphone.GetPosition(DeviceArg(_device));
            int len = _clip.samples;
            if (pos < 0 || len <= 0)
                return;
            int n = (pos - _readPos + len) % len;
            if (n <= 0)
                return;
            // The worker gets a recycled buffer and the count; it gives the buffer back.
            float[] read = _worker.Rent(n);
            ReadClip(read, n, _readPos, len);
            _readPos = pos;

            float gain = VolumeScale.Clamp(ModConfig.VoiceMicVolume?.Value ?? 1f);
            bool suppress = ModConfig.VoiceNoiseSuppression == null || ModConfig.VoiceNoiseSuppression.Value;
            float strength = ModConfig.VoiceNoiseSuppressionStrength?.Value ?? ModConfig.DefaultNoiseSuppressionStrength;
            _worker.Push(read, n, gain, suppress, strength);

            _done.Clear();
            _worker.Take(_done);
            // The level is taken after the cleaning: it decides how far the voice carries
            // and whether the open mic's gate opens.
            for (int i = 0; i < _done.Count; i++)
            {
                Measure(_done[i]);
                frames.Add(_done[i]);
            }
            _done.Clear();

            string error = _worker.Error;
            if (error != null)
            {
                if (NetLogThrottle.ShouldLog("voice-mic-worker", 60f, out _))
                    ModLog.Error(LogCat.Audio, "[Voice] microphone processing stopped: " + error);
                Stop();
                return;
            }
            string suppression = _worker.SuppressionError;
            if (suppression != null && !_warnedSuppression)
            {
                _warnedSuppression = true;
                ModLog.Warn(LogCat.Audio, "[Voice] noise suppression unavailable: " + suppression);
            }
        }

        /// <summary>Power-of-two scratch for <see cref="ReadClip"/>, by size (main thread).</summary>
        private static readonly float[][] _pieces = new float[31][]; // process-scoped: scratch

        /// <summary>
        /// <paramref name="n"/> samples of the looping clip from <paramref name="from"/> into
        /// <paramref name="dst"/>. <c>GetData</c> fills the whole array it is given, and the count
        /// changes every frame: read in power-of-two pieces into kept arrays instead of a new
        /// array a frame. GetData wraps around the end of the looping clip by itself.
        /// </summary>
        private static void ReadClip(float[] dst, int n, int from, int len)
        {
            int at = 0;
            for (int bit = 30; bit >= 0 && at < n; bit--)
            {
                int size = 1 << bit;
                if ((n & size) == 0)
                    continue;
                float[] piece = _pieces[bit] ?? (_pieces[bit] = new float[size]);
                _clip.GetData(piece, (from + at) % len);
                Array.Copy(piece, 0, dst, at, size);
                at += size;
            }
        }

        /// <summary>
        /// The recording's processing on its own thread: the game's frame pushes what the
        /// microphone recorded and takes the packets that are ready.
        /// </summary>
        private sealed class Worker
        {
            private struct Chunk
            {
                internal float[] Data;
                internal int Count;
                internal float Gain;
                internal bool Suppress;
                internal float Strength;
            }

            private readonly VoiceMicChain _chain;
            private readonly Queue<Chunk> _in = new Queue<Chunk>();
            private readonly List<float[]> _ready = new List<float[]>();
            private readonly AutoResetEvent _wake = new AutoResetEvent(false);
            /// <summary>Hand-over buffers the worker is done with, for the next frames to fill.</summary>
            private readonly Stack<float[]> _free = new Stack<float[]>(4);
            private volatile bool _stop;
            private volatile string _error;
            private volatile string _suppressionError;

            internal string Error => _error;
            internal string SuppressionError => _suppressionError;

            internal Worker(int rate)
            {
                _chain = new VoiceMicChain(rate);
                new Thread(Run) { IsBackground = true, Name = "YokWare voice mic" }.Start();
            }

            /// <summary>A buffer for at least <paramref name="n"/> samples.</summary>
            internal float[] Rent(int n)
            {
                float[] buf = null;
                lock (_free)
                {
                    if (_free.Count > 0)
                        buf = _free.Pop();
                }
                if (buf == null || buf.Length < n)
                {
                    int size = 1024;
                    while (size < n)
                        size <<= 1;
                    buf = new float[size];
                }
                return buf;
            }

            internal void Push(float[] data, int count, float gain, bool suppress, float strength)
            {
                lock (_in)
                    _in.Enqueue(new Chunk { Data = data, Count = count, Gain = gain, Suppress = suppress, Strength = strength });
                _wake.Set();
            }

            internal void Take(List<float[]> frames)
            {
                lock (_ready)
                {
                    frames.AddRange(_ready);
                    _ready.Clear();
                }
            }

            internal void Stop()
            {
                _stop = true;
                _wake.Set();
            }

            private void Run()
            {
                var frames = new List<float[]>(8);
                try
                {
                    while (!_stop)
                    {
                        _wake.WaitOne(250);
                        while (!_stop)
                        {
                            Chunk chunk;
                            lock (_in)
                            {
                                if (_in.Count == 0)
                                    break;
                                chunk = _in.Dequeue();
                            }
                            _chain.Strength = chunk.Strength;
                            _chain.Process(chunk.Data, chunk.Count, chunk.Gain, chunk.Suppress, frames);
                            lock (_free)
                            {
                                if (_free.Count < 4)
                                    _free.Push(chunk.Data);
                            }
                            if (_chain.SuppressionError != null)
                                _suppressionError = _chain.SuppressionError;
                            if (frames.Count > 0)
                            {
                                lock (_ready)
                                    _ready.AddRange(frames);
                                frames.Clear();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ModLog.Swallowed(ex);
                    _error = ex.GetType().Name + ": " + ex.Message;
                }
            }
        }

        private static void Measure(float[] frame)
        {
            float db = VoiceCodec.DbOf(frame, frame.Length);
            Db = db;
            Level = VoiceCodec.LevelOf(frame, frame.Length);
            // The floor falls at once to a quieter frame and creeps up slowly, so speech never
            // becomes the floor but a fan switched on does after a while.
            float dt = FrameSamples / (float)VoiceCodec.SampleRate;
            _floorDb = db < _floorDb ? db : Mathf.Min(_floorDb + 1.5f * dt, db);
            if (db > Mathf.Max(_floorDb + GateOverFloorDb, GateMinDb))
                _gateUntil = Time.unscaledTime + GateHoldSec;
        }
    }
}
