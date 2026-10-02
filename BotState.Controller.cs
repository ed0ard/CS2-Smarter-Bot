using CounterStrikeSharp.API.Core.Capabilities;
using System.Runtime.CompilerServices;

namespace BotState;

public partial class BotState
{
    private object? _botController;

    // Isolates optional BotControllerApi types from the main plugin type
    private static class BotControllerBridge
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsViewControlled(object api, int slot)
        {
            var controller = (BotControllerApi.IBotControllerApi)api;
            return controller.IsLocked(slot, BotControllerApi.LockKind.All)
                || controller.IsLocked(slot, BotControllerApi.LockKind.Aim)
                || controller.IsReplaying(slot);
        }

        // Resolves the optional BotController capability at runtime
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object? TryGet()
        {
            var capability =
                new PluginCapability<BotControllerApi.IBotControllerApi>(
                    "botcontroller:api");
            return capability.Get();
        }

        // Switches one Bot to its knife definition
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool SwitchBotWeapon(object api, int slot, int defIndex)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .SwitchBotWeapon(slot, defIndex);
        }

        // Creates an independently cancellable usercmd injection on a Bot
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long InjectUsercmd(
            object api, int slot, ulong buttonMask, int durationMs = 0)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .InjectUsercmd(slot, buttonMask, durationMs);
        }

        // Starts a cancellable persistent usercmd button suppression
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long StartUsercmdSuppression(
            object api, int slot, ulong buttonMask)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .StartUsercmdSuppression(slot, buttonMask);
        }

        // Cancels one persistent usercmd suppression by its token
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CancelUsercmdSuppression(
            object api, int slot, long suppressionId)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .CancelUsercmdSuppression(slot, suppressionId);
        }

        // Applies the knife-slot weapon lock to one Bot
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool LockKnife(object api, int slot)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .Lock(slot, BotControllerApi.LockTarget.Slot3);
        }

        // Checks whether one Bot still has the knife-slot lock
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsKnifeLocked(object api, int slot)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .GetWeaponLock(slot) == BotControllerApi.LockTarget.Slot3;
        }

        // Releases the weapon lock from one Bot
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool UnlockWeapon(object api, int slot)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .Unlock(slot, BotControllerApi.LockKind.Weapon);
        }
    }
}
