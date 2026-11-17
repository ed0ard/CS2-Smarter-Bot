using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

public partial class BotState
{
    private const int KnifeDefinitionIndex = 9001;
    private const float ReloadInterruptCooldown = 0.75f;
    private const ulong InspectButtonMask = (ulong)PlayerButtons.Inspect;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _gunReequipTimer = null;
    private readonly Dictionary<int, float> _reloadInterruptCooldown = new();

    private readonly HashSet<int> _knifeLockedBotSlots = new();
    private bool _eliminationHandled;

    // Rolls a 10% inspect for the Bot credited with a kill
    private void MaybeInspectOnKill(CCSPlayerController? attacker)
    {
        if (attacker == null || !attacker.IsValid || !attacker.IsBot ||
            !attacker.PawnIsAlive || attacker.HasBeenControlledByPlayerThisRound)
            return;

        if (_random.NextDouble() >= 0.10)
            return;

        QueueInspectInjection(attacker.Slot);
    }

    private void LockBotsToKnife(object botController, List<CCSPlayerController> bots)
    {
        foreach (var bot in bots)
        {
            int slot = bot.Slot;
            if (!BotControllerBridge.LockKnife(botController, slot))
            {
                Console.WriteLine($"[Smarter-Bot] Knife lock failed for slot {slot}");
                continue;
            }

            _knifeLockedBotSlots.Add(slot);
            QueueInspectInjection(slot, requireKnife: true);
        }
    }

    private void QueueInspectInjection(int slot, bool requireKnife = false)
    {
        Server.NextFrame(() =>
        {
            if (GetBotController() is not { } botController) return;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid || !player.IsBot ||
                !player.PawnIsAlive || player.HasBeenControlledByPlayerThisRound)
                return;

            if (requireKnife && !BotControllerBridge.IsKnifeActive(botController, slot)) return;

            if (BotControllerBridge.InjectUsercmd(botController, slot, InspectButtonMask) <= 0)
            {
                Console.WriteLine(
                    $"[Smarter-Bot] Inspect injection failed for slot {slot}");
            }
        });
    }

    private void ReleaseKnifeLocks()
    {
        if (_knifeLockedBotSlots.Count > 0 && GetBotController() is { } botController)
        {
            foreach (int slot in _knifeLockedBotSlots)
                BotControllerBridge.ReleaseKnifeLock(botController, slot);
        }

        _knifeLockedBotSlots.Clear();
    }

    private void InterruptReload(
        object botController, int slot, CCSPlayerPawn pawn, CCSBot bot, float now)
    {
        if (!bot.IsEnemyVisible) return;

        if (_reloadInterruptCooldown.TryGetValue(slot, out float cooldownEnd) &&
            now < cooldownEnd)
            return;

        var active = pawn.WeaponServices?.ActiveWeapon?.Value;
        if (active is not { IsValid: true } || !new CCSWeaponBase(active.Handle).InReload)
            return;

        // Switching to the knife cancels the reload.
        if (!TrySelectFirearm(pawn, loadedOnly: true, out int weaponDefIndex) ||
            !BotControllerBridge.SwitchBotWeapon(botController, slot, KnifeDefinitionIndex))
            return;

        _reloadInterruptCooldown[slot] = now + ReloadInterruptCooldown;
        Server.NextFrame(() => SwitchBackAfterReloadInterrupt(
            slot, weaponDefIndex));
    }

    private void SwitchBackAfterReloadInterrupt(int slot, int weaponDefIndex)
    {
        if (GetBotController() is not { } botController) return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null || !player.IsValid || !player.IsBot ||
            !player.PawnIsAlive || player.HasBeenControlledByPlayerThisRound)
            return;

        if (!BotControllerBridge.SwitchBotWeapon(botController, slot, weaponDefIndex))
        {
            Console.WriteLine(
                $"[Smarter-Bot] Reload interrupt restore failed for slot {slot}");
        }
    }

    private void ReequipGunForActiveBots()
    {
        if (GetBotController() is not { } botController)
            return;

        var players = GetConnectedPlayers().ToList();

        int aliveT = 0, aliveCT = 0;
        foreach (var p in players)
        {
            if (!p.IsValid || !p.PawnIsAlive) continue;
            int t = (int)p.TeamNum;
            if (t == (int)CsTeam.Terrorist) aliveT++;
            else if (t == (int)CsTeam.CounterTerrorist) aliveCT++;
        }
        if (aliveT == 0 && aliveCT == 0) return;

        foreach (var player in players)
        {
            if (!player.IsValid || !player.IsBot ||
                !player.PawnIsAlive || player.HasBeenControlledByPlayerThisRound)
                continue;

            int team = (int)player.TeamNum;
            int enemiesAlive = team == (int)CsTeam.Terrorist ? aliveCT
                             : team == (int)CsTeam.CounterTerrorist ? aliveT
                             : 0;
            if (enemiesAlive == 0) continue;

            var pawn = player.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.IsDefusing) continue;

            var active = pawn.WeaponServices?.ActiveWeapon?.Value;
            if (active == null || !active.IsValid) continue;

            var activeWeapon = new CCSWeaponBase(active.Handle);
            if (activeWeapon.VData?.GearSlot != gear_slot_t.GEAR_SLOT_KNIFE)
                continue;

            if (!TrySelectFirearm(pawn, loadedOnly: false, out int targetDef)) continue;

            BotControllerBridge.SwitchBotWeapon(botController, player.Slot, targetDef);
        }
    }

    private static bool TrySelectFirearm(CCSPlayerPawn pawn, bool loadedOnly, out int weaponDefIndex)
    {
        weaponDefIndex = -1;
        int secondaryDefIndex = -1;
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null) return false;

        foreach (var weaponHandle in weapons)
        {
            var baseWeapon = weaponHandle.Value;
            if (baseWeapon == null || !baseWeapon.IsValid) continue;

            var weapon = new CCSWeaponBase(baseWeapon.Handle);
            var gearSlot = weapon.VData?.GearSlot;
            if (gearSlot is not (gear_slot_t.GEAR_SLOT_RIFLE or gear_slot_t.GEAR_SLOT_PISTOL)) continue;

            int defIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
            if (defIndex <= 0 || (loadedOnly ? weapon.Clip1 <= 0 : !WeaponHasAmmo(weapon))) continue;

            if (gearSlot == gear_slot_t.GEAR_SLOT_RIFLE)
            {
                weaponDefIndex = defIndex;
                return true;
            }

            if (secondaryDefIndex < 0)
                secondaryDefIndex = defIndex;
        }

        weaponDefIndex = secondaryDefIndex;
        return weaponDefIndex >= 0;
    }

    // A gun still has a round in either clip or in reserve.
    private static bool WeaponHasAmmo(CCSWeaponBase weapon)
    {
        if (weapon.Clip1 > 0 || weapon.Clip2 > 0)
            return true;

        var reserve = weapon.ReserveAmmo;
        return reserve.Length > 0 && reserve[0] > 0;
    }
    //---------------------------------------------------------------------------------------
    // Clears weapons state owned by the completed round.
    private void ResetWeaponsRoundState()
    {
        _reloadInterruptCooldown.Clear();
    }
}
