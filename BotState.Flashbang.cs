using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Commands;
using BotControllerApi;
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.InteropServices;

namespace BotState;

public partial class BotState
{
    private const string BotBlindWindowsSignature =
        "40 53 48 81 EC ? ? ? ? 0F 29 B4 24 ? ? ? ? 48 8D 15";
    private const string BotBlindLinuxSignature =
        "55 48 8D 35 ? ? ? ? B8 ? ? ? ? F3 0F 5A D2";

    // Flashbang avoidance
    private Vector? _scratchEye;
    private readonly List<CHandle<CBaseEntity>> _flashProjectiles = new();
    private readonly List<CHandle<CCSPlayerController>> _flashControllers = new();
    private bool _flashEntitiesDirty = true;
    private MemoryFunctionVoid<nint, float, float, float>? _botBlindFunction;

    private const float FlashFuseSeconds = 1.5f;        // CS2 flashbang fuse
    private const float FlashFovHorizDeg = 110f;        // bot horizontal cone (full angle)
    private const float FlashFovVertDeg = 90f;         // bot vertical cone (full angle)
    private const float FlashMatchSlackSeconds = 0.25f;
    private readonly Dictionary<uint, float> _flashThrownAt = new();   // flash entindex -> server time first seen
    private readonly Dictionary<int, HashSet<uint>> _flashRolledByBot = new(); // bot idx  -> evaluated flashes

    // Per-(bot, flash) decision shared by the native blind hook and OnPlayerBlind
    private struct FlashDecision
    {
        public float FirstSeen;
        public float LastSeen;
        public float DetonateAt;
        public bool Avoided;
    }
    private readonly Dictionary<(int bot, uint flash), FlashDecision> _flashDecisions = new();
    private readonly HashSet<(int bot, uint flash)> _flashRejectLogged = new();
    // While this window is open, later flashes are not rolled and cannot clear the effect for this bot.
    private readonly Dictionary<int, float> _flashBlindUntil = new();

    // Debug logging (toggle with `css_botstate_flashdebug`)
    private bool _debugFlash = false;

    // Toggles or explicitly sets flashbang decision diagnostics.
    [ConsoleCommand("css_botstate_flashdebug", "Toggle Smarter-Bot flashbang debug log")]
    [CommandHelper(minArgs: 0, usage: "[0|1]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnFlashDebugCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string arg = cmd.GetArg(1);
            _debugFlash = arg == "1"
                       || arg.Equals("true", StringComparison.OrdinalIgnoreCase)
                       || arg.Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            _debugFlash = !_debugFlash;
        }

        cmd.ReplyToCommand($"[Smarter-Bot] flash debug = {_debugFlash}");
        Console.WriteLine($"[Smarter-Bot] flash debug = {_debugFlash}");
    }

