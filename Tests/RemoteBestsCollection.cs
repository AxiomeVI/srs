using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// RemoteBests and SheetReader are static: the classes touching them run one at a time
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RemoteBestsCollection {
    public const string Name = "RemoteBests";
}
