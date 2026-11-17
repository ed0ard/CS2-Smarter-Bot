using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.InteropServices;

namespace BotState;

public partial class BotState
{
    private const ulong UseButtonMask = (ulong)PlayerButtons.Use;
    private const float FakeDefuseHoldMinSeconds = 0.1f;
    private const float FakeDefuseHoldMaxSeconds = 0.8f;
    private const float FakeDefuseSearchMinSeconds = 2.0f;
    private const float FakeDefuseSearchMaxSeconds = 4.0f;
    private const string DefuseBombWindowsSignature =
        "48 8D 91 08 04 00 00 E9 ? ? ? ?";
    private const string DefuseBombLinuxSignature =
        "48 8D B7 00 04 00 00 E9 ? ? ? ?";
    private readonly Dictionary<int, float> _fakeDefuseCooldown = new();
    private readonly Dictionary<nint, float> _fakeDefuseGuardUntil = new();
    private readonly Dictionary<nint, int> _fakeDefuseCounts = new();
    private readonly HashSet<int> _fakeDefuseSearchingBots = new();
    private readonly HashSet<int> _pendingDefuseRestore = new();
    private readonly Dictionary<int, long> _fakeDefuseSuppressionIds = new();
    private MemoryFunctionVoid<nint>? _defuseBombFunction;

    // Ends the defuser reveal cycle when defusing is interrupted.
    private HookResult OnBombAbortDefuse(EventBombAbortdefuse @event, GameEventInfo info)
    {
        StopDefuseReveal();
        return HookResult.Continue;
    }

