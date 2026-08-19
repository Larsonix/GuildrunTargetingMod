using System;
using UnityEngine;

namespace GuildrunTargetingMod;

// How much of this frame the mod is allowed to spend.
//
// The rule is not "a fixed number of milliseconds". It is "whatever this machine can give without
// the player noticing". A constant is cheap on the machine it was chosen on and ruinous on a slower
// one, which is why this is a controller rather than a setting.
//
// HOW IT WORKS, AND WHY IT IS NOT THE OBVIOUS THING.
//
// The obvious thing is to measure the spare time directly: take the target frame time and subtract
// what the frame actually cost. That is what this class did first, and it was WRONG, in a way that
// was invisible here and produced a bad delay in play.
//
// Under a frame rate limit, a frame that finishes early does not report finishing early. It waits
// for the limit and then reports the whole interval. A capped 60 fps frame measures about 16.7 ms
// whether the machine did 3 ms of work or 16, because the rest of it is idle waiting. Subtracting
// that from the target gives about zero, always, on every frame, however idle the processor is. The
// mod therefore concluded it had no time to spare at all and started deferring the work that draws
// the drag preview, on a machine that was 78 percent idle.
//
// This exact trap is written down in this project already, for the draw cost probe, in the same
// words: unscaledDeltaTime is the whole frame including the wait. It was read, quoted into the
// design for this cycle, and then walked into anyway, one layer down. Measured 2026-08-19 from
// play: SimTick fell to 0.01 ms a frame, which is its floor, and the owner reported the drag
// waiting too long.
//
// So this no longer tries to INFER the spare time. It watches the OUTCOME instead. Spend a little
// more on every frame that hits its target, and halve it the moment one does not. That works
// identically capped or uncapped, needs no knowledge of what the game costs internally, and
// optimises the only thing anyone actually cares about: whether the mod is costing the player
// frames. A frame that misses under a limit is unmissable, because missing a vsync interval does
// not cost a little time, it costs the whole next interval.
internal static class FrameBudget
{
    // Progress must never stop completely, however busy the machine is. A machine with no room at
    // all still gets this much, so a prediction always finishes eventually rather than never.
    private const double FloorMs = 0.5;

    // The mod never takes more than about a third of a frame, however much room it appears to have.
    // Expressed against the machine's own target rather than as a flat number of milliseconds, so
    // it means the same thing at 30, 60 and 240 frames a second.
    private const double CeilingFraction = 0.35;

    // Slow up, fast down. Growth is gentle so the allowance settles just under what the machine can
    // actually give; the cut is severe because a dropped frame is a thing the player already saw
    // and the mod should get out of the way immediately rather than negotiate.
    private const double IncreaseMs = 0.25;
    private const double DecreaseFactor = 0.5;

    // How far past the target a frame has to land before it counts as missed. Generous, because
    // ordinary jitter must not be read as the mod's fault, and a genuine miss under a limit
    // overshoots by a whole interval rather than by a little.
    private const double MissFactor = 1.15;

    // A pause or an alt tab is a discontinuity, not a frame the game needed to render. Feeding one
    // in would collapse the allowance for no reason every time the window loses focus.
    private const double DiscontinuousFrameMs = 250.0;

    // Nothing may be withheld for longer than this. A machine that never has room would otherwise
    // never run the jobs gated on having some, which is the mod deactivating something that could
    // work: forbidden, and the reason this bound exists rather than being trusted to the arithmetic.
    internal const float MaxDeferSeconds = 0.25f;

    // The slowest frame that is still allowed to seed the reference below. A cold first frame can
    // be two hundred milliseconds of one-time setup; adopting that as "normal" would tell the
    // controller this machine runs at five frames a second.
    private const double MaxReferenceMs = 100.0;

    // How fast the reference follows a machine that turns out to be quicker than believed, and how
    // slowly it follows one that turns out to be slower. Asymmetric on purpose: dropping towards a
    // better frame should be quick, because it corrects an over-generous budget, while creeping
    // upward has to be slow or the mod's own overspending would raise the bar it is judged against
    // and the controller would stop noticing its own cost.
    private const double ReferenceFallWeight = 0.05;
    private const double ReferenceRiseWeight = 0.002;

    // How far past a normal frame this machine produces before it counts as the mod's fault.
    private const double MissMarginMs = 1.0;

    private static double _referenceMs = -1.0;
    private static double _allowanceMs = -1.0;
    private static double _spentMs;

    /// <summary>What the controller currently believes this machine can give, before spending.</summary>
    internal static double AllowanceMs => _allowanceMs < 0.0 ? 0.0 : _allowanceMs;

    /// <summary>Whether a frame has been observed yet.</summary>
    internal static bool Calibrated => _allowanceMs >= 0.0;

