using System;
using MelonLoader;
using UnityEngine;

namespace GuildrunTargetingMod;

// The target frame time is a property of the machine and its current game settings, not of the
// simulation that first needed it. Keeping the answer here gives every instrument the same scale
// and, just as importantly, leaves only one copy of the fallback rules to drift.
internal static class MachineProfile
{
    private static bool _resolved;
    private static double _targetFrameMs;
    private static bool _capped;
    private static Func<bool> _devLog;

    /// <summary>
    /// Whether a frame rate limit is actually in force, as opposed to assumed.
    /// </summary>
    /// <remarks>
    /// This is deliberately a separate fact from the target frame time, because the target has a
    /// 60 fps fallback for the uncapped case and the two must never be confused. The draw-cost
    /// probe needs to know whether a cap is real: under a cap that the machine is comfortably
    /// meeting, a drawn frame and a hidden frame both finish inside the same interval and both
    /// measure it, so their difference is zero no matter what drawing costs. Inferring the cap
    /// from the measurements themselves would call an uncapped machine that happens to sit near
    /// 60 fps "capped", so the cap is read from the settings that create it instead.
    /// </remarks>
    internal static bool FrameRateCapped
    {
        get
        {
            _ = TargetFrameMs; // resolves both, and only once.
            return _capped;
        }
    }

    internal static double TargetFrameMs
    {
        get
        {
            if (_resolved) return _targetFrameMs;
            double targetFps = 60.0;
            bool capped = false;
            try
            {
                if (Application.targetFrameRate > 0)
                {
                    targetFps = Application.targetFrameRate;
                    capped = true;
                }
                else if (QualitySettings.vSyncCount > 0)
                {
                    targetFps = Screen.currentResolution.refreshRate / (double)QualitySettings.vSyncCount;
                    capped = true;
                }
                if (targetFps <= 0.0) { targetFps = 60.0; capped = false; }
            }
            catch
            {
                targetFps = 60.0;
                capped = false;
            }
            _targetFrameMs = 1000.0 / targetFps;
            _capped = capped;
            _resolved = true;
            try
            {
                if (_devLog?.Invoke() == true)
                    MelonLogger.Msg($"[TargetingMod] shadow target frame rate resolved to {targetFps:0.##} fps");
            }
            catch
            {
                // Reporting the resolved rate is diagnostic. It must not make the shared machine
                // profile capable of throwing into either the simulation or the profiler.
            }
            return _targetFrameMs;
        }
    }

    internal static void SetDevLog(Func<bool> devLog) => _devLog = devLog;

    internal static void Reset() => _resolved = false;
}
