namespace DWMPHorde.Networking
{
    /// <summary>
    /// Host announces a one-shot new-world save package (files follow as chunks).
    /// </summary>
    public struct WorldSaveBeginMessage
    {
        /// <summary>Host profile slot id (1–5). Clients write into the same slot number.</summary>
        public int ProfileId;
        public int ChapterId;
        public int DayIndex;
        public int FileCount;
        public string[] FileNames;
        public int[] UncompressedSizes;
        public int[] CompressedSizes;
        public int[] ChunkCounts;

        public void Serialize(NetWriter w)
        {
            w.Put(ProfileId);
            w.Put(ChapterId);
            w.Put(DayIndex);
            w.Put(FileCount);
            for (int i = 0; i < FileCount; i++)
            {
                w.Put(FileNames != null && i < FileNames.Length ? FileNames[i] : "");
                w.Put(UncompressedSizes != null && i < UncompressedSizes.Length ? UncompressedSizes[i] : 0);
                w.Put(CompressedSizes != null && i < CompressedSizes.Length ? CompressedSizes[i] : 0);
                w.Put(ChunkCounts != null && i < ChunkCounts.Length ? ChunkCounts[i] : 0);
            }
        }

        public static WorldSaveBeginMessage Deserialize(NetReader r)
        {
            var msg = new WorldSaveBeginMessage
            {
                ProfileId = r.GetInt(),
                ChapterId = r.GetInt(),
                DayIndex = r.GetInt(),
                FileCount = r.GetInt()
            };
            if (msg.FileCount < 0 || msg.FileCount > 8)
                msg.FileCount = 0;

            msg.FileNames = new string[msg.FileCount];
            msg.UncompressedSizes = new int[msg.FileCount];
            msg.CompressedSizes = new int[msg.FileCount];
            msg.ChunkCounts = new int[msg.FileCount];
            for (int i = 0; i < msg.FileCount; i++)
            {
                msg.FileNames[i] = r.GetString();
                msg.UncompressedSizes[i] = r.GetInt();
                msg.CompressedSizes[i] = r.GetInt();
                msg.ChunkCounts[i] = r.GetInt();
            }
            return msg;
        }
    }

    /// <summary>One compressed chunk of a single save file.</summary>
    public struct WorldSaveChunkMessage
    {
        public byte FileIndex;
        public int ChunkIndex;
        public byte[] Data;

        public void Serialize(NetWriter w)
        {
            w.Put(FileIndex);
            w.Put(ChunkIndex);
            w.Put(Data);
        }

        public static WorldSaveChunkMessage Deserialize(NetReader r) => new WorldSaveChunkMessage
        {
            FileIndex = r.GetByte(),
            ChunkIndex = r.GetInt(),
            Data = r.GetBytes()
        };
    }

    /// <summary>Host finished streaming the package (client may apply).</summary>
    public struct WorldSaveEndMessage
    {
        public bool Success;

        public void Serialize(NetWriter w) => w.Put(Success);

        public static WorldSaveEndMessage Deserialize(NetReader r) => new WorldSaveEndMessage
        {
            Success = r.GetBool()
        };
    }

    /// <summary>
    /// Client→host: request world save share (Yokyy RequestWorld equivalent).
    /// Host uses receive-side player id; RequesterId is diagnostic only.
    /// </summary>
    public struct WorldRequestMessage
    {
        public int RequesterId;

        public void Serialize(NetWriter w) => w.Put(RequesterId);

        public static WorldRequestMessage Deserialize(NetReader r) => new WorldRequestMessage
        {
            RequesterId = r.GetInt()
        };
    }
}
