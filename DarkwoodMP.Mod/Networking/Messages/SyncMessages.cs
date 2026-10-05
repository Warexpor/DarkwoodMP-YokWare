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
        /// <summary>Which body, when two traders share a name (valid when HasPos).</summary>
        public bool HasPos;
        public float PosX, PosY, PosZ;
        /// <summary>
        /// Per-entry recipe flag. ItemTypes stores recipeFor when true
        /// (vanilla InvItemClass ctor flips type to "recipe").
        /// </summary>
        public bool[] IsRecipe;
        /// <summary>Absolute durability per entry.</summary>
        public float[] Durabilities;
        /// <summary>Per-entry workbench upgrade names.</summary>
        public string[][] Upgrades;
        /// <summary>Per-entry shouldBeActive (flashlight on).</summary>
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
            for (int i = 0; i < ItemCount; i++)
                w.Put(IsRecipe != null && i < IsRecipe.Length && IsRecipe[i]);
            for (int i = 0; i < ItemCount; i++)
                w.Put(Durabilities != null && i < Durabilities.Length ? Durabilities[i] : 0f);
            for (int i = 0; i < ItemCount; i++)
                DWMPHorde.Sync.InvItemUpgradeWire.Write(w, Upgrades != null && i < Upgrades.Length ? Upgrades[i] : null);
            for (int i = 0; i < ItemCount; i++)
                w.Put(ShouldBeActive != null && i < ShouldBeActive.Length && ShouldBeActive[i]);
        }

        public static TradeInventorySyncMessage Deserialize(NetReader r)
        {
            var msg = new TradeInventorySyncMessage { NpcName = r.GetString(), ItemCount = r.GetInt() };
            if (msg.ItemCount < 0 || msg.ItemCount > 4096)
                throw new System.IO.InvalidDataException("TradeInventorySync item count " + msg.ItemCount);
            int n = msg.ItemCount;
            msg.ItemTypes = new string[n];
            msg.Amounts = new int[n];
            for (int i = 0; i < n; i++)
            {
                msg.ItemTypes[i] = r.GetString();
                msg.Amounts[i] = r.GetInt();
            }
            msg.InDream = r.GetBool();
            msg.HasPos = r.GetBool();
            msg.PosX = r.GetFloat();
            msg.PosY = r.GetFloat();
            msg.PosZ = r.GetFloat();
            msg.IsRecipe = new bool[n];
            msg.Durabilities = new float[n];
            for (int i = 0; i < n; i++)
                msg.IsRecipe[i] = r.GetBool();
            for (int i = 0; i < n; i++)
                msg.Durabilities[i] = r.GetFloat();
            msg.Upgrades = new string[n][];
            for (int i = 0; i < n; i++)
                msg.Upgrades[i] = DWMPHorde.Sync.InvItemUpgradeWire.Read(r);
            msg.ShouldBeActive = new bool[n];
            for (int i = 0; i < n; i++)
                msg.ShouldBeActive[i] = r.GetBool();
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
        /// <summary>
        /// The lock's world: the NPC is the dream-pad twin (true) or the overworld one. The
        /// requester's view on a request; the host's decision on grant / deny / release.
        /// </summary>
        public bool Dream;
        /// <summary>Client lease renewal of a talk already open: never a fresh grant on the host.</summary>
        public bool Renewal;

        public void Serialize(NetWriter w)
        {
            w.Put(NpcName ?? "");
            w.Put(OwnerPlayerId);
            w.Put(Granted);
            w.Put(Release);
            w.Put(IsRequest);
            w.Put(Dream);
            w.Put(Renewal);
        }

        public static DialogNpcLockMessage Deserialize(NetReader r)
        {
            var msg = new DialogNpcLockMessage
            {
                NpcName = r.GetString(),
                OwnerPlayerId = r.GetInt(),
                Granted = r.GetBool(),
                Release = r.GetBool(),
                IsRequest = r.GetBool()
            };
            msg.Dream = r.GetBool();
            msg.Renewal = r.GetBool();
            return msg;
        }
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

    /// <summary>How a creature one-shot is played, matching the vanilla call that played it on the host.</summary>
    public enum EntitySoundKind : byte
    {
        /// <summary><c>CharacterSounds.play</c>: overlaps the creature's other sounds.</summary>
        Play = 0,
        /// <summary><c>CharacterSounds.playSingleInstance</c> / <c>playGrowl</c>: cuts the creature's previous voice line.</summary>
        Single = 1,
        /// <summary><c>AudioController.Play</c> parented to the creature: footsteps, shots, sniffs, howls.</summary>
        Attached = 2,
        /// <summary><c>CharacterSounds.playGetHitByAxe1</c>.</summary>
        GetHit = 3,
        /// <summary>The creature's death line.</summary>
        Death = 4,
    }

    /// <summary>
    /// Host→clients: a one-shot a host creature played. Loops are not sent here; the creature's
    /// current loop travels in its <see cref="EntitySnapshotNet.Loop"/>.
    /// </summary>
    public struct EntitySoundMessage
    {
        public short HostId;
        public EntitySoundKind Kind;
        public string SoundId;
        public float Volume;
        /// <summary>GetHit: the player whose attack the host was applying (-1: host or AI).</summary>
        public int AttackerId;

        public void Serialize(NetWriter w)
        {
            w.Put(HostId); w.Put((byte)Kind); w.Put(SoundId ?? string.Empty); w.Put(Volume); w.Put(AttackerId);
        }

        public static EntitySoundMessage Deserialize(NetReader r) => new EntitySoundMessage
        {
            HostId = r.GetShort(),
            Kind = (EntitySoundKind)r.GetByte(),
            SoundId = r.GetString(),
            Volume = r.GetFloat(),
            AttackerId = r.GetInt()
        };
    }

    /// <summary>
    /// Host→clients: a banshee started or stopped screaming at a player. Every peer turns the
    /// banshee's sight light on or off; only <see cref="VictimId"/> hears the scream, feels the
    /// shake and sees the overlay (vanilla plays them for the one player it sees).
    /// </summary>
    public struct TradeEntry
    {
        public string Type;
        public bool IsRecipe;
        /// <summary>Stack amount.</summary>
        public int Count;
        public float Durability;
        public int Ammo;
        public bool Active;
        public string[] Upgrades;

        public void Serialize(NetWriter w)
        {
            w.Put(Type ?? "");
            w.Put(IsRecipe);
            w.Put(Count);
            w.Put(Durability);
            w.Put(Ammo);
            w.Put(Active);
            int n = Upgrades != null ? Upgrades.Length : 0;
            w.Put((byte)n);
            for (int i = 0; i < n; i++)
                w.Put(Upgrades[i] ?? "");
        }

        public static TradeEntry Deserialize(NetReader r)
        {
            var e = new TradeEntry
            {
                Type = r.GetString(),
                IsRecipe = r.GetBool(),
                Count = r.GetInt(),
                Durability = r.GetFloat(),
                Ammo = r.GetInt(),
                Active = r.GetBool()
            };
            int n = r.GetByte();
            e.Upgrades = n > 0 ? new string[n] : null;
            for (int i = 0; i < n; i++)
                e.Upgrades[i] = r.GetString();
            return e;
        }
    }

    public struct TradeCommitMessage
    {
        public string NpcName;
        public float PosX, PosY, PosZ;
        public bool InDream;
        /// <summary>Host→client: the host's stock did not have what was bought; undo it.</summary>
        public bool Denied;
        public TradeEntry[] Bought;
        public TradeEntry[] Sold;

        public void Serialize(NetWriter w)
        {
            w.Put(NpcName ?? "");
            w.Put(PosX);
            w.Put(PosY);
            w.Put(PosZ);
            w.Put(InDream);
            w.Put(Denied);
            PutEntries(w, Bought);
            PutEntries(w, Sold);
        }

        private static void PutEntries(NetWriter w, TradeEntry[] entries)
        {
            int n = entries != null ? entries.Length : 0;
            w.Put((short)n);
            for (int i = 0; i < n; i++)
                entries[i].Serialize(w);
        }

        private static TradeEntry[] GetEntries(NetReader r)
        {
            int n = r.GetShort();
            var a = new TradeEntry[n < 0 ? 0 : n];
            for (int i = 0; i < a.Length; i++)
                a[i] = TradeEntry.Deserialize(r);
            return a;
        }

        public static TradeCommitMessage Deserialize(NetReader r) => new TradeCommitMessage
        {
            NpcName = r.GetString(),
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            InDream = r.GetBool(),
            Denied = r.GetBool(),
            Bought = GetEntries(r),
            Sold = GetEntries(r)
        };
    }

    public struct PlayerSpecialMessage
    {
        /// <summary>The Player method to run (whitelisted on receipt).</summary>
        public string Method;

        public void Serialize(NetWriter w) => w.Put(Method ?? "");

        public static PlayerSpecialMessage Deserialize(NetReader r) => new PlayerSpecialMessage { Method = r.GetString() };
    }

    public struct PorterTransportMessage
    {
        /// <summary>The hideout whose containers were emptied.</summary>
        public string Source;
        /// <summary>The hideout the package goes to.</summary>
        public string Dest;

        public void Serialize(NetWriter w)
        {
            w.Put(Source ?? "");
            w.Put(Dest ?? "");
        }

        public static PorterTransportMessage Deserialize(NetReader r) => new PorterTransportMessage
        {
            Source = r.GetString(),
            Dest = r.GetString()
        };
    }

    public struct BansheeAgitationMessage
    {
        public short HostId;
        public int VictimId;
        public bool Agitated;
        /// <summary>Agitated from a sighting (vanilla fades the banshee overlay in); false from a defensive start.</summary>
        public bool Overlay;

        public void Serialize(NetWriter w) { w.Put(HostId); w.Put(VictimId); w.Put(Agitated); w.Put(Overlay); }

        public static BansheeAgitationMessage Deserialize(NetReader r) => new BansheeAgitationMessage
        {
            HostId = r.GetShort(),
            VictimId = r.GetInt(),
            Agitated = r.GetBool(),
            Overlay = r.GetBool()
        };
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
        /// <summary>The village's friendly villagers are away for the night (NightVillage).</summary>
        public bool VillagersAway;
        /// <summary>
        /// The overworld clock. Equals <see cref="CurrentTime"/> except while the host is in a dream,
        /// where CurrentTime is the dream's own time and this is the time the dream will wake to
        /// (vanilla <c>Dreams.timeCopy</c>). A peer outside the dream (dead, or its pad failed to
        /// load) shows this one.
        /// </summary>
        public int OverworldTime;
        public void Serialize(NetWriter w) { w.Put(CurrentTime); w.Put(Day); w.Put(IsAfterNight); w.Put(VillagersAway); w.Put(OverworldTime); }
        public static TimeSyncMessage Deserialize(NetReader r) => new TimeSyncMessage { CurrentTime = r.GetInt(), Day = r.GetInt(), IsAfterNight = r.GetBool(), VillagersAway = r.GetBool(), OverworldTime = r.GetInt() };
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
            int n = r.GetByte();
            if (n > 32)
                throw new System.IO.InvalidDataException("PeerRoster entry count " + n);
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
        /// <summary>Host Flags.NPCState.attackedID (valid when HasAttackedId).</summary>
        public bool HasAttackedId;
        public int AttackedId;
        /// <summary>Host Flags.NPCState.dead + deadID (valid when HasDead; otherwise death fields are left alone).</summary>
        public bool HasDead;
        public bool Dead;
        public int DeadId;
        /// <summary>NPC.portraitType after GameEvent CharacterModify (valid when HasPortrait).</summary>
        public bool HasPortrait;
        public int PortraitType;
        /// <summary>Vanilla activeModifier: also write characterDialogue.portraitType.</summary>
        public bool ApplyDialoguePortrait;
        public float PosX, PosY, PosZ;
        /// <summary>
        /// Character.animationLibraryOverride after GameEvent CharacterModify.
        /// Valid when HasAnimLibrary. String is the Resources path vanilla's setter loads.
        /// </summary>
        public bool HasAnimLibrary;
        public string AnimLibraryName;

        public void Serialize(NetWriter w)
        {
            w.Put(NpcName ?? "");
            w.Put(Reputation);
            w.Put(HasAttackedId);
            if (HasAttackedId)
                w.Put(AttackedId);
            w.Put(HasDead);
            if (HasDead)
            {
                w.Put(Dead);
                w.Put(DeadId);
            }
            w.Put(HasPortrait);
            if (HasPortrait)
            {
                w.Put(PortraitType);
                w.Put(ApplyDialoguePortrait);
                w.Put(PosX);
                w.Put(PosY);
                w.Put(PosZ);
            }
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
            msg.HasAttackedId = r.GetBool();
            if (msg.HasAttackedId)
                msg.AttackedId = r.GetInt();
            msg.HasDead = r.GetBool();
            if (msg.HasDead)
            {
                msg.Dead = r.GetBool();
                msg.DeadId = r.GetInt();
            }
            msg.HasPortrait = r.GetBool();
            if (msg.HasPortrait)
            {
                msg.PortraitType = r.GetInt();
                msg.ApplyDialoguePortrait = r.GetBool();
                msg.PosX = r.GetFloat();
                msg.PosY = r.GetFloat();
                msg.PosZ = r.GetFloat();
            }
            msg.HasAnimLibrary = r.GetBool();
            if (msg.HasAnimLibrary)
            {
                msg.AnimLibraryName = r.GetString();
                msg.PosX = r.GetFloat();
                msg.PosY = r.GetFloat();
                msg.PosZ = r.GetFloat();
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
        /// <summary>Journal locationsDict keys.</summary>
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
                JournalEntryTypes = ReadArray(r),
                LocationTypes = ReadArray(r)
            };
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
        /// <summary>The wave ended on the host (vanilla Player.endShadows); else it started.</summary>
        public bool End;
        /// <summary>Whose curse the starting wave is (vanilla spawnedShadows blocks only that player's natural lights).</summary>
        public short OwnerId;

        public void Serialize(NetWriter w) { w.Put(End); w.Put(OwnerId); }
        public static ShadowEventMessage Deserialize(NetReader r) => new ShadowEventMessage { End = r.GetBool(), OwnerId = r.GetShort() };
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
        /// <summary>Host Flags.NPCState.wantsToTalk.</summary>
        public bool[] WantsToTalk;
        /// <summary>Host Flags.NPCState.attackedID.</summary>
        public int[] AttackedIds;
        /// <summary>Host Flags.NPCState.deadID.</summary>
        public int[] DeadIds;
        /// <summary>Sparse NPC.portraitType after GameEvent CharacterModify (per-slot HasPortrait + payload).</summary>
        public bool[] HasPortrait;
        public int[] PortraitTypes;
        public bool[] ApplyDialoguePortrait;
        public float[] PortraitPosX, PortraitPosY, PortraitPosZ;
        /// <summary>Sparse Character.animationLibraryOverride (per-slot HasAnimLibrary + payload).</summary>
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
                w.Put(WantsToTalk == null || i >= WantsToTalk.Length || WantsToTalk[i]);
                w.Put(AttackedIds != null && i < AttackedIds.Length ? AttackedIds[i] : 0);
                w.Put(DeadIds != null && i < DeadIds.Length ? DeadIds[i] : 0);

                bool portrait = HasPortrait != null && i < HasPortrait.Length && HasPortrait[i];
                w.Put(portrait);
                if (portrait)
                {
                    w.Put(PortraitTypes != null && i < PortraitTypes.Length ? PortraitTypes[i] : 0);
                    w.Put(ApplyDialoguePortrait != null && i < ApplyDialoguePortrait.Length
                        && ApplyDialoguePortrait[i]);
                    w.Put(PortraitPosX != null && i < PortraitPosX.Length ? PortraitPosX[i] : 0f);
                    w.Put(PortraitPosY != null && i < PortraitPosY.Length ? PortraitPosY[i] : 0f);
                    w.Put(PortraitPosZ != null && i < PortraitPosZ.Length ? PortraitPosZ[i] : 0f);
                }

                bool anim = HasAnimLibrary != null && i < HasAnimLibrary.Length && HasAnimLibrary[i];
                w.Put(anim);
                if (anim)
                {
                    w.Put(AnimLibraryNames != null && i < AnimLibraryNames.Length
                        ? (AnimLibraryNames[i] ?? "") : "");
                    w.Put(AnimPosX != null && i < AnimPosX.Length ? AnimPosX[i] : 0f);
                    w.Put(AnimPosY != null && i < AnimPosY.Length ? AnimPosY[i] : 0f);
                    w.Put(AnimPosZ != null && i < AnimPosZ.Length ? AnimPosZ[i] : 0f);
                }
            }
        }

        public static ReputationBulkSyncMessage Deserialize(NetReader r)
        {
            var msg = new ReputationBulkSyncMessage { NpcCount = r.GetInt() };
            if (msg.NpcCount < 0 || msg.NpcCount > 4096)
                throw new System.IO.InvalidDataException("ReputationBulkSync NPC count " + msg.NpcCount);
            int n = msg.NpcCount;
            msg.NpcNames = new string[n];
            msg.Reputations = new int[n];
            msg.Dead = new bool[n];
            msg.WantsToTalk = new bool[n];
            msg.AttackedIds = new int[n];
            msg.DeadIds = new int[n];
            msg.HasPortrait = new bool[n];
            msg.PortraitTypes = new int[n];
            msg.ApplyDialoguePortrait = new bool[n];
            msg.PortraitPosX = new float[n];
            msg.PortraitPosY = new float[n];
            msg.PortraitPosZ = new float[n];
            msg.HasAnimLibrary = new bool[n];
            msg.AnimLibraryNames = new string[n];
            msg.AnimPosX = new float[n];
            msg.AnimPosY = new float[n];
            msg.AnimPosZ = new float[n];
            for (int i = 0; i < n; i++)
            {
                msg.NpcNames[i] = r.GetString();
                msg.Reputations[i] = r.GetInt();
                msg.Dead[i] = r.GetBool();
                msg.WantsToTalk[i] = r.GetBool();
                msg.AttackedIds[i] = r.GetInt();
                msg.DeadIds[i] = r.GetInt();
                msg.HasPortrait[i] = r.GetBool();
                if (msg.HasPortrait[i])
                {
                    msg.PortraitTypes[i] = r.GetInt();
                    msg.ApplyDialoguePortrait[i] = r.GetBool();
                    msg.PortraitPosX[i] = r.GetFloat();
                    msg.PortraitPosY[i] = r.GetFloat();
                    msg.PortraitPosZ[i] = r.GetFloat();
                }
                msg.HasAnimLibrary[i] = r.GetBool();
                if (msg.HasAnimLibrary[i])
                {
                    msg.AnimLibraryNames[i] = r.GetString();
                    msg.AnimPosX[i] = r.GetFloat();
                    msg.AnimPosY[i] = r.GetFloat();
                    msg.AnimPosZ[i] = r.GetFloat();
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
                FogIsActive = r.GetBool(),
                Strike = r.GetByte()
            };
            return msg;
        }
    }
}
