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
        /// <summary>True when this stock belongs to the dream-pad copy, not the overworld twin.</summary>
        public bool InDream;
        /// <summary>Which body, when two traders share a name. Missing on old packets.</summary>
        public bool HasPos;
        public float PosX, PosY, PosZ;
        /// <summary>
        /// 0.8.62: per-entry recipe flag. ItemTypes stores recipeFor when true
        /// (vanilla InvItemClass ctor flips type to "recipe"). Null/absent = pre-0.8.62.
        /// </summary>
        public bool[] IsRecipe;
        /// <summary>0.8.62: absolute durability per entry. Null/absent = pre-0.8.62.</summary>
        public float[] Durabilities;
        /// <summary>0.8.68: per-entry workbench upgrade names. Null/absent = pre-0.8.68.</summary>
        public string[][] Upgrades;
        /// <summary>0.8.68: per-entry shouldBeActive (flashlight on). Absent = pre-0.8.68.</summary>
        public bool[] ShouldBeActive;

        public void Serialize(NetWriter w)
        {
            w.Put(NpcName ?? "");
            w.Put(ItemCount);
            for (int i = 0; i < ItemCount; i++)
            {
                w.Put(ItemTypes?[i] ?? "");
                w.Put(Amounts != null && i < Amounts.Length ? Amounts[i] : 0);
            }
            w.Put(InDream);
            w.Put(HasPos);
            w.Put(PosX);
            w.Put(PosY);
            w.Put(PosZ);
            // Recipe/durability trailer (0.8.62). Always written so dual-deploy peers match.
            for (int i = 0; i < ItemCount; i++)
                w.Put(IsRecipe != null && i < IsRecipe.Length && IsRecipe[i]);
            for (int i = 0; i < ItemCount; i++)
                w.Put(Durabilities != null && i < Durabilities.Length ? Durabilities[i] : 0f);
            // 0.8.68 upgrade trailer (count+names per entry). Dual-deploy writes always.
            for (int i = 0; i < ItemCount; i++)
                DWMPHorde.Sync.InvItemUpgradeWire.Write(w, Upgrades != null && i < Upgrades.Length ? Upgrades[i] : null);
            // 0.8.68 shouldBeActive trailer (one bool per entry). Dual-deploy writes always.
            for (int i = 0; i < ItemCount; i++)
                w.Put(ShouldBeActive != null && i < ShouldBeActive.Length && ShouldBeActive[i]);
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
            if (r.AvailableBytes >= 1)
                msg.InDream = r.GetBool();
            if (r.AvailableBytes >= 13)
            {
                msg.HasPos = r.GetBool();
                msg.PosX = r.GetFloat();
                msg.PosY = r.GetFloat();
                msg.PosZ = r.GetFloat();
            }
            // 0.8.62 trailer: ItemCount bools + ItemCount floats.
            int trailer = msg.ItemCount * 5;
            if (msg.ItemCount > 0 && r.AvailableBytes >= trailer)
            {
                msg.IsRecipe = new bool[msg.ItemCount];
                msg.Durabilities = new float[msg.ItemCount];
                for (int i = 0; i < msg.ItemCount; i++)
                    msg.IsRecipe[i] = r.GetBool();
                for (int i = 0; i < msg.ItemCount; i++)
                    msg.Durabilities[i] = r.GetFloat();
            }
            // 0.8.68 upgrade trailer.
            if (msg.ItemCount > 0 && r.AvailableBytes >= 1)
            {
                msg.Upgrades = new string[msg.ItemCount][];
                for (int i = 0; i < msg.ItemCount; i++)
                    msg.Upgrades[i] = DWMPHorde.Sync.InvItemUpgradeWire.TryRead(r);
            }
            // 0.8.68 shouldBeActive trailer.
            if (msg.ItemCount > 0 && r.AvailableBytes >= msg.ItemCount)
            {
                msg.ShouldBeActive = new bool[msg.ItemCount];
                for (int i = 0; i < msg.ItemCount; i++)
                    msg.ShouldBeActive[i] = r.GetBool();
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
        /// <summary>DialogueButton.destDialogueName. The host applies this node without matching UI.</summary>
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

    /// <summary>Compact per-peer bag presence for EventTrigger haveItem (protocol 25).</summary>
    public struct PeerHasItemMessage
    {
        public int PlayerId;
        public string ItemType;
        public int Amount;

        public void Serialize(NetWriter w)
        {
            w.Put(PlayerId);
            w.Put(ItemType ?? "");
            w.Put(Amount);
        }

        public static PeerHasItemMessage Deserialize(NetReader r) => new PeerHasItemMessage
        {
            PlayerId = r.GetInt(),
            ItemType = r.GetString(),
            Amount = r.GetInt()
        };
    }

    /// <summary>
    /// NPC dialogue lock: client request or host grant, deny, or release
    /// fan-out. IsRequest is true only on client→host acquire attempts.
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

        public void Serialize(NetWriter w) => w.PutLongString(Payload);
        public static DialogTreeStateMessage Deserialize(NetReader r) => new DialogTreeStateMessage
        {
            Payload = r.GetLongString()
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
        /// <summary>Vanilla runAway stinger (playSingleInstance).</summary>
        EscapingStart = 10,
        /// <summary>Vanilla runAway crow overlay (play).</summary>
        EscapingStart2 = 11,
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

    public enum JournalItemKind : byte { Note = 0, Key = 1, QuestItem = 2, JournalEntry = 3, Remove = 4, Location = 5 }

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
        // Long-string framing: a full inventory/skills/journal backup can pass the 65535-byte limit of
        // Put(string), which wrapped the length and corrupted the packet.
        public void Serialize(NetWriter w) { w.PutLongString(JsonData); }
        public static ClientStateBackupMessage Deserialize(NetReader r) => new ClientStateBackupMessage { JsonData = r.GetLongString() };
    }

    public struct ReputationSyncMessage
    {
        public string NpcName;
        public int Reputation;
        /// <summary>0.8.93: host Flags.NPCState.attackedID. Absent = legacy rep-only packet.</summary>
        public bool HasAttackedId;
        public int AttackedId;
        /// <summary>0.8.94: host Flags.NPCState.dead + deadID. Absent = do not touch death fields.</summary>
        public bool HasDead;
        public bool Dead;
        public int DeadId;
        /// <summary>0.8.96: NPC.portraitType after GameEvent CharacterModify. Absent = leave portrait.</summary>
        public bool HasPortrait;
        public int PortraitType;
        /// <summary>Vanilla activeModifier: also write characterDialogue.portraitType.</summary>
        public bool ApplyDialoguePortrait;
        public float PosX, PosY, PosZ;
        /// <summary>
        /// 0.8.97: Character.animationLibraryOverride after GameEvent CharacterModify.
        /// Absent = leave library. String is the Resources path vanilla's setter loads.
        /// </summary>
        public bool HasAnimLibrary;
        public string AnimLibraryName;

        public void Serialize(NetWriter w)
        {
            w.Put(NpcName ?? "");
            w.Put(Reputation);
            // Trailer: hasAttackedId + int. Pre-0.8.93 peers stop after Reputation.
            w.Put(HasAttackedId);
            if (HasAttackedId)
                w.Put(AttackedId);
            // 0.8.94: hasDead + dead + deadID. Pre-0.8.94 peers stop after attacked trailer.
            w.Put(HasDead);
            if (HasDead)
            {
                w.Put(Dead);
                w.Put(DeadId);
            }
            // 0.8.96: hasPortrait + portrait int + applyDialogue + pos. Pre-0.8.96 stop after dead.
            w.Put(HasPortrait);
            if (HasPortrait)
            {
                w.Put(PortraitType);
                w.Put(ApplyDialoguePortrait);
                w.Put(PosX);
                w.Put(PosY);
                w.Put(PosZ);
            }
            // 0.8.97: hasAnimLibrary + library string + pos. Pre-0.8.97 stop after portrait.
            w.Put(HasAnimLibrary);
            if (HasAnimLibrary)
            {
                w.Put(AnimLibraryName ?? "");
                w.Put(PosX);
                w.Put(PosY);
                w.Put(PosZ);
            }
        }

        public static ReputationSyncMessage Deserialize(NetReader r)
        {
            var msg = new ReputationSyncMessage
            {
                NpcName = r.GetString(),
                Reputation = r.GetInt()
            };
            if (r.AvailableBytes >= 1)
            {
                msg.HasAttackedId = r.GetBool();
                if (msg.HasAttackedId && r.AvailableBytes >= 4)
                    msg.AttackedId = r.GetInt();
                else
                    msg.HasAttackedId = false;
            }
            // 0.8.94+: hasDead + bool + int. Pre-0.8.94: no trailer.
            if (r.AvailableBytes >= 1)
            {
                msg.HasDead = r.GetBool();
                if (msg.HasDead && r.AvailableBytes >= 5)
                {
                    msg.Dead = r.GetBool();
                    msg.DeadId = r.GetInt();
                }
                else
                    msg.HasDead = false;
            }
            // 0.8.96+: hasPortrait + int + bool + 3 floats (18 bytes after flag).
            if (r.AvailableBytes >= 1)
            {
                msg.HasPortrait = r.GetBool();
                if (msg.HasPortrait && r.AvailableBytes >= 17)
                {
                    msg.PortraitType = r.GetInt();
                    msg.ApplyDialoguePortrait = r.GetBool();
                    msg.PosX = r.GetFloat();
                    msg.PosY = r.GetFloat();
                    msg.PosZ = r.GetFloat();
                }
                else
                    msg.HasPortrait = false;
            }
            // 0.8.97+: hasAnimLibrary + string + 3 floats.
            if (r.AvailableBytes >= 1)
            {
                msg.HasAnimLibrary = r.GetBool();
                if (msg.HasAnimLibrary)
                {
                    msg.AnimLibraryName = r.GetString();
                    if (r.AvailableBytes >= 12)
                    {
                        msg.PosX = r.GetFloat();
                        msg.PosY = r.GetFloat();
                        msg.PosZ = r.GetFloat();
                    }
                    else
                        msg.HasAnimLibrary = false;
                }
            }
            return msg;
        }
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

    /// <summary>
    /// Host→late-joiner: night scenario latch without replaying fire.
    /// Per decompile <c>CustomEvent</c> / <c>RandomEvent</c>: set
    /// <c>started</c>, <c>startedToday</c>, <c>disabled</c>, and
    /// <c>NightScenario.currentEvent</c> only — never call
    /// <c>CustomEvent.fire</c> / <c>RandomEvent.fire</c>.
    /// </summary>
    public struct ScenarioStateBulkMessage
    {
        public string ScenarioName;
        /// <summary>Index into <c>customEventAndInts</c>, or -1 when none.</summary>
        public int CurrentEventIndex;
        public int CurrentEventTimeDay;
        public int CurrentEventTimeTime;
        public int FiredCount;
        public int[] EventIndices;
        public bool[] Started;
        public bool[] StartedToday;
        public bool[] Disabled;

        public const int MaxEvents = 256;

        public void Serialize(NetWriter w)
        {
            w.Put(ScenarioName ?? string.Empty);
            w.Put(CurrentEventIndex);
            w.Put(CurrentEventTimeDay);
            w.Put(CurrentEventTimeTime);
            w.Put(FiredCount);
            for (int i = 0; i < FiredCount; i++)
            {
                w.Put(EventIndices != null && i < EventIndices.Length ? EventIndices[i] : -1);
                w.Put(Started != null && i < Started.Length && Started[i]);
                w.Put(StartedToday != null && i < StartedToday.Length && StartedToday[i]);
                w.Put(Disabled != null && i < Disabled.Length && Disabled[i]);
            }
        }

        public static ScenarioStateBulkMessage Deserialize(NetReader r)
        {
            var msg = new ScenarioStateBulkMessage
            {
                ScenarioName = r.GetString(),
                CurrentEventIndex = r.GetInt(),
                CurrentEventTimeDay = r.GetInt(),
                CurrentEventTimeTime = r.GetInt(),
                FiredCount = r.GetInt()
            };
            if (msg.FiredCount < 0 || msg.FiredCount > MaxEvents)
                msg.FiredCount = 0;
            msg.EventIndices = new int[msg.FiredCount];
            msg.Started = new bool[msg.FiredCount];
            msg.StartedToday = new bool[msg.FiredCount];
            msg.Disabled = new bool[msg.FiredCount];
            for (int i = 0; i < msg.FiredCount; i++)
            {
                msg.EventIndices[i] = r.GetInt();
                msg.Started[i] = r.GetBool();
                msg.StartedToday[i] = r.GetBool();
                msg.Disabled[i] = r.GetBool();
            }
            return msg;
        }
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
        /// <summary>Journal locationsDict keys (0.8.61). AvailableBytes trailer.</summary>
        public string[] LocationTypes;

        public void Serialize(NetWriter w)
        {
            WriteArray(w, NoteTypes); WriteArray(w, KeyTypes);
            WriteArray(w, QuestItemTypes); WriteArray(w, JournalEntryTypes);
            WriteArray(w, LocationTypes);
        }

        public static JournalBulkSyncMessage Deserialize(NetReader r)
        {
            var msg = new JournalBulkSyncMessage
            {
                NoteTypes = ReadArray(r),
                KeyTypes = ReadArray(r),
                QuestItemTypes = ReadArray(r),
                JournalEntryTypes = ReadArray(r)
            };
            // Pre-0.8.61 peers omit LocationTypes; AvailableBytes-safe.
            if (r.AvailableBytes >= 4)
                msg.LocationTypes = ReadArray(r);
            return msg;
        }

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
        /// <summary>Host Flags.NPCState.wantsToTalk (0.8.55). End-of-message trailer; AvailableBytes-safe.</summary>
        public bool[] WantsToTalk;
        /// <summary>Host Flags.NPCState.attackedID (0.8.93). Trailer after wantsToTalk.</summary>
        public int[] AttackedIds;
        /// <summary>Host Flags.NPCState.deadID (0.8.94). Trailer after attackedID; Dead bool is legacy.</summary>
        public int[] DeadIds;
        /// <summary>
        /// 0.8.115: sparse NPC.portraitType after GameEvent CharacterModify (live had this
        /// on ReputationSync 0.8.96; bulk did not). Per-slot HasPortrait + payload.
        /// </summary>
        public bool[] HasPortrait;
        public int[] PortraitTypes;
        public bool[] ApplyDialoguePortrait;
        public float[] PortraitPosX, PortraitPosY, PortraitPosZ;
        /// <summary>
        /// 0.8.115: sparse Character.animationLibraryOverride (live 0.8.97).
        /// </summary>
        public bool[] HasAnimLibrary;
        public string[] AnimLibraryNames;
        public float[] AnimPosX, AnimPosY, AnimPosZ;

        public void Serialize(NetWriter w)
        {
            w.Put(NpcCount);
            for (int i = 0; i < NpcCount; i++)
            {
                w.Put(NpcNames?[i] ?? "");
                w.Put(Reputations != null && i < Reputations.Length ? Reputations[i] : 0);
                w.Put(Dead != null && i < Dead.Length && Dead[i]);
            }
            // Trailer after legacy fields so 0.8.54 readers stop at Dead without desync.
            w.Put(true); // hasWantsToTalkTrailer
            for (int i = 0; i < NpcCount; i++)
                w.Put(WantsToTalk == null || i >= WantsToTalk.Length || WantsToTalk[i]);
            // 0.8.93: attackedID per NPC. Pre-0.8.93 peers stop after wantsToTalk.
            w.Put(true); // hasAttackedIdTrailer
            for (int i = 0; i < NpcCount; i++)
                w.Put(AttackedIds != null && i < AttackedIds.Length ? AttackedIds[i] : 0);
            // 0.8.94: deadID per NPC. Pre-0.8.94 peers stop after attackedID.
            w.Put(true); // hasDeadIdTrailer
            for (int i = 0; i < NpcCount; i++)
                w.Put(DeadIds != null && i < DeadIds.Length ? DeadIds[i] : 0);
            // 0.8.115: sparse portrait trailer. Pre-0.8.115 peers stop after deadID.
            w.Put(true); // hasPortraitTrailer
            for (int i = 0; i < NpcCount; i++)
            {
                bool has = HasPortrait != null && i < HasPortrait.Length && HasPortrait[i];
                w.Put(has);
                if (!has) continue;
                w.Put(PortraitTypes != null && i < PortraitTypes.Length ? PortraitTypes[i] : 0);
                w.Put(ApplyDialoguePortrait != null && i < ApplyDialoguePortrait.Length
                    && ApplyDialoguePortrait[i]);
                w.Put(PortraitPosX != null && i < PortraitPosX.Length ? PortraitPosX[i] : 0f);
                w.Put(PortraitPosY != null && i < PortraitPosY.Length ? PortraitPosY[i] : 0f);
                w.Put(PortraitPosZ != null && i < PortraitPosZ.Length ? PortraitPosZ[i] : 0f);
            }
            // 0.8.115: sparse anim-library trailer. Pre-0.8.115 peers stop after portrait.
            w.Put(true); // hasAnimLibraryTrailer
            for (int i = 0; i < NpcCount; i++)
            {
                bool has = HasAnimLibrary != null && i < HasAnimLibrary.Length && HasAnimLibrary[i];
                w.Put(has);
                if (!has) continue;
                w.Put(AnimLibraryNames != null && i < AnimLibraryNames.Length
                    ? (AnimLibraryNames[i] ?? "") : "");
                w.Put(AnimPosX != null && i < AnimPosX.Length ? AnimPosX[i] : 0f);
                w.Put(AnimPosY != null && i < AnimPosY.Length ? AnimPosY[i] : 0f);
                w.Put(AnimPosZ != null && i < AnimPosZ.Length ? AnimPosZ[i] : 0f);
            }
        }

        public static ReputationBulkSyncMessage Deserialize(NetReader r)
        {
            var msg = new ReputationBulkSyncMessage { NpcCount = r.GetInt() };
            if (msg.NpcCount < 0 || msg.NpcCount > 4096) msg.NpcCount = 0;
            msg.NpcNames = new string[msg.NpcCount];
            msg.Reputations = new int[msg.NpcCount];
            msg.Dead = new bool[msg.NpcCount];
            msg.WantsToTalk = new bool[msg.NpcCount];
            msg.AttackedIds = new int[msg.NpcCount];
            msg.DeadIds = new int[msg.NpcCount];
            msg.HasPortrait = new bool[msg.NpcCount];
            msg.PortraitTypes = new int[msg.NpcCount];
            msg.ApplyDialoguePortrait = new bool[msg.NpcCount];
            msg.PortraitPosX = new float[msg.NpcCount];
            msg.PortraitPosY = new float[msg.NpcCount];
            msg.PortraitPosZ = new float[msg.NpcCount];
            msg.HasAnimLibrary = new bool[msg.NpcCount];
            msg.AnimLibraryNames = new string[msg.NpcCount];
            msg.AnimPosX = new float[msg.NpcCount];
            msg.AnimPosY = new float[msg.NpcCount];
            msg.AnimPosZ = new float[msg.NpcCount];
            for (int i = 0; i < msg.NpcCount; i++)
            {
                msg.NpcNames[i] = r.GetString();
                msg.Reputations[i] = r.GetInt();
                msg.Dead[i] = r.GetBool();
                msg.WantsToTalk[i] = true; // fail open for talkTo() story gates
            }
            // 0.8.55+: bool hasTrailer + NpcCount wants bits. Pre-0.8.55: no trailer.
            if (r.AvailableBytes >= 1 + msg.NpcCount)
            {
                bool hasTrailer = r.GetBool();
                if (hasTrailer)
                {
                    for (int i = 0; i < msg.NpcCount; i++)
                        msg.WantsToTalk[i] = r.GetBool();
                }
            }
            // 0.8.93+: bool hasTrailer + NpcCount ints. Pre-0.8.93: no trailer.
            if (r.AvailableBytes >= 1 + 4 * msg.NpcCount)
            {
                bool hasAttacked = r.GetBool();
                if (hasAttacked)
                {
                    for (int i = 0; i < msg.NpcCount; i++)
                        msg.AttackedIds[i] = r.GetInt();
                }
            }
            // 0.8.94+: bool hasTrailer + NpcCount ints. Pre-0.8.94: no trailer.
            if (r.AvailableBytes >= 1 + 4 * msg.NpcCount)
            {
                bool hasDeadId = r.GetBool();
                if (hasDeadId)
                {
                    for (int i = 0; i < msg.NpcCount; i++)
                        msg.DeadIds[i] = r.GetInt();
                }
            }
            // 0.8.115+: sparse portrait. Variable length — stop if bytes run out.
            if (r.AvailableBytes >= 1)
            {
                bool hasPortraitTrailer = r.GetBool();
                if (hasPortraitTrailer)
                {
                    for (int i = 0; i < msg.NpcCount; i++)
                    {
                        if (r.AvailableBytes < 1) break;
                        bool has = r.GetBool();
                        msg.HasPortrait[i] = has;
                        if (!has) continue;
                        if (r.AvailableBytes < 17)
                        {
                            msg.HasPortrait[i] = false;
                            break;
                        }
                        msg.PortraitTypes[i] = r.GetInt();
                        msg.ApplyDialoguePortrait[i] = r.GetBool();
                        msg.PortraitPosX[i] = r.GetFloat();
                        msg.PortraitPosY[i] = r.GetFloat();
                        msg.PortraitPosZ[i] = r.GetFloat();
                    }
                }
            }
            // 0.8.115+: sparse anim library.
            if (r.AvailableBytes >= 1)
            {
                bool hasAnimTrailer = r.GetBool();
                if (hasAnimTrailer)
                {
                    for (int i = 0; i < msg.NpcCount; i++)
                    {
                        if (r.AvailableBytes < 1) break;
                        bool has = r.GetBool();
                        msg.HasAnimLibrary[i] = has;
                        if (!has) continue;
                        msg.AnimLibraryNames[i] = r.GetString();
                        if (r.AvailableBytes < 12)
                        {
                            msg.HasAnimLibrary[i] = false;
                            break;
                        }
                        msg.AnimPosX[i] = r.GetFloat();
                        msg.AnimPosY[i] = r.GetFloat();
                        msg.AnimPosZ[i] = r.GetFloat();
                    }
                }
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
        /// <summary>0 none, 1 strike, 2 distant pre-rain flash. Missing on old packets means none.</summary>
        public byte Strike;

        public void Serialize(NetWriter w)
        {
            w.Put(Raining); w.Put(RainToday); w.Put(TimeToStart); w.Put(LightningTime);
            w.Put(PreRainLightning); w.Put(PreRainLightningTime); w.Put(Duration);
            w.Put(TimeToFadeInFog_Hours); w.Put(TimeToFadeInFog_Day);
            w.Put(TimeToFadeOutFog_Hours); w.Put(TimeToFadeOutFog_Day);
            w.Put(FogFadedOutToday); w.Put(FogIsActive);
            w.Put(Strike);
        }

        public static WeatherSyncMessage Deserialize(NetReader r)
        {
            var msg = new WeatherSyncMessage
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
            if (r.AvailableBytes >= 1)
                msg.Strike = r.GetByte();
            return msg;
        }
    }
}
