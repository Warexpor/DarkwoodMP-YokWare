using DWMPHorde.Sync;

namespace DWMPHorde.Networking
{
    public struct DreamStartedMessage
    {
        public string PresetName;
        public float LocPosX, LocPosY, LocPosZ;
        /// <summary>Optional trailer (protocol 19+ dream harden).</summary>
        public int SessionId;
        public byte LvlFlags;
        public string[] CompletedPresets;

        public void Serialize(NetWriter w)
        {
            w.Put(PresetName ?? "");
            w.Put(LocPosX); w.Put(LocPosY); w.Put(LocPosZ);
            w.Put(SessionId);
            w.Put(LvlFlags);
            string[] done = CompletedPresets ?? System.Array.Empty<string>();
            w.Put(done.Length);
            for (int i = 0; i < done.Length; i++)
                w.Put(done[i] ?? "");
        }

        public static DreamStartedMessage Deserialize(NetReader r)
        {
            var msg = new DreamStartedMessage
            {
                PresetName = r.GetString(),
                LocPosX = r.GetFloat(),
                LocPosY = r.GetFloat(),
                LocPosZ = r.GetFloat(),
                CompletedPresets = System.Array.Empty<string>()
            };
            if (r.AvailableBytes >= 9)
            {
                msg.SessionId = r.GetInt();
                msg.LvlFlags = r.GetByte();
                int n = r.GetInt();
                if (n < 0 || n > 256) n = 0;
                msg.CompletedPresets = new string[n];
                for (int i = 0; i < n; i++)
                    msg.CompletedPresets[i] = r.GetString();
            }
            return msg;
        }

        public static DreamStartedMessage Build(string preset, float x, float y, float z) => new DreamStartedMessage
        {
            PresetName = preset ?? "",
            LocPosX = x,
            LocPosY = y,
            LocPosZ = z,
            SessionId = DreamSession.SessionId,
            LvlFlags = DreamSession.ReadLocalLvlFlags(),
            CompletedPresets = DreamSession.GetCompletedPresets()
        };
    }

    public struct DreamEndedMessage
    {
        public string PresetName;
        public string OutcomeName;
        public int SessionId;
        public byte LvlFlags;
        public string[] CompletedPresets;

        public void Serialize(NetWriter w)
        {
            w.Put(PresetName ?? "");
            w.Put(OutcomeName ?? "");
            w.Put(SessionId);
            w.Put(LvlFlags);
            string[] done = CompletedPresets ?? System.Array.Empty<string>();
            w.Put(done.Length);
            for (int i = 0; i < done.Length; i++)
                w.Put(done[i] ?? "");
        }

        public static DreamEndedMessage Deserialize(NetReader r)
        {
            var msg = new DreamEndedMessage
            {
                PresetName = r.GetString(),
                OutcomeName = r.GetString(),
                CompletedPresets = System.Array.Empty<string>()
            };
            if (r.AvailableBytes >= 9)
            {
                msg.SessionId = r.GetInt();
                msg.LvlFlags = r.GetByte();
                int n = r.GetInt();
                if (n < 0 || n > 256) n = 0;
                msg.CompletedPresets = new string[n];
                for (int i = 0; i < n; i++)
                    msg.CompletedPresets[i] = r.GetString();
            }
            return msg;
        }

        public static DreamEndedMessage Build(string preset, string outcome) => new DreamEndedMessage
        {
            PresetName = preset ?? "",
            OutcomeName = outcome ?? "",
            SessionId = DreamSession.SessionId,
            LvlFlags = DreamSession.ReadLocalLvlFlags(),
            CompletedPresets = DreamSession.GetCompletedPresets()
        };
    }

    public struct DreamStartRequestMessage
    {
        public string PresetName;
        public int RequestId;

        public void Serialize(NetWriter w)
        {
            w.Put(PresetName ?? "");
            w.Put(RequestId);
        }

        public static DreamStartRequestMessage Deserialize(NetReader r)
        {
            var msg = new DreamStartRequestMessage { PresetName = r.GetString() };
            if (r.AvailableBytes >= 4)
                msg.RequestId = r.GetInt();
            return msg;
        }
    }

    public struct DreamSessionBulkMessage
    {
        public byte LvlFlags;
        public string[] CompletedPresets;
        public bool SessionActive;
        public string ActivePreset;

        public void Serialize(NetWriter w)
        {
            w.Put(LvlFlags);
            w.Put(SessionActive);
            w.Put(ActivePreset ?? "");
            string[] done = CompletedPresets ?? System.Array.Empty<string>();
            w.Put(done.Length);
            for (int i = 0; i < done.Length; i++)
                w.Put(done[i] ?? "");
        }

        public static DreamSessionBulkMessage Deserialize(NetReader r)
        {
            var msg = new DreamSessionBulkMessage
            {
                LvlFlags = r.GetByte(),
                SessionActive = r.GetBool(),
                ActivePreset = r.GetString(),
                CompletedPresets = System.Array.Empty<string>()
            };
            int n = r.GetInt();
            if (n < 0 || n > 256) n = 0;
            msg.CompletedPresets = new string[n];
            for (int i = 0; i < n; i++)
                msg.CompletedPresets[i] = r.GetString();
            return msg;
        }

