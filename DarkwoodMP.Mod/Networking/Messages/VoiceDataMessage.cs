namespace DWMPHorde.Networking
{
    /// <summary>Steam Voice compressed blob over Horde wire (optional; older peers ignore).</summary>
    public struct VoiceDataMessage
    {
        public const byte FlagWalkie = 1;

        public int PlayerId;
        public ushort Seq;
        public byte Flags;
        public byte[] Data;

        public void Serialize(NetWriter w)
        {
            w.Put(PlayerId);
            w.Put((short)Seq);
            w.Put(Flags);
            w.Put(Data ?? new byte[0]);
        }

        public static VoiceDataMessage Deserialize(NetReader r)
        {
            return new VoiceDataMessage
            {
                PlayerId = r.GetInt(),
                Seq = (ushort)r.GetShort(),
                Flags = r.GetByte(),
                Data = r.GetBytes()
            };
        }
    }
}
