using UnityEngine;

namespace DWMPHorde.Networking
{
    public struct HandshakeMessage
    {
        public int ProtocolVersion;
        public short PlayerId;
        /// <summary>
        /// Client→host: true after join-pipeline offline load (or any reconnect already in chapter).
        /// Host skips world share and only arms late-join bulk (phase 3 co-op connect).
        /// </summary>
        public bool AlreadyInWorld;
        /// <summary>
        /// Host→client: host's stable player id (may be ≠1 after host-grant migration).
        /// Client→host: unused (0).
        /// </summary>
        public short HostPlayerId;

        public void Serialize(NetWriter writer)
        {
            writer.Put(ProtocolVersion);
            writer.Put(PlayerId);
            writer.Put(AlreadyInWorld);
            writer.Put(HostPlayerId);
        }

        public static HandshakeMessage Deserialize(NetReader reader)
        {
            var msg = new HandshakeMessage
            {
                ProtocolVersion = reader.GetInt(),
                PlayerId = reader.GetShort(),
            };
            // Forward-compat: older peers omitted trailers (both boxes should be same DLL).
            if (reader.AvailableBytes >= 1)
                msg.AlreadyInWorld = reader.GetBool();
            if (reader.AvailableBytes >= 2)
                msg.HostPlayerId = reader.GetShort();
            return msg;
        }
    }

    public struct WorldSessionMessage
    {
        public string SaveSlotName;
        public int WorldSeed;
        public int ChapterId;
        public int DayIndex;
        public string BigLocationName;

        public void Serialize(NetWriter writer)
        {
            writer.Put(SaveSlotName ?? string.Empty);
            writer.Put(WorldSeed);
            writer.Put(ChapterId);
            writer.Put(DayIndex);
            writer.Put(BigLocationName ?? string.Empty);
        }

        public static WorldSessionMessage Deserialize(NetReader reader)
        {
            return new WorldSessionMessage
            {
                SaveSlotName = reader.GetString(),
                WorldSeed = reader.GetInt(),
                ChapterId = reader.GetInt(),
                DayIndex = reader.GetInt(),
                BigLocationName = reader.GetString()
            };
        }
    }

    public struct PlayerStateMessage
    {
        public int PlayerId;
        public float PosX, PosY, PosZ;
        public float VelX, VelZ;
        public byte LocomotionState;
        public bool FlipX;
        public bool Running;
        public short LegFacingY;
        public bool ReverseLegs;
        public short TorsoFacingY;
        public string TorsoClip;
        public string LegsClip;
        public bool InBearTrap;
        public bool HasLightProtection;
        public bool AfterNightActive;
        public short CurrentFrame;

        // Protocol 19 continuous lights — conditional payload via LightFlags.
        // bit0 Flare/Match held | bit1 Flashlight | bit2 FlareParams | bit3 FlashParams | bit4 ItemType
        // bit5 MatchKind (held is match not flare) | bit6 Remain01 present | bit7 FlashAim present
        public const byte LightFlagFlare = 1;
        public const byte LightFlagFlashlight = 2;
        public const byte LightFlagFlareParams = 4;
        public const byte LightFlagFlashParams = 8;
        public const byte LightFlagFlareItemType = 16;
        public const byte LightFlagMatch = 32;
        public const byte LightFlagRemain = 64;
        public const byte LightFlagFlashAim = 128;

        public byte LightFlags;
        public bool FlareActive;
        public bool FlashlightActive;
        public bool MatchActive;
        public bool FlareHasParams;
        public bool FlashHasParams;
        public bool FlareHasItemType;

        public float FlareLocalX, FlareLocalY, FlareLocalZ;
        public float FlareRadius;
        public float FlareIntensity;
        public float FlareColorR, FlareColorG, FlareColorB;
        public string FlareItemType;
        /// <summary>0–255 remaining life for flare/match; only valid when LightFlagRemain set.</summary>
        public byte HeldLightRemain01;

        public float FlashRadius;
        public float FlashIntensity;
        public float FlashColorR, FlashColorG, FlashColorB;
        /// <summary>Flashlight cone yaw degrees (0–360); valid when LightFlagFlashAim set.</summary>
        public short FlashAimY;

        /// <summary>Trap instance this player occupies (0 = none). Trailer after AfterNightActive.</summary>
        public int TrapNetId;