    // Ends the defuser reveal cycle after a successful defuse.
    private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        StopDefuseReveal();
        return HookResult.Continue;
    }

    // Ends the defuser reveal cycle when the bomb explodes.
    private HookResult OnBombExploded(EventBombExploded @event, GameEventInfo info)
    {
        StopDefuseReveal();
        return HookResult.Continue;
    }

    // Makes non-possessed Bots hurry once the bomb has been planted.
    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot)
                continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            var bot = pawn.Bot;
            if (bot == null)
                continue;

            bool isTakenOver = player.HasBeenControlledByPlayerThisRound;
            if (isTakenOver) continue;

            CountdownTimer hurryTimer = bot.HurryTimer;

            ref float duration = ref hurryTimer.Duration;
            duration = 40.0f;

            ref float timestamp = ref hurryTimer.Timestamp;
            timestamp = Server.CurrentTime + duration;

            ref float timescale = ref hurryTimer.Timescale;
            timescale = 1.0f;

            ref bool isRunning = ref bot.IsRunning;
            isRunning = true;
        }
        return HookResult.Continue;
    }

    // Starts the reveal cycle and rolls the existing fake-defuse probability.
    private HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        ResetLookAroundForBot(@event.Userid);

        var player = @event.Userid;
        // The bomb-defuser is revealed
        _defuserSlot = player != null && player.IsValid ? player.Slot : -1;
        _isBombBeingDefused = true;
        StartDefuseRevealCycle();

        if (player == null || !player.IsValid || !player.IsBot) return HookResult.Continue;

        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return HookResult.Continue;

        var bot = pawn.Bot;
        if (bot == null) return HookResult.Continue;

        bool isTakenOver = player.HasBeenControlledByPlayerThisRound;
        if (isTakenOver) return HookResult.Continue;

        bool hasLivingEnemies = Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Any(p => p.IsValid && p.PawnIsAlive
                && ((int)p.TeamNum == 2 || (int)p.TeamNum == 3)
                && (int)p.TeamNum != (int)player.TeamNum);
        // Fake Defuse
        if (hasLivingEnemies)
        {
            // If we have a defuser, tend to defuse directly
            var itemSvc = pawn.ItemServices?.Handle != nint.Zero
                ? new CCSPlayer_ItemServices(pawn.ItemServices!.Handle)
                : null;
            bool hasDefuser = itemSvc?.HasDefuser ?? false;
            double baseFakeChance = hasDefuser ? 0.20 : 0.66;
            int fakeDefuseCount = _fakeDefuseCounts.GetValueOrDefault(bot.Handle);
            double fakeChance = baseFakeChance * Math.Pow(0.66, fakeDefuseCount);
            int slot = player.Slot;
            bool fakeDefuseCoolingDown = _fakeDefuseCooldown.TryGetValue(
                slot, out float cooldownEnd) && Server.CurrentTime < cooldownEnd;

            if (!fakeDefuseCoolingDown && _random.NextDouble() < fakeChance)
            {
                ScheduleFakeDefuse(player);
            }
        }

        return HookResult.Continue;
    }

    // Keeps the real defuse sound briefly before entering the guard search window
    private void ScheduleFakeDefuse(CCSPlayerController player)
    {
        if (_botController == null || _defuseBombFunction == null) return;

        float holdSeconds = FakeDefuseHoldMinSeconds +
            (float)_random.NextDouble() *
            (FakeDefuseHoldMaxSeconds - FakeDefuseHoldMinSeconds);
        float searchSeconds = FakeDefuseSearchMinSeconds +
            (float)_random.NextDouble() *
            (FakeDefuseSearchMaxSeconds - FakeDefuseSearchMinSeconds);

        float now = Server.CurrentTime;
        int slot = player.Slot;
        _fakeDefuseCooldown[slot] = now + holdSeconds + searchSeconds;

        AddTimer(
            holdSeconds,
            () => FinishFakeDefuse(slot, searchSeconds),
            CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
    }

    // Releases Use and leaves the Bot near the bomb to acquire threats normally
    private void FinishFakeDefuse(int slot, float searchSeconds)
    {
        if (_botController == null) return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null || !player.IsValid || !player.IsBot ||
            !player.PawnIsAlive || player.HasBeenControlledByPlayerThisRound)
            return;

        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid || !pawn.IsDefusing) return;

        var bot = pawn.Bot;
        if (bot == null) return;

        long suppressionId = BotControllerBridge.StartUsercmdSuppression(
            _botController, slot, UseButtonMask);
        if (suppressionId <= 0) return;

        _fakeDefuseSuppressionIds[slot] = suppressionId;

        _fakeDefuseGuardUntil[bot.Handle] = Server.CurrentTime + searchSeconds;
        _fakeDefuseCounts[bot.Handle] =
            _fakeDefuseCounts.GetValueOrDefault(bot.Handle) + 1;
        _pendingDefuseRestore.Add(slot);

        ref float stateTimestamp = ref bot.StateTimestamp;
        stateTimestamp = Server.CurrentTime - 2.0f;

        ResetLookAroundForBot(player);

        // Enable 360 FOV for this bot during search phase
        _fakeDefuseSearchingBots.Add(slot);
        ApplyFovPatches();

        // Schedule FOV restoration after search completes
        AddTimer(
            searchSeconds,
            () => EndFakeDefuseSearch(slot),
            CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
    }

    // Removes bot from search phase and restores FOV if no other bots are searching
    private void EndFakeDefuseSearch(int slot)
    {
        CancelFakeDefuseSuppression(slot);
        _fakeDefuseSearchingBots.Remove(slot);
        if (_fakeDefuseSearchingBots.Count == 0)
        {
            RestoreAllFovPatches();
        }
    }

    // Releases one Bot's fake-defuse +use suppression, if it still holds one
    private void CancelFakeDefuseSuppression(int slot)
    {
        if (!_fakeDefuseSuppressionIds.Remove(slot, out long suppressionId))
            return;
        if (_botController == null) return;

        BotControllerBridge.CancelUsercmdSuppression(
            _botController, slot, suppressionId);
    }

    // Releases every outstanding fake-defuse +use suppression.
    private void CancelAllFakeDefuseSuppressions()
    {
        foreach (int slot in _fakeDefuseSuppressionIds.Keys.ToList())
            CancelFakeDefuseSuppression(slot);
    }

    // Grants each Bot that faked a defuse this round one DefuseBomb re-entry at
    // the moment its last enemy dies
    private void RestoreDefuseAfterElimination(List<CCSPlayerController> bots)
    {
        foreach (var bot in bots)
        {
            if (_pendingDefuseRestore.Remove(bot.Slot))
                ForceDefuseBombState(bot.Slot);
        }
    }

    // Pushes the native state machine straight back into DefuseBomb
    private void ForceDefuseBombState(int slot)
    {
        Server.NextFrame(() =>
        {
            if (_defuseBombFunction == null) return;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid || !player.IsBot ||
                !player.PawnIsAlive || player.HasBeenControlledByPlayerThisRound)
                return;

            var pawn = player.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) return;

            var bot = pawn.Bot;
            if (bot == null) return;

            // Re-entering the state is useless while Use is still stripped
            CancelFakeDefuseSuppression(slot);

            // Our own guard would otherwise stop this state entry
            _fakeDefuseGuardUntil.Remove(bot.Handle);
            _defuseBombFunction.Invoke(bot.Handle);
        });
    }

    // Installs the Smarter-Bot-owned guard for native DefuseBomb state entry
    private void InstallDefuseBombHook()
    {
        try
        {
            string signature = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? DefuseBombWindowsSignature
                : DefuseBombLinuxSignature;
            _defuseBombFunction = new MemoryFunctionVoid<nint>(signature);
            _defuseBombFunction.Hook(OnDefuseBombPre, HookMode.Pre);
        }
        catch (Exception ex)
        {
            _defuseBombFunction = null;
            Logger.LogError(ex,
                "[Smarter-Bot] CCSBot::DefuseBomb hook unavailable; fake defuse disabled");
        }
    }

    // Removes the Smarter-Bot-owned DefuseBomb state-entry guard
    private void UninstallDefuseBombHook()
    {
        if (_defuseBombFunction == null) return;

        try
        {
            _defuseBombFunction.Unhook(OnDefuseBombPre, HookMode.Pre);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "[Smarter-Bot] Failed to remove CCSBot::DefuseBomb hook cleanly");
        }
        _defuseBombFunction = null;
    }

    // Stops guarded Bots before the engine can enter DefuseBomb again
    private HookResult OnDefuseBombPre(DynamicHook hook)
    {
        try
        {
            nint botAddress = hook.GetParam<nint>(0);
            if (botAddress == nint.Zero) return HookResult.Continue;

            if (_fakeDefuseGuardUntil.TryGetValue(botAddress, out float guardUntil))
            {
                if (Server.CurrentTime < guardUntil) return HookResult.Stop;
                _fakeDefuseGuardUntil.Remove(botAddress);
            }
        }
        catch
        {
            return HookResult.Continue;
        }

        return HookResult.Continue;
    }

    // Clears bomb state owned by the completed round.
    private void ResetBombRoundState()
    {
        _fakeDefuseCooldown.Clear();
        _fakeDefuseGuardUntil.Clear();
        _fakeDefuseCounts.Clear();
        CancelAllFakeDefuseSuppressions();
        _fakeDefuseSearchingBots.Clear();
        _pendingDefuseRestore.Clear();
        RestoreAllFovPatches();
    }
}
