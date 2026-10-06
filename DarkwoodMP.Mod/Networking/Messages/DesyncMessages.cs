namespace DWMPHorde.Networking
{
    /// <summary>
    /// Host→one client (<see cref="NetMessageType.DesyncDigest"/>): the host's fingerprint of the
    /// world state that client should share. Per section either a hash of the sorted entries
    /// (<see cref="Inline"/> empty; the client asks for the entries only on a mismatch) or, for the
    /// small sections around that player, the entries themselves. Entries are
    /// <c>key\tvalue</c> lines (see <see cref="Sync.DesyncEntries"/>). Protocol 33.
    /// </summary>
    public struct DesyncDigestMessage
    {
        public int Seq;
        /// <summary>Host <c>Controller.totalTime</c> (1440 per day + minutes).</summary>
        public int TotalTime;
        public int Chapter;
        public byte SectionCount;
        public byte[] SectionIds;
        public uint[] Hashes;
        public int[] Counts;
        public string[] Inline;

        public void Serialize(NetWriter w)
        {
            w.Put(Seq);
            w.Put(TotalTime);
            w.Put(Chapter);
            w.Put(SectionCount);
            for (int i = 0; i < SectionCount; i++)
            {
                w.Put(SectionIds != null && i < SectionIds.Length ? SectionIds[i] : (byte)0);
                w.Put(Hashes != null && i < Hashes.Length ? Hashes[i] : 0u);
                w.Put(Counts != null && i < Counts.Length ? Counts[i] : 0);
                w.PutLongString(Inline != null && i < Inline.Length ? Inline[i] : string.Empty);
            }
        }

        public static DesyncDigestMessage Deserialize(NetReader r)
        {
            var m = new DesyncDigestMessage
            {
                Seq = r.GetInt(),
                TotalTime = r.GetInt(),
                Chapter = r.GetInt(),
                SectionCount = r.GetByte()
            };
            m.SectionIds = new byte[m.SectionCount];
            m.Hashes = new uint[m.SectionCount];
            m.Counts = new int[m.SectionCount];
            m.Inline = new string[m.SectionCount];
            for (int i = 0; i < m.SectionCount; i++)
            {
                m.SectionIds[i] = r.GetByte();
                m.Hashes[i] = r.GetUInt();
                m.Counts[i] = r.GetInt();
                m.Inline[i] = r.GetLongString();
            }
            return m;
        }
    }

    /// <summary>
    /// Client→host (<see cref="NetMessageType.DesyncDetailRequest"/>): the hashed sections of digest
    /// <see cref="Seq"/> that differed here; the host answers with their entries. Protocol 33.
    /// </summary>
    public struct DesyncDetailRequestMessage
    {
        public int Seq;
        public byte SectionCount;
        public byte[] SectionIds;

        public void Serialize(NetWriter w)
        {
            w.Put(Seq);
            w.Put(SectionCount);
            for (int i = 0; i < SectionCount; i++)
                w.Put(SectionIds != null && i < SectionIds.Length ? SectionIds[i] : (byte)0);
        }

        public static DesyncDetailRequestMessage Deserialize(NetReader r)
        {
            var m = new DesyncDetailRequestMessage { Seq = r.GetInt(), SectionCount = r.GetByte() };
            m.SectionIds = new byte[m.SectionCount];
            for (int i = 0; i < m.SectionCount; i++)
                m.SectionIds[i] = r.GetByte();
            return m;
        }
    }

    /// <summary>
    /// Host→client (<see cref="NetMessageType.DesyncDetail"/>): one section's entries for a
    /// detail request. <see cref="Truncated"/>: the list passed the wire cap and was cut. Protocol 33.
    /// </summary>
    public struct DesyncDetailMessage
    {
        public int Seq;
        public byte SectionId;
        public bool Truncated;
        public string Entries;

        public void Serialize(NetWriter w)
        {
            w.Put(Seq);
            w.Put(SectionId);
            w.Put(Truncated);
            w.PutLongString(Entries);
        }

        public static DesyncDetailMessage Deserialize(NetReader r) => new DesyncDetailMessage
        {
            Seq = r.GetInt(),
            SectionId = r.GetByte(),
            Truncated = r.GetBool(),
            Entries = r.GetLongString()
        };
    }

    /// <summary>
    /// Client→host (<see cref="NetMessageType.DesyncReport"/>): what this client's check of digest
    /// <see cref="Seq"/> found (new and resolved desyncs, one per line), so the host log carries
    /// the same timeline. Protocol 33.
    /// </summary>
    public struct DesyncReportMessage
    {
        public int Seq;
        public string Lines;

        public void Serialize(NetWriter w)
        {
            w.Put(Seq);
            w.PutLongString(Lines);
        }

        public static DesyncReportMessage Deserialize(NetReader r) => new DesyncReportMessage
        {
            Seq = r.GetInt(),
            Lines = r.GetLongString()
        };
    }
}
