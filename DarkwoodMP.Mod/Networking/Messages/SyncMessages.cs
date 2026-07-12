using UnityEngine;

namespace DWMPHorde.Networking
{
    public struct FlagSyncMessage
    {
        public string Name;
        public bool IsInt;
        public bool BoolValue;
        public int IntValue;

        public void Serialize(NetWriter w) { w.Put(Name ?? ""); w.Put(IsInt); w.Put(BoolValue); w.Put(IntValue); }
        public static FlagSyncMessage Deserialize(NetReader r) => new FlagSyncMessage { Name = r.GetString(), IsInt = r.GetBool(), BoolValue = r.GetBool(), IntValue = r.GetInt() };
    }

    public struct TradeSyncMessage
    {
        public string NpcName;
        public int ItemCount;
        public string[] ItemTypes;
        public int[] Amounts;

        public void Serialize(NetWriter w)
        {
            w.Put(NpcName ?? ""); w.Put(ItemCount);
            for (int i = 0; i < ItemCount; i++)
            { w.Put(ItemTypes?[i] ?? ""); w.Put(Amounts != null && i < Amounts.Length ? Amounts[i] : 1); }
        }

        public static TradeSyncMessage Deserialize(NetReader r)
        {
            var msg = new TradeSyncMessage { NpcName = r.GetString(), ItemCount = r.GetInt() };
            if (msg.ItemCount < 0 || msg.ItemCount > 4096) msg.ItemCount = 0;
            msg.ItemTypes = new string[msg.ItemCount]; msg.Amounts = new int[msg.ItemCount];
            for (int i = 0; i < msg.ItemCount; i++) { msg.ItemTypes[i] = r.GetString(); msg.Amounts[i] = r.GetInt(); }
            return msg;
        }
    }

    /// <summary>
    /// Absolute trader shop stock (type → total amount). Used for join bulk,
    /// post-trade, and host morning restock so all peers share one assortment.
    /// </summary>
    public struct TradeInventorySyncMessage
    {
        public string NpcName;
        public int ItemCount;
        public string[] ItemTypes;
        public int[] Amounts;

        public void Serialize(NetWriter w)
        {
            w.Put(NpcName ?? "");
            w.Put(ItemCount);
            for (int i = 0; i < ItemCount; i++)
            {
                w.Put(ItemTypes?[i] ?? "");
                w.Put(Amounts != null && i < Amounts.Length ? Amounts[i] : 0);
            }
        }

        public static TradeInventorySyncMessage Deserialize(NetReader r)
        {
            var msg = new TradeInventorySyncMessage { NpcName = r.GetString(), ItemCount = r.GetInt() };
            if (msg.ItemCount < 0 || msg.ItemCount > 4096) msg.ItemCount = 0;
            msg.ItemTypes = new string[msg.ItemCount];
            msg.Amounts = new int[msg.ItemCount];
            for (int i = 0; i < msg.ItemCount; i++)
            {
                msg.ItemTypes[i] = r.GetString();
                msg.Amounts[i] = r.GetInt();
            }
            return msg;
        }
    }

    public struct DialogOutcomeSyncMessage
    {
        public string NpcName;
        public int DecisionIndex;
        public string DialogueName;
        public int BoardIndex;
        /// <summary>DialogueButton.destDialogueName — host applies this node without matching UI.</summary>
        public string TargetDialogueName;

        public void Serialize(NetWriter w)
        {
            w.Put(NpcName ?? "");
            w.Put(DecisionIndex);
            w.Put(DialogueName ?? "");
            w.Put(BoardIndex);
            w.Put(TargetDialogueName ?? "");
        }
        public static DialogOutcomeSyncMessage Deserialize(NetReader r) => new DialogOutcomeSyncMessage
        {
            NpcName = r.GetString(),
            DecisionIndex = r.GetInt(),
            DialogueName = r.GetString(),
            BoardIndex = r.GetInt(),
            TargetDialogueName = r.GetString()
        };
    }

    /// <summary>
    /// NPC dialogue lock (0.9.2): client request or host grant/deny/release fan-out.
    /// IsRequest=true only on client→host acquire attempts.
    /// </summary>
    public struct DialogNpcLockMessage
    {
        public string NpcName;
        public int OwnerPlayerId;
        public bool Granted;
        public bool Release;
        public bool IsRequest;

