using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DarkwoodMP.Patches;

/// <summary>
/// Chapter progression (Horde ChapterProgressionPatches slim):
/// - loadChapterSave: chat notice (existing)
/// - generateChapter: non-authority blocked; authority coordinates LoadScene("chapterN")
/// </summary>
public sealed class Chapter_Patch : IPatch
{
    private static bool _chapterLoadPending;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (target.DeclaringType!.Name == "SaveManager")
        {
            baseHarmony.Patch(target,
                postfix: new HarmonyMethod(typeof(Chapter_Patch).GetMethod(nameof(LoadChapterSavePostfix), statics)!));
        }
        else if (target.Name == "generateChapter")
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(Chapter_Patch).GetMethod(nameof(GenerateChapterPrefix), statics)!));
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("SaveManager", "loadChapterSave");
        yield return ("Controller", "generateChapter");
    }

    public static void LoadChapterSavePostfix()
    {
        try
        {
            if (RemoteApply.Active) return;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            network.SendReliable(new ChapterNotifyPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                ChapterId = 2
            });
            ModLogger.Msg("[Chapter_Patch] Ironbark ChapterNotify - partner notified");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Chapter_Patch] loadChapterSave: {ex.Message}");
        }
    }

    /// <summary>
    /// generateChapter(int chapterId, bool generateSave, bool loadChapterSave)
    /// </summary>
    public static bool GenerateChapterPrefix(int _chapterId, bool generateSave, bool loadChapterSave)
    {
        try
        {
            if (RemoteApply.Active) return true;
            var manager = NetworkManager.Instance;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (manager == null || network == null || !network.IsConnected)
                return true;

            // Non-authority never starts chapter loads alone
            if (!manager.IsTimeAuthority)
            {
                ModLogger.Msg($"[Chapter] Non-auth blocked generateChapter({_chapterId}) — wait for authority");
                return false;
            }

            if (_chapterId < 1) _chapterId = 1;

            if (generateSave)
            {
                try
                {
                    if (Singleton<WorldGenerator>.Instance != null)
                        Singleton<WorldGenerator>.Instance.chapterID = _chapterId;
                    if (Core.currentProfile != null)
                        Core.currentProfile.chapter = _chapterId;
                    if (Singleton<SaveManager>.Instance != null)
                        Singleton<SaveManager>.Instance.saveEmptyChapterSave();
                }
                catch (Exception ex)
                {
                    ModLogger.Warning($"[Chapter] empty save: {ex.Message}");
                }
            }

            network.SendReliableCritical(new ChapterTransitionPacket
            {
                PlayerId = manager.LocalPlayerId,
                ChapterId = _chapterId,
                LoadChapterSave = loadChapterSave,
                GenerateSave = generateSave
            });

            ApplyChapterLoad(_chapterId, loadChapterSave, stopNetwork: true);
            ModLogger.Msg($"[Chapter] Auth generateChapter({_chapterId}) coordinated");
            return false;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Chapter] generateChapter: {ex.Message}");
            return true;
        }
    }

    public static void ApplyChapterLoad(int chapterId, bool loadChapterSave, bool stopNetwork)
    {
        if (chapterId < 1) chapterId = 1;
        if (_chapterLoadPending) return;
        _chapterLoadPending = true;

        string scene = "chapter" + chapterId;
        ModLogger.Msg($"[Chapter] ApplyChapterLoad {scene} loadSave={loadChapterSave}");

        try { DreamSession.End("chapter:" + chapterId); } catch { }

        try
        {
            if (Singleton<WorldGenerator>.Instance != null)
                Singleton<WorldGenerator>.Instance.chapterID = chapterId;
            if (Core.currentProfile != null)
                Core.currentProfile.chapter = chapterId;
            Core.loadedGame = false;
            Core.coreStarted = false;
            Core.loadingGame = false;
            if (loadChapterSave)
                Core.doLoadChapterSave = true;
        }
        catch (Exception ex)
        {
            ModLogger.Warning($"[Chapter] prep flags: {ex.Message}");
        }

        var nm = NetworkManager.Instance;
        if (nm != null)
            nm.StartCoroutine(DelayedChapterLoad(scene, stopNetwork));
        else
            FinishChapterLoad(scene, stopNetwork);
    }

    private static System.Collections.IEnumerator DelayedChapterLoad(string scene, bool stopNetwork)
    {
        // Let chaptergen packet leave the socket
        yield return new WaitForSecondsRealtime(0.4f);
        FinishChapterLoad(scene, stopNetwork);
    }

    private static void FinishChapterLoad(string scene, bool stopNetwork)
    {
        try
        {
            if (stopNetwork)
                NetworkManager.Instance?.Disconnect();
        }
        catch { }

        try
        {
            RemoteApply.Active = true;
            try { SceneManager.LoadScene(scene); }
            finally { RemoteApply.Active = false; }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Chapter] LoadScene failed: {ex.Message}");
            _chapterLoadPending = false;
        }
    }

    public static void Reset()
    {
        _chapterLoadPending = false;
    }
}
