using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;

namespace BotState;

public partial class BotState
{
    [ConsoleCommand("css_rush_antenna", "Show the current Rush antenna owner and element color (read-only).")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnRushAntennaCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (command.ArgCount != 1)
        {
            command.ReplyToCommand("[Smarter-Bot] Usage: css_rush_antenna");
            return;
        }

        if (!string.Equals(Server.MapName, "rush_001", StringComparison.OrdinalIgnoreCase))
        {
            command.ReplyToCommand("[Smarter-Bot] Rush antenna: unavailable (requires rush_001).");
            return;
        }

        try
        {
            const string antennaName = "ant.base.radar";
            CBaseEntity? antenna = null;
            CBaseEntity? prefixedAntenna = null;
            int prefixedMatches = 0;
            foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("radar_element"))
            {
                if (!entity.IsValid) continue;
                string? name = entity.Entity?.Name;
                if (name == antennaName)
                {
                    antenna = entity;
                    break;
                }
                // Match the map script's FindPrefabEntity: exact name first,
                // then *ant.base.radar. Do not guess between multiple prefabs.
                if (name?.EndsWith(antennaName, StringComparison.Ordinal) == true)
                {
                    prefixedAntenna = entity;
                    prefixedMatches++;
                }
            }

            if (antenna == null && prefixedMatches > 1)
            {
                command.ReplyToCommand("[Smarter-Bot] Rush antenna: unavailable (multiple antenna markers).");
                return;
            }
            antenna ??= prefixedAntenna;
            if (antenna?.IsValid != true)
            {
                command.ReplyToCommand("[Smarter-Bot] Rush antenna: unavailable (antenna marker not found).");
                return;
            }

            // rush_001.vjs publishes UIUpdateRoomControl through CSRadarPoint.SetColor,
            // including warmup. These are CSRadarColor values, NOT CS team numbers.
            uint color = Schema.GetSchemaValue<uint>(antenna.Handle, "CCSRadarElement", "m_nElementColor");
            string owner = color switch
            {
                1 => "neutral (team=0)",
                3 => "CT (team=3)",
                4 => "T (team=2)",
                _ => "unknown"
            };
            command.ReplyToCommand($"[Smarter-Bot] Rush antenna: {owner}; elementColor={color}.");
        }
        catch (Exception)
        {
            command.ReplyToCommand("[Smarter-Bot] Rush antenna: unavailable (could not read map state).");
        }
    }
}
