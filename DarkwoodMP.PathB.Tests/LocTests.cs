using System.Text.RegularExpressions;
using DWMPHorde;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// The mod's Russian text (<see cref="Loc"/>): every literal the code shows has an entry, and
/// the patterns turn the composed status lines into Russian with their numbers kept.
/// </summary>
public class LocTests
{
    /// <summary>Literal English handed to a display sink in the mod's sources.</summary>
    private static readonly Regex[] Sinks =
    {
        new Regex(@"Loc\.T\(""((?:[^""\\]|\\.)+)""\)"),
        new Regex(@"(?:StatusText|ProgressText|_status)\s*=\s*""([^""]+)"";"),
        new Regex(@"SetStatus\(""([^""]+)""\)"),
        new Regex(@"Say\(""([^""]+)""\)"),
        new Regex(@"CloneButton\(template,\s*[^,]+,\s*""[^""]+"",\s*""([^""]+)"""),
    };

    /// <summary>
    /// Menu calls whose first argument is shown translated (the vanilla-style screens and the
    /// manual-saves add-on): every literal in that argument, both sides of a ?: included.
    /// </summary>
    private static readonly Regex MenuCall = new Regex(
        @"\b(?:Header|Item|Name|Entry|Confirm|Flash|PickerFlash|Status|T|TextField|Choice|Slider|KeyField)\(");

    /// <summary>Files whose methods return shown English (status lines, refusal reasons).</summary>
    private static readonly string[] ReturnShownFiles =
    {
        "MainMenuMultiplayerInject.HostJoin.cs", "MainMenuMultiplayerInject.JoinState.cs", "MultiplayerScreens.cs", "SaveSlots.cs",
    };

    private static readonly Regex ReturnLiteral = new Regex(@"return\s+""([^""]+)""\s*;");
    private static readonly Regex ChoiceArray = new Regex(@"string\[\]\s+(\w+)\s*=\s*\{([^}]*)\}");
    private static readonly Regex Literal = new Regex(@"""((?:[^""\\]|\\.)*)""");

    private static IEnumerable<string> SourceFiles()
    {
        foreach (string dir in new[] { "UI", "Networking", "Domains", "Bootstrap" })
        {
            foreach (string f in Directory.GetFiles(Path.Combine(TestPaths.ModDir, dir), "*.cs", SearchOption.AllDirectories))
            {
                if (!f.Contains("TestPilot"))
                    yield return f;
            }
        }
        foreach (string f in Directory.GetFiles(Path.Combine(TestPaths.RepoRoot, "DarkwoodMP.ManualSaves"), "*.cs", SearchOption.TopDirectoryOnly))
            yield return f;
    }

    /// <summary>The text of the first argument of the call whose "(" is at <paramref name="open"/>.</summary>
    private static string FirstArgument(string src, int open)
    {
        int depth = 0;
        bool inString = false;
        for (int i = open + 1; i < src.Length; i++)
        {
            char c = src[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']' || c == '}')
            {
                if (depth == 0) return src.Substring(open + 1, i - open - 1);
                depth--;
            }
            else if (c == ',' && depth == 0)
                return src.Substring(open + 1, i - open - 1);
        }
        return "";
    }

    [Fact]
    public void EveryShownLiteral_HasRussian()
    {
        var missing = new List<string>();
        void Check(string file, string en)
        {
            en = Regex.Unescape(en);
            if (en.Length == 0 || en.Trim().Length == 0 || Regex.IsMatch(en, @"^[\d\s·,.:—\-]+$"))
                return;
            if (!Loc.Knows(en))
                missing.Add(Path.GetFileName(file) + ": " + en);
        }

        foreach (string f in SourceFiles())
        {
            string src = File.ReadAllText(f);
            foreach (Regex sink in Sinks)
                foreach (Match m in sink.Matches(src))
                    for (int g = 1; g < m.Groups.Count; g++)
                        Check(f, m.Groups[g].Value);

            bool menuFile = f.Contains(Path.Combine("UI", "MultiplayerScreens.cs")) || f.Contains("MainMenuMultiplayerInject")
                || f.Contains("DarkwoodMP.ManualSaves");
            if (menuFile)
            {
                foreach (Match m in MenuCall.Matches(src))
                {
                    string arg = FirstArgument(src, m.Index + m.Length - 1);
                    // A composed argument ("Slot " + n) is checked as a whole by the theories below.
                    if (arg.Contains(" + "))
                        continue;
                    foreach (Match lit in Literal.Matches(arg))
                        Check(f, lit.Groups[1].Value);
                }
                foreach (Match m in ChoiceArray.Matches(src))
                {
                    if (!m.Groups[1].Value.EndsWith("Choices") && m.Groups[1].Value != "YesNo")
                        continue;
                    foreach (Match lit in Literal.Matches(m.Groups[2].Value))
                        Check(f, lit.Groups[1].Value);
                }
            }
            if (ReturnShownFiles.Contains(Path.GetFileName(f)))
                foreach (Match m in ReturnLiteral.Matches(src))
                    Check(f, m.Groups[1].Value);
        }
        Assert.True(missing.Count == 0, "No Russian for:\n" + string.Join("\n", missing.Distinct()));
    }

