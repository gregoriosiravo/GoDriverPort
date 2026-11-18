using LegionGoConverter.App;

namespace LegionGoConverter.Core.Tests;

/// <summary>The pure decision: scroll a focused reading area by one step, or let focus move on.</summary>
public class ReadingScrollTests
{
    const double Step = ReadingScroll.DefaultStep;

    [Fact]
    public void Down_InMiddle_ScrollsOneStep() =>
        Assert.Equal(Step, ReadingScroll.Decide(Direction.Down, offset: 100, extent: 1000, viewport: 300));

    [Fact]
    public void Up_InMiddle_ScrollsOneStepBack() =>
        Assert.Equal(-Step, ReadingScroll.Decide(Direction.Up, offset: 100, extent: 1000, viewport: 300));

    [Fact]
    public void Up_AtTop_MovesFocus() =>
        Assert.Null(ReadingScroll.Decide(Direction.Up, offset: 0, extent: 1000, viewport: 300));

    [Fact]
    public void Down_AtBottom_MovesFocus() =>
        Assert.Null(ReadingScroll.Decide(Direction.Down, offset: 700, extent: 1000, viewport: 300));

    [Fact]
    public void Down_NearBottom_ScrollsOnlyTheRemainder() =>
        Assert.Equal(20, ReadingScroll.Decide(Direction.Down, offset: 680, extent: 1000, viewport: 300));

    [Fact]
    public void Up_NearTop_ScrollsOnlyTheRemainder() =>
        Assert.Equal(-10, ReadingScroll.Decide(Direction.Up, offset: 10, extent: 1000, viewport: 300));

    [Theory]
    [InlineData(Direction.Up)]
    [InlineData(Direction.Down)]
    public void NotScrollable_MovesFocusImmediately(Direction d)
    {
        Assert.Null(ReadingScroll.Decide(d, offset: 0, extent: 300, viewport: 300));
        Assert.Null(ReadingScroll.Decide(d, offset: 0, extent: 200, viewport: 300));
    }

    [Theory]
    [InlineData(Direction.Left)]
    [InlineData(Direction.Right)]
    public void Sideways_AlwaysMovesFocus(Direction d) =>
        Assert.Null(ReadingScroll.Decide(d, offset: 100, extent: 1000, viewport: 300));

    [Fact]
    public void SubPixelLeftovers_CountAsAtTheEdge()
    {
        Assert.Null(ReadingScroll.Decide(Direction.Down, offset: 699.6, extent: 1000, viewport: 300));
        Assert.Null(ReadingScroll.Decide(Direction.Up, offset: 0.4, extent: 1000, viewport: 300));
    }

    [Fact]
    public void BadNumbers_MoveFocus()
    {
        Assert.Null(ReadingScroll.Decide(Direction.Down, double.NaN, 1000, 300));
        Assert.Null(ReadingScroll.Decide(Direction.Down, 0, double.PositiveInfinity, 300));
    }
}