        public void Serialize(NetWriter w)
        {
            w.Put(NpcName ?? "");
            w.Put(OwnerPlayerId);
            w.Put(Granted);
            w.Put(Release);
            w.Put(IsRequest);
        }

        public static DialogNpcLockMessage Deserialize(NetReader r) => new DialogNpcLockMessage
        {
            NpcName = r.GetString(),
            OwnerPlayerId = r.GetInt(),
            Granted = r.GetBool(),
            Release = r.GetBool(),
            IsRequest = r.GetBool()
        };
    }

    /// <summary>
    /// Yokyy DialogueSync v2 body: base64 tree snapshot (alreadyShown/disabled/specials/portrait + NPC wants/rep).
    /// </summary>
    public struct DialogTreeStateMessage
    {
        public string Payload;

        public void Serialize(NetWriter w) => w.Put(Payload ?? "");
        public static DialogTreeStateMessage Deserialize(NetReader r) => new DialogTreeStateMessage
        {
            Payload = r.GetString()
        };
    }

    public struct RemotePlayerForwardMessage
    {
        public int OriginalPlayerId;
        public byte InnerType;
        public byte[] InnerPayload;

        public void Serialize(NetWriter w) { w.Put(OriginalPlayerId); w.Put(InnerType); w.Put(InnerPayload); }
        public static RemotePlayerForwardMessage Deserialize(NetReader r) => new RemotePlayerForwardMessage { OriginalPlayerId = r.GetInt(), InnerType = r.GetByte(), InnerPayload = r.GetBytes() };
    }

    public enum EntitySoundType : byte
    {
        Growl = 0, Attack1 = 1, Attack2 = 2, Death = 3, Curious = 4,
        Aggressive = 5, Defensive = 6, Escaping = 7, Idle = 8, GetHit = 9,
    }

    public struct EntitySoundMessage
    {
        public short HostId;
        public EntitySoundType SoundType;
        public string LoopName;

        public void Serialize(NetWriter w) { w.Put(HostId); w.Put((byte)SoundType); w.Put(LoopName ?? string.Empty); }
        public static EntitySoundMessage Deserialize(NetReader r) => new EntitySoundMessage { HostId = r.GetShort(), SoundType = (EntitySoundType)r.GetByte(), LoopName = r.GetString() };
    }

    public struct EntityBurningMessage
    {
        public short EntityId;
        public bool IsBurning;
        public float BurnTime, Modifier, Interval;

        public void Serialize(NetWriter w) { w.Put(EntityId); w.Put(IsBurning); w.Put(BurnTime); w.Put(Modifier); w.Put(Interval); }
        public static EntityBurningMessage Deserialize(NetReader r) => new EntityBurningMessage { EntityId = r.GetShort(), IsBurning = r.GetBool(), BurnTime = r.GetFloat(), Modifier = r.GetFloat(), Interval = r.GetFloat() };
    }

    public struct LiquidStopBurningMessage
    {
        public float PosX, PosY, PosZ;
        public void Serialize(NetWriter w) { w.Put(PosX); w.Put(PosY); w.Put(PosZ); }
        public static LiquidStopBurningMessage Deserialize(NetReader r) => new LiquidStopBurningMessage { PosX = r.GetFloat(), PosY = r.GetFloat(), PosZ = r.GetFloat() };
    }

    public struct ExplosionSpawnObjectMessage
    {
        public string PrefabName;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ;

        public void Serialize(NetWriter w) { w.Put(PrefabName ?? ""); w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(RotX); w.Put(RotY); w.Put(RotZ); }
        public static ExplosionSpawnObjectMessage Deserialize(NetReader r) => new ExplosionSpawnObjectMessage { PrefabName = r.GetString(), PosX = r.GetFloat(), PosY = r.GetFloat(), PosZ = r.GetFloat(), RotX = r.GetFloat(), RotY = r.GetFloat(), RotZ = r.GetFloat() };
    }

    public struct WorkbenchLevelMessage
    {
        public int Level;
        public void Serialize(NetWriter w) => w.Put(Level);
        public static WorkbenchLevelMessage Deserialize(NetReader r) => new WorkbenchLevelMessage { Level = r.GetInt() };
    }

