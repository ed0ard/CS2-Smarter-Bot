using CounterStrikeSharp.API.Core.Capabilities;
using System.Runtime.CompilerServices;

namespace BotState;

public partial class BotState
{
    private long _retryBotControllerAt;

    private object? GetBotController()
    {
        long now = Environment.TickCount64;
        if (now < _retryBotControllerAt) return null;

        try
        {
            if (BotControllerBridge.Get() is { } api) return api;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or FileNotFoundException or FileLoadException
            or TypeLoadException or TypeInitializationException) { }

        _retryBotControllerAt = now + 1000;
        return null;
    }

    // The bridge isolates the optional BC API.
    private static class BotControllerBridge
    {
        private static readonly PluginCapability<BotControllerApi.IBotControllerApi> Capability =
            new("botcontroller:api");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsViewControlled(object api, int slot)
        {
            var controller = (BotControllerApi.IBotControllerApi)api;
            return controller.IsLocked(slot, BotControllerApi.LockKind.All)
                || controller.IsLocked(slot, BotControllerApi.LockKind.Aim)
                || controller.IsReplaying(slot);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object? Get() => Capability.Get();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool SwitchBotWeapon(object api, int slot, int defIndex)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .SwitchBotWeapon(slot, defIndex);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long InjectUsercmd(
            object api, int slot, ulong buttonMask, int durationMs = 0)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .InjectUsercmd(slot, buttonMask, durationMs);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long StartUsercmdSuppression(
            object api, int slot, ulong buttonMask)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .StartUsercmdSuppression(slot, buttonMask);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CancelUsercmdSuppression(
            object api, int slot, long suppressionId)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .CancelUsercmdSuppression(slot, suppressionId);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool LockKnife(object api, int slot)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .Lock(slot, BotControllerApi.LockTarget.Slot3);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsKnifeActive(object api, int slot) =>
            ((BotControllerApi.IBotControllerApi)api).BotActiveWeaponDef(slot) == KnifeDefinitionIndex;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReleaseKnifeLock(object api, int slot)
        {
            var controller = (BotControllerApi.IBotControllerApi)api;
            if (controller.GetWeaponLock(slot) == BotControllerApi.LockTarget.Slot3)
                controller.Unlock(slot, BotControllerApi.LockKind.Weapon);
        }
    }
}
