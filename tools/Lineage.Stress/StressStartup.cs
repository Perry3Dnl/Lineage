using System.Runtime.CompilerServices;
using Lineage;

namespace Lineage.Stress;

internal static class StressStartup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // The original feasibility harness is intentionally the RAM/GC baseline. Cold
        // storage has its own stress executable so regressions in either tier stay visible.
        LineageSettings.ColdStorageEnabledByDefault = false;
    }
}