        public void Serialize(NetWriter writer)
        {
            writer.Put(PlayerId);
            writer.Put(PosX); writer.Put(PosY); writer.Put(PosZ);
            writer.Put(VelX); writer.Put(VelZ);
            writer.Put(LocomotionState);
            writer.Put(FlipX);
            writer.Put(Running);
            writer.Put(LegFacingY);
            writer.Put(ReverseLegs);
            writer.Put(TorsoFacingY);
            writer.Put(TorsoClip);
            writer.Put(LegsClip);
            writer.Put(CurrentFrame);
            writer.Put(InBearTrap);
            writer.Put(HasLightProtection);
            writer.Put(LightFlags);
            if ((LightFlags & LightFlagFlare) != 0)
            {
                writer.Put(FlareLocalX); writer.Put(FlareLocalY); writer.Put(FlareLocalZ);
                if ((LightFlags & LightFlagFlareParams) != 0)
                {
                    writer.Put(FlareRadius);
                    writer.Put(FlareIntensity);
                    writer.Put(FlareColorR); writer.Put(FlareColorG); writer.Put(FlareColorB);
                }
                if ((LightFlags & LightFlagFlareItemType) != 0)
                    writer.Put(FlareItemType ?? string.Empty);
            }
            if ((LightFlags & LightFlagFlashlight) != 0)
            {
                if ((LightFlags & LightFlagFlashParams) != 0)
                {
                    writer.Put(FlashRadius);
                    writer.Put(FlashIntensity);
                    writer.Put(FlashColorR); writer.Put(FlashColorG); writer.Put(FlashColorB);
                }
            }
            writer.Put(AfterNightActive);
            // Optional trailer (proto 19 extend): TrapNetId + remain + flash aim
            writer.Put(TrapNetId);
            writer.Put(HeldLightRemain01);
            writer.Put(FlashAimY);
        }

        public static PlayerStateMessage Deserialize(NetReader reader)
        {
            var msg = new PlayerStateMessage
            {
                PlayerId = reader.GetInt(),
                PosX = reader.GetFloat(),
                PosY = reader.GetFloat(),
                PosZ = reader.GetFloat(),
                VelX = reader.GetFloat(),
                VelZ = reader.GetFloat(),
                LocomotionState = reader.GetByte(),
                FlipX = reader.GetBool(),
                Running = reader.GetBool(),
                LegFacingY = reader.GetShort(),
                ReverseLegs = reader.GetBool(),
                TorsoFacingY = reader.GetShort(),
                TorsoClip = reader.GetString(),
                LegsClip = reader.GetString(),
                CurrentFrame = reader.GetShort(),
                InBearTrap = reader.GetBool(),
                HasLightProtection = reader.GetBool(),
                LightFlags = reader.GetByte()
            };

            msg.FlareActive = (msg.LightFlags & LightFlagFlare) != 0
                && (msg.LightFlags & LightFlagMatch) == 0;
            msg.MatchActive = (msg.LightFlags & LightFlagFlare) != 0
                && (msg.LightFlags & LightFlagMatch) != 0;
            msg.FlashlightActive = (msg.LightFlags & LightFlagFlashlight) != 0;
            msg.FlareHasParams = (msg.LightFlags & LightFlagFlareParams) != 0;
            msg.FlashHasParams = (msg.LightFlags & LightFlagFlashParams) != 0;
            msg.FlareHasItemType = (msg.LightFlags & LightFlagFlareItemType) != 0;

            if ((msg.LightFlags & LightFlagFlare) != 0)
            {
                msg.FlareLocalX = reader.GetFloat();
                msg.FlareLocalY = reader.GetFloat();
                msg.FlareLocalZ = reader.GetFloat();
                if (msg.FlareHasParams)
                {
                    msg.FlareRadius = reader.GetFloat();
                    msg.FlareIntensity = reader.GetFloat();
                    msg.FlareColorR = reader.GetFloat();
                    msg.FlareColorG = reader.GetFloat();
                    msg.FlareColorB = reader.GetFloat();
                }
                if (msg.FlareHasItemType)
                    msg.FlareItemType = reader.GetString();
            }
            if (msg.FlashlightActive && msg.FlashHasParams)
            {
                msg.FlashRadius = reader.GetFloat();
                msg.FlashIntensity = reader.GetFloat();
                msg.FlashColorR = reader.GetFloat();
                msg.FlashColorG = reader.GetFloat();
                msg.FlashColorB = reader.GetFloat();
            }

            msg.AfterNightActive = reader.GetBool();
            // Trailer: TrapNetId(int) + Remain(byte) + FlashAimY(short) = 7 bytes
            if (reader.AvailableBytes >= 7)
            {
                msg.TrapNetId = reader.GetInt();
                msg.HeldLightRemain01 = reader.GetByte();
                msg.FlashAimY = reader.GetShort();
            }
            return msg;
        }
    }