    // Server stdout + every connected human's console. Use only for debug-gated lines
    // so we don't spam non-debug runs.
    private static void BroadcastDebug(string msg)
    {
        Console.WriteLine(msg);
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
            p.PrintToConsole(msg);
        }
    }
    //---------------------------------------------------------------------------------------
    // Applies the matched avoidance decision to the Bot's visual blind state.
    private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player is null || !player.IsValid || !player.IsBot)
            return HookResult.Continue;
        // In case the bot has been taken over
        bool isTakenOver = player.HasBeenControlledByPlayerThisRound;
        if (isTakenOver)
            return HookResult.Continue;

        int bidx = (int)player.Index;
        float origBlind = @event.BlindDuration;
        bool isImmune;

        // Match this blind event to the bot's most-recently-detonating tracked flash
        float matchNow = Server.CurrentTime;
        bool hasMatchedDecision = TryMatchFlashDecision(
            bidx,
            matchNow,
            out (int bot, uint flash) matchedKey,
            out FlashDecision matched);

        if (hasMatchedDecision)
        {
            isImmune = matched.Avoided;
            _flashDecisions.Remove(matchedKey);
        }
        else
        {
            // Bot never saw this flash through FOV+LOS — should be flashed normally
            isImmune = false;
        }

        bool committed = InCommittedBlind(bidx, matchNow);
        if (isImmune && !committed)
        {
            @event.BlindDuration = 0f;
            var pawn = player.PlayerPawn?.Value;
            if (pawn != null && pawn.IsValid)
            {
                ref float blindStartTime = ref pawn.BlindStartTime;
                blindStartTime = 0f;

                ref float blindUntilTime = ref pawn.BlindUntilTime;
                blindUntilTime = 0f;

                ref float flashDuration = ref pawn.FlashDuration;
                flashDuration = 0f;

                ref float flashMaxAlpha = ref pawn.FlashMaxAlpha;
                flashMaxAlpha = 0f;
            }
        }
        else if (!isImmune && origBlind > 0f)
        {
            float until = matchNow + origBlind;
            if (!_flashBlindUntil.TryGetValue(bidx, out float current) || until > current)
                _flashBlindUntil[bidx] = until;
        }

        if (_debugFlash)
        {
            string detail;
            if (hasMatchedDecision)
            {
                float visibleMs = (matched.LastSeen - matched.FirstSeen) * 1000f;
                detail = $"flash#{matchedKey.flash} visible={visibleMs:F0}ms rolled={(matched.Avoided ? "AVOID" : "flash")}";
            }
            else
            {
                detail = "no tracked flash (out of FOV / occluded entire flight)";
            }
            if (isImmune && committed)
                detail += "; kept prior blind";
            BroadcastDebug(
                $"[Smarter-Bot/Flash] blind event bot={player.PlayerName} immune={isImmune && !committed} origDur={origBlind:F2}s ({detail})");
        }

        return HookResult.Continue;
    }

    // Finds the tracked flash whose predicted detonation is closest to the blind call
    private bool TryMatchFlashDecision(
        int botIndex,
        float now,
        out (int bot, uint flash) matchedKey,
        out FlashDecision matched)
    {
        matchedKey = default;
        matched = default;
        float bestDelta = float.MaxValue;
        bool found = false;

        foreach (var kvp in _flashDecisions)
        {
            if (kvp.Key.bot != botIndex) continue;

            float delta = Math.Abs(kvp.Value.DetonateAt - now);
            if (delta >= bestDelta || delta >= FlashMatchSlackSeconds) continue;

            bestDelta = delta;
            matchedKey = kvp.Key;
            matched = kvp.Value;
            found = true;
        }

        return found;
    }

    // Installs the platform-specific CCSBot::Blind Pre Hook
    private void InstallBotBlindHook()
    {
        string? signature = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? BotBlindWindowsSignature
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? BotBlindLinuxSignature
                : null;

        if (signature == null)
        {
            Logger.LogWarning(
                "[Smarter-Bot] CCSBot::Blind hook is unavailable on this platform; using event fallback");
            return;
        }

        try
        {
            _botBlindFunction =
                new MemoryFunctionVoid<nint, float, float, float>(signature);
            _botBlindFunction.Hook(OnBotBlindPre, HookMode.Pre);
        }
        catch (Exception ex)
        {
            _botBlindFunction = null;
            Logger.LogError(ex,
                "[Smarter-Bot] CCSBot::Blind hook unavailable; using event fallback");
        }
    }

    // Removes the CCSBot::Blind Pre Hook during plugin unload
    private void UninstallBotBlindHook()
    {
        if (_botBlindFunction == null) return;

        try
        {
            _botBlindFunction.Unhook(OnBotBlindPre, HookMode.Pre);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "[Smarter-Bot] Failed to remove CCSBot::Blind hook cleanly");
        }
        _botBlindFunction = null;
    }

    // Stops native blind AI handling only when this flash was successfully avoided
    private HookResult OnBotBlindPre(DynamicHook hook)
    {
        try
        {
            nint botAddress = hook.GetParam<nint>(0);
            if (botAddress == nint.Zero) return HookResult.Continue;

            CCSPlayerController? player = FindBotControllerByAddress(botAddress);
            if (player == null || player.HasBeenControlledByPlayerThisRound)
                return HookResult.Continue;

            int botIndex = (int)player.Index;
            if (InCommittedBlind(botIndex, Server.CurrentTime))
                return HookResult.Continue;

            if (!TryMatchFlashDecision(
                    botIndex,
                    Server.CurrentTime,
                    out (int bot, uint flash) matchedKey,
                    out FlashDecision matched) ||
                !matched.Avoided)
            {
                return HookResult.Continue;
            }

            if (_debugFlash)
            {
                float holdTime = hook.GetParam<float>(1);
                float fadeTime = hook.GetParam<float>(2);
                float alpha = hook.GetParam<float>(3);
                BroadcastDebug(
                    $"[Smarter-Bot/Flash] native blind blocked bot={player.PlayerName} flash#{matchedKey.flash} hold={holdTime:F2}s fade={fadeTime:F2}s alpha={alpha:F0}");
            }

            return HookResult.Stop;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "[Smarter-Bot] CCSBot::Blind hook failed open");
            return HookResult.Continue;
        }
    }

    // Resolves a native CCSBot pointer back to its player controller
    private static CCSPlayerController? FindBotControllerByAddress(nint botAddress)
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || !player.IsBot) continue;

            var pawn = player.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) continue;

            var bot = pawn.Bot;
            if (bot != null && bot.Handle == botAddress) return player;
        }

        return null;
    }
    //---------------------------------------------------------------------------------------
    // Entity lifecycle notifications only invalidate discovery; positions and visibility
    // are still read every tick. Rebuild in native entity-list order to preserve roll order.
    private void InvalidateFlashEntities(CEntityInstance entity) => _flashEntitiesDirty = true;

    private void ClearFlashEntities()
    {
        _flashProjectiles.Clear();
        _flashControllers.Clear();
        _flashEntitiesDirty = true;
    }

    private void RefreshFlashEntities()
    {
        if (!_flashEntitiesDirty) return;

        _flashProjectiles.Clear();
        foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("flashbang_projectile"))
        {
            if (entity.IsValid)
                _flashProjectiles.Add(new CHandle<CBaseEntity>(entity.EntityHandle.Raw));
        }
        _flashControllers.Clear();
        if (_flashProjectiles.Count > 0)
        {
            foreach (var controller in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
            {
                if (controller.IsValid)
                    _flashControllers.Add(new CHandle<CCSPlayerController>(controller.EntityHandle.Raw));
            }
        }
        _flashEntitiesDirty = false;
    }

    // Pre-rolls flash avoidance when the bot first sees the projectile through FOV and LOS
    // The native blind hook reads the result before OnPlayerBlind consumes it
    private void ProcessFlashbangAvoidance()
    {
        if (_scratchEye == null) return;

        float now = Server.CurrentTime;

        RefreshFlashEntities();
        var live = new List<(uint idx, Vector pos, float detonateAt)>();
        foreach (var handle in _flashProjectiles)
        {
            var ent = handle.Value;
            if (ent == null || !ent.IsValid) continue;
            var pos = ent.AbsOrigin;
            if (pos == null) continue;

            uint eidx = ent.Index;
            bool isNew = !_flashThrownAt.ContainsKey(eidx);
            if (isNew)
            {
                _flashThrownAt[eidx] = now;
                if (_debugFlash)
                    BroadcastDebug($"[Smarter-Bot/Flash] new flash#{eidx} at ({pos.X:F0},{pos.Y:F0},{pos.Z:F0}) fuse={FlashFuseSeconds:F2}s");
            }
            live.Add((eidx, pos, _flashThrownAt[eidx] + FlashFuseSeconds));
        }

        // Drop tracking for flashes that no longer exist (detonated / round end). Decisions
        // linger for 2 seconds past detonation so OnPlayerBlind can still match them.
        if (_flashThrownAt.Count > live.Count)
        {
            var alive = new HashSet<uint>(live.Select(f => f.idx));
            var stale = _flashThrownAt.Keys.Where(k => !alive.Contains(k)).ToList();
            foreach (var k in stale)
            {
                if (_debugFlash)
                {
                    foreach (var key in _flashDecisions.Keys.Where(p => p.flash == k).ToList())
                    {
                        var d = _flashDecisions[key];
                        BroadcastDebug(
                            $"[Smarter-Bot/Flash] flash#{k} ended; bot#{key.bot} visible {(d.LastSeen - d.FirstSeen) * 1000f:F0}ms");
                    }
                }
                _flashThrownAt.Remove(k);
                foreach (var s in _flashRolledByBot.Values) s.Remove(k);
                _flashRejectLogged.RemoveWhere(p => p.flash == k);
            }
        }

        // Expire stale decisions (2s past their detonation) so we don't leak across rounds.
        if (_flashDecisions.Count > 0)
        {
            var expired = _flashDecisions.Where(kvp => now - kvp.Value.DetonateAt > 2f)
                                          .Select(kvp => kvp.Key)
                                          .ToList();
            foreach (var k in expired) _flashDecisions.Remove(k);
        }

        if (live.Count == 0) return;

        foreach (var handle in _flashControllers)
        {
            var bot = handle.Value;
            if (bot == null || !bot.IsValid || !bot.IsBot) continue;
            if (bot.HasBeenControlledByPlayerThisRound) continue;

            var pawn = bot.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;

            int bidx = (int)bot.Index;
            if (InCommittedBlind(bidx, now))
                continue;

            if (!_flashRolledByBot.TryGetValue(bidx, out var rolled))
            {
                rolled = new HashSet<uint>();
                _flashRolledByBot[bidx] = rolled;
            }

            foreach (var (fidx, fpos, detonateAt) in live)
            {
                if (now > detonateAt) continue;

                bool inFov = IsInFov(pawn, fpos, FlashFovHorizDeg, FlashFovVertDeg,
                                     out float dYaw, out float dPit);
                if (!inFov)
                {
                    if (_debugFlash && _flashRejectLogged.Add((bidx, fidx)))
                        BroadcastDebug($"[Smarter-Bot/Flash] bot={bot.PlayerName} flash#{fidx} REJECT-FOV dYaw={dYaw:F1} dPit={dPit:F1}");
                    continue;
                }
                if (!BotCanSee(pawn, fpos))
                {
                    if (_debugFlash && _flashRejectLogged.Add((bidx, fidx)))
                        BroadcastDebug($"[Smarter-Bot/Flash] bot={bot.PlayerName} flash#{fidx} REJECT-LOS dYaw={dYaw:F1} dPit={dPit:F1}");
                    continue;
                }
                _flashRejectLogged.Remove((bidx, fidx));

                var key = (bidx, fidx);

                if (rolled.Contains(fidx))
                {
                    // Already rolled — refresh lastSeen so visible duration reflects full sight window
                    if (_flashDecisions.TryGetValue(key, out var d))
                    {
                        d.LastSeen = now;
                        _flashDecisions[key] = d;
                    }
                    continue;
                }
                rolled.Add(fidx);

                float msLeft = (detonateAt - now) * 1000f;
                double prob = msLeft <= 150f ? 0.05
                            : msLeft <= 250f ? 0.20
                            : msLeft <= 400f ? 0.50
                            : msLeft <= 600f ? 0.90
                            : 0.95;

                bool avoided = _random.NextDouble() <= prob;

                _flashDecisions[key] = new FlashDecision
                {
                    FirstSeen = now,
                    LastSeen = now,
                    DetonateAt = detonateAt,
                    Avoided = avoided,
                };

                if (_debugFlash)
                {
                    BroadcastDebug(
                        $"[Smarter-Bot/Flash] bot={bot.PlayerName} sees flash#{fidx} t-{msLeft:F0}ms prob={prob * 100:F0}% roll={(avoided ? "AVOID" : "flash")}");
                }
            }
        }
    }

    // Decoupled horizontal/vertical FOV check. Source 2 QAngle convention:
    //   EyeAngles.Y = yaw   (0 deg => +X axis, 90 deg => +Y axis)
    //   EyeAngles.X = pitch (positive => looking DOWN; this is the Quake/Source convention)
    // Returns true when target is inside both cones; outDeltaYaw/outDeltaPitch are
    // signed angle deltas (target relative to bot view) for debug logging.
    private static bool IsInFov(CCSPlayerPawn pawn, Vector target,
                                float horizDeg, float vertDeg,
                                out float outDeltaYaw, out float outDeltaPitch)
    {
        outDeltaYaw = 0f;
        outDeltaPitch = 0f;

        var origin = pawn.AbsOrigin;
        if (origin == null) return false;

        float eyeZ = origin.Z + pawn.ViewOffset.Z;

        double dx = target.X - origin.X;
        double dy = target.Y - origin.Y;
        double dz = target.Z - eyeZ;

        double horizDist = Math.Sqrt(dx * dx + dy * dy);
        if (horizDist < 1e-3 && Math.Abs(dz) < 1e-3) return true;

        double yawToTarget = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        double pitchToTarget = -Math.Atan2(dz, horizDist) * 180.0 / Math.PI;

        double yawDelta = NormalizeAngleDeg(yawToTarget - pawn.EyeAngles.Y);
        double pitchDelta = NormalizeAngleDeg(pitchToTarget - pawn.EyeAngles.X);

        outDeltaYaw = (float)yawDelta;
        outDeltaPitch = (float)pitchDelta;

        return Math.Abs(yawDelta) <= horizDeg * 0.5
            && Math.Abs(pitchDelta) <= vertDeg * 0.5;
    }

    // Wraps an angular difference to the signed half-turn range.
    private static double NormalizeAngleDeg(double a)
    {
        a %= 360.0;
        if (a > 180.0) a -= 360.0;
        if (a < -180.0) a += 360.0;
        return a;
    }

    // Checks brush visibility from the Bot's eye position to a flash projectile.
    private bool BotCanSee(CCSPlayerPawn pawn, Vector target)
    {
        if (_scratchEye == null) return false;

        var origin = pawn.AbsOrigin;
        if (origin == null) return false;

        _scratchEye.X = origin.X;
        _scratchEye.Y = origin.Y;
        _scratchEye.Z = origin.Z + pawn.ViewOffset.Z;

        var opts = new TraceOptions { InteractsWith = Masks.SolidBrushOnly };
        var result = Trace.TraceEndShape(_scratchEye, target, pawn, opts);
        return result.Fraction >= 0.999f;
    }

    // Clears flashbang state owned by the completed round.
    private void ResetFlashbangRoundState()
    {
        // Flash entity indices may be reused after a round transition.
        _flashThrownAt.Clear();
        _flashRolledByBot.Clear();
        _flashDecisions.Clear();
        _flashRejectLogged.Clear();
        _flashBlindUntil.Clear();
    }

    // A flash that was not avoided owns the bot until CurrentTime + that event's BlindDuration.
    private bool InCommittedBlind(int botIndex, float now)
        => _flashBlindUntil.TryGetValue(botIndex, out float until) && now < until;
}
