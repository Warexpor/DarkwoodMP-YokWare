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
        /// <summary>Inbound handlers: DialogueDream.</summary>
        private void RegisterDialogueDreamHandlers()
        {
            On(NetMessageType.DialogOutcomeSync, DialogOutcomeSyncMessage.Deserialize, m => DialogOutcomeApplyHandlers.HandleDialogOutcomeSync(m));
            On(NetMessageType.DreamStarted, DreamStartedMessage.Deserialize, m => DreamHandlers.HandleDreamStarted(m));
            On(NetMessageType.DreamEnded, DreamEndedMessage.Deserialize, m => DreamHandlers.HandleDreamEnded(m));
            On(NetMessageType.DreamStartRequest, DreamStartRequestMessage.Deserialize, m => DreamHandlers.HandleDreamStartRequest(m));
            On(NetMessageType.DreamItemPickup, DreamItemPickupMessage.Deserialize, m => DreamHandlers.HandleDreamItemPickup(m));
            On(NetMessageType.DreamAudio, DreamAudioMessage.Deserialize, m => DreamHandlers.HandleDreamAudio(m));
            On(NetMessageType.DreamEntered, DreamEnteredMessage.Deserialize, m => DreamHandlers.HandleDreamEntered(m));
            On(NetMessageType.DreamPropCollider, DreamPropColliderMessage.Deserialize, m => DreamHandlers.HandleDreamPropCollider(m));
            On(NetMessageType.DreamChainStart, DreamChainStartMessage.Deserialize, m => DreamHandlers.HandleDreamChainStart(m));
            On(NetMessageType.FinalDreamsceneDeath, FinalDreamsceneDeathMessage.Deserialize, m => CombatDeathStateHandlers.HandleFinalDreamsceneDeath(m));
            On(NetMessageType.SceneLoad, SceneLoadMessage.Deserialize, m => EpilogueHandlers.HandleSceneLoad(m));
            On(NetMessageType.CutsceneSync, CutsceneSyncMessage.Deserialize, m => CutsceneHandlers.HandleCutsceneSync(m));
            On(NetMessageType.ChapterTransition, ChapterTransitionMessage.Deserialize, m => ChapterHandlers.HandleChapterTransition(m));
            On(NetMessageType.ChapterShareAck, ChapterShareAckMessage.Deserialize, m => ChapterHandlers.HandleChapterShareAck(m));
            On(NetMessageType.ChapterLoadGo, ChapterLoadGoMessage.Deserialize, m => ChapterHandlers.HandleChapterLoadGo(m));
            On(NetMessageType.GameEventsFired, GameEventsFiredMessage.Deserialize, m => GameEventHandlers.HandleGameEventsFired(m));
            On(NetMessageType.DialogNpcLock, DialogNpcLockMessage.Deserialize, m => DialogNpcLockHandlers.HandleDialogNpcLock(m));
            On(NetMessageType.DialogTreeState, DialogTreeStateMessage.Deserialize, m => DialogOutcomeApplyHandlers.HandleDialogTreeState(m));
        }
    }
}