        public static DreamSessionBulkMessage FromLocal() => new DreamSessionBulkMessage
        {
            LvlFlags = DreamSession.ReadLocalLvlFlags(),
            SessionActive = DreamSession.IsActive,
            ActivePreset = DreamSession.PresetName ?? "",
            CompletedPresets = DreamSession.GetCompletedPresets()
        };
    }

    public struct DreamChainStartMessage
    {
        public string NextPresetName;
        public int SessionId;

        public void Serialize(NetWriter w)
        {
            w.Put(NextPresetName ?? "");
            w.Put(SessionId);
        }

        public static DreamChainStartMessage Deserialize(NetReader r) => new DreamChainStartMessage
        {
            NextPresetName = r.GetString(),
            SessionId = r.AvailableBytes >= 4 ? r.GetInt() : 0
        };
    }

    public struct DreamItemPickupMessage
    {
        public string ItemType;
        public int Amount;
        public float PosX, PosY, PosZ;

        public void Serialize(NetWriter w)
        {
            w.Put(ItemType ?? "");
            w.Put(Amount);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
        }

        public static DreamItemPickupMessage Deserialize(NetReader r) => new DreamItemPickupMessage
        {
            ItemType = r.GetString(),
            Amount = r.GetInt(),
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat()
        };
    }

    public struct DreamAudioMessage
    {
        public string AudioID;
        public float PosX, PosY, PosZ;
        public float Volume;
        public float Pitch;

        public void Serialize(NetWriter w)
        {
            w.Put(AudioID ?? "");
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(Volume);
            w.Put(Pitch);
        }

        public static DreamAudioMessage Deserialize(NetReader r) => new DreamAudioMessage
        {
            AudioID = r.GetString(),
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            Volume = r.GetFloat(),
            Pitch = r.GetFloat()
        };
    }

    public struct DreamEnteredMessage
    {
        public void Serialize(NetWriter w) { }
        public static DreamEnteredMessage Deserialize(NetReader r) => new DreamEnteredMessage();
    }

    public struct NightDeathStateMessage
    {
        public bool IsDead;
        public bool AllDeadTrigger;

        public void Serialize(NetWriter w)
        {
            w.Put(IsDead);
            w.Put(AllDeadTrigger);
        }

        public static NightDeathStateMessage Deserialize(NetReader r) => new NightDeathStateMessage
        {
            IsDead = r.GetBool(),
            AllDeadTrigger = r.GetBool()
        };
    }

    public struct FinalDreamsceneDeathMessage
    {
        public bool IsDead;

        public void Serialize(NetWriter w) { w.Put(IsDead); }
        public static FinalDreamsceneDeathMessage Deserialize(NetReader r) => new FinalDreamsceneDeathMessage
        {
            IsDead = r.GetBool()
        };
    }

    /// <summary>Load a Unity scene (e.g. credits after epilogue outcomes).</summary>
    public struct SceneLoadMessage
    {
        public string SceneName;

        public void Serialize(NetWriter w) { w.Put(SceneName ?? ""); }
        public static SceneLoadMessage Deserialize(NetReader r) => new SceneLoadMessage
        {
            SceneName = r.GetString()
        };
    }

    /// <summary>Host-authoritative chapter scene change (chapter1 / chapter2).</summary>
    public struct ChapterTransitionMessage
    {
        public int ChapterId;
        public bool LoadChapterSave;
        /// <summary>Host wrote empty chapter save / clients should expect world share first when true.</summary>
        public bool ExpectWorldShare;

        public void Serialize(NetWriter w)
        {
            w.Put(ChapterId);
            w.Put(LoadChapterSave);
            w.Put(ExpectWorldShare);
        }

        public static ChapterTransitionMessage Deserialize(NetReader r) => new ChapterTransitionMessage
        {
            ChapterId = r.GetInt(),
            LoadChapterSave = r.GetBool(),
            ExpectWorldShare = r.GetBool()
        };
    }

    /// <summary>
    /// Host-authoritative cutscene control.
    /// Action: 0 = begin (init+start), 1 = end all (prologue_endCutscene), 2 = skip transition video.
    /// </summary>
    public struct CutsceneSyncMessage
    {
        public const byte ActionBegin = 0;
        public const byte ActionEnd = 1;
        public const byte ActionSkipTransition = 2;

        public byte Action;
        public float PosX, PosY, PosZ;
        public string ManagerName;
        public int SceneIndex;

        public void Serialize(NetWriter w)
        {
            w.Put(Action);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(ManagerName ?? "");
            w.Put(SceneIndex);
        }

        public static CutsceneSyncMessage Deserialize(NetReader r) => new CutsceneSyncMessage
        {
            Action = r.GetByte(),
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            ManagerName = r.GetString(),
            SceneIndex = r.GetInt()
        };
    }
}
