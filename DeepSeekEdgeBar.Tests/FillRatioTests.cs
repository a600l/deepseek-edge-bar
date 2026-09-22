using DeepSeekEdgeBar;

namespace DeepSeekEdgeBar.Tests;

/// <summary>
/// The collapsed strip's fill fraction, which feeds a GradientStop offset.
///
/// This is worth a test because the failure is invisible and non-obvious: the ratio used to be
/// computed unguarded, and the per-second peak tick can run before the first balance refresh has
/// stored a balance — so the very first peak of a session could divide 0 by 0. The result is NaN,
/// and a GradientStop with a NaN offset is not a valid gradient.
/// </summary>
public class FillRatioTests
{
    [Fact]
    public void ZeroBalanceOverZeroFullIsEmptyRatherThanNaN()
    {
        double ratio = MainWindow.FillRatio(0m, 0m);

        Assert.False(double.IsNaN(ratio));
        Assert.Equal(0d, ratio);
    }

    [Theory]
    [InlineData(0, 0, 0d)]        // both unset: the pre-refresh peak tick
    [InlineData(5, 0, 0d)]        // balance known, full amount unset
    [InlineData(5, 10, 0.5d)]     // half full
    [InlineData(10, 10, 1d)]      // exactly full
    [InlineData(20, 10, 1d)]      // clamped: never exceeds a full strip
    [InlineData(-1, 10, 0d)]      // clamped: a negative balance cannot underfill
    [InlineData(0, 10, 0d)]       // empty
    public void FillIsClampedToTheStrip(decimal balance, decimal fullAmount, double expected)
        => Assert.Equal(expected, MainWindow.FillRatio(balance, fullAmount), 6);

    [Fact]
    public void EveryValidInputProducesAUsableGradientOffset()
    {
        // Whatever the inputs, the value must be a real number inside 0..1, since it is used
        // directly as a GradientStop offset.
        foreach (decimal balance in new[] { 0m, 1m, 7.5m, 100m, -5m })
            foreach (decimal full in new[] { 0m, 1m, 10m, 100m })
            {
                double ratio = MainWindow.FillRatio(balance, full);
                Assert.False(double.IsNaN(ratio));
                Assert.InRange(ratio, 0d, 1d);
            }
    }
}
