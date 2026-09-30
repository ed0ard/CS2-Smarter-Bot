using System.Numerics;

namespace BotState;

public sealed record BotFovOptions
{
    public bool Enabled { get; init; }
    public float HorizontalDegrees { get; init; } = 120;
    public float AspectRatio { get; init; } = 16f / 9;

    internal void Validate()
    {
        if (!float.IsFinite(HorizontalDegrees)
            || (HorizontalDegrees != 360 && (HorizontalDegrees < 1 || HorizontalDegrees > 180))
            || !float.IsFinite(AspectRatio) || AspectRatio <= 0)
            throw new ArgumentException("FOV requires 1..180 or 360 degrees and a finite positive aspect ratio.");
    }
}

// Perspective half-spaces, using Source pitch/yaw/roll. No engine calls, traces
// or view-angle writes. The forward hemisphere is the limit at exactly 180.
internal readonly record struct BotViewFrustum(Vector3 Eye, Vector3 Front, Vector3 Left,
    Vector3 Right, Vector3 Bottom, Vector3 Top, bool Omnidirectional)
{
    private const float Radians = MathF.PI / 180;
    internal static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    internal static BotViewFrustum Build(Vector3 eye, Vector3 angles, BotFovOptions options)
    {
        options.Validate();
        if (!Finite(eye) || !Finite(angles)) throw new ArgumentException("Invalid bot eye/angles.");
        float p = angles.X * Radians, y = angles.Y * Radians, r = angles.Z * Radians;
        float sp = MathF.Sin(p), cp = MathF.Cos(p), sy = MathF.Sin(y), cy = MathF.Cos(y);
        Vector3 forward = new(cp * cy, cp * sy, -sp), right = new(-sy, cy, 0), up = new(sp * cy, sp * sy, cp);
        Vector3 rolledRight = right * MathF.Cos(r) + up * MathF.Sin(r);
        Vector3 rolledUp = up * MathF.Cos(r) - right * MathF.Sin(r);
        float half = (options.HorizontalDegrees == 360 ? 90 : options.HorizontalDegrees) * Radians * .5f;
        float sh = MathF.Sin(half), ch = options.HorizontalDegrees == 180 ? 0 : MathF.Cos(half);
        float vertical = MathF.Atan2(sh, options.AspectRatio * ch);
        float sv = MathF.Sin(vertical), cv = ch == 0 ? 0 : MathF.Cos(vertical);
        return new(eye, forward, forward * sh + rolledRight * ch, forward * sh - rolledRight * ch,
            forward * sv + rolledUp * cv, forward * sv - rolledUp * cv, options.HorizontalDegrees == 360);
    }

    internal bool Contains(Vector3 point)
    {
        Vector3 d = point - Eye;
        if (!Finite(d)) return false;
        if (Omnidirectional) return true;
        const float tolerance = .0001f;
        return Vector3.Dot(d, Front) > 0 && Vector3.Dot(d, Left) >= -tolerance
            && Vector3.Dot(d, Right) >= -tolerance && Vector3.Dot(d, Bottom) >= -tolerance
            && Vector3.Dot(d, Top) >= -tolerance;
    }
}

// IsVisible(player) forwards its testFov argument to its body-point queries.
// Disabling its incorrect center gate must not also disable our point gate.
// Scope by observer and restore even on nested calls or exceptions.
internal sealed class BotFovQueryContext
{
    private (nint Bot, BotViewFrustum Frame)? current;
    internal bool TryGet(nint bot, out BotViewFrustum frame)
    {
        frame = default;
        if (current is not { } value || value.Bot != bot) return false;
        frame = value.Frame; return true;
    }
    internal Scope Enter(nint bot, BotViewFrustum frame) => new(this, bot, frame);
    internal readonly struct Scope : IDisposable
    {
        private readonly BotFovQueryContext owner;
        private readonly (nint Bot, BotViewFrustum Frame)? previous;
        internal Scope(BotFovQueryContext owner, nint bot, BotViewFrustum frame)
        { this.owner = owner; previous = owner.current; owner.current = (bot, frame); }
        public void Dispose() => owner.current = previous;
    }
}
