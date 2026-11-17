using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace BotState;

public partial class BotState
{
    private void OnTick()
    {
        ProcessFlashbangAvoidance();
        ExpireReveals(Server.CurrentTime);
        var botController = GetBotController();

        foreach (var player in GetConnectedPlayers())
        {
            if (!player.IsBot) continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            var bot = pawn.Bot;
            if (bot == null)
                continue;
            // In case the bot has been taken over
            bool isTakenOver = player.HasBeenControlledByPlayerThisRound;
            if (isTakenOver) continue;

            int idx = (int)player.Index;
            float now = Server.CurrentTime;
            if (botController != null)
                InterruptReload(botController, player.Slot, pawn, bot, now);
            // Door Stuck Fix
            bool inDoorCooldown = _doorEventCooldown.TryGetValue(idx, out float doorCooldownEnd) && now < doorCooldownEnd;

            UpdateBotBehavior(bot, now);
            bool curIsAttacking = UpdateBotCombat(player, pawn, bot, idx);
            if (!UpdateBotMovement(pawn, bot, idx, curIsAttacking, inDoorCooldown))
                continue;

            RecoverStuckBot(player, pawn, bot, idx, now, curIsAttacking);
            ApplyMapMovementFix(pawn, bot);
        }
    }

    private static IEnumerable<CCSPlayerController> GetConnectedPlayers()
    {
        // Server.MaxPlayers can cache the menu's one-player limit.
        int maxPlayers = NativeAPI.GetMaxClients();
        for (int slot = 0; slot < maxPlayers; slot++)
        {
            if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true, Connected: PlayerConnectedState.Connected } player)
                yield return player;
        }
    }
}
