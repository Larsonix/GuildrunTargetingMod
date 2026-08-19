using System;
using Il2CppInterop.Runtime.Injection;
using Il2CppEmber.Balancing.SimulationBridge.Context;
using StringMap = Il2CppSystem.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace GuildrunTargetingMod.Interop;

// The simulation wants somewhere to report errors to. The live reporter is the game's crash
// reporting, which the mod must not feed : a prediction is not a real fight, and its noise would
// land in the developers' inbox as if it were. This one accepts every call and does nothing.
internal sealed class NoOpErrorReporter : Il2CppSystem.Object
{
    private static bool _registered;

    public NoOpErrorReporter(IntPtr pointer) : base(pointer) { }
    public NoOpErrorReporter() : base(ClassInjector.DerivedConstructorPointer<NoOpErrorReporter>())
        => ClassInjector.DerivedConstructorBody(this);

    public static NoOpErrorReporter CreateRegistered()
    {
        if (!_registered)
        {
            // Registering the type builds it a native interface table, which is what lets the
            // simulation call into a class that only exists on the managed side. Every member of
            // the interface has to be present or the call table is incomplete.
            ClassInjector.RegisterTypeInIl2Cpp<NoOpErrorReporter>(new RegisterTypeOptions
            {
                Interfaces = new[] { typeof(IErrorReporting) }
            });
            _registered = true;
        }
        return new NoOpErrorReporter();
    }

    public void SetEffectTags(string effectExecutionType, string effectOrigin) { }
    public void ClearEffectTags() { }
    public void OnStageStarted(string stage) { }
    public void OnScopeEntered(string gameArea) { }
    public void OnBattleFlowChanged(string battleFlow) { }
    public void SetTag(string key, string value) { }
    public void SetContext(string key, Il2CppSystem.Object value) { }
    public void CaptureException(Il2CppSystem.Exception ex) { }
    public void CaptureException(Il2CppSystem.Exception ex, string tagKey, string tagValue) { }
    public void CaptureMessage(string message) { }
    public void CaptureMessage(string message, string tagKey, string tagValue) { }
    public void CaptureMessage(string message, StringMap tags) { }
    public void AddBreadcrumb(string message, string category) { }
}
