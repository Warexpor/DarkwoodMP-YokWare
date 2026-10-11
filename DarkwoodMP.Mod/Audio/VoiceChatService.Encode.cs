using System;
using System.Collections.Generic;
using System.Threading;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using LiteNetLib;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// The outgoing voice is encoded on its own thread. The managed Opus encoder takes 1 to 2.5 ms
    /// for one 40 ms packet; done in the game's Update, a first word (the kept packet from before
    /// the key plus the new ones) or a radio packet with its picked-up sounds cost 3 to 8 ms of a
    /// 10 ms frame, and the talker's own game dropped a frame. The game thread only queues the
    /// samples and sends the finished bytes on its next frame.
    /// </summary>
    public static partial class VoiceChatService
    {
        private sealed class TxJob
        {
            public int Epoch;
            public float[] Voice;
            /// <summary>What the keyed radio picked up around its holder, or null.</summary>
            public float[] World;
            public int PlayerId;
            public ushort Seq;
            public byte Flags;
            public byte Level;
            public byte[] Data;
            public int Length;
        }

        private static readonly Queue<TxJob> _txIn = new Queue<TxJob>(8);   // reset-in: ResetEncoder
        private static readonly Queue<TxJob> _txOut = new Queue<TxJob>(8);  // reset-in: ResetEncoder
        private static readonly AutoResetEvent _txWake = new AutoResetEvent(false); // process-scoped: encoder thread wake
        private static Thread _txThread;      // process-scoped: one encoder thread for the process
        private static volatile int _txEpoch; // process-scoped: bumped by ResetEncoder; older jobs are dropped
        private static volatile string _txError; // process-scoped: the encoder thread's failure, reported once

        /// <summary>A session ended: what is queued is dropped and the encoders start fresh.</summary>
        private static void ResetEncoder()
        {
            _txEpoch++;
            lock (_txIn) _txIn.Clear();
            lock (_txOut) _txOut.Clear();
        }

        private static void QueueEncode(TxJob job)
        {
            job.Epoch = _txEpoch;
            if (_txThread == null)
            {
                _txThread = new Thread(EncodeLoop) { IsBackground = true, Name = "YokWare voice encode" };
                _txThread.Start();
            }
            lock (_txIn) _txIn.Enqueue(job);
            _txWake.Set();
        }

        /// <summary>Game thread, every frame: the packets encoded since the last one go out, in order.</summary>
        private static void SendEncoded(LanNetworkManager net)
        {
            if (_txError != null)
            {
                if (NetLogThrottle.ShouldLog("voice-encode", 10f, out _))
                    ModLog.Warn(LogCat.Audio, "Voice encode: " + _txError);
                _txError = null;
            }
            while (true)
            {
                TxJob job;
                lock (_txOut)
                {
                    if (_txOut.Count == 0)
                        return;
                    job = _txOut.Dequeue();
                }
                if (job.Epoch != _txEpoch || net == null || !net.IsConnected)
                    continue;
                net.Broadcast(NetMessageType.VoiceData,
                    w => VoiceDataMessage.WriteSlice(w, job.PlayerId, job.Seq, job.Flags, job.Level, job.Data, job.Length),
                    DeliveryMethod.Unreliable);
                _txPackets++;
            }
        }

        private static void EncodeLoop()
        {
            VoiceEncoder voiceEnc = null, worldEnc = null;
            int epoch = -1;
            var buf = new byte[WorldPrefixBytes + VoiceCodec.MaxBlockBytes * 2];
            while (true)
            {
                _txWake.WaitOne(250);
                while (true)
                {
                    TxJob job;
                    lock (_txIn)
                    {
                        if (_txIn.Count == 0)
                            break;
                        job = _txIn.Dequeue();
                    }
                    if (job.Epoch != _txEpoch)
                        continue;
                    try
                    {
                        if (job.Epoch != epoch)
                        {
                            epoch = job.Epoch;
                            voiceEnc = null;
                            worldEnc = null;
                        }
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        bool world = job.World != null;
                        int at = world ? WorldPrefixBytes : 0;
                        int voiceLen = (voiceEnc ?? (voiceEnc = new VoiceEncoder(speech: true)))
                            .Encode(job.Voice, job.Voice.Length, buf, at);
                        int len = at + voiceLen;
                        if (world)
                        {
                            // Two blocks: the voice's length, the voice, then the picked-up sounds.
                            buf[0] = (byte)(voiceLen & 0xFF);
                            buf[1] = (byte)(voiceLen >> 8);
                            len += (worldEnc ?? (worldEnc = new VoiceEncoder(speech: false)))
                                .Encode(job.World, VoiceCodec.PacketSamples, buf, len);
                        }
                        Interlocked.Add(ref _encodeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                        job.Data = new byte[len];
                        Buffer.BlockCopy(buf, 0, job.Data, 0, len);
                        job.Length = len;
                        lock (_txOut) _txOut.Enqueue(job);
                    }
                    catch (Exception ex)
                    {
                        _txError = ex.GetType().Name + ": " + ex.Message;
                        voiceEnc = null;
                        worldEnc = null;
                    }
                }
            }
        }
    }
}
