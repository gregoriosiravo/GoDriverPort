using LegionGoConverter.App;
using Windows.Gaming.Input;

namespace LegionGoConverter.Core.Tests;

public class SystemActionsArgsTests
{
    [Fact]
    public void RestartArguments_AreDelayedAndCommented()
    {
        var a = SystemActions.RestartArguments();
        Assert.Equal(new[] { "/r", "/t", "15", "/c", "Legion Go Driver Port: restarting Windows to finish the driver change." }, a);
    }

    [Fact]
    public void OpenFolderArguments_IsTheFolderOnly()
    {
        Assert.Equal(new[] { @"C:\a b\c" }, SystemActions.OpenFolderArguments(@"C:\a b\c"));
    }
}

public class GamepadMappingTests
{
    static GamepadReading R(GamepadButtons b, double x = 0, double y = 0) =>
        new() { Buttons = b, LeftThumbstickX = x, LeftThumbstickY = y };

    [Fact]
    public void Buttons_MapToDpadAndAB()
    {
        var s = GamepadNavigator.ToState(R(GamepadButtons.DPadUp | GamepadButtons.A));
        Assert.True(s.Up); Assert.True(s.A);
        Assert.False(s.Down); Assert.False(s.Left); Assert.False(s.Right); Assert.False(s.B);
        Assert.True(GamepadNavigator.ToState(R(GamepadButtons.DPadDown)).Down);
        Assert.True(GamepadNavigator.ToState(R(GamepadButtons.DPadLeft)).Left);
        Assert.True(GamepadNavigator.ToState(R(GamepadButtons.DPadRight)).Right);
        Assert.True(GamepadNavigator.ToState(R(GamepadButtons.B)).B);
    }

    [Fact]
    public void StickAxes_PassThrough()
    {
        var s = GamepadNavigator.ToState(R(GamepadButtons.None, 0.75, -0.25));
        Assert.Equal(0.75, s.StickX); Assert.Equal(-0.25, s.StickY);
    }

    [Fact]
    public void Deadzone_IsHandledByTheLogic()
    {
        var logic = new GamepadNavigationLogic();
        Assert.Equal(NavIntentKind.None, logic.Update(GamepadNavigator.ToState(R(GamepadButtons.None, 0.3, 0.2)), TimeSpan.Zero).Kind);
        var i = logic.Update(GamepadNavigator.ToState(R(GamepadButtons.None, 0.9, 0)), TimeSpan.FromMilliseconds(16));
        Assert.Equal(Direction.Right, i.Direction);
    }
}