    public enum JournalItemKind : byte { Note = 0, Key = 1, QuestItem = 2, JournalEntry = 3 }

    public struct JournalItemMessage
    {
        public JournalItemKind Kind;
        public string Type;
        public void Serialize(NetWriter w) { w.Put((byte)Kind); w.Put(Type ?? ""); }
        public static JournalItemMessage Deserialize(NetReader r) => new JournalItemMessage { Kind = (JournalItemKind)r.GetByte(), Type = r.GetString() };
    }

    public struct SaveSyncMessage
    {
        public void Serialize(NetWriter w) { }
        public static SaveSyncMessage Deserialize(NetReader r) => new SaveSyncMessage();
    }

    public struct TimeSyncMessage
    {
        public int CurrentTime, Day;
        public bool IsAfterNight;
        public void Serialize(NetWriter w) { w.Put(CurrentTime); w.Put(Day); w.Put(IsAfterNight); }
        public static TimeSyncMessage Deserialize(NetReader r) => new TimeSyncMessage { CurrentTime = r.GetInt(), Day = r.GetInt(), IsAfterNight = r.GetBool() };
    }

    /// <summary>Client→host: post-sleep clock for host-authority forward adopt.</summary>
    public struct SleepEndRequestMessage
    {
        public int CurrentTime, Day;
        public bool IsAfterNight;

        public void Serialize(NetWriter w)
        {
            w.Put(CurrentTime);
            w.Put(Day);
            w.Put(IsAfterNight);
        }

        public static SleepEndRequestMessage Deserialize(NetReader r) => new SleepEndRequestMessage
        {
            CurrentTime = r.GetInt(),
            Day = r.GetInt(),
            IsAfterNight = r.GetBool()
        };
    }

    /// <summary>Client→host: leave-hideout / clear morning freeze (host endAfterNight).</summary>
    public struct AfterNightEndRequestMessage
    {
        public void Serialize(NetWriter w) { }
        public static AfterNightEndRequestMessage Deserialize(NetReader r) => new AfterNightEndRequestMessage();
    }

    /// <summary>
    /// Host→all: peer LAN endpoints for host-crash migration.
    /// Port is the session listen port (not ephemeral outbound). Address is IPv4 as seen by host.
    /// </summary>
    public struct PeerRosterMessage
    {
        public int HostPlayerId;
        public int SessionPort;
        public PeerRosterEntry[] Entries;

        public void Serialize(NetWriter w)
        {
            w.Put(HostPlayerId);
            w.Put(SessionPort);
            int n = Entries != null ? Entries.Length : 0;
            if (n > 32) n = 32;
            w.Put((byte)n);
            for (int i = 0; i < n; i++)
                Entries[i].Serialize(w);
        }

        public static PeerRosterMessage Deserialize(NetReader r)
        {
            var msg = new PeerRosterMessage
            {
                HostPlayerId = r.GetInt(),
                SessionPort = r.GetInt()
            };
            int n = r.AvailableBytes >= 1 ? r.GetByte() : 0;
            if (n < 0) n = 0;
            if (n > 32) n = 32;
            msg.Entries = new PeerRosterEntry[n];
            for (int i = 0; i < n; i++)
                msg.Entries[i] = PeerRosterEntry.Deserialize(r);
            return msg;
        }
    }

    public struct PeerRosterEntry
    {
        public int PlayerId;
        public string Address;
        /// <summary>Session listen port for promote/reconnect (same for all peers).</summary>
        public int Port;

        public void Serialize(NetWriter w)
        {
            w.Put(PlayerId);
            w.Put(Address ?? "");
            w.Put(Port);
        }

        public static PeerRosterEntry Deserialize(NetReader r) => new PeerRosterEntry
        {
            PlayerId = r.GetInt(),
            Address = r.GetString(),
            Port = r.GetInt()
        };
    }

    /// <summary>Host→all: graceful leave; elect takes host grant.</summary>
    public struct HostHandoffMessage
    {
        public int ElectPlayerId;
        public int SessionPort;

        public void Serialize(NetWriter w)
        {
            w.Put(ElectPlayerId);
            w.Put(SessionPort);
        }

