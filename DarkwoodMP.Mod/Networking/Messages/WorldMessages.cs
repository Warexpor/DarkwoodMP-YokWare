using UnityEngine;

namespace DWMPHorde.Networking
{
    public struct LightStateMessage
    {
        public float PosX, PosY, PosZ;
        public bool IsOn;
        public string ItemName;
        public string ItemType;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(IsOn);
            w.Put(ItemName ?? string.Empty);
            w.Put(ItemType ?? string.Empty);
        }

        public static LightStateMessage Deserialize(NetReader r) => new LightStateMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            IsOn = r.GetBool(),
            ItemName = r.GetString(),
            ItemType = r.GetString()
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

    public struct EntitySnapshotNet
    {
        public short Index;
        public float PosX, PosY, PosZ;
        public float RotY;
        public string Clip;
        public short ClipFrame;
        public bool Alive;
        public byte HealthPct;
        public string EntityName;
        public string PrefabPath;

        public void Serialize(NetWriter w)
        {
            w.Put(Index);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(RotY);
            w.Put(Clip ?? "");
            w.Put(ClipFrame);
            w.Put(Alive);
            w.Put(HealthPct);
            w.Put(EntityName ?? "");
            w.Put(PrefabPath ?? "");
        }

        public static EntitySnapshotNet Deserialize(NetReader r) => new EntitySnapshotNet
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
            EntityName = r.GetString(),
            PrefabPath = r.GetString()
        };
    }

    public struct EntityStateMessage
    {
        public EntitySnapshotNet[] Entities;

        public void Serialize(NetWriter w)
        {
            int count = Entities != null ? Entities.Length : 0;
            w.Put(count);
            for (int i = 0; i < count; i++)
                Entities[i].Serialize(w);
        }

        public static EntityStateMessage Deserialize(NetReader r)
        {
            int count = r.GetInt();
            if (count < 0 || count > 4096) count = 0;
            var arr = new EntitySnapshotNet[count];
            for (int i = 0; i < count; i++)
                arr[i] = EntitySnapshotNet.Deserialize(r);
            return new EntityStateMessage { Entities = arr };
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

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(RotX); w.Put(RotY); w.Put(RotZ);
            w.Put(IsDragging);
            w.Put(ObjectName ?? "");
            w.Put(ItemType ?? "");
            w.Put(ClaimedByPlayerId);
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
            ClaimedByPlayerId = r.GetInt()
        };
    }

    public struct WorldObjectRemovedMessage
    {
        public float PosX, PosY, PosZ;
        public string ObjectName;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(ObjectName ?? "");
        }

        public static WorldObjectRemovedMessage Deserialize(NetReader r) => new WorldObjectRemovedMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            ObjectName = r.GetString()
        };
    }

    public struct SawStateMessage
    {
        public float PosX, PosY, PosZ;
        public float Fuel;
        public int WoodLogAmount;
        public int WoodAmount;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(Fuel);
            w.Put(WoodLogAmount);
            w.Put(WoodAmount);
        }

        public static SawStateMessage Deserialize(NetReader r) => new SawStateMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            Fuel = r.GetFloat(),
            WoodLogAmount = r.GetInt(),
            WoodAmount = r.GetInt()
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

        public void Serialize(NetWriter w) { w.Put(PosX); w.Put(PosY); w.Put(PosZ); }
        public static TrapTriggeredMessage Deserialize(NetReader r) => new TrapTriggeredMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat()
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

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(DoorName ?? "");
        }

        public static DoorOpenMessage Deserialize(NetReader r) => new DoorOpenMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            DoorName = r.GetString()
        };
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

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(EventName ?? "");
        }
        public static GameEventsFiredMessage Deserialize(NetReader r) => new GameEventsFiredMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            EventName = r.GetString()
        };
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
        }

        public static DroppedItemSpawnMessage Deserialize(NetReader r) => new DroppedItemSpawnMessage
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
            Ammo = r.GetInt()
        };
    }

    public struct DroppedItemPickupMessage
    {
        public string Guid;

        public void Serialize(NetWriter w) => w.Put(Guid ?? string.Empty);
        public static DroppedItemPickupMessage Deserialize(NetReader r) => new DroppedItemPickupMessage { Guid = r.GetString() };
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
        /// <summary>Stable id (protocol 6+). Empty only if sender is broken.</summary>
        public string BagId;

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
            if (count < 0 || count > 4096) count = 0;
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
            return msg;
        }
    }

    public struct DeathBagLootedMessage
    {
        public float PosX, PosY, PosZ;
        /// <summary>Stable id (protocol 6+). Prefer over position for destroy matching.</summary>
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
}
