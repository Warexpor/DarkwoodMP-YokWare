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
        new Regex(@"SetLabel\([^,()]+,\s*""([^""]+)""\)"),
        new Regex(@"SetJoinProgress\(""([^""]+)""\)"),
        new Regex(@"ShowTransientFailure\([^,()]+,\s*""([^""]+)"",\s*""([^""]+)""\)"),
        new Regex(@"CloneButton\(template,\s*[^,]+,\s*""[^""]+"",\s*""([^""]+)"""),
        new Regex(@"(?:StatusText|ProgressText|_status)\s*=\s*""([^""]+)"";"),
        new Regex(@"SetStatus\(""([^""]+)""\)"),
        new Regex(@"SetRestoreSelfStatus\(""([^""]+)""\)"),
        new Regex(@"Say\(""([^""]+)""\)"),
        new Regex(@"FailureLabel\([^,()]+,\s*""([^""]+)""\)"),
        new Regex(@"return ""([A-Z][A-Z ]+)"";"),
    };

    private static readonly Regex ReasonSink = new Regex(@"reason\s*=\s*""([^""]+)"";");

    [Fact]
    public void EveryShownLiteral_HasRussian()
    {
        var missing = new List<string>();
        foreach (string dir in new[] { "UI", "Networking", "Domains", "Bootstrap" })
        {
            foreach (string f in Directory.GetFiles(Path.Combine(TestPaths.ModDir, dir), "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains("TestPilot"))
                    continue;
                string src = File.ReadAllText(f);
                // The F2 window shows its restore gate reason; elsewhere "reason" is a log-only word.
                IEnumerable<Regex> sinks = Path.GetFileName(f) == "MultiplayerMenu.cs" ? Sinks.Append(ReasonSink) : Sinks;
                foreach (Regex sink in sinks)
                {
                    foreach (Match m in sink.Matches(src))
                    {
                        for (int g = 1; g < m.Groups.Count; g++)
                        {
                            string en = Regex.Unescape(m.Groups[g].Value);
                            if (!Loc.Knows(en))
                                missing.Add(Path.GetFileName(f) + ": " + en);
                        }
                    }
                }
            }
        }
        Assert.True(missing.Count == 0, "No Russian for:\n" + string.Join("\n", missing.Distinct()));
    }

    [Fact]
    public void BlockReasons_AndPinKinds_HaveRussian()
    {
        foreach (string en in new[] { "partial night death", "night death held", "dream session", "prologue",
                     "application quitting", "promoted host (survivor world is not authoritative)", "chapter transition",
                     "Mark", "Danger", "Loot", "Shelter", "Camp", "Grave" })
            Assert.True(Loc.Knows(en), en);
    }

    [Theory]
    [InlineData("Player 3 left (1 remaining, ready=1)", "Игрок 3 вышел (осталось 1, готовы 1)")]
    [InlineData("Receiving host world 45%", "Получение мира хоста 45%")]
    [InlineData("Sending world (slot 2) 80%", "Отправка мира (профиль 2) 80%")]
    [InlineData("Reconnect failed (timeout) — retry 2/3 in 4s", "Переподключение не удалось (timeout) — попытка 2/3 через 4 с")]
    [InlineData("Blocked: cannot save during dream session", "Недоступно: нельзя сохраняться — сон")]
    [InlineData("Saved to slot 4", "Сохранено в слот 4")]
    [InlineData("Same world already on Profile 3 — press ENTER WORLD", "Этот мир уже есть в профиле 3 — нажмите ВОЙТИ В МИР")]
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
    public void OnlyRussianTranslates()
    {
        Loc.SetLanguage("DE");
        Assert.Equal("HOST", Loc.T("HOST"));
        Loc.SetLanguage("RU");
        Assert.Equal("СОЗДАТЬ", Loc.T("HOST"));
        Loc.SetLanguage("EN");
    }
}
