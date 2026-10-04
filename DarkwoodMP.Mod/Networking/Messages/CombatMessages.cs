namespace DWMPHorde.Networking
{
    public struct FriendlyFireMessage
    {
        public int Damage;
        public float AttackerPosX, AttackerPosY, AttackerPosZ;
        public bool CanCutInHalf;
        public int AttackerPlayerId;
        public int VictimPlayerId;
        /// <summary>Melee weapon status effects (<c>MeleeSensor.effects</c>) for the victim. Trailing count + effects; null = none.</summary>
        public SensorEffectWire[] Effects;

        public void Serialize(NetWriter w)
        {
            w.Put(Damage);
            w.Put(AttackerPosX); w.Put(AttackerPosY); w.Put(AttackerPosZ);
            w.Put(CanCutInHalf);
            w.Put(AttackerPlayerId);
            w.Put(VictimPlayerId);
            SensorEffectWire.WriteList(w, Effects);
        }

        public static FriendlyFireMessage Deserialize(NetReader r)
        {
            var msg = new FriendlyFireMessage
            {
                Damage = r.GetInt(),
                AttackerPosX = r.GetFloat(),
                AttackerPosY = r.GetFloat(),
                AttackerPosZ = r.GetFloat(),
                CanCutInHalf = r.GetBool(),
                AttackerPlayerId = r.GetInt(),
                VictimPlayerId = r.GetInt()
            };
            msg.Effects = SensorEffectWire.ReadList(r);
            return msg;
        }
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
        /// <summary>Stable throw instance id (0 = none).</summary>
        public int ThrowId;
        /// <summary>Seconds until light/projectile expires (0 = use prefab default).</summary>
        public float LongevitySec;
        /// <summary>
        /// Vanilla <see cref="ThrownItem.landTarget"/> after throwItem (valid when <see cref="HasLandTarget"/>).
        /// Peers use this so checkIfWantToLand / flyTime match the thrower's arc.
        /// </summary>
        public float LandX, LandY, LandZ;
        /// <summary>True when the Land* fields carry a real land target.</summary>
        public bool HasLandTarget;
        /// <summary>A weapon picked back up after the throw (vanilla recoverableAfterThrown): its wear and upgrades ride along.</summary>
        public bool Recoverable;
        public float Durability;
        public string[] Upgrades;

        public void Serialize(NetWriter w)
        {
            w.Put(ItemType ?? "");
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(AimY);
            w.Put(Distance);
            w.Put(VelX); w.Put(VelY); w.Put(VelZ);
            w.Put(ThrowId);
            w.Put(LongevitySec);
            w.Put(HasLandTarget);
            w.Put(LandX); w.Put(LandY); w.Put(LandZ);
            w.Put(Recoverable);
            w.Put(Durability);
            DWMPHorde.Sync.InvItemUpgradeWire.Write(w, Upgrades);
        }

        public static ThrowableSpawnMessage Deserialize(NetReader r)
        {
            var msg = new ThrowableSpawnMessage
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
            msg.ThrowId = r.GetInt();
            msg.LongevitySec = r.GetFloat();
            msg.HasLandTarget = r.GetBool();
            msg.LandX = r.GetFloat();
            msg.LandY = r.GetFloat();
            msg.LandZ = r.GetFloat();
            msg.Recoverable = r.GetBool();
            msg.Durability = r.GetFloat();
            msg.Upgrades = DWMPHorde.Sync.InvItemUpgradeWire.Read(r);
            return msg;
        }
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
    /// <summary>
    /// Host→clients: one host enemy attack frame (vanilla <c>Character.melee</c>,
    /// <c>rangedAttack</c> or <c>spawnProjectile</c>). Each client re-creates the attack on
    /// its own copy of the enemy, and only that client's own player can be hit by it
    /// ("defender decides"); the host copy no longer hits remote-player stand-ins.
    /// </summary>
    public struct EnemyAttackMessage
    {
        /// <summary>Melee sensor; <see cref="Name"/> is the prefab name in the "Sensors" pool.</summary>
        public const byte KindMelee = 0;
        /// <summary>Ranged <c>SensorType</c>; <see cref="Name"/> is the <c>SensorType.name</c>.</summary>
        public const byte KindRangedSensor = 1;
        /// <summary>Activity projectile; <see cref="Name"/> is the pooled prefab name.</summary>
        public const byte KindPooledProjectile = 2;

        public short EntityId;
        public byte Kind;
        public string Name;
        /// <summary>Host clock when the attack fired (same clock as <see cref="EntityStateMessage.HostTime"/>).</summary>
        public float HostTime;
        /// <summary>Host position of the attacker (melee) or of the projectile spawn (ranged).</summary>
        public float PosX, PosY, PosZ;
        /// <summary>Melee: attacker yaw. Ranged: projectile yaw, accuracy roll included.</summary>
        public float RotY;
        /// <summary>Final damage, the attacker's strength modifier already applied for melee.</summary>
        public float Damage;
        /// <summary>Host attack clip and frame at the attack moment (presentation).</summary>
        public string Clip;
        public short ClipFrame;
        /// <summary>Ranged ThrownItem flight; the fields below are written only when true.</summary>
        public bool Thrown;
        public float VelX, VelY, VelZ;
        public float LandX, LandY, LandZ;
        public float FlyTime;
        public float Drag;

        public void Serialize(NetWriter w)
        {
            w.Put(EntityId);
            w.Put(Kind);
            w.Put(Name ?? "");
            w.Put(HostTime);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(RotY);
            w.Put(Damage);
            w.Put(Clip ?? "");
            w.Put(ClipFrame);
            w.Put(Thrown);
            if (Thrown)
            {
                w.Put(VelX); w.Put(VelY); w.Put(VelZ);
                w.Put(LandX); w.Put(LandY); w.Put(LandZ);
                w.Put(FlyTime);
                w.Put(Drag);
            }
        }

        public static EnemyAttackMessage Deserialize(NetReader r)
        {
            var m = new EnemyAttackMessage
            {
                EntityId = r.GetShort(),
                Kind = r.GetByte(),
                Name = r.GetString(),
                HostTime = r.GetFloat(),
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                RotY = r.GetFloat(),
                Damage = r.GetFloat(),
                Clip = r.GetString(),
                ClipFrame = r.GetShort(),
                Thrown = r.GetBool()
            };
            if (m.Thrown)
            {
                m.VelX = r.GetFloat(); m.VelY = r.GetFloat(); m.VelZ = r.GetFloat();
                m.LandX = r.GetFloat(); m.LandY = r.GetFloat(); m.LandZ = r.GetFloat();
                m.FlyTime = r.GetFloat();
                m.Drag = r.GetFloat();
            }
            return m;
        }
    }

    /// <summary>
    /// Client→host: this client's own player was hit by an enemy attack it re-created from
    /// <see cref="EnemyAttackMessage"/>. The damage is already applied on the client; the
    /// host only shows the hit (sound, blood) on that player's stand-in for everyone else.
    /// </summary>
    public struct EnemyHitConfirmMessage
    {
        public short EntityId;
        public byte Kind;
        public int Damage;
        public float HitX, HitY, HitZ;

        public void Serialize(NetWriter w)
        {
            w.Put(EntityId);
            w.Put(Kind);
            w.Put(Damage);
            w.Put(HitX); w.Put(HitY); w.Put(HitZ);
        }

        public static EnemyHitConfirmMessage Deserialize(NetReader r) => new EnemyHitConfirmMessage
        {
            EntityId = r.GetShort(),
            Kind = r.GetByte(),
            Damage = r.GetInt(),
            HitX = r.GetFloat(),
            HitY = r.GetFloat(),
            HitZ = r.GetFloat()
        };
    }
}
