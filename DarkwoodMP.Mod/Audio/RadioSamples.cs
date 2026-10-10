using System;
using System.IO;
using DWMPHorde.Logging;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// Recorded radio sounds shipped inside the DLL (Resources/Radio, 16-bit PCM WAV). Sources
    /// and licences: Resources/Radio/SOURCES.md.
    /// </summary>
    internal static class RadioSamples
    {
        private const string Prefix = "DWMPHorde.Resources.Radio.";

        /// <summary>The recording as a clip, or null when it is missing or not readable.</summary>
        internal static AudioClip Load(string file, string clipName)
        {
            try
            {
                using (Stream stream = typeof(RadioSamples).Assembly.GetManifestResourceStream(Prefix + file))
                {
                    if (stream == null)
                    {
                        ModLog.Warn(LogCat.Audio, "Radio sound missing from the DLL: " + file);
                        return null;
                    }
                    using (var r = new BinaryReader(stream))
                        return Read(r, clipName);
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Audio, "Radio sound " + file + " not readable: " + ex.Message);
                return null;
            }
        }

        private static AudioClip Read(BinaryReader r, string clipName)
        {
            if (new string(r.ReadChars(4)) != "RIFF")
                throw new InvalidDataException("not a RIFF file");
            r.ReadInt32();
            if (new string(r.ReadChars(4)) != "WAVE")
                throw new InvalidDataException("not a WAVE file");

            int channels = 0, rate = 0, bits = 0;
            while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
            {
                string id = new string(r.ReadChars(4));
                int size = r.ReadInt32();
                long next = r.BaseStream.Position + size + (size & 1);
                if (id == "fmt ")
                {
                    int format = r.ReadInt16();
                    channels = r.ReadInt16();
                    rate = r.ReadInt32();
                    r.ReadInt32();
                    r.ReadInt16();
                    bits = r.ReadInt16();
                    if (format != 1 || bits != 16 || channels < 1)
                        throw new InvalidDataException("only 16-bit PCM is read");
                }
                else if (id == "data")
                {
                    if (channels == 0)
                        throw new InvalidDataException("data before fmt");
                    int frames = size / (2 * channels);
                    var data = new float[frames * channels];
                    for (int i = 0; i < data.Length; i++)
                        data[i] = r.ReadInt16() / 32768f;
                    AudioClip clip = AudioClip.Create(clipName, frames, channels, rate, false);
                    clip.SetData(data, 0);
                    return clip;
                }
                r.BaseStream.Position = next;
            }
            throw new InvalidDataException("no data chunk");
        }
    }
}