    [Fact]
    public void BlockReasons_AndPinKinds_HaveRussian()
    {
        foreach (string en in new[] { "partial night death", "night death held", "dream session", "prologue",
                     "application quitting", "promoted host (survivor world is not authoritative)", "chapter transition",
                     "Mark", "Danger", "Loot", "Shelter", "Camp", "Grave",
                     "Hosting a Steam game", "Hosting on the local network" })
            Assert.True(Loc.Knows(en), en);
    }

    [Theory]
    [InlineData("Player 3 left (1 remaining, ready=1)", "Игрок 3 вышел (осталось 1, готовы 1)")]
    [InlineData("Receiving host world 45%", "Получение мира хоста 45%")]
    [InlineData("Sending world (slot 2) 80%", "Отправка мира (профиль 2) 80%")]
    [InlineData("Reconnect failed (timeout) — retry 2/3 in 4s", "Переподключение не удалось (timeout) — попытка 2/3 через 4 с")]
    [InlineData("Blocked: cannot save during dream session", "Недоступно: нельзя сохраняться — сон")]
    [InlineData("Saved to slot 4", "Сохранено в слот 4")]
    [InlineData("Overwrite slot 7?", "Перезаписать слот 7?")]
    [InlineData("Load slot 2? Progress since your last save is lost.", "Загрузить слот 2? Прогресс после последнего сохранения пропадет.")]
    [InlineData("Same world already on Profile 3 — press Enter world", "Этот мир уже есть в профиле 3 — нажмите «Войти в мир»")]
    [InlineData("Hosting on the local network — 2 players joined", "Игра в локальной сети создана — подключилось игроков: 2")]
    [InlineData("Hosting a Steam game — 1 player joined", "Игра в Steam создана — подключился 1 игрок")]
    [InlineData("Hosting a Steam game — choose a profile to play", "Игра в Steam создана — выберите профиль для игры")]
    [InlineData("Co-op copy · refreshed 2026-10-09 12:00 · 192.168.1.5", "Копия для кооператива · обновлено 2026-10-09 12:00 · 192.168.1.5")]
    [InlineData("Something nobody wrote down", "Something nobody wrote down")]
    public void Patterns_KeepNumbers(string en, string ru)
    {
        Assert.Equal(ru, Loc.ToRussian(en));
    }

    [Fact]
    public void ShareFailure_TranslatesFrameAndReason()
    {
        string en = WorldSharePolicy.FormatShareFailure("host reported failure") + " The host has moved on to chapter 2.";
        string ru = Loc.ToRussian(en);
        Assert.StartsWith("ПЕРЕДАЧА МИРА НЕ УДАЛАСЬ: хост сообщил об ошибке — ", ru);
        Assert.EndsWith("Хост уже перешел к главе 2.", ru);
    }

    [Fact]
    public void WrongSave_MultiLine_TranslatesEachLine()
    {
        string en = WorldSharePolicy.FormatWrongSave("Profile 2 belongs to a different co-op campaign than this host.\n"
            + "Overwrite will replace that save with the host world.");
        Assert.Equal("НЕ ТО СОХРАНЕНИЕ: Профиль 2 принадлежит другой кооперативной кампании, чем у этого хоста.\n"
            + "Перезапись заменит это сохранение миром хоста.", Loc.ToRussian(en));
    }

    [Fact]
    public void SlotPickerQuestions_TranslateEachLine()
    {
        Assert.Equal("В профиле 3 уже есть сохранение.\nПерезаписать миром хоста? Это нельзя отменить.",
            Loc.ToRussian("Profile 3 already has a save.\nOverwrite with the host world? This cannot be undone."));
    }

    [Fact]
    public void OnlyRussianTranslates()
    {
        Loc.SetLanguage("DE");
        Assert.Equal("Host", Loc.T("Host"));
        Loc.SetLanguage("RU");
        Assert.Equal("Создать игру", Loc.T("Host"));
        Loc.SetLanguage("EN");
    }
}
