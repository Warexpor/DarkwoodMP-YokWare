namespace DWMPHorde.Networking
{
    /// <summary>Steam Voice compressed blob over the Horde wire (sent only when voice is enabled).</summary>
    public struct VoiceDataMessage
    {
        public const byte FlagWalkie = 1;

        public int PlayerId;
        public ushort Seq;
        public byte Flags;
        /// <summary>How loud the talker spoke in this packet, measured where it was recorded (0 silence .. 255 shouting).</summary>
        public byte Level;
        public byte[] Data;

        public void Serialize(NetWriter w)
        {
            w.Put(PlayerId);
            w.Put((short)Seq);
            w.Put(Flags);
            w.Put(Level);
            w.Put(Data ?? new byte[0]);
        }

        /// <summary>The same layout written from a recycled capture buffer (the hot send path, no copy).</summary>
        public static void WriteSlice(NetWriter w, int playerId, ushort seq, byte flags, byte level, byte[] data, int length)
        {
            w.Put(playerId);
            w.Put((short)seq);
            w.Put(flags);
            w.Put(level);
            w.Put(data, 0, length);
        }

        public static VoiceDataMessage Deserialize(NetReader r)
        {
            return new VoiceDataMessage
            {
                PlayerId = r.GetInt(),
                Seq = (ushort)r.GetShort(),
                Flags = r.GetByte(),
                Level = r.GetByte(),
                Data = r.GetBytes()
            };
        }
    }
}
