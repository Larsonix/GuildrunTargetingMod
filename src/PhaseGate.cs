using System;
using Il2CppEmber.Scopes.Battle.BattleSimulation.Data;
using Il2CppEmber.Scopes.GameRun.RunSession.Data;
using Il2Cppgg.leyline.core.Mvcs.Model;

namespace GuildrunTargetingMod;

internal enum ModPhase
{
    Dormant,
    Placement,
    Resolution,
    Other
}

internal sealed class PhaseGate
{
    public ModPhase Current { get; private set; } = ModPhase.Dormant;
    public event Action<ModPhase, ModPhase> Transitioned;

    public void Poll()
    {
        ModPhase next = ReadPhase();
        if (next == Current) return;
        ModPhase previous = Current;
        Current = next;
        Transitioned?.Invoke(previous, next);
    }

    private static ModPhase ReadPhase()
    {
        try
        {
            // Polled, not subscribed : handing a managed delegate to the game's reactive property
            // is one more interop lifetime to get right, and LateUpdate already reads the state
            // after the game's own tick.
            if (!DataReaders.Has<BattleSimulationDataReader>() ||
                !DataReaders.TryGet<RunSessionDataReader>(out var run) || run == null)
                return ModPhase.Dormant;

            return run.BattleFlowState.CurrentValue switch
            {
                BattleFlowState.Placement => ModPhase.Placement,
                BattleFlowState.Resolution => ModPhase.Resolution,
                _ => ModPhase.Other
            };
        }
        catch
        {
            // Reader teardown races scope changes, and dormant is the safe answer.
            return ModPhase.Dormant;
        }
    }
}