        public static HostHandoffMessage Deserialize(NetReader r) => new HostHandoffMessage
        {
            ElectPlayerId = r.GetInt(),
            SessionPort = r.GetInt()
        };
    }

    /// <summary>
    /// Workbench open lock: client request or host grant/deny/release fan-out.
    /// IsRequest=true only on client→host acquire attempts. Key = pos-stable workbench id.
    /// </summary>
    public struct WorkbenchLockMessage
    {
        public string WorkbenchKey;
        public int OwnerPlayerId;
        public bool Granted;
        public bool Release;
        public bool IsRequest;

        public void Serialize(NetWriter w)
        {
            w.Put(WorkbenchKey ?? "");
            w.Put(OwnerPlayerId);
            w.Put(Granted);
            w.Put(Release);
            w.Put(IsRequest);
        }

        public static WorkbenchLockMessage Deserialize(NetReader r) => new WorkbenchLockMessage
        {
            WorkbenchKey = r.GetString(),
            OwnerPlayerId = r.GetInt(),
            Granted = r.GetBool(),
            Release = r.GetBool(),
            IsRequest = r.GetBool()
        };
    }

    public struct ClientStateBackupMessage
    {
        public string JsonData;
        public void Serialize(NetWriter w) { w.Put(JsonData ?? ""); }
        public static ClientStateBackupMessage Deserialize(NetReader r) => new ClientStateBackupMessage { JsonData = r.GetString() };
    }

    public struct ReputationSyncMessage
    {
        public string NpcName;
        public int Reputation;
        public void Serialize(NetWriter w) { w.Put(NpcName ?? ""); w.Put(Reputation); }
        public static ReputationSyncMessage Deserialize(NetReader r) => new ReputationSyncMessage { NpcName = r.GetString(), Reputation = r.GetInt() };
    }

    public struct ScenarioSyncMessage
    {
        public string ScenarioName;
        public void Serialize(NetWriter w) { w.Put(ScenarioName ?? string.Empty); }
        public static ScenarioSyncMessage Deserialize(NetReader r) => new ScenarioSyncMessage { ScenarioName = r.GetString() };
    }

    public struct ScenarioEventFiredMessage
    {
        public int NightId, EventIndex;
        public void Serialize(NetWriter w) { w.Put(NightId); w.Put(EventIndex); }
        public static ScenarioEventFiredMessage Deserialize(NetReader r) => new ScenarioEventFiredMessage { NightId = r.GetInt(), EventIndex = r.GetInt() };
    }

    public struct MapMarkerMessage
    {
        public float PosX, PosY, PosZ;
        public int PlayerId;
        public void Serialize(NetWriter w) { w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(PlayerId); }
        public static MapMarkerMessage Deserialize(NetReader r) => new MapMarkerMessage
        {
            PosX = r.GetFloat(), PosY = r.GetFloat(), PosZ = r.GetFloat(), PlayerId = r.GetInt()
        };
    }

    public struct MapMarkerRemoveMessage
    {
        public float PosX, PosY, PosZ;
        public int PlayerId;
        public void Serialize(NetWriter w) { w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(PlayerId); }
        public static MapMarkerRemoveMessage Deserialize(NetReader r) => new MapMarkerRemoveMessage
        {
            PosX = r.GetFloat(), PosY = r.GetFloat(), PosZ = r.GetFloat(), PlayerId = r.GetInt()
        };
    }

    public struct MapElementDiscoveredMessage
    {
        public string ElementName;
        public void Serialize(NetWriter w) => w.Put(ElementName ?? "");
        public static MapElementDiscoveredMessage Deserialize(NetReader r) => new MapElementDiscoveredMessage { ElementName = r.GetString() };
    }

    public struct OxygenTankStashMessage
    {
        public void Serialize(NetWriter w) { }
        public static OxygenTankStashMessage Deserialize(NetReader r) => new OxygenTankStashMessage();
    }

    public struct CompressorTankConvertMessage
    {
        public void Serialize(NetWriter w) { }
        public static CompressorTankConvertMessage Deserialize(NetReader r) => new CompressorTankConvertMessage();
    }

    public struct JournalBulkSyncMessage
    {
        public string[] NoteTypes, KeyTypes, QuestItemTypes, JournalEntryTypes;

