using System.IO;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public struct LightStateMessage
    {
        public float PosX, PosY, PosZ;
        public bool IsOn;
        public string ItemName;
        public string ItemType;
        /// <summary>Toggled by hand (Item.switchMe): vanilla played the switch click first.</summary>
        public bool Switched;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(IsOn);
            w.Put(ItemName ?? string.Empty);
            w.Put(ItemType ?? string.Empty);
            w.Put(Switched);
        }

        public static LightStateMessage Deserialize(NetReader r) => new LightStateMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            IsOn = r.GetBool(),
            ItemName = r.GetString(),
            ItemType = r.GetString(),
            Switched = r.GetBool()
        };
    }

    public struct ItemSpawnMessage
    {
        public string ItemType;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ;

        public void Serialize(NetWriter writer)
        {
            writer.Put(ItemType ?? string.Empty);
            writer.Put(PosX); writer.Put(PosY); writer.Put(PosZ);
            writer.Put(RotX); writer.Put(RotY); writer.Put(RotZ);
        }

        public static ItemSpawnMessage Deserialize(NetReader reader) => new ItemSpawnMessage
        {
            ItemType = reader.GetString(),
            PosX = reader.GetFloat(),
            PosY = reader.GetFloat(),
            PosZ = reader.GetFloat(),
            RotX = reader.GetFloat(),
            RotY = reader.GetFloat(),
            RotZ = reader.GetFloat()
        };
    }

    /// <summary>Host→clients: Character.removeMe (flee-despawn wildlife, temp spawns).</summary>
    public struct EntityDespawnMessage
    {
        public short EntityId;

        public void Serialize(NetWriter w) => w.Put(EntityId);

        public static EntityDespawnMessage Deserialize(NetReader r) => new EntityDespawnMessage
        {
            EntityId = r.GetShort()
        };
    }

    public struct EntitySnapshotNet
    {
        public short Index;
        public float PosX, PosY, PosZ;
        public float RotY;
        public string Clip;
        public short ClipFrame;
        public bool Alive;
        public byte HealthPct;
        /// <summary>
        /// Name and prefab path never change for one host id, so they travel only when
        /// <see cref="HasDescriptor"/> is set (first sends of an id and the 1 s full resync).
        /// The client caches them per id.
        /// </summary>
        public bool HasDescriptor;
        public string EntityName;
        public string PrefabPath;
        /// <summary>bit0=sleeping, bit1=eating, bit2=downed, bit3=fleeing, bits4-6=behaviour.</summary>
        public byte Flags;
        /// <summary>
        /// The creature's current CharacterSounds loop as a slot of its own loop fields
        /// (0 = none); see <c>EntityLoopSync</c>. State, not an event: a late joiner or a client
        /// walking up hears the loop the host is playing.
        /// </summary>
        public byte Loop;

        public const byte FlagSleeping = 1;
        public const byte FlagEating = 2;
        public const byte FlagDowned = 4;
        public const byte FlagFleeing = 8;

        public bool Sleeping => (Flags & FlagSleeping) != 0;
        public bool Eating => (Flags & FlagEating) != 0;
        public bool Downed => (Flags & FlagDowned) != 0;
        public bool Fleeing => (Flags & FlagFleeing) != 0;

        public Character.Behaviour PackedBehaviour
        {
            get
            {
                switch ((Flags >> 4) & 7)
                {
                    case 1: return Character.Behaviour.walking;
                    case 2: return Character.Behaviour.running;
                    case 3: return Character.Behaviour.defensive;
                    case 4: return Character.Behaviour.chasingTarget;
                    case 5: return Character.Behaviour.escaping;
                    case 6: return Character.Behaviour.listening;
                    case 7: return Character.Behaviour.following;
                    default: return Character.Behaviour.idle;
                }
            }
        }

        public static byte PackBehaviour(Character.Behaviour behaviour)
        {
            int n = 0;
            if (behaviour == Character.Behaviour.walking) n = 1;
            else if (behaviour == Character.Behaviour.running) n = 2;
            else if (behaviour == Character.Behaviour.defensive) n = 3;
            else if (behaviour == Character.Behaviour.chasingTarget) n = 4;
            else if (behaviour == Character.Behaviour.escaping) n = 5;
            else if (behaviour == Character.Behaviour.listening) n = 6;
            else if (behaviour == Character.Behaviour.following) n = 7;
            return (byte)(n << 4);
        }

        public void Serialize(NetWriter w)
        {
            w.Put(Index);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(RotY);
            w.Put(Clip ?? "");
            w.Put(ClipFrame);
            w.Put(Alive);
            w.Put(HealthPct);
            w.Put(Flags);
            w.Put(Loop);
            w.Put(HasDescriptor);
            if (HasDescriptor)
            {
                w.Put(EntityName ?? "");
                w.Put(PrefabPath ?? "");
            }
        }

        public static EntitySnapshotNet Deserialize(NetReader r)
        {
            var e = new EntitySnapshotNet
            {
                Index = r.GetShort(),
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                RotY = r.GetFloat(),
                Clip = r.GetString(),
                ClipFrame = r.GetShort(),
                Alive = r.GetBool(),
                HealthPct = r.GetByte(),
                Flags = r.GetByte(),
                Loop = r.GetByte(),
                HasDescriptor = r.GetBool()
            };
            if (e.HasDescriptor)
            {
                e.EntityName = r.GetString();
                e.PrefabPath = r.GetString();
            }
            return e;
        }
    }

    public struct EntityStateMessage
    {
        /// <summary>Monotonic per-sender sequence for the unreliable batch.</summary>
        public uint Sequence;
        /// <summary>Host clock (unscaled seconds since start) when the batch was sampled. Clients interpolate on this timeline.</summary>
        public float HostTime;
        public EntitySnapshotNet[] Entities;

        public void Serialize(NetWriter w)
        {
            w.Put(Sequence);
            w.Put(HostTime);
            int count = Entities != null ? Entities.Length : 0;
            w.Put(count);
            for (int i = 0; i < count; i++)
                Entities[i].Serialize(w);
        }

        public static EntityStateMessage Deserialize(NetReader r)
        {
            uint sequence = r.GetUInt();
            float hostTime = r.GetFloat();
            int count = r.GetInt();
            if (count < 0 || count > 4096)
                throw new InvalidDataException("Entity snapshot count is out of range: " + count);
            var arr = new EntitySnapshotNet[count];
            for (int i = 0; i < count; i++)
                arr[i] = EntitySnapshotNet.Deserialize(r);
            return new EntityStateMessage { Sequence = sequence, HostTime = hostTime, Entities = arr };
        }
    }

    public struct DragSyncMessage
    {
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ;
        public bool IsDragging;
        public string ObjectName;
        public string ItemType;
        public int ClaimedByPlayerId;
        /// <summary>
        /// Local player-intent scrape gate, matching body-push player movement.
        /// A false value stops peer scrape without waiting for a quiet
        /// position delta on an unreliable packet.
        /// </summary>
        public bool ScrapeActive;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(RotX); w.Put(RotY); w.Put(RotZ);
            w.Put(IsDragging);
            w.Put(ObjectName ?? "");
            w.Put(ItemType ?? "");
            w.Put(ClaimedByPlayerId);
            w.Put(ScrapeActive);
        }

        public static DragSyncMessage Deserialize(NetReader r) => new DragSyncMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            RotX = r.GetFloat(),
            RotY = r.GetFloat(),
            RotZ = r.GetFloat(),
            IsDragging = r.GetBool(),
            ObjectName = r.GetString(),
            ItemType = r.GetString(),
            ClaimedByPlayerId = r.GetInt(),
            ScrapeActive = r.GetBool()
        };
    }

    public struct WorldObjectRemovedMessage
    {
        public const byte ModeRemove = 0;
        /// <summary>Client→host: optimistic world pickup claim (non-GUID, non-trap).</summary>
        public const byte ModeClaimRequest = 1;
        /// <summary>Host→client: claim lost — refund optimistic grant.</summary>
        public const byte ModeClaimDeny = 2;

        public float PosX, PosY, PosZ;
        public string ObjectName;
        /// <summary>0 = remove (default), 1 = claim request, 2 = claim deny.</summary>
        public byte Mode;
        /// <summary>ModeRemove: player id that won the pickup (0 = unknown).</summary>
        public int ClaimedByPlayerId;
        public string ItemType;
        public int Amount;
        public float Durability;
        public int Ammo;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(ObjectName ?? "");
            w.Put(Mode);
            w.Put(ClaimedByPlayerId);
            w.Put(ItemType ?? "");
            w.Put(Amount);
            w.Put(Durability);
            w.Put(Ammo);
        }

        public static WorldObjectRemovedMessage Deserialize(NetReader r)
        {
            var msg = new WorldObjectRemovedMessage
            {
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                ObjectName = r.GetString(),
                Mode = r.GetByte(),
                ClaimedByPlayerId = r.GetInt(),
                ItemType = r.GetString(),
                Amount = r.GetInt(),
                Durability = r.GetFloat(),
                Ammo = r.GetInt()
            };
            return msg;
        }
    }

    /// <summary>Saw message kind: host absolute snapshot, or a client stock-change request.</summary>
    public enum SawStateKind : byte
    {
        /// <summary>Host-authoritative absolute fuel / log / wood stock.</summary>
        Absolute = 0,
        /// <summary>Client request: apply these deltas (addFuel / convert) to the host's stock.</summary>
        Delta = 1
    }

    public struct SawStateMessage
    {
        public float PosX, PosY, PosZ;
        public float Fuel;
        public int WoodLogAmount;
        public int WoodAmount;
        /// <summary>
        /// Client addFuel / convert fuel change for host-auth accumulation (Delta kind only).
        /// Always on the wire (same-DLL dual deploy).
        /// </summary>
        public float FuelDelta;
        public SawStateKind Kind;
        /// <summary>Delta kind: wood logs consumed by a client convert (&lt;= 0).</summary>
        public int WoodLogDelta;
        /// <summary>Delta kind: planks produced by a client convert (&gt;= 0).</summary>
        public int WoodDelta;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(Fuel);
            w.Put(WoodLogAmount);
            w.Put(WoodAmount);
            w.Put(FuelDelta);
            w.Put((byte)Kind);
            w.Put(WoodLogDelta);
            w.Put(WoodDelta);
        }

        public static SawStateMessage Deserialize(NetReader r) => new SawStateMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            Fuel = r.GetFloat(),
            WoodLogAmount = r.GetInt(),
            WoodAmount = r.GetInt(),
            FuelDelta = r.GetFloat(),
            Kind = (SawStateKind)r.GetByte(),
            WoodLogDelta = r.GetInt(),
            WoodDelta = r.GetInt()
        };
    }

    /// <summary>Hideout feeder: absolute Active flag (use → inactive). Pos-keyed like Saw.</summary>
    public struct FeederStateMessage
    {
        public float PosX, PosY, PosZ;
        public bool Active;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(Active);
        }

        public static FeederStateMessage Deserialize(NetReader r) => new FeederStateMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            Active = r.GetBool()
        };
    }

    /// <summary>Lure bait absolute health. Coalesced on send side.</summary>
    public struct LureStateMessage
    {
        public float PosX, PosY, PosZ;
        public int Health;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(Health);
        }

        public static LureStateMessage Deserialize(NetReader r) => new LureStateMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            Health = r.GetInt()
        };
    }

    public struct VaultStateMessage
    {
        public bool IsVaulting;
        /// <summary>Network PlayerId of the vaulting player (required for 3+).</summary>
        public int PlayerId;

        public void Serialize(NetWriter w)
        {
            w.Put(IsVaulting);
            w.Put(PlayerId);
        }

        public static VaultStateMessage Deserialize(NetReader r) => new VaultStateMessage
        {
            IsVaulting = r.GetBool(),
            PlayerId = r.GetInt()
        };
    }

    public struct EntitySpawnMessage
    {
        public string PrefabPath;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ;

        public void Serialize(NetWriter w)
        {
            w.Put(PrefabPath ?? "");
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(RotX); w.Put(RotY); w.Put(RotZ);
        }

        public static EntitySpawnMessage Deserialize(NetReader r) => new EntitySpawnMessage
        {
            PrefabPath = r.GetString(),
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            RotX = r.GetFloat(),
            RotY = r.GetFloat(),
            RotZ = r.GetFloat()
        };
    }

    public struct TrapTriggeredMessage
    {
        public float PosX, PosY, PosZ;
        /// <summary>Stable trap id (0 = resolve by position only).</summary>
        public int TrapNetId;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(TrapNetId);
        }
        public static TrapTriggeredMessage Deserialize(NetReader r)
        {
            var msg = new TrapTriggeredMessage
            {
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                TrapNetId = r.GetInt()
            };
            return msg;
        }
    }

    /// <summary>Host→all: thrown light/projectile expired (flare burnout).</summary>
    public struct ThrowableDespawnMessage
    {
        public int ThrowId;
        public float PosX, PosY, PosZ;

        public void Serialize(NetWriter w)
        {
            w.Put(ThrowId);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
        }

        public static ThrowableDespawnMessage Deserialize(NetReader r) => new ThrowableDespawnMessage
        {
            ThrowId = r.GetInt(),
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat()
        };
    }

    /// <summary>Host→peer late-join: full trap table (id + pos + triggered + occupant).</summary>
    public struct TrapBulkMessage
    {
        /// <summary>Most entries one message may carry; senders chunk above this, the reader rejects above it.</summary>
        public const int MaxEntries = 512;

        public TrapBulkEntry[] Entries;

        public void Serialize(NetWriter w)
        {
            int n = Entries != null ? Entries.Length : 0;
            w.Put(n);
            for (int i = 0; i < n; i++)
                Entries[i].Serialize(w);
        }

        public static TrapBulkMessage Deserialize(NetReader r)
        {
            int n = r.GetInt();
            // Zeroing an oversize count used to drop the whole trap table without a trace.
            if (n < 0 || n > MaxEntries)
                throw new System.IO.InvalidDataException("Trap bulk entry count is out of range: " + n);
            var entries = new TrapBulkEntry[n];
            for (int i = 0; i < n; i++)
                entries[i] = TrapBulkEntry.Deserialize(r);
            return new TrapBulkMessage { Entries = entries };
        }
    }

    public struct TrapBulkEntry
    {
        public int TrapNetId;
        public float PosX, PosY, PosZ;
        public bool Triggered;
        public short OccupantPlayerId;

        public void Serialize(NetWriter w)
        {
            w.Put(TrapNetId);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(Triggered);
            w.Put(OccupantPlayerId);
        }

        public static TrapBulkEntry Deserialize(NetReader r) => new TrapBulkEntry
        {
            TrapNetId = r.GetInt(),
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            Triggered = r.GetBool(),
            OccupantPlayerId = r.GetShort()
        };
    }

    public struct GasTrailSpawnMessage
    {
        public float PosX, PosY, PosZ;

        public void Serialize(NetWriter w) { w.Put(PosX); w.Put(PosY); w.Put(PosZ); }
        public static GasTrailSpawnMessage Deserialize(NetReader r) => new GasTrailSpawnMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat()
        };
    }

    public struct GasIgniteMessage
    {
        public float PosX, PosY, PosZ;

        public void Serialize(NetWriter w) { w.Put(PosX); w.Put(PosY); w.Put(PosZ); }
        public static GasIgniteMessage Deserialize(NetReader r) => new GasIgniteMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat()
        };
    }

    public struct DoorOpenMessage
    {
        public float PosX, PosY, PosZ;
        public string DoorName;
        /// <summary>Real Door.open OpenForce (thump=45000 → door_hit_run).</summary>
        public float OpenForce;
        /// <summary>Opener world pos for hinge force direction.</summary>
        public float OpenerPosX, OpenerPosY, OpenerPosZ;
        /// <summary>True when OpenForce/Opener trailer was present on the wire.</summary>
        /// <summary>client tried a padlocked door. Host fires the story trigger and does not open.</summary>
        public bool AttemptOnly;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(DoorName ?? "");
            w.Put(OpenForce);
            w.Put(OpenerPosX); w.Put(OpenerPosY); w.Put(OpenerPosZ);
            // True = rattle a locked door, do not open it.
            w.Put(AttemptOnly);
        }

        public static DoorOpenMessage Deserialize(NetReader r)
        {
            var msg = new DoorOpenMessage
            {
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                DoorName = r.GetString(),
                OpenForce = r.GetFloat(),
                OpenerPosX = r.GetFloat(),
                OpenerPosY = r.GetFloat(),
                OpenerPosZ = r.GetFloat(),
                AttemptOnly = r.GetBool()
            };
            return msg;
        }
    }

    public struct ConstructibleMessage
    {
        public float PosX, PosY, PosZ;
        public bool UseIngredients;
        public int OptionIndex;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(UseIngredients);
            w.Put(OptionIndex);
        }

        public static ConstructibleMessage Deserialize(NetReader r) => new ConstructibleMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            UseIngredients = r.GetBool(),
            OptionIndex = r.GetInt()
        };
    }

    public struct InteractiveItemSwitchMessage
    {
        public float PosX, PosY, PosZ;
        public bool IsOn;

        public void Serialize(NetWriter w) { w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(IsOn); }
        public static InteractiveItemSwitchMessage Deserialize(NetReader r) => new InteractiveItemSwitchMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            IsOn = r.GetBool()
        };
    }

    public struct PadlockUnlockMessage
    {
        public float PosX, PosY, PosZ;

        public void Serialize(NetWriter w) { w.Put(PosX); w.Put(PosY); w.Put(PosZ); }
        public static PadlockUnlockMessage Deserialize(NetReader r) => new PadlockUnlockMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat()
        };
    }

    public struct LockedUnlockMessage
    {
        public float PosX, PosY, PosZ;

        public void Serialize(NetWriter w) { w.Put(PosX); w.Put(PosY); w.Put(PosZ); }
        public static LockedUnlockMessage Deserialize(NetReader r) => new LockedUnlockMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat()
        };
    }

    public struct GameEventsFiredMessage
    {
        public float PosX, PosY, PosZ;
        /// <summary>GameObject name for reliable lookup when several events sit nearby.</summary>
        public string EventName;
        /// <summary>
        /// Player who triggered the one-shot. Personal Player.Instance effects
        /// (addOrRemoveInvItem, addRecipes, transport*) apply only for this id.
        /// 0 = late-join bulk / unknown → world effects only.
        /// </summary>
        public int ActorPlayerId;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(EventName ?? "");
            w.Put(ActorPlayerId);
        }
        public static GameEventsFiredMessage Deserialize(NetReader r) => new GameEventsFiredMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            EventName = r.GetString(),
            ActorPlayerId = r.GetInt()
        };
    }

    /// <summary>
    /// Host→late-joiner: already-fired one-shot GameEvents (conservative subset:
    /// <c>fired &amp;&amp; !multipleFire</c> on host components). Each entry reuses
    /// the live GameEventsFired identity (rounded pos + name).
    /// </summary>
    public struct GameEventsBulkMessage
    {
        public int EventCount;
        public float[] PosX;
        public float[] PosY;
        public float[] PosZ;
        public string[] EventNames;

        public const int MaxEvents = 2048;

        public void Serialize(NetWriter w)
        {
            w.Put(EventCount);
            for (int i = 0; i < EventCount; i++)
            {
                w.Put(PosX != null && i < PosX.Length ? PosX[i] : 0f);
                w.Put(PosY != null && i < PosY.Length ? PosY[i] : 0f);
                w.Put(PosZ != null && i < PosZ.Length ? PosZ[i] : 0f);
                w.Put(EventNames != null && i < EventNames.Length ? (EventNames[i] ?? "") : "");
            }
        }

        public static GameEventsBulkMessage Deserialize(NetReader r)
        {
            var msg = new GameEventsBulkMessage { EventCount = r.GetInt() };
            if (msg.EventCount < 0 || msg.EventCount > MaxEvents)
                msg.EventCount = 0;
            msg.PosX = new float[msg.EventCount];
            msg.PosY = new float[msg.EventCount];
            msg.PosZ = new float[msg.EventCount];
            msg.EventNames = new string[msg.EventCount];
            for (int i = 0; i < msg.EventCount; i++)
            {
                msg.PosX[i] = r.GetFloat();
                msg.PosY[i] = r.GetFloat();
                msg.PosZ[i] = r.GetFloat();
                msg.EventNames[i] = r.GetString();
            }
            return msg;
        }
    }

    public struct HideoutUpgradeMessage
    {
        public float PosX, PosY, PosZ;
        public bool IsOn;

        public void Serialize(NetWriter w) { w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(IsOn); }
        public static HideoutUpgradeMessage Deserialize(NetReader r) => new HideoutUpgradeMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            IsOn = r.GetBool()
        };
    }

    public struct DroppedItemSpawnMessage
    {
        public string Guid;
        public string PrefabPath;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ;
        public string ItemType;
        public int Amount;
        public float Durability;
        public int Ammo;
        /// <summary>ItemType is recipeFor when true.</summary>
        public bool IsRecipe;
        /// <summary>Workbench ItemUpgrade names.</summary>
        public string[] Upgrades;
        /// <summary>Flashlight / toggle on.</summary>
        public bool ShouldBeActive;

        public void Serialize(NetWriter w)
        {
            w.Put(Guid ?? string.Empty);
            w.Put(PrefabPath ?? string.Empty);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(RotX); w.Put(RotY); w.Put(RotZ);
            w.Put(ItemType ?? string.Empty);
            w.Put(Amount);
            w.Put(Durability);
            w.Put(Ammo);
            w.Put(IsRecipe);
            DWMPHorde.Sync.InvItemUpgradeWire.Write(w, Upgrades);
            w.Put(ShouldBeActive);
        }

        public static DroppedItemSpawnMessage Deserialize(NetReader r)
        {
            var msg = new DroppedItemSpawnMessage
            {
                Guid = r.GetString(),
                PrefabPath = r.GetString(),
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                RotX = r.GetFloat(),
                RotY = r.GetFloat(),
                RotZ = r.GetFloat(),
                ItemType = r.GetString(),
                Amount = r.GetInt(),
                Durability = r.GetFloat(),
                Ammo = r.GetInt(),
                IsRecipe = r.GetBool()
            };
            msg.Upgrades = DWMPHorde.Sync.InvItemUpgradeWire.Read(r);
            msg.ShouldBeActive = r.GetBool();
            return msg;
        }
    }

    public struct DroppedItemPickupMessage
    {
        public const byte ModeRemove = 0;
        /// <summary>Client→host: optimistic GUID drop claim (mirror WorldObjectRemoved claim).</summary>
        public const byte ModeClaimRequest = 1;
        /// <summary>Host→client: claim lost — refund optimistic grant.</summary>
        public const byte ModeClaimDeny = 2;

        public string Guid;
        public byte Mode;
        public int ClaimedByPlayerId;
        public string ItemType;
        public int Amount;
        public float Durability;
        public int Ammo;

        public void Serialize(NetWriter w)
        {
            w.Put(Guid ?? string.Empty);
            w.Put(Mode);
            w.Put(ClaimedByPlayerId);
            w.Put(ItemType ?? "");
            w.Put(Amount);
            w.Put(Durability);
            w.Put(Ammo);
        }

        public static DroppedItemPickupMessage Deserialize(NetReader r)
        {
            var msg = new DroppedItemPickupMessage
            {
                Guid = r.GetString(),
                Mode = r.GetByte(),
                ClaimedByPlayerId = r.GetInt(),
                ItemType = r.GetString(),
                Amount = r.GetInt(),
                Durability = r.GetFloat(),
                Ammo = r.GetInt()
            };
            return msg;
        }
    }

    public struct DeathBagSpawnMessage
    {
        public float PosX, PosY, PosZ;
        public bool InWater;
        public int ExpAmount;
        public int ItemCount;
        public string[] ItemTypes;
        public int[] ItemAmounts;
        public float[] ItemDurabilities;
        public int[] ItemAmmos;
        /// <summary>Stable ID. Empty only if the sender is broken.</summary>
        public string BagId;
        /// <summary>Per-entry recipe flag. ItemTypes stores recipeFor when true.</summary>
        public bool[] IsRecipe;
        /// <summary>Per-entry workbench ItemUpgrade names.</summary>
        public string[][] ItemUpgrades;
        /// <summary>Per-entry shouldBeActive (flashlight on).</summary>
        public bool[] ShouldBeActive;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(InWater);
            w.Put(ExpAmount);
            int count = ItemTypes != null ? ItemTypes.Length : 0;
            w.Put(count);
            for (int i = 0; i < count; i++)
            {
                w.Put(ItemTypes[i] ?? "");
                w.Put(ItemAmounts != null && i < ItemAmounts.Length ? ItemAmounts[i] : 1);
                w.Put(ItemDurabilities != null && i < ItemDurabilities.Length ? ItemDurabilities[i] : 0f);
                w.Put(ItemAmmos != null && i < ItemAmmos.Length ? ItemAmmos[i] : 0);
            }
            w.Put(BagId ?? "");
            for (int i = 0; i < count; i++)
                w.Put(IsRecipe != null && i < IsRecipe.Length && IsRecipe[i]);
            DWMPHorde.Sync.InvItemUpgradeWire.WriteMany(w, ItemUpgrades, count);
            for (int i = 0; i < count; i++)
                w.Put(ShouldBeActive != null && i < ShouldBeActive.Length && ShouldBeActive[i]);
        }

        public static DeathBagSpawnMessage Deserialize(NetReader r)
        {
            var msg = new DeathBagSpawnMessage
            {
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                InWater = r.GetBool(),
                ExpAmount = r.GetInt()
            };
            int count = r.GetInt();
            if (count < 0 || count > 4096)
                throw new System.IO.InvalidDataException("DeathBagSpawn item count " + count);
            msg.ItemCount = count;
            msg.ItemTypes = new string[count];
            msg.ItemAmounts = new int[count];
            msg.ItemDurabilities = new float[count];
            msg.ItemAmmos = new int[count];
            for (int i = 0; i < count; i++)
            {
                msg.ItemTypes[i] = r.GetString();
                msg.ItemAmounts[i] = r.GetInt();
                msg.ItemDurabilities[i] = r.GetFloat();
                msg.ItemAmmos[i] = r.GetInt();
            }
            msg.BagId = r.GetString();
            msg.IsRecipe = new bool[count];
            for (int i = 0; i < count; i++)
                msg.IsRecipe[i] = r.GetBool();
            msg.ItemUpgrades = DWMPHorde.Sync.InvItemUpgradeWire.ReadMany(r, count);
            msg.ShouldBeActive = new bool[count];
            for (int i = 0; i < count; i++)
                msg.ShouldBeActive[i] = r.GetBool();
            return msg;
        }
    }

    public struct DeathBagLootedMessage
    {
        public float PosX, PosY, PosZ;
        /// <summary>Stable ID. Prefer it over position for destroy matching.</summary>
        public string BagId;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(BagId ?? "");
        }

        public static DeathBagLootedMessage Deserialize(NetReader r) => new DeathBagLootedMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            BagId = r.GetString()
        };
    }

    public struct LocationEnterMessage
    {
        public string LocationName;
        /// <summary>Network PlayerId of the player who entered (required for 3+ forward).</summary>
        public int PlayerId;

        public void Serialize(NetWriter w)
        {
            w.Put(LocationName ?? "");
            w.Put(PlayerId);
        }

        public static LocationEnterMessage Deserialize(NetReader r) => new LocationEnterMessage
        {
            LocationName = r.GetString(),
            PlayerId = r.GetInt()
        };
    }

    public struct LocationExitMessage
    {
        /// <summary>World position after leaving the outside location.</summary>
        public float PosX, PosY, PosZ;
        public int PlayerId;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX);
            w.Put(PosY);
            w.Put(PosZ);
            w.Put(PlayerId);
        }

        public static LocationExitMessage Deserialize(NetReader r) => new LocationExitMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            PlayerId = r.GetInt()
        };
    }

    /// <summary>
    /// Examinable.examine co-op (4.11).
    /// Action 0 = client→host request (run host examine + story triggers).
    /// Action 1 = host→all state (examined / description pool flags only).
    /// </summary>
    public struct ExamineObjectMessage
    {
        public const byte ActionRequest = 0;
        public const byte ActionState = 1;

        public byte Action;
        public float PosX, PosY, PosZ;
        public string ObjectName;
        public bool Examined;
        public bool DisplayedDescriptionPool;

        public void Serialize(NetWriter w)
        {
            w.Put(Action);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(ObjectName ?? "");
            w.Put(Examined);
            w.Put(DisplayedDescriptionPool);
        }

        public static ExamineObjectMessage Deserialize(NetReader r) => new ExamineObjectMessage
        {
            Action = r.GetByte(),
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            ObjectName = r.GetString(),
            Examined = r.GetBool(),
            DisplayedDescriptionPool = r.GetBool()
        };
    }

    /// <summary>
    /// Client→host: CustomCursorAction onActivate (dream bed Lie down → endDream GE).
    /// </summary>
    public struct ActivateCursorActionMessage
    {
        public float PosX, PosY, PosZ;
        public string ObjectName;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(ObjectName ?? "");
        }

        public static ActivateCursorActionMessage Deserialize(NetReader r) =>
            new ActivateCursorActionMessage
            {
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                ObjectName = r.GetString()
            };
    }

    /// <summary>
    /// Host→requesting client: run OutsideLocations.prepareLocation (spawn if needed + transport).
    /// </summary>
    public struct LocationTransportMessage
    {
        public string LocationName;
        public bool FromWorld;

        public void Serialize(NetWriter w)
        {
            w.Put(LocationName ?? "");
            w.Put(FromWorld);
        }

        public static LocationTransportMessage Deserialize(NetReader r) =>
            new LocationTransportMessage
            {
                LocationName = r.GetString(),
                FromWorld = r.GetBool()
            };
    }

    /// <summary>
    /// ChainParent absolute health/attached. Pos-keyed (rounded like traps).
    /// Optional MaxHealth trailer for late-join.
    /// </summary>
    public struct ChainStateMessage
    {
        public float PosX, PosY, PosZ;
        public float Health;
        /// <summary>1 = attached, 0 = detached.</summary>
        public byte Attached;
        public bool HasMaxHealth;
        public float MaxHealth;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(Health);
            w.Put(Attached);
            w.Put(HasMaxHealth);
            if (HasMaxHealth)
                w.Put(MaxHealth);
        }

        public static ChainStateMessage Deserialize(NetReader r)
        {
            var msg = new ChainStateMessage
            {
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                Health = r.GetFloat(),
                Attached = r.GetByte(),
                HasMaxHealth = r.GetBool()
            };
            if (msg.HasMaxHealth)
                msg.MaxHealth = r.GetFloat();
            return msg;
        }
    }

    /// <summary>
    /// ShadowArmor absolute health / destroyed. Pos-keyed (rounded like chains).
    /// </summary>
    public struct ShadowArmorStateMessage
    {
        public float PosX, PosY, PosZ;
        public float Health;
        public float MaxHealth;
        /// <summary>1 = destroyed (call die), 0 = alive with Health/MaxHealth.</summary>
        public byte Destroyed;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(Health);
            w.Put(MaxHealth);
            w.Put(Destroyed);
        }

        public static ShadowArmorStateMessage Deserialize(NetReader r) =>
            new ShadowArmorStateMessage
            {
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                Health = r.GetFloat(),
                MaxHealth = r.GetFloat(),
                Destroyed = r.GetByte()
            };
    }

    /// <summary>
    /// World Door / Window / Item Burn state. Pos-keyed (rounded like chains).
    /// Optional RemainingTime trailer when Burning != 0.
    /// </summary>
    public struct WorldBurnStateMessage
    {
        public const byte TargetDoor = 0;
        public const byte TargetWindow = 1;
        public const byte TargetItem = 2;

        public float PosX, PosY, PosZ;
        /// <summary>0 = Door, 1 = Window, 2 = Item.</summary>
        public byte TargetType;
        /// <summary>1 = burning (AddComponent Burn), 0 = stop / destroy Burn.</summary>
        public byte Burning;
        public bool HasRemainingTime;
        public float RemainingTime;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(TargetType);
            w.Put(Burning);
            w.Put(HasRemainingTime);
            if (HasRemainingTime)
                w.Put(RemainingTime);
        }

        public static WorldBurnStateMessage Deserialize(NetReader r)
        {
            var msg = new WorldBurnStateMessage
            {
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                TargetType = r.GetByte(),
                Burning = r.GetByte(),
                HasRemainingTime = r.GetBool()
            };
            if (msg.HasRemainingTime)
                msg.RemainingTime = r.GetFloat();
            return msg;
        }
    }
}
