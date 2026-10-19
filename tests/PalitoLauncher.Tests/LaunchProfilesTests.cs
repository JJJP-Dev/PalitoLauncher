using PalitoLauncher.Core.Launch;

namespace PalitoLauncher.Tests;

public class LaunchProfilesTests
{
    [Theory]
    [InlineData(LaunchProfile.Default, "")]
    [InlineData(LaunchProfile.Windowed720, "-screen-fullscreen 0 -screen-width 1280 -screen-height 720")]
    [InlineData(LaunchProfile.Windowed1080, "-screen-fullscreen 0 -screen-width 1920 -screen-height 1080")]
    [InlineData(LaunchProfile.Borderless, "-screen-fullscreen 1 -window-mode borderless")]
    [InlineData(LaunchProfile.Fullscreen, "-screen-fullscreen 1 -window-mode exclusive")]
    public void GetArguments_MatchesUnityArguments(LaunchProfile profile, string expected)
    {
        Assert.Equal(expected, LaunchProfiles.GetArguments(profile));
    }

    [Theory]
    [InlineData(LaunchProfile.Default, "Lo que diga el juego")]
    [InlineData(LaunchProfile.Windowed720, "Ventana 1280×720")]
    [InlineData(LaunchProfile.Windowed1080, "Ventana 1920×1080")]
    [InlineData(LaunchProfile.Borderless, "Pantalla completa sin bordes")]
    [InlineData(LaunchProfile.Fullscreen, "Pantalla completa")]
    public void GetDisplayName_IsSpanish(LaunchProfile profile, string expected)
    {
        Assert.Equal(expected, LaunchProfiles.GetDisplayName(profile));
    }

    [Fact]
    public void UnknownProfileValue_BehavesLikeDefault()
    {
        var unknown = (LaunchProfile)99;

        Assert.Equal("", LaunchProfiles.GetArguments(unknown));
        Assert.Equal("Lo que diga el juego", LaunchProfiles.GetDisplayName(unknown));
        Assert.Equal("-batchmode", LaunchProfiles.Combine(unknown, "-batchmode"));
    }

    [Fact]
    public void All_ContainsEveryProfileOnceInEnumOrder()
    {
        Assert.Equal(Enum.GetValues<LaunchProfile>(), LaunchProfiles.All);
        Assert.Equal(LaunchProfiles.All.Count, LaunchProfiles.All.Select(LaunchProfiles.GetDisplayName).Distinct().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void Combine_NoUserArguments_ReturnsProfileArguments(string? user)
    {
        Assert.Equal("-screen-fullscreen 1 -window-mode borderless", LaunchProfiles.Combine(LaunchProfile.Borderless, user));
        Assert.Equal("", LaunchProfiles.Combine(LaunchProfile.Default, user));
    }

    [Fact]
    public void Combine_UserArgumentsGoLast()
    {
        Assert.Equal(
            "-screen-fullscreen 0 -screen-width 1280 -screen-height 720 -monitor 2 -force-d3d11",
            LaunchProfiles.Combine(LaunchProfile.Windowed720, "-monitor 2 -force-d3d11"));
    }

    [Fact]
    public void Combine_DefaultProfile_OnlyNormalizesUserArguments()
    {
        Assert.Equal("-monitor 2 -force-d3d11", LaunchProfiles.Combine(LaunchProfile.Default, "  -monitor   2\r\n\t-force-d3d11  "));
    }

    [Fact]
    public void Combine_NormalizesWhitespaceButKeepsQuotedText()
    {
        var combined = LaunchProfiles.Combine(LaunchProfile.Fullscreen, "\t-logFile   \"C:\\Mis  cosas\\log.txt\"\n -monitor 1 ");

        Assert.Equal("-screen-fullscreen 1 -window-mode exclusive -logFile \"C:\\Mis  cosas\\log.txt\" -monitor 1", combined);
    }

    [Fact]
    public void Combine_UserOptionReplacesTheSameProfileOption()
    {
        // El usuario manda: su -screen-width sustituye al del perfil (Unity podría quedarse con la primera aparición).
        Assert.Equal(
            "-screen-fullscreen 0 -screen-height 720 -screen-width 1600",
            LaunchProfiles.Combine(LaunchProfile.Windowed720, "-screen-width 1600"));

        // Sin distinguir mayúsculas.
        Assert.Equal(
            "-screen-fullscreen 1 -Window-Mode exclusive",
            LaunchProfiles.Combine(LaunchProfile.Borderless, "-Window-Mode exclusive"));

        // Varias opciones repetidas.
        Assert.Equal(
            "-screen-width 800 -screen-height 600 -screen-fullscreen 1",
            LaunchProfiles.Combine(LaunchProfile.Windowed1080, "-screen-width 800 -screen-height 600 -screen-fullscreen 1"));
    }

    [Fact]
    public void Combine_ValuesThatLookLikeProfileOptionsInsideQuotesDoNotRemoveThem()
    {
        Assert.Equal(
            "-screen-fullscreen 1 -window-mode borderless -logFile \"-window-mode.txt\"",
            LaunchProfiles.Combine(LaunchProfile.Borderless, "-logFile \"-window-mode.txt\""));
    }

    [Fact]
    public void Tokenize_UnclosedQuote_RunsToTheEnd()
    {
        Assert.Equal(new[] { "-logFile", "\"C:\\a  b" }, LaunchProfiles.Tokenize("-logFile \"C:\\a  b  "));
    }
}