    public struct PlayerAttackMessage
    {
        public short TargetNameHash;
        public int Damage;
        public float AttackerPosX, AttackerPosY, AttackerPosZ;
        public string TargetName;
        public float TargetPosX, TargetPosY, TargetPosZ;
        public bool CanCutInHalf;

        public void Serialize(NetWriter w)
        {
            w.Put(TargetNameHash);
            w.Put(Damage);
            w.Put(AttackerPosX); w.Put(AttackerPosY); w.Put(AttackerPosZ);
            w.Put(TargetName ?? "");
            w.Put(TargetPosX); w.Put(TargetPosY); w.Put(TargetPosZ);
            w.Put(CanCutInHalf);
        }

        public static PlayerAttackMessage Deserialize(NetReader r) => new PlayerAttackMessage
        {
            TargetNameHash = r.GetShort(),
            Damage = r.GetInt(),
            AttackerPosX = r.GetFloat(),
            AttackerPosY = r.GetFloat(),
            AttackerPosZ = r.GetFloat(),
            TargetName = r.GetString(),
            TargetPosX = r.GetFloat(),
            TargetPosY = r.GetFloat(),
            TargetPosZ = r.GetFloat(),
            CanCutInHalf = r.GetBool()
        };
    }

    public struct DamagePlayerMessage
    {
        public int Damage;
        public float AttackerPosX, AttackerPosY, AttackerPosZ;
        public bool CanCutInHalf;
        public bool ShowRedScreen;

        public void Serialize(NetWriter w)
        {
            w.Put(Damage);
            w.Put(AttackerPosX); w.Put(AttackerPosY); w.Put(AttackerPosZ);
            w.Put(CanCutInHalf);
            w.Put(ShowRedScreen);
        }

        public static DamagePlayerMessage Deserialize(NetReader r) => new DamagePlayerMessage
        {
            Damage = r.GetInt(),
            AttackerPosX = r.GetFloat(),
            AttackerPosY = r.GetFloat(),
            AttackerPosZ = r.GetFloat(),
            CanCutInHalf = r.GetBool(),
            ShowRedScreen = r.GetBool()
        };
    }

