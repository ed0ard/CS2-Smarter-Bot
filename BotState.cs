using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;

namespace BotState;

public class BotState : BasePlugin
{
    public override string ModuleName        => "BotState";
    public override string ModuleVersion     => "1.1.0";
    public override string ModuleAuthor      => "ed0ard";

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
    }

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
