namespace DWMPHorde.Networking
{
    public struct DreamStartedMessage
    {
        public string PresetName;
        public float LocPosX, LocPosY, LocPosZ;

        public void Serialize(NetWriter w)
        {
            w.Put(PresetName ?? "");
            w.Put(LocPosX); w.Put(LocPosY); w.Put(LocPosZ);
        }

        public static DreamStartedMessage Deserialize(NetReader r) => new DreamStartedMessage
        {
            PresetName = r.GetString(),
            LocPosX = r.GetFloat(),
            LocPosY = r.GetFloat(),
            LocPosZ = r.GetFloat()
        };
    }

    public struct DreamEndedMessage
    {
        public string PresetName;
        public string OutcomeName;

        public void Serialize(NetWriter w)
        {
            w.Put(PresetName ?? "");
            w.Put(OutcomeName ?? "");
        }

        public static DreamEndedMessage Deserialize(NetReader r) => new DreamEndedMessage
        {
            PresetName = r.GetString(),
            OutcomeName = r.GetString()
        };
    }

    public struct DreamStartRequestMessage
    {
        public string PresetName;

        public void Serialize(NetWriter w) { w.Put(PresetName ?? ""); }
        public static DreamStartRequestMessage Deserialize(NetReader r) => new DreamStartRequestMessage
        {
            PresetName = r.GetString()
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
