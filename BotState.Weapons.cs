using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
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
            bool switched = BotControllerBridge.SwitchBotWeapon(botController, bot.Slot, KnifeDefinitionIndex);
            bool locked = BotControllerBridge.LockKnife(botController, bot.Slot);
            if (switched)
                QueueInspectInjection(bot.Slot);
            if (locked)
                _knifeLockedBotSlots.Add(bot.Slot);

            if (!switched || !locked)
            {
                Console.WriteLine(
                    $"[Smarter-Bot] Knife action failed for slot {bot.Slot}: switch={switched}, lock={locked}");
            }
        }
    }

    // Queues a one-command inspect injection after the knife becomes active
    private void QueueInspectInjection(int slot)
    {
        Server.NextFrame(() =>
        {
            if (_botController == null) return;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid || !player.IsBot ||
                !player.PawnIsAlive || player.HasBeenControlledByPlayerThisRound)
                return;

            if (BotControllerBridge.InjectUsercmd(
                    _botController, slot, InspectButtonMask) <= 0)
            {
                Console.WriteLine(
                    $"[Smarter-Bot] Inspect injection failed for slot {slot}");
            }
        });
    }

    // Releases only Slot3 locks successfully applied by this plugin
    private void ReleaseKnifeLocks()
    {
        if (_botController != null)
        {
            foreach (int slot in _knifeLockedBotSlots)
            {
                if (BotControllerBridge.IsKnifeLocked(_botController, slot))
                    BotControllerBridge.UnlockWeapon(_botController, slot);
            }
        }

        _knifeLockedBotSlots.Clear();
    }
    //---------------------------------------------------------------------------------------
    // Cancels a reload when Valve reports a visible enemy and a usable firearm exists
    private void InterruptReload(
        CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, float now)
    {
        if (_botController == null || !bot.IsEnemyVisible ||
            !IsReloading(player))
            return;

        int slot = player.Slot;
        if (_reloadInterruptCooldown.TryGetValue(slot, out float cooldownEnd) &&
            now < cooldownEnd)
            return;

        if (!GetReloadInterruptWeapon(pawn, out int weaponDefIndex))
            return;

        if (!BotControllerBridge.SwitchBotWeapon(
                _botController, slot, KnifeDefinitionIndex))
            return;

        _reloadInterruptCooldown[slot] = now + ReloadInterruptCooldown;
        Server.NextFrame(() => SwitchBackAfterReloadInterrupt(
            slot, weaponDefIndex));
    }

    // Selects a loaded primary first, then a loaded secondary weapon
    private static bool GetReloadInterruptWeapon(
        CCSPlayerPawn pawn, out int weaponDefIndex)
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
            var weaponData = weapon.VData;
            if (weaponData == null) continue;

            int defIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
            if (defIndex <= 0) continue;

            if (weaponData.GearSlot == gear_slot_t.GEAR_SLOT_RIFLE &&
                weapon.Clip1 > 0)
            {
                weaponDefIndex = defIndex;
                return true;
            }

            if (secondaryDefIndex < 0 &&
                weaponData.GearSlot == gear_slot_t.GEAR_SLOT_PISTOL &&
                weapon.Clip1 > 0)
            {
                secondaryDefIndex = defIndex;
            }
        }

        weaponDefIndex = secondaryDefIndex;
        return weaponDefIndex >= 0;
    }

    // Returns a live Bot to the chosen firearm one frame after selecting its knife
    private void SwitchBackAfterReloadInterrupt(int slot, int weaponDefIndex)
    {
        if (_botController == null) return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null || !player.IsValid || !player.IsBot ||
            !player.PawnIsAlive || player.HasBeenControlledByPlayerThisRound)
            return;

        if (!BotControllerBridge.SwitchBotWeapon(
                _botController, slot, weaponDefIndex))
        {
            Console.WriteLine(
                $"[Smarter-Bot] Reload interrupt restore failed for slot {slot}");
        }
    }

    // Repeating scan: while the Bot still has living enemies, nudge it back
    // onto its gun exactly once.
    private void ReequipGunForActiveBots()
    {
        if (_botController == null)
            return;

        var players = Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .ToList();

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
            // No living enemies, skip
            if (enemiesAlive == 0) continue;

            var pawn = player.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.IsDefusing) continue;

            // Only act when the bot is idly holding a knife; the sole purpose is
            // to stop a bot roaming with its knife while enemies live.
            var active = pawn.WeaponServices?.ActiveWeapon?.Value;
            if (active == null || !active.IsValid) continue;

            var activeWeapon = new CCSWeaponBase(active.Handle);
            if (activeWeapon.VData?.GearSlot != gear_slot_t.GEAR_SLOT_KNIFE)
                continue;

            if (!GetReequipWeapon(pawn, out int targetDef)) continue;

            BotControllerBridge.SwitchBotWeapon(_botController, player.Slot, targetDef);
        }
    }

    // Picks a loaded primary if the Bot owns one, else a loaded secondary.
    // False when the Bot owns neither, or neither still has a round.
    private static bool GetReequipWeapon(CCSPlayerPawn pawn, out int weaponDefIndex)
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
            var weaponData = weapon.VData;
            if (weaponData == null) continue;

            int defIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
            if (defIndex <= 0 || !WeaponHasAmmo(weapon)) continue;

            if (weaponData.GearSlot == gear_slot_t.GEAR_SLOT_RIFLE)
            {
                weaponDefIndex = defIndex;
                return true;
            }

            if (secondaryDefIndex < 0 &&
                weaponData.GearSlot == gear_slot_t.GEAR_SLOT_PISTOL)
            {
                secondaryDefIndex = defIndex;
            }
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
    // Reports whether the Bot's active weapon is currently reloading
    private bool IsReloading(CCSPlayerController player)
    {
        if (player == null || !player.IsValid)
            return false;

        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        var activeWeapon = pawn.WeaponServices?.ActiveWeapon?.Value;
        if (activeWeapon == null || !activeWeapon.IsValid)
            return false;

        return Schema.GetRef<bool>(activeWeapon.Handle, "CCSWeaponBase", "m_bInReload");
    }

    // Clears weapons state owned by the completed round.
    private void ResetWeaponsRoundState()
    {
        _reloadInterruptCooldown.Clear();
    }
}
