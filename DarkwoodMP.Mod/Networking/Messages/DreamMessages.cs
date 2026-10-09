using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    internal static class DreamWire
    {
        internal const int MaxPresets = 256;

        internal static string[] ReadPresets(NetReader r)
        {
            int n = r.GetInt();
            if (n < 0 || n > MaxPresets)
                throw new System.IO.InvalidDataException("completed preset count " + n);
            var presets = new string[n];
            for (int i = 0; i < n; i++)
                presets[i] = r.GetString();
            return presets;
        }
    }

    public struct DreamStartedMessage
    {
        public string PresetName;
        public float LocPosX, LocPosY, LocPosZ;
        /// <summary>Optional trailer containing session and completion state.</summary>
        public int SessionId;
        public byte LvlFlags;
        public string[] CompletedPresets;
        /// <summary>
        /// The entry played the startTransition video (skill / sleep dreams). A dialogue or
        /// game-event start has only a black fade; peers then skip the video too.
        /// </summary>
        public bool EntryTransition;

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
            w.Put(EntryTransition);
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
            msg.SessionId = r.GetInt();
            msg.LvlFlags = r.GetByte();
            msg.CompletedPresets = DreamWire.ReadPresets(r);
            msg.EntryTransition = r.GetBool();
            return msg;
        }

        public static DreamStartedMessage Build(string preset, float x, float y, float z) => new DreamStartedMessage
        {
            PresetName = preset ?? "",
            LocPosX = x,
            LocPosY = y,
            LocPosZ = z,
            SessionId = DreamSession.SessionId,
            LvlFlags = DreamSession.LevelBits,
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
            msg.SessionId = r.GetInt();
            msg.LvlFlags = r.GetByte();
            msg.CompletedPresets = DreamWire.ReadPresets(r);
            return msg;
        }

        public static DreamEndedMessage Build(string preset, string outcome) => new DreamEndedMessage
        {
            PresetName = preset ?? "",
            OutcomeName = outcome ?? "",
            SessionId = DreamSession.SessionId,
            LvlFlags = DreamSession.LevelBits,
            CompletedPresets = DreamSession.GetCompletedPresets()
        };
    }

    public struct DreamStartRequestMessage
    {
        public string PresetName;
        public int RequestId;
        /// <summary>Optional trailer: client hadDreamAtLvl* for host union before prepare.</summary>
        public byte LvlFlags;

        public void Serialize(NetWriter w)
        {
            w.Put(PresetName ?? "");
            w.Put(RequestId);
            w.Put(LvlFlags);
        }

        public static DreamStartRequestMessage Deserialize(NetReader r)
        {
            var msg = new DreamStartRequestMessage { PresetName = r.GetString() };
            msg.RequestId = r.GetInt();
            msg.LvlFlags = r.GetByte();
            return msg;
        }
    }

    public struct DreamSessionBulkMessage
    {
        public byte LvlFlags;
        public string[] CompletedPresets;
        public bool SessionActive;
        public string ActivePreset;
        public int SessionId;
        /// <summary>live pad position so a joiner can enter the dream.</summary>
        public bool HasPadPosition;
        public float PadX, PadY, PadZ;

        public void Serialize(NetWriter w)
        {
            w.Put(LvlFlags);
            w.Put(SessionActive);
            w.Put(ActivePreset ?? "");
            string[] done = CompletedPresets ?? System.Array.Empty<string>();
            w.Put(done.Length);
            for (int i = 0; i < done.Length; i++)
                w.Put(done[i] ?? "");
            w.Put(SessionId);
            w.Put(HasPadPosition);
            if (HasPadPosition)
            {
                w.Put(PadX);
                w.Put(PadY);
                w.Put(PadZ);
            }
        }

        public static DreamSessionBulkMessage Deserialize(NetReader r)
        {
            var msg = new DreamSessionBulkMessage
            {
                LvlFlags = r.GetByte(),
                SessionActive = r.GetBool(),
                ActivePreset = r.GetString(),
            };
            msg.CompletedPresets = DreamWire.ReadPresets(r);
            msg.SessionId = r.GetInt();
            msg.HasPadPosition = r.GetBool();
            if (msg.HasPadPosition)
            {
                msg.PadX = r.GetFloat();
                msg.PadY = r.GetFloat();
                msg.PadZ = r.GetFloat();
            }
            return msg;
        }

        public static DreamSessionBulkMessage FromLocal()
        {
            var msg = new DreamSessionBulkMessage
            {
                LvlFlags = DreamSession.LevelBits,
                SessionActive = DreamSession.IsActive,
                ActivePreset = DreamSession.PresetName ?? "",
                CompletedPresets = DreamSession.GetCompletedPresets(),
                SessionId = DreamSession.SessionId
            };
            var loc = Dreams.Instance != null ? Dreams.Instance.dreamLocation : null;
            // Only a live dream. prepareDream's early roll bulk must not ship a
            // leftover pad and pull peers in before DreamStarted.
            if (msg.SessionActive && Dreams.Instance != null && Dreams.Instance.dreaming && loc != null)
            {
                Vector3 p = loc.transform.position;
                msg.HasPadPosition = true;
                msg.PadX = p.x;
                msg.PadY = p.y;
                msg.PadZ = p.z;
            }
            return msg;
        }
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
            SessionId = r.GetInt()
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
        /// <summary>
        /// Host→clients: the whole party is down and every night death was a
        /// permadeath-eligible one (nightmare, or hard with lives spent). Every
        /// peer runs the real permadeath outcome instead of a morning respawn.
        /// </summary>
        public bool PartyWipe;
        /// <summary>
        /// Host→one rejoining client: it died this night before it left, so it is down again
        /// until the morning (spectating), at <see cref="PosX"/>/<see cref="PosY"/>/<see cref="PosZ"/>.
        /// Protocol 46.
        /// </summary>
        public bool ResumeDead;
        public float PosX, PosY, PosZ;

        public void Serialize(NetWriter w)
        {
            w.Put(IsDead);
            w.Put(AllDeadTrigger);
            w.Put(PartyWipe);
            w.Put(ResumeDead);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
        }

        public static NightDeathStateMessage Deserialize(NetReader r) => new NightDeathStateMessage
        {
            IsDead = r.GetBool(),
            AllDeadTrigger = r.GetBool(),
            PartyWipe = r.GetBool(),
            ResumeDead = r.GetBool(),
            PosX = r.GetFloat(), PosY = r.GetFloat(), PosZ = r.GetFloat()
        };
    }

    /// <summary>
    /// Host→all clients on the morning edge. Peers that are still night-dead are
    /// released (spectator off, sent home); everyone drops night-death bookkeeping.
    /// </summary>
    public struct NightDeathReleaseMessage
    {
        public int Day;

        public void Serialize(NetWriter w) { w.Put(Day); }
        public static NightDeathReleaseMessage Deserialize(NetReader r) => new NightDeathReleaseMessage
        {
            Day = r.GetInt()
        };
    }

    /// <summary>
    /// Host→one surviving peer: its own copy of the vanilla startAfterNight reward.
    /// Trader standing is per-player in this mod, so each peer applies it locally.
    /// </summary>
    public struct MorningRewardMessage
    {
        public int Day;
        public string TraderName;
        public int Reputation;
        public float Saturation;
        public bool ShowTraderHelp;

        public void Serialize(NetWriter w)
        {
            w.Put(Day);
            w.Put(TraderName);
            w.Put(Reputation);
            w.Put(Saturation);
            w.Put(ShowTraderHelp);
        }

        public static MorningRewardMessage Deserialize(NetReader r) => new MorningRewardMessage
        {
            Day = r.GetInt(),
            TraderName = r.GetString(),
            Reputation = r.GetInt(),
            Saturation = r.GetFloat(),
            ShowTraderHelp = r.GetBool()
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
        /// <summary>
        /// Host is collecting <see cref="ChapterShareAckMessage"/> and will answer with
        /// <see cref="ChapterLoadGoMessage"/>; the client must wait for that go before it
        /// drops the link. False for a single-peer world resync (client loads on its own).
        /// Optional trailing byte.
        /// </summary>
        public bool AckRequired;
        /// <summary>
        /// The party starts the chapter over (permadeath "start over"): the chapter save resets
        /// every character, so every character snapshot is discarded. Protocol 33.
        /// </summary>
        public bool StartOver;

        public void Serialize(NetWriter w)
        {
            w.Put(ChapterId);
            w.Put(LoadChapterSave);
            w.Put(ExpectWorldShare);
            w.Put(AckRequired);
            w.Put(StartOver);
        }

        public static ChapterTransitionMessage Deserialize(NetReader r) => new ChapterTransitionMessage
        {
            ChapterId = r.GetInt(),
            LoadChapterSave = r.GetBool(),
            ExpectWorldShare = r.GetBool(),
            AckRequired = r.GetBool(),
            StartOver = r.GetBool()
        };
    }

    /// <summary>Client→host: outcome of a chapter world share (see <see cref="Status"/>).</summary>
    public struct ChapterShareAckMessage
    {
        /// <summary>
        /// World package received, verified and inflated in memory; ready to commit. The client's
        /// save slot is untouched until the host's go (see <see cref="ChapterLoadGoMessage"/>).
        /// </summary>
        public const byte StatusCommitted = 0;
        /// <summary>Package missing, corrupt, or could not be written. Host may re-share.</summary>
        public const byte StatusFailed = 1;
        /// <summary>Client is on the title screen; it does not take part in the transition.</summary>
        public const byte StatusNotInWorld = 2;
        /// <summary>
        /// Progress heartbeat while the package is still arriving. Carries no verdict; the host only
        /// uses it to push this peer's confirmation deadline out so a slow link is not refused.
        /// </summary>
        public const byte StatusReceiving = 3;

        public int ChapterId;
        public byte Status;
        public string Reason;
        /// <summary>
        /// The <see cref="WorldSaveBeginMessage.SharePass"/> this verdict is about. The host ignores a
        /// Committed / Failed ack from an older pass (0 = not carried; NotInWorld needs none).
        /// </summary>
        public int SharePass;

        public void Serialize(NetWriter w)
        {
            w.Put(ChapterId);
            w.Put(Status);
            w.Put(Reason ?? string.Empty);
            w.Put(SharePass);
        }

        public static ChapterShareAckMessage Deserialize(NetReader r)
        {
            var msg = new ChapterShareAckMessage
            {
                ChapterId = r.GetInt(),
                Status = r.GetByte(),
                Reason = r.GetString()
            };
            msg.SharePass = r.GetInt();
            return msg;
        }
    }

    /// <summary>
    /// Host→client: all acks are in (Proceed=true → load the chapter now), or the client
    /// must leave without loading (Proceed=false, <see cref="Reason"/> is player-visible).
    /// </summary>
    public struct ChapterLoadGoMessage
    {
        public int ChapterId;
        public bool Proceed;
        public string Reason;

        public void Serialize(NetWriter w)
        {
            w.Put(ChapterId);
            w.Put(Proceed);
            w.Put(Reason ?? string.Empty);
        }

        public static ChapterLoadGoMessage Deserialize(NetReader r) => new ChapterLoadGoMessage
        {
            ChapterId = r.GetInt(),
            Proceed = r.GetBool(),
            Reason = r.GetString()
        };
    }

    /// <summary>
    /// Host-authoritative cutscene control.
    /// Action: 0 = begin, 1 = end, 2 = skip transition, 3 = dream entry video (pre-prepare),
    /// 4/5 = prologue start/end, 6 = dream entry cancelled (start request refused).
    /// </summary>
    public struct CutsceneSyncMessage
    {
        public const byte ActionBegin = 0;
        public const byte ActionEnd = 1;
        public const byte ActionSkipTransition = 2;
        /// <summary>Peer started Dreams.startTransition (video before prepareDream).</summary>
        public const byte ActionDreamEntryTransition = 3;
        // 4 and 5 (shared opening movie start / end) retired: each player plays its own prologue.
        /// <summary>Host: the start request behind the last entry movie was refused; stop waiting for it.</summary>
        public const byte ActionDreamEntryCancel = 6;

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

    /// <summary>
    /// Host→client: dream-pad Item collider isTrigger parity (lamp walk-through vs bell solid).
    /// Optional; protocol stays 19.
    /// </summary>
    public struct DreamPropColliderMessage
    {
        public struct Entry
        {
            public string Name;
            public float PosX, PosY, PosZ;
            public bool IsTrigger;
        }

        public Entry[] Entries;

        public void Serialize(NetWriter w)
        {
            Entry[] e = Entries ?? System.Array.Empty<Entry>();
            int n = e.Length > 64 ? 64 : e.Length;
            w.Put(n);
            for (int i = 0; i < n; i++)
            {
                w.Put(e[i].Name ?? "");
                w.Put(e[i].PosX);
                w.Put(e[i].PosY);
                w.Put(e[i].PosZ);
                w.Put(e[i].IsTrigger);
            }
        }

        public static DreamPropColliderMessage Deserialize(NetReader r)
        {
            int n = r.GetInt();
            if (n < 0) n = 0;
            if (n > 64) n = 64;
            var entries = new Entry[n];
            for (int i = 0; i < n; i++)
            {
                entries[i] = new Entry
                {
                    Name = r.GetString(),
                    PosX = r.GetFloat(),
                    PosY = r.GetFloat(),
                    PosZ = r.GetFloat(),
                    IsTrigger = r.GetBool()
                };
            }
            return new DreamPropColliderMessage { Entries = entries };
        }
    }
}
