using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

public partial class BotState
{
    private readonly Dictionary<int, float> _lastLateralDir = new();

    private readonly HashSet<int> _hasFiredThisAttack = new();
    private readonly Dictionary<int, bool> _prevIsAttacking = new();

    // Records sniper shots, applies counter-strafe, and rolls combat crouching.
    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var shooter = @event.Userid;
        if (shooter == null || !shooter.IsValid || !shooter.IsBot) return HookResult.Continue;

        int idx = (int)shooter.Index;
        var pawn = shooter.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return HookResult.Continue;

        var bot = pawn.Bot;
        if (bot == null) return HookResult.Continue;
        // Sniper Peek
        _hasFiredThisAttack.Add(idx);

        // Counter-strafe on fire
        bool cachedInAir = _cachedInAir.GetValueOrDefault(idx, false);
        bool cachedNearLadder = _cachedNearLadder.GetValueOrDefault(idx, false);
        if (!cachedInAir && !cachedNearLadder)
        {
            string? wpnFire = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
            if (wpnFire is not (null or "weapon_knife" or "weapon_taser"))
            {
                float vx = pawn.AbsVelocity.X;
                float vy = pawn.AbsVelocity.Y;
                float speed2D = MathF.Sqrt(vx * vx + vy * vy);

                if (wpnFire is "weapon_glock" or "weapon_hkp2000" or "weapon_p250"
                            or "weapon_fiveseven" or "weapon_cz75a" or "weapon_tec9"
                            or "weapon_mac10" or "weapon_mp9")
                {
                    if (speed2D > 70f)
                    {
                        float scale = 70f / speed2D;
                        pawn.AbsVelocity.X = vx * scale;
                        pawn.AbsVelocity.Y = vy * scale;
                    }
                }
                else if (wpnFire is "weapon_usp_silencer" or "weapon_deagle"
                                or "weapon_ssg08" or "weapon_awp"
                                or "weapon_scar20" or "weapon_g3sg1"
                                or "weapon_galilar" or "weapon_ak47" or "weapon_sg556"
                                or "weapon_famas" or "weapon_m4a1" or "weapon_m4a1_silencer"
                                or "weapon_aug" or "weapon_m249" or "weapon_negev")
                {
                    if (speed2D > 0f)
                    {
                        pawn.AbsVelocity.X = 0f;
                        pawn.AbsVelocity.Y = 0f;
                    }
                }
                // Other weapons: no speed change
            }
        }

        if (pawn.IsDefusing || !bot.IsAttacking) return HookResult.Continue;
        // Random combat crouch
        double crouchChance = 0.0;
        string? wpn = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
        if (wpn is not (null or "weapon_knife" or "weapon_taser"))
        {
            if (wpn is "weapon_glock" or "weapon_hkp2000" or "weapon_p250" or "weapon_fiveseven")
                crouchChance = 0.20;

            else if (wpn is "weapon_usp_silencer" or "weapon_deagle")
                crouchChance = 0.30;

            else if (wpn is "weapon_elite" or "weapon_tec9" or "weapon_cz75a" or "weapon_revolver"
                    or "weapon_scar20" or "weapon_g3sg1")
                crouchChance = 0.10;

            else if (wpn is "weapon_mac10" or "weapon_mp9" or "weapon_bizon")
                crouchChance = 0.03;

            else if (wpn is "weapon_mp5sd" or "weapon_ump45" or "weapon_p90"
                    or "weapon_nova" or "weapon_xm1014" or "weapon_sawedoff" or "weapon_mag7"
                    or "weapon_ssg08" or "weapon_awp")
                crouchChance = 0.05;

            else if (wpn is "weapon_galilar" or "weapon_ak47" or "weapon_sg556"
                    or "weapon_famas" or "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_aug"
                    or "weapon_m249")
                crouchChance = 0.50;

            else if (wpn == "weapon_negev")
                crouchChance = 0.90;
        }

        ref bool isCrouching = ref bot.IsCrouching;
        isCrouching = _random.NextDouble() < crouchChance;

        CountdownTimer sneakTimer = bot.SneakTimer;

        ref float sneakduration = ref sneakTimer.Duration;
        sneakduration = 0.0f;

        ref float sneaktimestamp = ref sneakTimer.Timestamp;
        sneaktimestamp = 0.0f;

        ref float sneaktimescale = ref sneakTimer.Timescale;
        sneaktimescale = 1.0f;

        return HookResult.Continue;
    }

    // Updates combat reactions and returns the attack snapshot used by movement.
    private bool UpdateBotCombat(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx)
    {
        // Sniper Peek
        bool curIsAttacking = bot.IsAttacking;

        if (curIsAttacking && _hasFiredThisAttack.Remove(idx))
        {
            string? wpn = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
            if (wpn == "weapon_awp" || wpn == "weapon_ssg08")
            {
                _lastLateralDir.TryGetValue(idx, out float lastDir);
                if (lastDir != 0f)
                {
                    float yawS = pawn.EyeAngles.Y * MathF.PI / 180f;
                    float rx = -MathF.Sin(yawS), ry = MathF.Cos(yawS);
                    float injX = rx * (-lastDir) * 250f;
                    float injY = ry * (-lastDir) * 250f;
                    pawn.AbsVelocity.X += injX;
                    pawn.AbsVelocity.Y += injY;

                    ResetLookAroundForBot(player);
                }
            }
        }
        // Avoid Confusion
        if (curIsAttacking)
        {
            ref bool eyeAnglesUnderPathFinderControl = ref bot.EyeAnglesUnderPathFinderControl;
            eyeAnglesUnderPathFinderControl = false;

            ref float inhibitLookAroundTimestamp = ref bot.InhibitLookAroundTimestamp;
            inhibitLookAroundTimestamp = 0f;
        }
        //Test Alert! Can cause crash when bot_debug 1 !
        ref bool isAimingAtEnemy = ref bot.IsAimingAtEnemy;
        if (isAimingAtEnemy && !curIsAttacking)
        {
            bot.IsAttacking = true;
        }
        // Cancel Crouch After Attack
        if (_prevIsAttacking.TryGetValue(idx, out bool prevAttack))
        {
            if (prevAttack == true && curIsAttacking == false)
            {
                ref bool isCrouching = ref bot.IsCrouching;
                isCrouching = false;
            }
        }
        _prevIsAttacking[idx] = curIsAttacking;

        if (!curIsAttacking)
        {
            _hasFiredThisAttack.Remove(idx);
            float yawL2 = pawn.EyeAngles.Y * MathF.PI / 180f;
            float latX = -MathF.Sin(yawL2), latY = MathF.Cos(yawL2);
            float latSpd = pawn.AbsVelocity.X * latX + pawn.AbsVelocity.Y * latY;
            if (MathF.Abs(latSpd) > 10f)
            {
                float newDir = latSpd > 0f ? 1f : -1f;
                float prevDir = _lastLateralDir.GetValueOrDefault(idx);
                _lastLateralDir[idx] = newDir;
            }
        }

        return curIsAttacking;
    }

    // Clears combat state owned by the completed round.
    private void ResetCombatRoundState()
    {
        _lastLateralDir.Clear();
        _hasFiredThisAttack.Clear();
        _prevIsAttacking.Clear();
    }
}
