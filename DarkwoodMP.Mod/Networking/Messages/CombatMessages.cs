namespace DWMPHorde.Networking
{
    public struct FriendlyFireMessage
    {
        public int Damage;
        public float AttackerPosX, AttackerPosY, AttackerPosZ;
        public bool CanCutInHalf;
        public int AttackerPlayerId;
        public int VictimPlayerId;

        public void Serialize(NetWriter w)
        {
            w.Put(Damage);
            w.Put(AttackerPosX); w.Put(AttackerPosY); w.Put(AttackerPosZ);
            w.Put(CanCutInHalf);
            w.Put(AttackerPlayerId);
            w.Put(VictimPlayerId);
        }

        public static FriendlyFireMessage Deserialize(NetReader r) => new FriendlyFireMessage
        {
            Damage = r.GetInt(),
            AttackerPosX = r.GetFloat(),
            AttackerPosY = r.GetFloat(),
            AttackerPosZ = r.GetFloat(),
            CanCutInHalf = r.GetBool(),
            AttackerPlayerId = r.GetInt(),
            VictimPlayerId = r.GetInt()
        };
    }

    public struct BulletImpactMessage
    {
        public string PrefabName;
        public string PoolName;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ;

        public void Serialize(NetWriter w)
        {
            w.Put(PrefabName ?? "");
            w.Put(PoolName ?? "");
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(RotX); w.Put(RotY); w.Put(RotZ);
        }

        public static BulletImpactMessage Deserialize(NetReader r) => new BulletImpactMessage
        {
            PrefabName = r.GetString(),
            PoolName = r.GetString(),
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            RotX = r.GetFloat(),
            RotY = r.GetFloat(),
            RotZ = r.GetFloat()
        };
    }

    public struct ThrowableSpawnMessage
    {
        public string ItemType;
        public float PosX, PosY, PosZ;
        public float AimY;
        public float Distance;
        public float VelX, VelY, VelZ;

        public void Serialize(NetWriter w)
        {
            w.Put(ItemType ?? "");
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(AimY);
            w.Put(Distance);
            w.Put(VelX); w.Put(VelY); w.Put(VelZ);
        }

        public static ThrowableSpawnMessage Deserialize(NetReader r) => new ThrowableSpawnMessage
        {
            ItemType = r.GetString(),
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            AimY = r.GetFloat(),
            Distance = r.GetFloat(),
            VelX = r.GetFloat(),
            VelY = r.GetFloat(),
            VelZ = r.GetFloat()
        };
    }

    public struct ExplosionTriggerMessage
    {
        public float PosX, PosY, PosZ;
        public string ObjectName;
        public bool Flaming;
        public string PrefabName;
        public string SoundId;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(ObjectName ?? "");
            w.Put(Flaming);
            w.Put(PrefabName ?? "");
            w.Put(SoundId ?? "");
        }

        public static ExplosionTriggerMessage Deserialize(NetReader r) => new ExplosionTriggerMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            ObjectName = r.GetString(),
            Flaming = r.GetBool(),
            PrefabName = r.GetString(),
            SoundId = r.GetString()
        };
    }

    public struct MeleeWorldHitMessage
    {
        public byte TargetType;
        public float PosX, PosY, PosZ;
        public float AttackerPosX, AttackerPosY, AttackerPosZ;
        public int Damage;

        public void Serialize(NetWriter w)
        {
            w.Put(TargetType);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(AttackerPosX); w.Put(AttackerPosY); w.Put(AttackerPosZ);
            w.Put(Damage);
        }

        public static MeleeWorldHitMessage Deserialize(NetReader r) => new MeleeWorldHitMessage
        {
            TargetType = r.GetByte(),
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            AttackerPosX = r.GetFloat(),
            AttackerPosY = r.GetFloat(),
            AttackerPosZ = r.GetFloat(),
            Damage = r.GetInt()
        };
    }
}
