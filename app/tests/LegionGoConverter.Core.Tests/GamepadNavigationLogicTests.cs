using LegionGoConverter.App;

namespace LegionGoConverter.Core.Tests;

public class GamepadNavigationLogicTests
{
    static readonly GamepadState Idle = new(false, false, false, false, 0, 0, false, false);
    static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Idle_GivesNone() =>
        Assert.Equal(NavIntent.None, new GamepadNavigationLogic().Update(Idle, Ms(0)));

    [Fact]
    public void StickInsideDeadZone_GivesNone()
    {
        var l = new GamepadNavigationLogic();
        Assert.Equal(NavIntent.None, l.Update(Idle with { StickX = 0.49, StickY = -0.3 }, Ms(0)));
    }

    [Theory]
    [InlineData(0.8, 0.0, Direction.Right)]
    [InlineData(-0.8, 0.1, Direction.Left)]
    [InlineData(0.1, 0.9, Direction.Up)]     // stick Y positive is up
    [InlineData(0.0, -0.9, Direction.Down)]
    [InlineData(0.6, 0.9, Direction.Up)]     // dominant axis wins
    public void StickBeyondDeadZone_MovesFocus(double x, double y, Direction expected) =>
        Assert.Equal(NavIntent.Move(expected), new GamepadNavigationLogic().Update(Idle with { StickX = x, StickY = y }, Ms(0)));

    [Fact]
    public void DPad_MovesFocus_AndTakesPriorityOverStick() =>
        Assert.Equal(NavIntent.Move(Direction.Left),
            new GamepadNavigationLogic().Update(Idle with { Left = true, StickX = 1.0 }, Ms(0)));

    [Fact]
    public void A_FiresOncePerPress()
    {
        var l = new GamepadNavigationLogic();
        var a = Idle with { A = true };
        Assert.Equal(NavIntent.Activate, l.Update(a, Ms(0)));
        Assert.Equal(NavIntent.None, l.Update(a, Ms(50)));
        Assert.Equal(NavIntent.None, l.Update(a, Ms(1000)));
        Assert.Equal(NavIntent.None, l.Update(Idle, Ms(1100)));
        Assert.Equal(NavIntent.Activate, l.Update(a, Ms(1200)));
    }

    [Fact]
    public void B_FiresOncePerPress()
    {
        var l = new GamepadNavigationLogic();
        var b = Idle with { B = true };
        Assert.Equal(NavIntent.Back, l.Update(b, Ms(0)));
        Assert.Equal(NavIntent.None, l.Update(b, Ms(500)));
        l.Update(Idle, Ms(600));
        Assert.Equal(NavIntent.Back, l.Update(b, Ms(700)));
    }

    [Fact]
    public void HeldDirection_RepeatsAfter400ms_ThenEvery120ms()
    {
        var l = new GamepadNavigationLogic();
        var down = Idle with { Down = true };
        var move = NavIntent.Move(Direction.Down);
        Assert.Equal(move, l.Update(down, Ms(0)));
        Assert.Equal(NavIntent.None, l.Update(down, Ms(100)));
        Assert.Equal(NavIntent.None, l.Update(down, Ms(399)));
        Assert.Equal(move, l.Update(down, Ms(400)));
        Assert.Equal(NavIntent.None, l.Update(down, Ms(519)));
        Assert.Equal(move, l.Update(down, Ms(520)));
        Assert.Equal(NavIntent.None, l.Update(down, Ms(600)));
        Assert.Equal(move, l.Update(down, Ms(640)));
    }

    [Fact]
    public void ReleaseAndRepress_RestartsInitialDelay()
    {
        var l = new GamepadNavigationLogic();
        var up = Idle with { Up = true };
        l.Update(up, Ms(0));
        l.Update(Idle, Ms(100));
        Assert.Equal(NavIntent.Move(Direction.Up), l.Update(up, Ms(200)));
        Assert.Equal(NavIntent.None, l.Update(up, Ms(550)));
        Assert.Equal(NavIntent.Move(Direction.Up), l.Update(up, Ms(600)));
    }

    [Fact]
    public void ChangingDirection_FiresImmediately()
    {
        var l = new GamepadNavigationLogic();
        l.Update(Idle with { Up = true }, Ms(0));
        Assert.Equal(NavIntent.Move(Direction.Right), l.Update(Idle with { Right = true }, Ms(50)));
    }
}
