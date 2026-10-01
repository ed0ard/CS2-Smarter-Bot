using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

public partial class BotState
{
    private bool _isFreezeTime = false;
    //---------------------------------------------------------------------------------------
    // Registers game events and the per-tick bot behavior listener
    public override void Load(bool hotReload)
    {
        InstallDefuseBombHook();
        InstallBotBlindHook();
        InitializeFovPatches();
        LoadCustomFov();
        RegisterListener<Listeners.OnMapEnd>(ClearFovObservers);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);
        RegisterEventHandler<EventBombAbortdefuse>(OnBombAbortDefuse);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventBombExploded>(OnBombExploded);
        RegisterEventHandler<EventDoorOpen>(OnDoorOpen);
        RegisterEventHandler<EventDoorClose>(OnDoorClose);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterListener<Listeners.OnTick>(OnTick);
        // Prevent bots from holding their knives when there're enemies alive
        _gunReequipTimer = AddTimer(
            1.0f,
            ReequipGunForActiveBots,
            CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
    }

    // Resolves capabilities supplied by plugins after every plugin has loaded
    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _scratchEye = new Vector();
        try { _botController = BotControllerBridge.TryGet(); } catch { _botController = null; }
        if (_botController == null)
            Console.WriteLine("[Smarter-Bot] BotController API not available");
    }

    // Restores plugin-owned state before the plugin unloads
    public override void Unload(bool hotReload)
    {
        _customFov?.Dispose(); _customFov = null;
        ClearFovObservers();
        CancelAllFakeDefuseSuppressions();
        UninstallBotBlindHook();
        UninstallDefuseBombHook();
        RestoreAllFovPatches();
        ReleaseKnifeLocks();
        ClearReveals();
        _defuseRevealTimer?.Kill();
        _gunReequipTimer?.Kill();
    }
    //---------------------------------------------------------------------------------------
    // Applies initial Bot state on the frame following spawn.
    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot)
            return HookResult.Continue;

        Server.NextFrame(() =>
        {
            if (player == null || !player.IsValid) return;
            ApplyBotState(player);
        });

        return HookResult.Continue;
    }

    // Reapplies initial Bot state when the freeze period ends.
    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        _isFreezeTime = false;
        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot) continue;
            ApplyBotState(player);
        }
        return HookResult.Continue;
    }
    //---------------------------------------------------------------------------------------
    // Clears per-round state and releases elimination knife locks
    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ClearFovObservers();
        ReleaseKnifeLocks();
        StopDefuseReveal();
        ClearReveals();
        _eliminationHandled = false;
        _isFreezeTime = true;

        ResetMovementRoundState();
        ResetCombatRoundState();
        ResetWeaponsRoundState();
        ResetBombRoundState();
        ResetFlashbangRoundState();
        return HookResult.Continue;
    }

    // Detects elimination while explicitly excluding the current death victim
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        // Deathmatch is free-for-all even though the engine still reports
        // temporary T/CT team numbers. Do not treat the last bot on one side
        // as a round elimination and lock the other bots to their knives.
        if (IsDeathmatch())
            return HookResult.Continue;

        if (_botController == null)
            return HookResult.Continue;

        var victim = @event.Userid;
        if (victim == null || !victim.IsValid)
            return HookResult.Continue;

        CsTeam victimTeam = (CsTeam)(int)victim.TeamNum;
        if (victimTeam != CsTeam.Terrorist &&
            victimTeam != CsTeam.CounterTerrorist)
            return HookResult.Continue;

        bool alreadyHandled = _eliminationHandled;
        HandleTeamElimination(victim.Slot, victimTeam);
        RestoreDefuseAfterElimination(victim.Slot, victimTeam);

        // 10% chance the killer Bot inspects its current weapon. Skip when this
        // exact kill just triggered the elimination switching, since that path
        // already inspects the knife.
        if (alreadyHandled || !_eliminationHandled)
            MaybeInspectOnKill(@event.Attacker);

        return HookResult.Continue;
    }

    // Excludes deathmatch from team-elimination behavior.
    private static bool IsDeathmatch()
    {
        var gameType = ConVar.Find("game_type");
        var gameMode = ConVar.Find("game_mode");
        return gameType?.GetPrimitiveValue<int>() == 1
            && gameMode?.GetPrimitiveValue<int>() == 2;
    }
}