        public void Serialize(NetWriter w)
        {
            WriteArray(w, NoteTypes); WriteArray(w, KeyTypes);
            WriteArray(w, QuestItemTypes); WriteArray(w, JournalEntryTypes);
        }

        public static JournalBulkSyncMessage Deserialize(NetReader r) => new JournalBulkSyncMessage
        {
            NoteTypes = ReadArray(r),
            KeyTypes = ReadArray(r),
            QuestItemTypes = ReadArray(r),
            JournalEntryTypes = ReadArray(r)
        };

        static void WriteArray(NetWriter w, string[] arr)
        {
            int count = arr?.Length ?? 0; w.Put(count);
            for (int i = 0; i < count; i++) w.Put(arr?[i] ?? "");
        }
        static string[] ReadArray(NetReader r)
        {
            int count = r.GetInt();
            if (count < 0 || count > 4096) count = 0;
            var arr = new string[count];
            for (int i = 0; i < count; i++) arr[i] = r.GetString();
            return arr;
        }
    }

    public struct ShadowEventMessage
    {
        public void Serialize(NetWriter w) { }
        public static ShadowEventMessage Deserialize(NetReader r) => new ShadowEventMessage();
    }

    /// <summary>Client→host: request a NightShadows perk wave around the requester's proxy.</summary>
    public struct NightShadowSpawnRequestMessage
    {
        public void Serialize(NetWriter w) { }
        public static NightShadowSpawnRequestMessage Deserialize(NetReader r) => new NightShadowSpawnRequestMessage();
    }

    public struct ShadowSpawnMessage
    {
        public short ShadowId;
        public byte ShadowType;
        public float PosX, PosY, PosZ, RotY, DistanceToPlayer;
        public byte Flags;

        public void Serialize(NetWriter w) { w.Put(ShadowId); w.Put(ShadowType); w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(RotY); w.Put(DistanceToPlayer); w.Put(Flags); }
        public static ShadowSpawnMessage Deserialize(NetReader r) => new ShadowSpawnMessage { ShadowId = r.GetShort(), ShadowType = r.GetByte(), PosX = r.GetFloat(), PosY = r.GetFloat(), PosZ = r.GetFloat(), RotY = r.GetFloat(), DistanceToPlayer = r.GetFloat(), Flags = r.GetByte() };
    }

    public struct ShadowStateUpdateMessage
    {
        public short ShadowId;
        public float PosX, PosY, PosZ, RotY, DistanceToPlayer;
        public byte Flags;

        public void Serialize(NetWriter w) { w.Put(ShadowId); w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(RotY); w.Put(DistanceToPlayer); w.Put(Flags); }
        public static ShadowStateUpdateMessage Deserialize(NetReader r) => new ShadowStateUpdateMessage { ShadowId = r.GetShort(), PosX = r.GetFloat(), PosY = r.GetFloat(), PosZ = r.GetFloat(), RotY = r.GetFloat(), DistanceToPlayer = r.GetFloat(), Flags = r.GetByte() };
    }

    public struct FlagBulkSyncMessage
    {
        public int FlagCount;
        public string[] FlagNames;
        public bool[] FlagIsTrue;
        public int[] FlagAmounts;

        public void Serialize(NetWriter w)
        {
            w.Put(FlagCount);
            for (int i = 0; i < FlagCount; i++)
            {
                w.Put(FlagNames?[i] ?? "");
                w.Put(FlagIsTrue != null && i < FlagIsTrue.Length && FlagIsTrue[i]);
                w.Put(FlagAmounts != null && i < FlagAmounts.Length ? FlagAmounts[i] : 0);
            }
        }

        public static FlagBulkSyncMessage Deserialize(NetReader r)
        {
            var msg = new FlagBulkSyncMessage { FlagCount = r.GetInt() };
            if (msg.FlagCount < 0 || msg.FlagCount > 4096) msg.FlagCount = 0;
            msg.FlagNames = new string[msg.FlagCount];
            msg.FlagIsTrue = new bool[msg.FlagCount];
            msg.FlagAmounts = new int[msg.FlagCount];
            for (int i = 0; i < msg.FlagCount; i++)
            {
                msg.FlagNames[i] = r.GetString();
                msg.FlagIsTrue[i] = r.GetBool();
                msg.FlagAmounts[i] = r.GetInt();
            }
            return msg;
        }
    }

