using System.Runtime.CompilerServices;
using Xunit;

// Runtime tests create GameRoot instances, and GameRoot.Instance is a process
// singleton. Running them in parallel makes MarkDirty target the wrong root.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class TestBootstrap
{
    // Networking tests perform BSON round-trips. Initialize the class-map
    // conventions before any test can auto-register a map with transient
    // members still attached.
    [ModuleInitializer]
    internal static void InitializeBson() => V12.Core.Networking.BsonConfig.Initialize();
}
