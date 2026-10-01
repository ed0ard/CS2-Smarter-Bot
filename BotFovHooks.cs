using System.Numerics;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace BotState;

internal sealed class BotFovHooks : IDisposable
{
    private readonly MemoryFunctionWithReturn<nint, nint, byte, nint, byte> player, position;
    private readonly Func<nint, BotViewFrustum?> getView;
    private readonly Action<Exception> fault;
    private readonly BotFovQueryContext context = new();
    private bool playerHooked, positionHooked;
    private readonly int thread = Environment.CurrentManagedThreadId;
    internal bool Failed { get; private set; }
    internal long PointsTested { get; private set; }
    internal long PointsRejected { get; private set; }

    internal BotFovHooks(Func<nint, BotViewFrustum?> getView, Action<Exception> fault)
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Custom FOV requires Windows or Linux x64.");
        this.getView = getView; this.fault = fault;
        // CSS caches signature bindings across Unhook/reload, so reuse the same
        // signatures rather than scanning already-detoured prologues ourselves.
        player = new(OperatingSystem.IsWindows() ? BotFovSignatures.WindowsPlayer : BotFovSignatures.LinuxPlayer);
        position = new(OperatingSystem.IsWindows() ? BotFovSignatures.WindowsPosition : BotFovSignatures.LinuxPosition);
        if (player.Handle == 0 || position.Handle == 0)
            throw new InvalidOperationException("Custom FOV visibility signatures unavailable; native vision remains active.");
        try
        {
            position.Hook(OnPosition, HookMode.Pre); positionHooked = true;
            player.Hook(OnPlayer, HookMode.Pre); playerHooked = true;
        }
        catch { Dispose(); throw; }
    }

    private bool Ready => !Failed && playerHooked && positionHooked && Environment.CurrentManagedThreadId == thread;

    private HookResult OnPlayer(DynamicHook hook)
    {
        if (!Ready || hook.GetParam<byte>(2) == 0) return HookResult.Continue;
        bool owned = false;
        try
        {
            nint bot = hook.GetParam<nint>(0);
            if (getView(bot) is not { } view) return HookResult.Continue;
            owned = true;
            using var scope = context.Enter(bot, view);
            // Retain native target validation, distance limits, sample order and
            // output part mask. Only the old center/point FOV test is replaced.
            nint parts = hook.GetParam<nint>(3);
            byte result = player.Invoke(bot, hook.GetParam<nint>(1), 0, parts, bypasshook: true);
            // A nested point failure disables future hooks, but this in-flight
            // player query already bypassed native FOV and must fail closed.
            if (Failed) { result = 0; if (parts != 0) Marshal.WriteByte(parts, 0); }
            hook.SetReturn(result);
            return HookResult.Handled;
        }
        catch (Exception ex) { return Fail(hook, ex, owned); }
    }

    private HookResult OnPosition(DynamicHook hook)
    {
        if (!Ready) return HookResult.Continue;
        bool owned = false;
        try
        {
            nint bot = hook.GetParam<nint>(0);
            if (!context.TryGet(bot, out var view))
            {
                // Native callers deliberately using testFov=false (including
                // sound investigation/objective LOS) retain that contract.
                if (hook.GetParam<byte>(2) == 0 || getView(bot) is not { } direct) return HookResult.Continue;
                view = direct;
            }
            owned = true;
            nint point = hook.GetParam<nint>(1);
            PointsTested++;
            if (point == 0 || !view.Contains(Marshal.PtrToStructure<Vector3>(point)))
            {
                PointsRejected++;
                hook.SetReturn((byte)0);
                return HookResult.Handled;
            }
            // Inside the frustum is not proof of visibility. The engine still
            // checks blindness, smoke and occlusion and supplies the result.
            byte result = position.Invoke(bot, point, 0, hook.GetParam<nint>(3), bypasshook: true);
            hook.SetReturn(result);
            return HookResult.Handled;
        }
        catch (Exception ex) { return Fail(hook, ex, owned); }
    }

    private HookResult Fail(DynamicHook hook, Exception ex, bool owned)
    {
        Failed = true; fault(ex);
        // Never retry a query or let its nested no-FOV call bypass both gates.
        if (!owned) return HookResult.Continue;
        hook.SetReturn((byte)0);
        return HookResult.Handled;
    }

    public void Dispose()
    {
        try { if (playerHooked) { player.Unhook(OnPlayer, HookMode.Pre); playerHooked = false; } }
        finally { if (positionHooked) { position.Unhook(OnPosition, HookMode.Pre); positionHooked = false; } }
    }
}