    public struct ReputationBulkSyncMessage
    {
        public int NpcCount;
        public string[] NpcNames;
        public int[] Reputations;
        public bool[] Dead;

        public void Serialize(NetWriter w)
        {
            w.Put(NpcCount);
            for (int i = 0; i < NpcCount; i++)
            {
                w.Put(NpcNames?[i] ?? "");
                w.Put(Reputations != null && i < Reputations.Length ? Reputations[i] : 0);
                w.Put(Dead != null && i < Dead.Length && Dead[i]);
            }
        }

        public static ReputationBulkSyncMessage Deserialize(NetReader r)
        {
            var msg = new ReputationBulkSyncMessage { NpcCount = r.GetInt() };
            if (msg.NpcCount < 0 || msg.NpcCount > 4096) msg.NpcCount = 0;
            msg.NpcNames = new string[msg.NpcCount];
            msg.Reputations = new int[msg.NpcCount];
            msg.Dead = new bool[msg.NpcCount];
            for (int i = 0; i < msg.NpcCount; i++)
            {
                msg.NpcNames[i] = r.GetString();
                msg.Reputations[i] = r.GetInt();
                msg.Dead[i] = r.GetBool();
            }
            return msg;
        }
    }

    public struct HideoutStateSyncMessage
    {
        public int OvenCount;
        public float[] PosX, PosY, PosZ;
        public bool[] IsOn;

        public void Serialize(NetWriter w)
        {
            w.Put(OvenCount);
            for (int i = 0; i < OvenCount; i++)
            {
                w.Put(PosX != null && i < PosX.Length ? PosX[i] : 0f);
                w.Put(PosY != null && i < PosY.Length ? PosY[i] : 0f);
                w.Put(PosZ != null && i < PosZ.Length ? PosZ[i] : 0f);
                w.Put(IsOn != null && i < IsOn.Length && IsOn[i]);
            }
        }

        public static HideoutStateSyncMessage Deserialize(NetReader r)
        {
            var msg = new HideoutStateSyncMessage { OvenCount = r.GetInt() };
            if (msg.OvenCount < 0 || msg.OvenCount > 4096) msg.OvenCount = 0;
            msg.PosX = new float[msg.OvenCount];
            msg.PosY = new float[msg.OvenCount];
            msg.PosZ = new float[msg.OvenCount];
            msg.IsOn = new bool[msg.OvenCount];
            for (int i = 0; i < msg.OvenCount; i++)
            {
                msg.PosX[i] = r.GetFloat();
                msg.PosY[i] = r.GetFloat();
                msg.PosZ[i] = r.GetFloat();
                msg.IsOn[i] = r.GetBool();
            }
            return msg;
        }
    }

    public struct MapStateSyncMessage
    {
        public int MarkerCount, DiscoveryCount;
        public float[] MarkerPosX, MarkerPosY, MarkerPosZ;
        public int[] MarkerPlayerIds;
        public string[] MarkerTexts;
        public string[] DiscoveryElementNames;

        public void Serialize(NetWriter w)
        {
            w.Put(MarkerCount);
            for (int i = 0; i < MarkerCount; i++)
            {
                w.Put(MarkerPosX != null && i < MarkerPosX.Length ? MarkerPosX[i] : 0f);
                w.Put(MarkerPosY != null && i < MarkerPosY.Length ? MarkerPosY[i] : 0f);
                w.Put(MarkerPosZ != null && i < MarkerPosZ.Length ? MarkerPosZ[i] : 0f);
                w.Put(MarkerPlayerIds != null && i < MarkerPlayerIds.Length ? MarkerPlayerIds[i] : 1);
                w.Put(MarkerTexts?[i] ?? "");
            }
            w.Put(DiscoveryCount);
            for (int i = 0; i < DiscoveryCount; i++)
                w.Put(DiscoveryElementNames?[i] ?? "");
        }

