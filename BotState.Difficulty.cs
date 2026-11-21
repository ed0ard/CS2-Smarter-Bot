using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace BotState;

public partial class BotState
{
    private sealed record Difficulty(string Name, Func<object, int, bool> Apply);

    private const string DifficultyCommand = "bot_preset";
    private const string DifficultyUsage = DifficultyCommand + " [low|medium|high]";
    private Difficulty? _activeDifficulty;
    private Difficulty? _pendingDifficulty;
    private readonly Dictionary<uint, CCSPlayerController> _difficultySpawns = [];
    private bool _allBotsQueued;

    [ConsoleCommand(DifficultyCommand, DifficultyUsage)]
    public void OnBotDifficultyCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (command.ArgCount == 1)
        {
            PrintDifficulty();
            return;
        }

        string? level = command.GetArg(1).ToLowerInvariant() switch { "low" => "Low", "medium" => "Medium", "high" => "High", _ => null };
        if (command.ArgCount != 2 || level == null)
        {
            Server.PrintToConsole($"[Smarter-Bot] Usage: {DifficultyUsage}");
            return;
        }
        if (GetDifficultyController() == null)
        {
            Server.PrintToConsole("[Smarter-Bot] BotController unavailable");
            return;
        }

        try
        {
            string path = Path.Combine(ModuleDirectory, "difficulty", level, "botprofile.db");
            _pendingDifficulty = new(level, BotControllerBridge.LoadDifficulty(path));
            PrintDifficulty();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
            or TypeLoadException or MissingMemberException)
        {
            Server.PrintToConsole($"[Smarter-Bot] Cannot load {level}: {ex.Message}");
        }
    }

    private static object? GetDifficultyController()
    {
        try { return BotControllerBridge.TryGet(); }
        catch (Exception ex) when (ex is KeyNotFoundException or FileNotFoundException or FileLoadException
            or TypeLoadException or TypeInitializationException) { return null; }
    }

    private static IEnumerable<CCSPlayerController> GetDifficultyPlayers()
    {
        // Server.MaxPlayers can cache the menu's one-player limit.
        int maxPlayers = NativeAPI.GetMaxClients();
        for (int slot = 0; slot < maxPlayers; slot++)
            if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true, Connected: PlayerConnectedState.Connected } player)
                yield return player;
    }

    private void PrintDifficulty()
    {
        string pending = _pendingDifficulty is { } next ? $" -> {next.Name}" : "";
        Server.PrintToConsole($"[Smarter-Bot] {DifficultyCommand} = {_activeDifficulty?.Name ?? "none"}{pending}");
    }

    private void StartDifficultyRound()
    {
        _activeDifficulty = _pendingDifficulty ?? _activeDifficulty;
        _pendingDifficulty = null;
        QueueDifficulty();
    }

    // Round start and spawns in the same frame share one profile write per bot.
    private void QueueDifficulty(CCSPlayerController? spawned = null)
    {
        if (_activeDifficulty == null || _allBotsQueued) return;
        if (_difficultySpawns.Count == 0) Server.NextFrame(ApplyDifficulty);
        if (spawned == null)
            _allBotsQueued = true;
        else
            _difficultySpawns[spawned.EntityHandle.Raw] = spawned;
    }

    private void ApplyDifficulty()
    {
        try
        {
            if (_activeDifficulty is not { } difficulty) return;
            var api = GetDifficultyController();
            var players = _allBotsQueued ? GetDifficultyPlayers() : _difficultySpawns.Values.Where(p => p.IsValid);
            int skipped = 0;
            foreach (var player in players)
                if (player.IsBot && !player.IsHLTV && (api == null || !difficulty.Apply(api, player.Slot)))
                    skipped++;
            if (skipped != 0)
                Logger.LogWarning("[Smarter-Bot] {Difficulty} not applied to {Count} bots.", difficulty.Name, skipped);
        }
        catch (Exception ex) when (ex is TypeLoadException or MissingMemberException)
        {
            _activeDifficulty = null;
            Logger.LogError("[Smarter-Bot] Difficulty disabled: incompatible BotController API ({Error}).", ex.Message);
        }
        finally
        {
            _allBotsQueued = false;
            _difficultySpawns.Clear();
        }
    }
}
