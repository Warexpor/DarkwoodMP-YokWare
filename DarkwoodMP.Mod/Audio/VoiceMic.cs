using System;
using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// This player's microphone, through Unity's own <c>Microphone</c> (any install, no Steam):
    /// the device chosen in Multiplayer > Settings > Voice (config <c>VoiceMicDevice</c>, empty
    /// for the system default), at the chosen mic volume (<c>VoiceMicVolume</c>), brought to
    /// 16 kHz, cleaned of steady noise (<see cref="VoiceDenoise"/>, config
    /// <c>VoiceNoiseSuppression</c>) and cut into 40 ms packets. It also keeps the live level for the meter on the Voice
    /// screen, and a noise gate for an always-on mic that follows the room's own noise floor.
    /// </summary>
    internal static class VoiceMic
    {
        private const int FrameSamples = VoiceCodec.PacketSamples;
        /// <summary>The gate opens this far over the room's noise floor (dB).</summary>
        private const float GateOverFloorDb = 12f;
        private const float GateMinDb = -52f;
        private const float GateHoldSec = 0.45f;

        private static AudioClip _clip; // process-scoped: the running recording
        private static string _device; // process-scoped: device the recording runs on ("" default)
        private static string _wanted; // process-scoped: the setting the recording was started for
        private static int _rate; // process-scoped: the recording's own rate
        private static int _readPos; // process-scoped: next unread sample in the looping clip
        private static float[] _read = new float[0]; // process-scoped: scratch
        private static readonly List<float> _out = new List<float>(4096); // process-scoped: 16 kHz samples not yet framed
        private static double _resamplePos; // process-scoped: resampler phase
        private static float _resamplePrev; // process-scoped: resampler history
        private static VoiceChatService.Biquad _antiAlias; // process-scoped: low-pass before going down to 16 kHz
        private static VoiceDenoise _denoise; // process-scoped: noise suppression state of the running recording (null while off)
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
                catch { return new string[0]; }
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
            catch { /* already gone */ }
            _clip = null;
            _out.Clear();
            Level = 0f;
            Db = -120f;
        }

        private static string DeviceArg(string device) => string.IsNullOrEmpty(device) ? null : device;

        private static bool IsRecording()
        {
            try { return Microphone.IsRecording(DeviceArg(_device)); }
            catch { return false; }
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
                int rate = VoiceCodec.SampleRate;
                if (min > 0 || max > 0)
                    rate = Mathf.Clamp(rate, min > 0 ? min : rate, max > 0 ? max : rate);
                AudioClip clip = Microphone.Start(DeviceArg(device), true, 1, rate);
                if (clip == null)
                    return false;
                _clip = clip;
                _device = device;
                _wanted = wanted;
                _rate = clip.frequency > 0 ? clip.frequency : rate;
                _readPos = 0;
                _resamplePos = 0;
                _resamplePrev = 0f;
                _antiAlias = VoiceChatService.Biquad.LowPass(7000f, _rate);
                _out.Clear();
                _denoise = null;
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
            if (_read.Length != n)
                _read = new float[n];
            // GetData wraps around the end of the looping clip by itself.
            _clip.GetData(_read, _readPos);
            _readPos = pos;

            float gain = Mathf.Clamp(ModConfig.VoiceMicVolume?.Value ?? 1f, 0f, 4f);
            if (_rate == VoiceCodec.SampleRate)
            {
                for (int i = 0; i < n; i++)
                    _out.Add(_read[i] * gain);
            }
            else
            {
                // Down (or up) to 16 kHz: low-pass, then linear between neighbours.
                double step = (double)_rate / VoiceCodec.SampleRate;
                for (int i = 0; i < n; i++)
                {
                    float x = _rate > VoiceCodec.SampleRate ? _antiAlias.Run(_read[i]) : _read[i];
                    while (_resamplePos <= 1.0)
                    {
                        _out.Add((_resamplePrev + (x - _resamplePrev) * (float)_resamplePos) * gain);
                        _resamplePos += step;
                    }
                    _resamplePos -= 1.0;
                    _resamplePrev = x;
                }
            }

            while (_out.Count >= FrameSamples)
            {
                var frame = new float[FrameSamples];
                for (int i = 0; i < FrameSamples; i++)
                {
                    float v = _out[i];
                    // A soft ceiling instead of hard clipping when the mic volume is turned up.
                    frame[i] = v > 0.9f || v < -0.9f ? Mathf.Sign(v) * (0.9f + 0.1f * (float)Math.Tanh((Math.Abs(v) - 0.9f) * 10f)) : v;
                }
                _out.RemoveRange(0, FrameSamples);
                // Steady noise out, after the mic volume and before the level is taken (the
                // level decides how far the voice carries and whether the open mic's gate opens).
                if (ModConfig.VoiceNoiseSuppression == null || ModConfig.VoiceNoiseSuppression.Value)
                {
                    if (_denoise == null)
                        _denoise = new VoiceDenoise();
                    _denoise.Process(frame, 0, FrameSamples);
                }
                else
                    _denoise = null;
                Measure(frame);
                frames.Add(frame);
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
