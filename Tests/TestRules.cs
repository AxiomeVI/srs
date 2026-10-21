using System;
using System.Linq;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// a rule of the real table by srs's (scope, name): srs itself never looks one
// up by name
internal static class TestRules {
    public static SegmentRule Find(string scope, string name) =>
        SegmentRules.All.FirstOrDefault(rule => rule.Scope == scope && rule.Name == name)
        ?? throw new Exception($"no rule for {scope}/{name}");
}
