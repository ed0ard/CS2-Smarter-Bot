using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

public partial class BotState
{
    private const string RushAntennaMarkerClass = "info_target";
    private const float RushAntennaPublishInterval = 0.1f;
    private const float RushRoundTime = 0.75f;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _rushAntennaTimer = null;

    private enum RushAntennaStatus
    {
        Read,
        Unavailable
    }

    [ConsoleCommand("css_rush_antenna", "Show the current Rush antenna owner (read-only).")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnRushAntennaCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (command.ArgCount != 1)
        {
            command.ReplyToCommand("[Smarter-Bot] Usage: css_rush_antenna");
            return;
        }

        if (TryReadRushAntenna(out _, out _, out string message) == RushAntennaStatus.Read)
            command.ReplyToCommand($"[Smarter-Bot] Rush antenna: {message}.");
        else
            command.ReplyToCommand($"[Smarter-Bot] Rush antenna: unavailable ({message}).");
    }

    // owner is a CS team number: 0 = neutral, 2 = T, 3 = CT.
    private static RushAntennaStatus TryReadRushAntenna(out int owner, out Vector? origin, out string message)
    {
        owner = 0;
        origin = null;

        if (!string.Equals(Server.MapName, "rush_001", StringComparison.OrdinalIgnoreCase))
        {
            message = "requires rush_001";
            return RushAntennaStatus.Unavailable;
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
                message = "multiple antenna markers";
                return RushAntennaStatus.Unavailable;
            }
            antenna ??= prefixedAntenna;
            if (antenna?.IsValid != true)
            {
                message = "antenna marker not found";
                return RushAntennaStatus.Unavailable;
            }

            // rush_001.vjs publishes UIUpdateRoomControl through CSRadarPoint.SetColor
            uint color = Schema.GetSchemaValue<uint>(antenna.Handle, "CCSRadarElement", "m_nElementColor");
            switch (color)
            {
                case 1: owner = 0; message = "neutral (team=0)"; break;
                case 3: owner = 3; message = "CT (team=3)"; break;
                case 4: owner = 2; message = "T (team=2)"; break;
                default:
                    message = $"unknown (radar color={color})";
                    return RushAntennaStatus.Read;
            }

            if (antenna.AbsOrigin is { } pos)
                origin = new Vector(pos.X, pos.Y, pos.Z);
            return RushAntennaStatus.Read;
        }
        catch (Exception)
        {
            message = "could not read map state";
            return RushAntennaStatus.Unavailable;
        }
    }

    // Behavior trees cannot run console commands, so the owner is published as an
    // info_target whose m_iTeamNum is the owning team.
    private static void PublishRushAntennaOwner()
    {
        if (!string.Equals(Server.MapName, "rush_001", StringComparison.OrdinalIgnoreCase))
            return;

        LockRushRoundTime();

        bool owned = TryReadRushAntenna(out int owner, out Vector? origin, out _) == RushAntennaStatus.Read
            && (owner == 2 || owner == 3);

        try
        {
            if (!owned)
            {
                RemoveRushAntennaMarkers();
                return;
            }

            // Map info_target entities stay on team 0, so any non-zero one is ours
            CBaseEntity? marker = null;
            foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(RushAntennaMarkerClass))
            {
                if (!entity.IsValid || entity.TeamNum == 0) continue;
                if (marker == null)
                    marker = entity;
                else
                    entity.Remove();
            }

            if (marker == null)
            {
                marker = Utilities.CreateEntityByName<CBaseEntity>(RushAntennaMarkerClass);
                if (marker == null) return;
                marker.DispatchSpawn();
                if (origin != null)
                    marker.Teleport(origin);
            }

            if (marker.TeamNum != owner)
                marker.TeamNum = (byte)owner;
        }
        catch (Exception)
        {
        }
    }

    private static void LockRushRoundTime()
    {
        try
        {
            var roundTime = ConVar.Find("mp_roundtime");
            if (roundTime == null) return;
            if (Math.Abs(roundTime.GetPrimitiveValue<float>() - RushRoundTime) > 0.0001f)
                roundTime.SetValue(RushRoundTime);
        }
        catch (Exception)
        {
        }
    }

    private static void RemoveRushAntennaMarkers()
    {
        foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(RushAntennaMarkerClass))
        {
            if (entity.IsValid && entity.TeamNum != 0)
                entity.Remove();
        }
    }

    private void StartRushAntennaPublisher()
    {
        _rushAntennaTimer = AddTimer(
            RushAntennaPublishInterval,
            PublishRushAntennaOwner,
            CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
    }

    private void StopRushAntennaPublisher()
    {
        _rushAntennaTimer?.Kill();
        _rushAntennaTimer = null;
        if (!string.Equals(Server.MapName, "rush_001", StringComparison.OrdinalIgnoreCase))
            return;
        try { RemoveRushAntennaMarkers(); } catch (Exception) { }
    }
}
