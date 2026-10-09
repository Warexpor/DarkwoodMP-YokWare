using System;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// The voice codec: 16 kHz mono, IMA ADPCM at 4 bits a sample (64 kbit/s). Every packet
    /// starts from its own predictor and step, so a lost or late packet costs only its own 40 ms
    /// and never the ones after it. No dependency, no native code: it runs the same on every
    /// install, with or without Steam.
    /// Packet: predictor (int16, little endian), step index (byte), then two samples a byte
    /// (low nibble first).
    /// </summary>
    internal static class VoiceCodec
    {
        public const int SampleRate = 16000;
        /// <summary>Samples in one packet (40 ms).</summary>
        public const int PacketSamples = 640;
        public const int HeaderBytes = 3;

        private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

        private static readonly int[] StepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66,
            73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408,
            449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
            2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630,
            9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
            32767
        };

        public static int EncodedSize(int samples) => HeaderBytes + (samples + 1) / 2;

        /// <summary>Encode <paramref name="count"/> samples (-1..1) into <paramref name="dst"/>; returns the bytes written.</summary>
        public static int Encode(float[] src, int count, byte[] dst)
        {
            if (count <= 0)
                return 0;
            int predictor = ToPcm(src[0]);
            int index = StartIndex(src, count, predictor);
            dst[0] = (byte)(predictor & 0xFF);
            dst[1] = (byte)((predictor >> 8) & 0xFF);
            dst[2] = (byte)index;
            int o = HeaderBytes;
            for (int i = 0; i < count; i++)
            {
                int nibble = EncodeOne(ToPcm(src[i]), ref predictor, ref index);
                if ((i & 1) == 0)
                    dst[o] = (byte)nibble;
                else
                    dst[o++] |= (byte)(nibble << 4);
            }
            return HeaderBytes + (count + 1) / 2;
        }

        /// <summary>Decode a packet into <paramref name="dst"/> (-1..1); returns the samples written.</summary>
        public static int Decode(byte[] src, int length, float[] dst)
        {
            if (src == null || length < HeaderBytes)
                return 0;
            int predictor = (short)(src[0] | (src[1] << 8));
            int index = Math.Min(Math.Max((int)src[2], 0), 88);
            int samples = Math.Min((length - HeaderBytes) * 2, dst.Length);
            for (int i = 0; i < samples; i++)
            {
                byte b = src[HeaderBytes + (i >> 1)];
                int nibble = (i & 1) == 0 ? b & 0x0F : b >> 4;
                DecodeOne(nibble, ref predictor, ref index);
                dst[i] = predictor / 32768f;
            }
            return samples;
        }

        private static int ToPcm(float f)
        {
            int v = (int)(f * 32767f);
            return v > 32767 ? 32767 : v < -32768 ? -32768 : v;
        }

        /// <summary>A step index that fits the packet's opening swing, so its first samples do not lag behind.</summary>
        private static int StartIndex(float[] src, int count, int predictor)
        {
            int n = Math.Min(count, 8);
            int swing = 0;
            int prev = predictor;
            for (int i = 1; i < n; i++)
            {
                int v = ToPcm(src[i]);
                swing = Math.Max(swing, Math.Abs(v - prev));
                prev = v;
            }
            int index = 0;
            while (index < 88 && StepTable[index] < swing / 2)
                index++;
            return index;
        }

        private static int EncodeOne(int sample, ref int predictor, ref int index)
        {
            int step = StepTable[index];
            int diff = sample - predictor;
            int nibble = 0;
            if (diff < 0)
            {
                nibble = 8;
                diff = -diff;
            }
            int delta = step >> 3;
            if (diff >= step) { nibble |= 4; diff -= step; delta += step; }
            step >>= 1;
            if (diff >= step) { nibble |= 2; diff -= step; delta += step; }
            step >>= 1;
            if (diff >= step) { nibble |= 1; delta += step; }
            predictor += (nibble & 8) != 0 ? -delta : delta;
            predictor = predictor > 32767 ? 32767 : predictor < -32768 ? -32768 : predictor;
            index += IndexTable[nibble];
            index = index < 0 ? 0 : index > 88 ? 88 : index;
            return nibble;
        }

        private static void DecodeOne(int nibble, ref int predictor, ref int index)
        {
            int step = StepTable[index];
            int delta = step >> 3;
            if ((nibble & 4) != 0) delta += step;
            if ((nibble & 2) != 0) delta += step >> 1;
            if ((nibble & 1) != 0) delta += step >> 2;
            predictor += (nibble & 8) != 0 ? -delta : delta;
            predictor = predictor > 32767 ? 32767 : predictor < -32768 ? -32768 : predictor;
            index += IndexTable[nibble];
            index = index < 0 ? 0 : index > 88 ? 88 : index;
        }

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
}