    public struct PlayerDiedMessage
    {
        public float PosX, PosY, PosZ;
        public bool IsNight;
        public bool HasDropBag;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(IsNight);
            w.Put(HasDropBag);
        }
        public static PlayerDiedMessage Deserialize(NetReader r) => new PlayerDiedMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            IsNight = r.GetBool(),
            HasDropBag = r.GetBool()
        };
    }

    public struct PlayerSoundMessage
    {
        public float Range;
        public bool DangerousSound;
        public float Volume;
        public bool Gunshot;

        public void Serialize(NetWriter w)
        {
            w.Put(Range);
            w.Put(DangerousSound);
            w.Put(Volume);
            w.Put(Gunshot);
        }

        public static PlayerSoundMessage Deserialize(NetReader r) => new PlayerSoundMessage
        {
            Range = r.GetFloat(),
            DangerousSound = r.GetBool(),
            Volume = r.GetFloat(),
            Gunshot = r.GetBool()
        };
    }

    public struct PlayerScareMessage
    {
        public float Range;

        public void Serialize(NetWriter w) => w.Put(Range);

        public static PlayerScareMessage Deserialize(NetReader r) => new PlayerScareMessage
        {
            Range = r.GetFloat()
        };
    }

    public struct PlayerEffectSyncMessage
    {
        public byte Flags;

        public bool HasShadowWard
        {
            get => (Flags & 1) != 0;
            set => Flags = (byte)((Flags & ~1) | (value ? 1 : 0));
        }
        public bool HasForestSpiritWard
        {
            get => (Flags & 2) != 0;
            set => Flags = (byte)((Flags & ~2) | (value ? 2 : 0));
        }
        public bool FriendOfTheForest
        {
            get => (Flags & 4) != 0;
            set => Flags = (byte)((Flags & ~4) | (value ? 4 : 0));
        }
        public bool EnemyOfTheForest
        {
            get => (Flags & 8) != 0;
            set => Flags = (byte)((Flags & ~8) | (value ? 8 : 0));
        }
        public bool Invisible
        {
            get => (Flags & 16) != 0;
            set => Flags = (byte)((Flags & ~16) | (value ? 16 : 0));
        }
        public bool IgnoreMe
        {
            get => (Flags & 32) != 0;
            set => Flags = (byte)((Flags & ~32) | (value ? 32 : 0));
        }
        /// <summary>Visual/AI hint: local player is poisoned (4.10).</summary>
        public bool Poisoned
        {
            get => (Flags & 64) != 0;
            set => Flags = (byte)((Flags & ~64) | (value ? 64 : 0));
        }
        /// <summary>Visual/AI hint: local player is bleeding (4.10).</summary>
        public bool Bleeding
        {
            get => (Flags & 128) != 0;
            set => Flags = (byte)((Flags & ~128) | (value ? 128 : 0));
        }

        public void Serialize(NetWriter w) => w.Put(Flags);
        public static PlayerEffectSyncMessage Deserialize(NetReader r)
            => new PlayerEffectSyncMessage { Flags = r.GetByte() };
    }

    public struct PlayerBurningMessage
    {
        public bool IsBurning;
        public float BurnTime;

        public void Serialize(NetWriter w)
        {
            w.Put(IsBurning);
            w.Put(BurnTime);
        }

        public static PlayerBurningMessage Deserialize(NetReader r) => new PlayerBurningMessage
        {
            IsBurning = r.GetBool(),
            BurnTime = r.GetFloat()
        };
    }

    public struct PlayerLightStateMessage
    {
        public bool LightOn;
        public string ItemType;
        public float LightRadius;
        public float LightColorR, LightColorG, LightColorB;
        public float LightIntensity;
        public bool HasLightEmitter;
        public bool IsFlashlight;
        public bool HasItemLight;
        public bool HasAmbientLight;

        public void Serialize(NetWriter w)
        {
            w.Put(LightOn);
            w.Put(ItemType ?? "");
            w.Put(LightRadius);
            w.Put(LightColorR); w.Put(LightColorG); w.Put(LightColorB);
            w.Put(LightIntensity);
            w.Put(HasLightEmitter);
            w.Put(IsFlashlight);
            w.Put(HasItemLight);
            w.Put(HasAmbientLight);
        }

        public static PlayerLightStateMessage Deserialize(NetReader r) => new PlayerLightStateMessage
        {
            LightOn = r.GetBool(),
            ItemType = r.GetString(),
            LightRadius = r.GetFloat(),
            LightColorR = r.GetFloat(),
            LightColorG = r.GetFloat(),
            LightColorB = r.GetFloat(),
            LightIntensity = r.GetFloat(),
            HasLightEmitter = r.GetBool(),
            IsFlashlight = r.GetBool(),
            HasItemLight = r.GetBool(),
            HasAmbientLight = r.GetBool()
        };
    }

    public struct PlayerAnimationMessage
    {
        public string TorsoClip;
        public string LegsClip;

        public void Serialize(NetWriter w)
        {
            w.Put(TorsoClip ?? "");
            w.Put(LegsClip ?? "");
        }

        public static PlayerAnimationMessage Deserialize(NetReader r) => new PlayerAnimationMessage
        {
            TorsoClip = r.GetString(),
            LegsClip = r.GetString()
        };
    }

    public struct PlayerAnimLibraryMessage
    {
        public string LibraryName;

        public void Serialize(NetWriter w)
        {
            w.Put(LibraryName ?? "");
        }

        public static PlayerAnimLibraryMessage Deserialize(NetReader r) => new PlayerAnimLibraryMessage
        {
            LibraryName = r.GetString()
        };
    }

    public struct PlayerFiredWeaponMessage
    {
        public string ItemType;
        public float AimY;
        public float PosX, PosY, PosZ;
        public int ProjectileCount;

        public void Serialize(NetWriter w)
        {
            w.Put(ItemType ?? "");
            w.Put(AimY);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(ProjectileCount);
        }

        public static PlayerFiredWeaponMessage Deserialize(NetReader r) => new PlayerFiredWeaponMessage
        {
            ItemType = r.GetString(),
            AimY = r.GetFloat(),
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            ProjectileCount = r.GetInt()
        };
    }

    public struct PlayerAudioMessage
    {
        public string SoundId;
        public float Volume;
        public float PosX, PosY, PosZ;
        public bool IsStopSignal;
        public string ObjectName;

        public void Serialize(NetWriter w)
        {
            w.Put(SoundId ?? "");
            w.Put(Volume);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(IsStopSignal);
            w.Put(ObjectName ?? "");
        }

        public static PlayerAudioMessage Deserialize(NetReader r) => new PlayerAudioMessage
        {
            SoundId = r.GetString(),
            Volume = r.GetFloat(),
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            IsStopSignal = r.GetBool(),
            ObjectName = r.GetString()
        };
    }
}
