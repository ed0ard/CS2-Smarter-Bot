using System.Numerics;
using Xunit;

namespace BotState.Tests;

public sealed class BotFovTests
{
    private static BotViewFrustum View(float horizontal = 120, Vector3 angles = default, float aspect = 16f / 9)
        => BotViewFrustum.Build(Vector3.Zero, angles, new() { HorizontalDegrees = horizontal, AspectRatio = aspect });
    private static Vector3 Direction(float yaw, float pitch = 0)
    {
        float y = yaw * MathF.PI / 180, p = pitch * MathF.PI / 180;
        return new Vector3(MathF.Cos(p) * MathF.Cos(y), MathF.Cos(p) * MathF.Sin(y), -MathF.Sin(p)) * 100;
    }

    [Theory]
    [InlineData(0, true)] [InlineData(59, true)] [InlineData(60, true)]
    [InlineData(61, false)] [InlineData(-59, true)] [InlineData(-61, false)] [InlineData(180, false)]
    public void HorizontalBoundaries(float yaw, bool expected) => Assert.Equal(expected, View().Contains(Direction(yaw)));

    [Fact]
    public void VerticalFovComesFromPerspectiveAndAspect()
    {
        // 120 degrees at 16:9 gives a vertical half-angle around 44.25 degrees.
        Assert.True(View().Contains(Direction(0, 44)));
        Assert.False(View().Contains(Direction(0, 45)));
        Assert.True(View(aspect: 1).Contains(Direction(0, 59)));
        Assert.False(View().Contains(Direction(0, -45)));
    }

    [Fact]
    public void PitchYawRollAndTranslationMoveTheWholeView()
    {
        var pitched = View(angles: new(60, 179, 0));
        Assert.True(pitched.Contains(Direction(-179, 60)));
        Assert.False(pitched.Contains(Direction(179, 0)));
        Assert.False(View().Contains(Direction(0, 55)));
        Assert.True(View(angles: new(0, 0, 90)).Contains(Direction(0, 55)));
        Assert.False(View(angles: new(0, 0, 90)).Contains(Direction(55)));
        Vector3 eye = new(1200, -300, 70);
        var translated = BotViewFrustum.Build(eye, Vector3.Zero, new());
        Assert.Equal(View().Contains(Direction(59)), translated.Contains(eye + Direction(59)));
    }

    [Fact]
    public void HemisphereAndOmnidirectionalModesHaveExplicitBoundaries()
    {
        Assert.True(View(180).Contains(new(.01f, 1000, 1000)));
        Assert.False(View(180).Contains(new(-.01f, 0, 0)));
        Assert.False(View(180).Contains(Vector3.Zero));
        Assert.True(View(360).Contains(new(-100, 10, 1000)));
        Assert.False(View(360).Contains(new(float.NaN, 0, 0)));
    }

    [Fact]
    public void PerspectiveCornersAreNotASphericalCone()
    {
        // Screen corner: forward projection is 1, x/y each just inside its half-plane.
        float x = MathF.Tan(MathF.PI / 3) * .99f, z = x / (16f / 9);
        Assert.True(View().Contains(new(1, x, z)));
        Assert.True(MathF.Acos(Vector3.Normalize(new(1, x, z)).X) > MathF.PI / 3);
    }

    [Fact]
    public void RandomProjectedPointsAgreeWithIndependentCameraInequalities()
    {
        var random = new Random(419);
        for (int i = 0; i < 3000; i++)
        {
            float fov = 10 + (float)random.NextDouble() * 160;
            float aspect = .5f + (float)random.NextDouble() * 2.5f;
            Vector3 p = new((float)random.NextDouble() * 200 - 100,
                (float)random.NextDouble() * 200 - 100, (float)random.NextDouble() * 200 - 100);
            float h = MathF.Tan(fov * MathF.PI / 360);
            bool expected = p.X > 0 && MathF.Abs(p.Y) <= p.X * h && MathF.Abs(p.Z) <= p.X * h / aspect;
            Assert.Equal(expected, View(fov, aspect: aspect).Contains(p));
        }
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(181, 1)] [InlineData(359, 1)]
    [InlineData(120, 0)] [InlineData(120, -1)] [InlineData(float.NaN, 1)] [InlineData(120, float.PositiveInfinity)]
    public void InvalidSettingsAreRejected(float horizontal, float aspect)
        => Assert.Throws<ArgumentException>(() => View(horizontal, aspect: aspect));

    [Fact]
    public void PlayerScopeRetainsPointFovWithoutLeakingToOtherBotsOrLaterCalls()
    {
        var context = new BotFovQueryContext();
        using (context.Enter(1, View()))
        {
            Assert.True(context.TryGet(1, out var frame));
            Assert.False(frame.Contains(Direction(100)));
            Assert.True(frame.Contains(Direction(59))); // An exposed point can pass even if center is outside.
            Assert.False(context.TryGet(2, out _));
            using (context.Enter(2, View(360)))
            {
                Assert.False(context.TryGet(1, out _));
                Assert.True(context.TryGet(2, out var nested));
                Assert.True(nested.Contains(Direction(180)));
            }
            Assert.True(context.TryGet(1, out _));
        }
        Assert.False(context.TryGet(1, out _)); // Direct no-FOV sound/LOS queries have no inherited scope.
    }

    [Fact]
    public void ScopeRestoresAfterNativeCallFailureAndSameBotRecursion()
    {
        var context = new BotFovQueryContext();
        using (context.Enter(1, View()))
        {
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var nested = context.Enter(1, View(360));
                throw new InvalidOperationException();
            }));
            Assert.True(context.TryGet(1, out var restored));
            Assert.False(restored.Contains(Direction(180)));
        }
        Assert.False(context.TryGet(1, out _));
    }
}
