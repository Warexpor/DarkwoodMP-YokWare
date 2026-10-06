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
        /// <summary>Stable campaign ID in the optional trailer; keys client backups.</summary>
        public string CampaignId;
        /// <summary>
        /// Host share pass this package belongs to. A chapter ack echoes it so a Committed ack for a
        /// package the host has since re-sent (broadcast re-run) cannot vouch for the new files.
        /// </summary>
        public int SharePass;
        /// <summary>Host profile difficulty (vanilla <c>GameProfile.Difficulty</c>: lives on hard, permadeath on nightmare).</summary>
        public int Difficulty;
        /// <summary>
        /// Chapter 1 of a game that did not skip the prologue: a player new to this world plays its
        /// own prologue before joining (<c>PersonalPrologue</c>). Protocol 33.
        /// </summary>
        public bool PrologueOffered;

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
            w.Put(CampaignId ?? "");
            w.Put(SharePass);
            w.Put(Difficulty);
            w.Put(PrologueOffered);
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
            msg.CampaignId = r.GetString();
            msg.SharePass = r.GetInt();
            msg.Difficulty = r.GetInt();
            msg.PrologueOffered = r.GetBool();
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

    /// <summary>
    /// Host→clients: host finished entering the chapter world (Ready=true),
    /// or left fully-in-world (Ready=false). Clients on title wait for Ready=true
    /// (or WorldSaveBegin) before treating the host as ready to share.
    /// Soft-reconnect (AlreadyInWorld) ignores both edges.
    /// </summary>
    public struct HostWorldReadyMessage
    {
        public bool Ready;
        public int ChapterId;
        public int DayIndex;

        public void Serialize(NetWriter w)
        {
            w.Put(Ready);
            w.Put(ChapterId);
            w.Put(DayIndex);
        }

        public static HostWorldReadyMessage Deserialize(NetReader r) => new HostWorldReadyMessage
        {
            Ready = r.GetBool(),
            ChapterId = r.GetInt(),
            DayIndex = r.GetInt()
        };
    }
}
