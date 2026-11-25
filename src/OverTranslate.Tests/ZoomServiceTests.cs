using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class ZoomServiceTests
{
    // Ctrl + Plus / Minus walk the same list the settings picker offers, from wherever the value is.
    [Theory]
    [InlineData(1.0, 1, 1.1)]
    [InlineData(1.0, -1, 0.9)]
    [InlineData(2.0, 1, 2.0)]    // clamped at the top
    [InlineData(0.8, -1, 0.8)]   // and at the bottom
    [InlineData(1.3, 1, 1.5)]    // a hand-edited value steps from the nearest entry (1.25)
    public void Step_moves_one_entry_from_the_nearest(double current, int direction, double expected) =>
        Assert.Equal(expected, ZoomService.Step(ZoomService.Zooms, current, direction));

    [Fact]
    public void Step_on_text_sizes() =>
        Assert.Equal(16, ZoomService.Step(ZoomService.TextSizes, ZoomService.DesignTextSize, 1));
}
