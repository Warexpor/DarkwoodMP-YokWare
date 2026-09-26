namespace DWMPHorde.Networking
{
    public enum ContainerAction : byte
    {
        TakeItem = 0,
        PlaceItem = 1,
        RemoveItem = 2,
        Searched = 3
    }

    public struct ContainerItemMessage
    {
        public float PosX, PosY, PosZ;
        public ContainerAction Action;
        public byte SlotIndex;
        public string ItemType;
        public int Amount;
        public float Durability;
        public int Ammo;
        public bool IsPlayerPlaced;
        /// <summary>0.8.62: ItemType is recipeFor when true. Absent on pre-0.8.62 packets.</summary>
        public bool IsRecipe;
        /// <summary>0.8.65: workbench ItemUpgrade names. Absent on pre-0.8.65 packets.</summary>
        public string[] Upgrades;
        /// <summary>0.8.66: flashlight / toggle on. Absent on pre-0.8.66 packets.</summary>
        public bool ShouldBeActive;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put((byte)Action);
            w.Put(SlotIndex);
            w.Put(ItemType ?? "");
            w.Put(Amount);
            w.Put(Durability);
            w.Put(Ammo);
            w.Put(IsPlayerPlaced);
            w.Put(IsRecipe);
            DWMPHorde.Sync.InvItemUpgradeWire.Write(w, Upgrades);
            w.Put(ShouldBeActive);
        }

        public static ContainerItemMessage Deserialize(NetReader r)
        {
            var msg = new ContainerItemMessage
            {
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                Action = (ContainerAction)r.GetByte(),
                SlotIndex = r.GetByte(),
                ItemType = r.GetString(),
                Amount = r.GetInt(),
                Durability = r.GetFloat(),
                Ammo = r.GetInt(),
                IsPlayerPlaced = r.GetBool(),
                ShouldBeActive = false
            };
            if (r.AvailableBytes >= 1)
                msg.IsRecipe = r.GetBool();
            msg.Upgrades = DWMPHorde.Sync.InvItemUpgradeWire.TryRead(r);
            if (r.AvailableBytes >= 1)
                msg.ShouldBeActive = r.GetBool();
            return msg;
        }
    }

    public struct ContainerStateRequestMessage
    {
        public float PosX, PosY, PosZ;
        public int TargetEntityHash;

        public void Serialize(NetWriter w) { w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(TargetEntityHash); }
        public static ContainerStateRequestMessage Deserialize(NetReader r) => new ContainerStateRequestMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            TargetEntityHash = r.GetInt()
        };
    }

    /// <summary>
    /// Host→client deny for container take OR place races.
    /// Mode 0 (default / legacy): take/remove lost — remove optimistic loot from player.
    /// Mode 1: place lost (slot type clash / bad amount) — add item back to player;
    /// host also pushes ContainerStateSync so the container slot is corrected.
    /// Durability/Ammo trailers are optional (AvailableBytes); used for place refund.
    /// </summary>
    public struct ContainerTakeDeniedMessage
    {
        public const byte ModeTakeRefund = 0;
        public const byte ModePlaceRefund = 1;

        public float PosX, PosY, PosZ;
        public byte SlotIndex;
        public string ItemType;
        public int Amount;
        /// <summary>0 = take refund (remove from player); 1 = place refund (add to player).</summary>
        public byte Mode;
        public float Durability;
        public int Ammo;
        /// <summary>0.8.65: place-refund workbench upgrades. Absent on pre-0.8.65.</summary>
        public string[] Upgrades;
        /// <summary>0.8.66: place-refund shouldBeActive. Absent on pre-0.8.66.</summary>
        public bool ShouldBeActive;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(SlotIndex);
            w.Put(ItemType ?? "");
            w.Put(Amount);
            w.Put(Mode);
            w.Put(Durability);
            w.Put(Ammo);
            DWMPHorde.Sync.InvItemUpgradeWire.Write(w, Upgrades);
            w.Put(ShouldBeActive);
        }

        public static ContainerTakeDeniedMessage Deserialize(NetReader r)
        {
            var msg = new ContainerTakeDeniedMessage
            {
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                SlotIndex = r.GetByte(),
                ItemType = r.GetString(),
                Amount = r.GetInt(),
                Mode = ModeTakeRefund,
                Durability = 0f,
                Ammo = 0,
                ShouldBeActive = false
            };
            // Trailer: Mode [+ Durability + Ammo] [+ upgrades] [+ shouldBeActive].
            // Legacy 0.8.44 packets stop after Amount.
            if (r.AvailableBytes >= 1)
                msg.Mode = r.GetByte();
            if (r.AvailableBytes >= 4)
                msg.Durability = r.GetFloat();
            if (r.AvailableBytes >= 4)
                msg.Ammo = r.GetInt();
            msg.Upgrades = DWMPHorde.Sync.InvItemUpgradeWire.TryRead(r);
            if (r.AvailableBytes >= 1)
                msg.ShouldBeActive = r.GetBool();
            return msg;
        }
    }

    public struct SlotStateEntry
    {
        public byte SlotIndex;
        public string ItemType;
        public int Amount;
        public float Durability;
        public int Ammo;
        /// <summary>0.8.62: vanilla InvItemClass.isRecipe. ItemType is recipeFor when set.</summary>
        public bool IsRecipe;
        /// <summary>0.8.65: workbench ItemUpgrade names. Null/absent = pre-0.8.65.</summary>
        public string[] Upgrades;
        /// <summary>0.8.66: shouldBeActive (flashlight on). Absent on pre-0.8.66.</summary>
        public bool ShouldBeActive;
    }

    public struct ContainerStateSyncMessage
    {
        public float PosX, PosY, PosZ;
        public int EntityHash;
        public int SlotCount;
        public SlotStateEntry[] Slots;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(EntityHash);
            w.Put(SlotCount);
            for (int i = 0; i < SlotCount; i++)
            {
                w.Put(Slots[i].SlotIndex);
                w.Put(Slots[i].ItemType ?? "");
                w.Put(Slots[i].Amount);
                w.Put(Slots[i].Durability);
                w.Put(Slots[i].Ammo);
            }
            // 0.8.62 recipe trailer (one bool per slot). Dual-deploy writes always.
            for (int i = 0; i < SlotCount; i++)
                w.Put(Slots[i].IsRecipe);
            // 0.8.65 upgrade trailer (count+names per slot). Dual-deploy writes always.
            for (int i = 0; i < SlotCount; i++)
                DWMPHorde.Sync.InvItemUpgradeWire.Write(w, i < Slots.Length ? Slots[i].Upgrades : null);
            // 0.8.66 shouldBeActive trailer (one bool per slot). Dual-deploy writes always.
            for (int i = 0; i < SlotCount; i++)
                w.Put(i < Slots.Length && Slots[i].ShouldBeActive);
        }

        public static ContainerStateSyncMessage Deserialize(NetReader r)
        {
            var msg = new ContainerStateSyncMessage
            {
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                EntityHash = r.GetInt(),
                SlotCount = r.GetInt()
            };
            if (msg.SlotCount < 0 || msg.SlotCount > 4096) msg.SlotCount = 0;
            msg.Slots = new SlotStateEntry[msg.SlotCount];
            for (int i = 0; i < msg.SlotCount; i++)
            {
                msg.Slots[i] = new SlotStateEntry
                {
                    SlotIndex = r.GetByte(),
                    ItemType = r.GetString(),
                    Amount = r.GetInt(),
                    Durability = r.GetFloat(),
                    Ammo = r.GetInt()
                };
            }
            if (msg.SlotCount > 0 && r.AvailableBytes >= msg.SlotCount)
            {
                for (int i = 0; i < msg.SlotCount; i++)
                    msg.Slots[i].IsRecipe = r.GetBool();
            }
            if (msg.SlotCount > 0 && r.AvailableBytes >= 1)
            {
                for (int i = 0; i < msg.SlotCount; i++)
                    msg.Slots[i].Upgrades = DWMPHorde.Sync.InvItemUpgradeWire.TryRead(r);
            }
            if (msg.SlotCount > 0 && r.AvailableBytes >= msg.SlotCount)
            {
                for (int i = 0; i < msg.SlotCount; i++)
                    msg.Slots[i].ShouldBeActive = r.GetBool();
            }
            return msg;
        }
    }
}
