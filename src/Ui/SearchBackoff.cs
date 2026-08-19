using System;
using MelonLoader;
using UnityEngine;

namespace GuildrunTargetingMod.Ui;

// Bounds a scene search, because the search behind it walks every loaded object in the game and
// costs 8 to 20 ms a call. Three of these ran every frame forever whenever what they wanted was
// absent, which is a quarter to all of a frame permanently, and is the reported "cannot play".
//
// The first attempt is always immediate, so nothing is ever delayed before it has failed once.
// After that the schedule is three flat tiers rather than a doubling curve, and the reason is the
// arithmetic of the thing being bounded:
//
//   A "let it run free for the first half second" window sounds harmless and is not. At 15 ms a
//   sweep and 60 frames a second, half a second of unbounded retrying is about 450 ms of processor
//   time spent inside 500 ms of wall clock. The stall it is supposed to prevent is the stall it
//   would cause. So there is no free window; the fast tier is a real interval from the first miss.
//
//   0.1 s is fast enough to be invisible. The object appears within a tenth of a second of when it
//   would have been noticed before, and the mod already waits three quarters of a second of warmup
//   before it does anything with it. What it buys is a sixfold cut in the cost of the failing case
//   at 60 fps, and far more above that.
//
// The tiers are deliberately in SECONDS. A frame count is a different duration on every machine,
// which is the defect this whole cycle keeps finding, including twice in its own new code.
internal sealed class SearchBackoff
{
    // Missing for less than this: try every FastIntervalSeconds. Still plausibly ordinary loading.
    private const float SlowAfterSeconds = 3f;
    // Missing for less than this: try every SlowIntervalSeconds. Now unlikely to be loading.
    private const float VerySlowAfterSeconds = 15f;
    private const float FastIntervalSeconds = 0.1f;
    private const float SlowIntervalSeconds = 1f;
    private const float VerySlowIntervalSeconds = 5f;

    private string _missingThing;
    private float _firstMissAt = -1f;
    private float _nextTryAt;
    private bool _logged;

    public SearchBackoff(string missingThing) => _missingThing = missingThing ?? "requested scene object";

    public void Name(string missingThing)
    {
        try { _missingThing = missingThing ?? "requested scene object"; }
        catch { }
    }

    public bool ShouldTry()
    {
        try { return _firstMissAt < 0f || Time.realtimeSinceStartup >= _nextTryAt; }
        catch { return true; }
    }

    public void Found()
    {
        try { Reset(); }
        catch { }
    }

    public void Missed()
    {
        try
        {
            float now = Time.realtimeSinceStartup;
            if (_firstMissAt < 0f) _firstMissAt = now;
            float missingSeconds = Math.Max(0f, now - _firstMissAt);
            float interval = missingSeconds < SlowAfterSeconds ? FastIntervalSeconds
                : missingSeconds < VerySlowAfterSeconds ? SlowIntervalSeconds
                : VerySlowIntervalSeconds;
            // Said once, and only once the wait is long enough that loading no longer explains it.
            // This line is the whole reason the failure is diagnosable at all: it happens on a
            // player's machine and never on the one the mod is written on, so the log is the only
            // place it can ever be seen. Name the object, because which one it is decides the fix.
            if (!_logged && missingSeconds >= SlowAfterSeconds)
            {
                _logged = true;
                MelonLogger.Warning($"[TargetingMod] {_missingThing} has been missing for {missingSeconds:F1} seconds. " +
                                    "Scene searches for it are now spaced out so they cannot cost the frame rate, and " +
                                    "they keep retrying. Please report this with the log if the mod seems incomplete");
            }
            _nextTryAt = now + interval;
        }
        catch
        {
            // Timing and diagnostics must never be able to break the lookup they protect.
        }
    }

    public void Reset()
    {
        _firstMissAt = -1f;
        _nextTryAt = 0f;
        _logged = false;
    }
}
