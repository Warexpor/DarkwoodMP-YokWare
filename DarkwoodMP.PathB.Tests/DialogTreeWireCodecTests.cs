using DWMPHorde.Sync;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>Drives shipped DialogTreeWireCodec (Yokyy DialogueSync v2 body).</summary>
public class DialogTreeWireCodecTests
{
    [Fact]
    public void RoundTrip_NodesPortraitSpecialsWantsRep()
    {
        var nodes = new[]
        {
            DialogTreeWireCodec.PackNodeFlag(true, false),
            DialogTreeWireCodec.PackNodeFlag(false, true),
            DialogTreeWireCodec.PackNodeFlag(true, true),
            0
        };
        var specials = new List<(string, string)>
        {
            ("optA", "typeA"),
            ("optB", "typeB")
        };

        string payload = DialogTreeWireCodec.Encode(
            "wolfman_main",
            nodes,
            portraitType: 3,
            npcName: "wolfman",
            wantsToTalk: '0',
            reputation: "42",
            specials);

        Assert.False(string.IsNullOrEmpty(payload));

        Assert.True(DialogTreeWireCodec.TryDecode(
            payload,
            out string name,
            out int[] flags,
            out int portrait,
            out string npc,
            out char wants,
            out string rep,
            out var decodedSpecials));

        Assert.Equal("wolfman_main", name);
        Assert.Equal(4, flags.Length);
        Assert.True(DialogTreeWireCodec.UnpackAlreadyShown(flags[0]));
        Assert.False(DialogTreeWireCodec.UnpackDisabled(flags[0]));
        Assert.False(DialogTreeWireCodec.UnpackAlreadyShown(flags[1]));
        Assert.True(DialogTreeWireCodec.UnpackDisabled(flags[1]));
        Assert.True(DialogTreeWireCodec.UnpackAlreadyShown(flags[2]));
        Assert.True(DialogTreeWireCodec.UnpackDisabled(flags[2]));
        Assert.Equal(0, flags[3]);
        Assert.Equal(3, portrait);
        Assert.Equal("wolfman", npc);
        Assert.Equal('0', wants);
        Assert.Equal("42", rep);
        Assert.Equal(2, decodedSpecials.Count);
        Assert.Equal(("optA", "typeA"), decodedSpecials[0]);
        Assert.Equal(("optB", "typeB"), decodedSpecials[1]);
    }

    [Fact]
    public void HasProgress_ShownNodeOrSpecialOrPortrait()
    {
        Assert.False(DialogTreeWireCodec.HasProgress(new[] { 0, 0 }, 0, 1, 1));
        Assert.True(DialogTreeWireCodec.HasProgress(new[] { 1, 0 }, 0, 1, 1));
        Assert.True(DialogTreeWireCodec.HasProgress(new[] { 0 }, 1, 1, 1));
        Assert.True(DialogTreeWireCodec.HasProgress(new[] { 0 }, 0, 2, 1));
    }

    [Fact]
    public void TryDecode_RejectsGarbage()
    {
        Assert.False(DialogTreeWireCodec.TryDecode(
            "", out _, out _, out _, out _, out _, out _, out _));
        Assert.False(DialogTreeWireCodec.TryDecode(
            "not-base64!!!", out _, out _, out _, out _, out _, out _, out _));
    }
}
