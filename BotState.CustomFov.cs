using System.Globalization;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace BotState;

public partial class BotState
{
    private BotFovOptions _fovOptions = new();
    private BotFovHooks? _customFov;
    private string _customFovStatus = "native";
    private int _fovRosterTick = -1;
    private readonly Dictionary<nint, CCSPlayerController> _fovObservers = new(64);
    private readonly Dictionary<nint, (uint Pawn, Vector3 Eye, Vector3 Angles, BotViewFrustum Frame)> _fovFrames = new(64);
    private bool CustomFovActive => _fovOptions.Enabled && _customFov is { Failed: false };

    private void LoadCustomFov()
    {
        try { ApplyCustomFov(_fovOptions); }
        catch (Exception ex)
        {
            _customFovStatus = "unavailable: " + ex.Message;
            Logger.LogError(ex, "[Smarter-Bot] Custom FOV unavailable; native vision remains active");
        }
    }

    private void ApplyCustomFov(BotFovOptions options)
    {
        options.Validate();
        if (options.Enabled)
        {
            if (_customFov is { Failed: true }) throw new InvalidOperationException("Reload the plugin after a native FOV failure.");
            _customFov ??= new(GetCustomFovView, ex =>
            {
                _customFovStatus = "failed: " + ex.Message;
                Logger.LogError(ex, "[Smarter-Bot] Custom FOV disabled; native vision resumes");
            });
            // Fake defuse's existing patches are process-wide. Custom FOV is
            // authoritative while enabled, including during its search phase.
            RestoreAllFovPatches();
        }
        _fovOptions = options;
        _customFovStatus = options.Enabled ? "custom" : "native";
        ClearFovObservers();
        if (!options.Enabled && _fakeDefuseSearchingBots.Count != 0) ApplyFovPatches();
    }

    private void ClearFovObservers()
    { _fovObservers.Clear(); _fovFrames.Clear(); _fovRosterTick = -1; }

    private BotViewFrustum? GetCustomFovView(nint bot)
    {
        if (!CustomFovActive || bot == 0) return null;
        if (_fovRosterTick != Server.TickCount)
        {
            ClearFovObservers(); _fovRosterTick = Server.TickCount;
            foreach (var p in Utilities.GetPlayers())
                if (p.IsValid && p.IsBot && p.PlayerPawn.Value?.Bot is { } native)
                    _fovObservers[native.Handle] = p;
        }
        if (!_fovObservers.TryGetValue(bot, out var player) || !player.IsValid || !player.IsBot || player.IsHLTV
            || player.HasBeenControlledByPlayerThisRound || player.ControllingBot
            || player.PlayerPawn.Value is not { IsValid: true, Health: > 0, LifeState: 0 } pawn
            || pawn.Bot?.Handle != bot || pawn.AbsOrigin is not { } origin
            || (_botController != null && BotControllerBridge.IsViewControlled(_botController, player.Slot))) return null;
        var offset = pawn.ViewOffset;
        Vector3 eye = new(origin.X + offset.X, origin.Y + offset.Y, origin.Z + offset.Z);
        var a = pawn.V_angle;
        Vector3 angles = new(a.X, a.Y, a.Z);
        if (!BotViewFrustum.Finite(eye) || !BotViewFrustum.Finite(angles)) return null;
        uint identity = pawn.EntityHandle.Raw;
        if (_fovFrames.TryGetValue(bot, out var saved) && saved.Pawn == identity && saved.Eye == eye && saved.Angles == angles)
            return saved.Frame;
        var frame = BotViewFrustum.Build(eye, angles, _fovOptions);
        _fovFrames[bot] = (identity, eye, angles, frame);
        return frame;
    }

    private const string FovUsage = "css_bot_fov status | native | <horizontal> [vertical]";

    [ConsoleCommand("css_bot_fov", FovUsage)]
    [RequiresPermissions("@css/root")]
    public void OnBotFovCommand(CCSPlayerController? caller, CommandInfo command)
    {
        string arg = command.ArgCount > 1 ? command.GetArg(1) : "status";
        try
        {
            if (arg.Equals("native", StringComparison.OrdinalIgnoreCase) || arg.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                if (command.ArgCount > 2) throw new ArgumentException("Usage: " + FovUsage);
                if (arg.Equals("native", StringComparison.OrdinalIgnoreCase))
                    ApplyCustomFov(_fovOptions with { Enabled = false });
            }
            else
            {
                if (command.ArgCount > 3)
                    throw new ArgumentException("Usage: " + FovUsage);
                ApplyCustomFov(BotFovOptions.FromDegrees(
                    float.Parse(arg, NumberStyles.Float, CultureInfo.InvariantCulture),
                    command.ArgCount == 3 ? float.Parse(command.GetArg(2), NumberStyles.Float, CultureInfo.InvariantCulture) : null));
            }
            var options = _fovOptions;
            command.ReplyToCommand(FormattableString.Invariant($"[Smarter-Bot] FOV={_customFovStatus}; active={CustomFovActive}; horizontal={options.HorizontalDegrees:0.###}; vertical={options.VerticalDegrees:0.###}; points={_customFov?.PointsTested ?? 0}; rejected={_customFov?.PointsRejected ?? 0}"));
        }
        catch (Exception ex) { command.ReplyToCommand("[Smarter-Bot] " + ex.Message); }
    }
}
