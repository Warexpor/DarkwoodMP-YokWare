using System;
using System.IO;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        /// <summary>Inbound dispatch slice: DialogueDream.</summary>
        private bool TryDispatchDialogueDream(NetMessageType type, byte[] payload)
        {
            switch (type)
            {
                        case NetMessageType.DialogOutcomeSync:
                            DialogOutcomeHandlers.HandleDialogOutcomeSync(
                                DialogOutcomeSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DreamStarted:
                            DreamHandlers.HandleDreamStarted(
                                DreamStartedMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DreamEnded:
                            DreamHandlers.HandleDreamEnded(
                                DreamEndedMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DreamStartRequest:
                            DreamHandlers.HandleDreamStartRequest(
                                DreamStartRequestMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DreamItemPickup:
                            DreamHandlers.HandleDreamItemPickup(
                                DreamItemPickupMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DreamAudio:
                            DreamHandlers.HandleDreamAudio(
                                DreamAudioMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DreamEntered:
                            DreamHandlers.HandleDreamEntered(
                                DreamEnteredMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DreamPropCollider:
                            DreamHandlers.HandleDreamPropCollider(
                                DreamPropColliderMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DreamChainStart:
                            DreamHandlers.HandleDreamChainStart(
                                DreamChainStartMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.FinalDreamsceneDeath:
                            CombatHandlers.HandleFinalDreamsceneDeath(FinalDreamsceneDeathMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.SceneLoad:
                            EpilogueHandlers.HandleSceneLoad(
                                SceneLoadMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.CutsceneSync:
                            CutsceneHandlers.HandleCutsceneSync(
                                CutsceneSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ChapterTransition:
                            ChapterHandlers.HandleChapterTransition(
                                ChapterTransitionMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.GameEventsFired:
                            GameEventHandlers.HandleGameEventsFired(
                                GameEventsFiredMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DialogNpcLock:
                            DialogNpcLockHandlers.HandleDialogNpcLock(
                                DialogNpcLockMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DialogTreeState:
                            DialogOutcomeHandlers.HandleDialogTreeState(
                                DialogTreeStateMessage.Deserialize(new NetReader(payload)));
                            return true;
                default:
                    return false;
            }
        }
    }
}
