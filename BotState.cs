using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Cvars;

namespace BotState;

public class BotState : BasePlugin
{
    public override string ModuleName        => "Smarter-Bot";
    public override string ModuleVersion     => "1.2.0";
    public override string ModuleAuthor      => "ed0ard";
    public override string ModuleDescription => "Make bots smarter";

    private const float ExpandedValue = 4000f;
    private const float NormalValue   = 100f;
    private const float RestoreDelay  = 1.0f;

    private bool _isExpanded = false;
    private ConVar? _smokeConVar;

    public override void Load(bool hotReload)
    {
        _smokeConVar = ConVar.Find("bot_max_visible_smoke_length");
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo _)
    {
        try
        {
            var victim = @event.Userid;
            if (victim == null || !victim.IsValid || !victim.IsBot) return HookResult.Continue;

            if (!_isExpanded)
            {
                _isExpanded = true;
                SetSmokeLength(ExpandedValue);
                AddTimer(RestoreDelay, () =>
                {
                    SetSmokeLength(NormalValue);
                    _isExpanded = false;
                });
            }
        }
        catch { }
        return HookResult.Continue;
    }

    private void SetSmokeLength(float value)
    {
        if (_smokeConVar != null)
            _smokeConVar.SetValue(value);
        else
            Server.ExecuteCommand($"bot_max_visible_smoke_length {value}");
    }

    public override void Unload(bool hotReload)
    {
        SetSmokeLength(NormalValue);
    }
//---------------------------------------------------------------------------------------
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

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || !player.IsBot) continue;
            ApplyBotState(player);
        }
        return HookResult.Continue;
    }

    private static void ApplyBotState(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;

        var bot = pawn.Bot;
        if (bot == null) return;

        bot.AllowActive = true;

        pawn.BotAllowActive = true;

        bot.IsSleeping = false;

        bot.SafeTime = 0f;

        pawn.IdleTimeSinceLastAction = 0f;

        bot.HasVisitedEnemySpawn = true;
    }
}
