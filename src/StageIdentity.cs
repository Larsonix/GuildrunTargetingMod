using System;
using Il2CppEmber.Scopes.GameRun.RunSession.Data;
using Il2CppInterop.Runtime.InteropTypes;
using Il2Cppgg.leyline.core.Mvcs.Model;
using Il2Cppgg.leyline.balancing.Data;

namespace GuildrunTargetingMod;

internal static class StageIdentity
{
    public static string Read()
    {
        try
        {
            if (!DataReaders.TryGet<RunSessionDataReader>(out var run) || run == null) return "unavailable";
            // GetCurrentEncounter hands back the encounter object, but the id lives on the
            // balancing-entry interface rather than on the encounter type itself.
            var encounter = run.GetCurrentEncounter();
            if (encounter == null) return "unavailable";
            IBalancingEntry entry = encounter.TryCast<IBalancingEntry>();
            return entry == null ? "unavailable" : entry.Id.ToString();
        }
        catch (Exception e) { return "unavailable:" + e.GetType().Name; }
    }
}
