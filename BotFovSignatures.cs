namespace BotState;

internal static class BotFovSignatures
{
    // Entry signatures include the profiling scope and argument saves, not
    // just a common prologue. Relocations and stack displacements are masked.
    // Both overloads: bool(this, target, bool testFov, output/ignoreEntity).
    // Windows uses RCX/RDX/R8B/R9; Linux uses RDI/RSI/DL/RCX; result is AL.
    // Windows: unique in all 15 supplied May-Sept builds and the Oct 1 server.
    // Mask the four-byte testFov save as well: BotAI/fake-defuse can patch it
    // to zero. The surrounding scope setup and target saves still identify it.
    internal const string WindowsPlayer =
        "48 89 5C 24 ? 44 88 44 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ? 48 81 EC ? ? ? ? 48 8D 05 ? ? ? ? 48 C7 45 ? ? ? ? ? 48 89 45 ? ? ? ? ? 0F 10 45 ? 48 8D 05 ? ? ? ? 4C 8B F2";
    internal const string WindowsPosition =
        "48 89 5C 24 ? 48 89 74 24 ? 48 89 7C 24 ? 55 41 54 41 55 41 56 41 57 48 8D AC 24 ? ? ? ? 48 81 EC ? ? ? ? 48 8D 05 ? ? ? ? 48 C7 45 ? ? ? ? ? 48 89 45 ? ? ? ? ? 0F 10 45 ? 48 8D 05 ? ? ? ? 4C 8B E2";

    // Supplied libserver.so SHA256 d81faffb3e3a5f2001932b3b55a96c4ac05c2ed4b99702b06fc416b6e9bb5300.
    // Player at 0xBF81C0 calls Position at 0xBF6750 for each body sample,
    // forwarding testFov; its fourth argument is a one-byte visible-part mask.
    internal const string LinuxPlayer =
        "55 48 8D 05 ? ? ? ? 48 89 E5 41 57 4C 8D 7D ? 41 56 41 89 D6 41 55 4C 89 FA 41 54 49 89 FC 53 48 8D 3D ? ? ? ? 48 89 F3 48 8D 35 ? ? ? ? 48 83 EC ? 48 89 45 ? 48 8D 05 ? ? ? ? 48 89 4D ?";
    internal const string LinuxPosition =
        "55 48 8D 05 ? ? ? ? 48 89 E5 41 57 4C 8D BD ? ? ? ? 41 56 41 55 41 54 49 89 CC 53 48 89 FB 48 8D 3D ? ? ? ? 48 81 EC ? ? ? ? 48 89 B5 ? ? ? ? 89 95 ? ? ? ? 4C 89 FA";
}