    /// <summary>Opens a new frame. Called once, before anything asks to spend.</summary>
    internal static void BeginFrame()
    {
        double frameMs = Perf.LastFrameTotalMs;

        // THE REFERENCE IS OBSERVED, NOT CONFIGURED, AND THAT DISTINCTION IS THE WHOLE BUG.
        //
        // This used to compare against MachineProfile.TargetFrameMs, the rate the GAME asks for.
        // Measured in play 2026-08-19: the game asked for 200 frames a second while something
        // outside it, a driver forcing vsync, held the machine at 60. So the target said 5 ms, real
        // frames were 16.8 ms, and the controller ruled EVERY frame a failure, halved the allowance
        // on every one of them and pinned it to its floor permanently. The log said so in one line:
        // "frame budget settled at 0.50 ms of a 5.00 ms target".
        //
        // What the mod needs is not what the machine was asked to do. It is what the machine
        // normally does, so that "worse than normal" can mean "the mod just cost the player a
        // frame". Nothing configured can answer that, because nothing configured knows about a
        // limit imposed from outside the game.
        if (frameMs > 0.0 && frameMs <= MaxReferenceMs)
        {
            if (_referenceMs < 0.0) _referenceMs = frameMs;
            else if (frameMs < _referenceMs) _referenceMs += ReferenceFallWeight * (frameMs - _referenceMs);
            else _referenceMs += ReferenceRiseWeight * (frameMs - _referenceMs);
        }

        // Until a frame has been seen, fall back to what was asked for. It is the only figure that
        // exists yet, and it is replaced by observation within one frame.
        double referenceMs = _referenceMs > 0.0 ? _referenceMs : MachineProfile.TargetFrameMs;
        double ceilingMs = Math.Max(FloorMs, referenceMs * CeilingFraction);

        if (_allowanceMs < 0.0)
        {
            // Start at the ceiling rather than at the floor. An unknown machine is presumed capable
            // and corrected within a frame or two if it is not, which costs at most a couple of
            // frames. Starting at the floor would instead make the mod slowest during exactly the
            // moments it is still working out what it is running on, and on a machine that was fine
            // all along it would take a second to climb out of a hole it never needed to be in.
            _allowanceMs = ceilingMs;
        }
        else if (frameMs > 0.0 && frameMs <= DiscontinuousFrameMs)
        {
            if (frameMs > referenceMs * MissFactor + MissMarginMs)
                _allowanceMs = Math.Max(FloorMs, _allowanceMs * DecreaseFactor);
            else
                _allowanceMs = Math.Min(ceilingMs, _allowanceMs + IncreaseMs);
        }
        _spentMs = 0.0;
    }

    /// <summary>What a normal frame costs on this machine, learned by watching it.</summary>
    internal static double ReferenceMs => _referenceMs > 0.0 ? _referenceMs : MachineProfile.TargetFrameMs;

    /// <summary>What is left of this frame's allowance.</summary>
    internal static double RemainingMs => Math.Max(0.0, AllowanceMs - _spentMs);

    /// <summary>
    /// Whether a job whose measured cost is <paramref name="estimateMs"/> fits in what is left.
    /// </summary>
    /// <remarks>
    /// A job that has never run estimates zero and is therefore always affordable. That is
    /// deliberate: the estimate comes from measuring the job on this machine, so the first run has
    /// to happen before there is anything to measure.
    /// </remarks>
    internal static bool CanAfford(double estimateMs) =>
        estimateMs <= 0.0 || !Calibrated || RemainingMs >= estimateMs;

    internal static void Spend(double ms)
    {
        if (ms > 0.0) _spentMs += ms;
    }

    /// <summary>
    /// What a job that cannot be split should do this frame: run it, or wait for a better one.
    /// </summary>
    /// <remarks>
    /// Building the board description and constructing the battle are one call each into code the
    /// mod does not own. No budget can make either shorter, so the only thing left to decide is
    /// WHICH frame pays. Waiting for a frame with room turns a stutter into a little latency.
    ///
    /// <paramref name="deferringSince"/> is the caller's own clock, held across frames and reset by
    /// this method when it answers true. Past <see cref="MaxDeferSeconds"/> the answer is true
    /// whatever the budget says.
    /// </remarks>
    internal static bool RunNowOrDefer(double estimateMs, ref float deferringSince)
    {
        if (CanAfford(estimateMs))
        {
            deferringSince = 0f;
            return true;
        }
        float now;
        try { now = Time.realtimeSinceStartup; }
        catch { return true; } // no clock means no way to bound the wait, so do not start one.
        if (deferringSince <= 0f)
        {
            deferringSince = now;
            return false;
        }
        if (now - deferringSince < MaxDeferSeconds) return false;
        deferringSince = 0f;
        return true;
    }

    /// <summary>Forgets the machine. Called when placement ends, like MachineProfile.</summary>
    internal static void Reset()
    {
        _allowanceMs = -1.0;
        _spentMs = 0.0;
        // The reference is deliberately NOT forgotten. It describes the machine, which does not
        // change between placements, and re-learning it every battle would spend the opening frames
        // of each one budgeting against whatever the loading screen happened to cost.
    }
}
