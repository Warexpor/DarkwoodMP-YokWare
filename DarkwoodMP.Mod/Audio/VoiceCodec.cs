using System;
using Concentus.Enums;
using Concentus.Structs;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// The voice codec: 16 kHz mono Opus in 40 ms packets, through the managed Concentus port
    /// compiled into the mod (ThirdParty/Concentus): no native code and no extra file, the same on
    /// every install. A stream carries its state from packet to packet, so each talker has one
    /// <see cref="VoiceEncoder"/> a stream and each listener one <see cref="VoiceDecoder"/> a
    /// talker and stream. Every packet also carries a thin copy of the one before it, which the
    /// decoder plays when that one was lost.
    /// </summary>
    internal static class VoiceCodec
    {
        public const int SampleRate = 16000;
        /// <summary>Samples in one packet (40 ms).</summary>
        public const int PacketSamples = 640;
        /// <summary>The most one encoded block takes; a usual one is a third of it.</summary>
        public const int MaxBlockBytes = 480;
        /// <summary>A talker's voice, in bits a second (it varies with what is said).</summary>
        public const int VoiceBitrate = 32000;
        /// <summary>The sounds a radio picks up around its talker (played through a radio's narrow band only).</summary>
        public const int WorldBitrate = 24000;

        /// <summary>
        /// 0 for silence, about 0.15 for a whisper, 0.65 for normal speech, 1 for shouting: the RMS
        /// of the samples in dBFS, from -45 to -10.
        /// </summary>
        public static float LevelOf(float[] samples, int count)
        {
            float db = DbOf(samples, count);
            float t = (db + 45f) / 35f;
            return t < 0f ? 0f : t > 1f ? 1f : t;
        }

        public static float DbOf(float[] samples, int count)
        {
            if (count <= 0)
                return -120f;
            double sum = 0;
            for (int i = 0; i < count; i++)
                sum += samples[i] * (double)samples[i];
            return 20f * (float)Math.Log10(Math.Sqrt(sum / count) + 1e-9);
        }
    }

    /// <summary>One outgoing stream. Fed every packet that is sent, in the order sent.</summary>
    internal sealed class VoiceEncoder
    {
        private readonly OpusEncoder _enc;
        private readonly short[] _pcm = new short[VoiceCodec.PacketSamples];

        /// <param name="speech">A voice (tuned for speech, carries a copy of the last packet); otherwise any sound.</param>
        public VoiceEncoder(bool speech)
        {
            _enc = new OpusEncoder(VoiceCodec.SampleRate, 1,
                speech ? OpusApplication.OPUS_APPLICATION_VOIP : OpusApplication.OPUS_APPLICATION_AUDIO);
            _enc.Bitrate = speech ? VoiceCodec.VoiceBitrate : VoiceCodec.WorldBitrate;
            _enc.UseVBR = true;
            _enc.Complexity = 8;
            _enc.UseDTX = false;
            if (speech)
            {
                _enc.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
                _enc.UseInbandFEC = true;
                _enc.PacketLossPercent = 10;
            }
        }

        /// <summary>
        /// Encode one packet of samples (-1..1) into <paramref name="dst"/> from <paramref name="at"/>;
        /// returns the bytes written. Fewer than a packet's samples are padded with silence.
        /// </summary>
        public int Encode(float[] src, int count, byte[] dst, int at)
        {
            int n = Math.Min(count, VoiceCodec.PacketSamples);
            for (int i = 0; i < n; i++)
            {
                int v = (int)(src[i] * 32767f);
                _pcm[i] = (short)(v > 32767 ? 32767 : v < -32768 ? -32768 : v);
            }
            for (int i = n; i < VoiceCodec.PacketSamples; i++)
                _pcm[i] = 0;
            int room = Math.Min(VoiceCodec.MaxBlockBytes, dst.Length - at);
            return _enc.Encode(_pcm, 0, VoiceCodec.PacketSamples, dst, at, room);
        }
    }

    /// <summary>One incoming stream of one talker.</summary>
    internal sealed class VoiceDecoder
    {
        private readonly OpusDecoder _dec = new OpusDecoder(VoiceCodec.SampleRate, 1);
        private readonly short[] _pcm = new short[VoiceCodec.PacketSamples];

        /// <summary>Decode a block into <paramref name="dst"/> from <paramref name="dstAt"/> (-1..1); returns the samples written.</summary>
        public int Decode(byte[] src, int at, int length, float[] dst, int dstAt)
        {
            if (src == null || length <= 0 || at < 0 || at + length > src.Length)
                return 0;
            return Out(_dec.Decode(src, at, length, _pcm, 0, VoiceCodec.PacketSamples, false), dst, dstAt);
        }

        /// <summary>
        /// The packet lost just before this block, from the copy the block carries of it (the
        /// decoder's own guess where the block has none). Called before <see cref="Decode"/> of the block.
        /// </summary>
        public int Recover(byte[] src, int at, int length, float[] dst, int dstAt)
        {
            if (src == null || length <= 0 || at < 0 || at + length > src.Length)
                return 0;
            return Out(_dec.Decode(src, at, length, _pcm, 0, VoiceCodec.PacketSamples, true), dst, dstAt);
        }

        /// <summary>A lost packet with nothing to rebuild it from: the voice carried on and faded.</summary>
        public int Conceal(float[] dst, int dstAt)
        {
            return Out(_dec.Decode(null, 0, 0, _pcm, 0, VoiceCodec.PacketSamples, false), dst, dstAt);
        }

        /// <summary>Forget the stream so far (after a long break in it, or a block that would not decode).</summary>
        public void Reset() => _dec.ResetState();

        private int Out(int samples, float[] dst, int dstAt)
        {
            int n = Math.Min(Math.Min(samples, VoiceCodec.PacketSamples), dst.Length - dstAt);
            for (int i = 0; i < n; i++)
                dst[dstAt + i] = _pcm[i] / 32768f;
            return n < 0 ? 0 : n;
        }
    }
}