        public static MapStateSyncMessage Deserialize(NetReader r)
        {
            var msg = new MapStateSyncMessage { MarkerCount = r.GetInt() };
            if (msg.MarkerCount < 0 || msg.MarkerCount > 4096) msg.MarkerCount = 0;
            msg.MarkerPosX = new float[msg.MarkerCount];
            msg.MarkerPosY = new float[msg.MarkerCount];
            msg.MarkerPosZ = new float[msg.MarkerCount];
            msg.MarkerPlayerIds = new int[msg.MarkerCount];
            msg.MarkerTexts = new string[msg.MarkerCount];
            for (int i = 0; i < msg.MarkerCount; i++)
            {
                msg.MarkerPosX[i] = r.GetFloat();
                msg.MarkerPosY[i] = r.GetFloat();
                msg.MarkerPosZ[i] = r.GetFloat();
                msg.MarkerPlayerIds[i] = r.GetInt();
                msg.MarkerTexts[i] = r.GetString();
            }
            msg.DiscoveryCount = r.GetInt();
            if (msg.DiscoveryCount < 0 || msg.DiscoveryCount > 4096) msg.DiscoveryCount = 0;
            msg.DiscoveryElementNames = new string[msg.DiscoveryCount];
            for (int i = 0; i < msg.DiscoveryCount; i++)
                msg.DiscoveryElementNames[i] = r.GetString();
            return msg;
        }
    }

    public struct PlayerSkillsSyncMessage
    {
        public int Experience;
        public int CurrentLevel;
        public int SkillCount;
        public string[] SkillTypes;
        public bool[] SkillChosen;

        public void Serialize(NetWriter w)
        {
            w.Put(Experience); w.Put(CurrentLevel);
            w.Put(SkillCount);
            for (int i = 0; i < SkillCount; i++)
            {
                w.Put(SkillTypes?[i] ?? "");
                w.Put(SkillChosen != null && i < SkillChosen.Length && SkillChosen[i]);
            }
        }

        public static PlayerSkillsSyncMessage Deserialize(NetReader r)
        {
            var msg = new PlayerSkillsSyncMessage
            {
                Experience = r.GetInt(),
                CurrentLevel = r.GetInt(),
                SkillCount = r.GetInt()
            };
            if (msg.SkillCount < 0 || msg.SkillCount > 4096) msg.SkillCount = 0;
            msg.SkillTypes = new string[msg.SkillCount];
            msg.SkillChosen = new bool[msg.SkillCount];
            for (int i = 0; i < msg.SkillCount; i++)
            {
                msg.SkillTypes[i] = r.GetString();
                msg.SkillChosen[i] = r.GetBool();
            }
            return msg;
        }
    }

    public struct WeatherSyncMessage
    {
        public bool Raining, RainToday, PreRainLightning, FogFadedOutToday, FogIsActive;
        public float TimeToStart, LightningTime, PreRainLightningTime, Duration;
        public float TimeToFadeInFog_Hours, TimeToFadeOutFog_Hours;
        public int TimeToFadeInFog_Day, TimeToFadeOutFog_Day;

        public void Serialize(NetWriter w)
        {
            w.Put(Raining); w.Put(RainToday); w.Put(TimeToStart); w.Put(LightningTime);
            w.Put(PreRainLightning); w.Put(PreRainLightningTime); w.Put(Duration);
            w.Put(TimeToFadeInFog_Hours); w.Put(TimeToFadeInFog_Day);
            w.Put(TimeToFadeOutFog_Hours); w.Put(TimeToFadeOutFog_Day);
            w.Put(FogFadedOutToday); w.Put(FogIsActive);
        }

        public static WeatherSyncMessage Deserialize(NetReader r) => new WeatherSyncMessage
        {
            Raining = r.GetBool(),
            RainToday = r.GetBool(),
            TimeToStart = r.GetFloat(),
            LightningTime = r.GetFloat(),
            PreRainLightning = r.GetBool(),
            PreRainLightningTime = r.GetFloat(),
            Duration = r.GetFloat(),
            TimeToFadeInFog_Hours = r.GetFloat(),
            TimeToFadeInFog_Day = r.GetInt(),
            TimeToFadeOutFog_Hours = r.GetFloat(),
            TimeToFadeOutFog_Day = r.GetInt(),
            FogFadedOutToday = r.GetBool(),
            FogIsActive = r.GetBool()
        };
    }
}
