using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace GuildrunTargetingMod;

// What the mod costs, per frame, per subsystem.
//
// It exists because the mod could not answer that question about itself. Two of its per-frame
// consumers timed themselves and the other six did not, nothing summed them, and the readings that
// did exist were printed only with verbose logging on. "The mod got faster" was a belief.
//
// Measurement is ALWAYS on ; only the reporting is gated. That is deliberate and it is not for the
// log : the simulation's time budget reads LastFrameModMs to work out how much slack the machine
// actually has, so switching the instrument off would switch off the thing that keeps a slow
// machine from stuttering.
//
// The cost of measuring is two Stopwatch reads per slot per frame, into flat arrays. No
// dictionary, no string, no allocation, nothing per frame that the thing being measured could be
// blamed for.
internal enum PerfSlot
{
    // Top-level slots. These sum to the mod's per-frame total.
    Phase, EnsureLayer, Views, Hover, DragTrack, Shadow, Anchor, LiveSnapshot,
    PartGlow, Aoe, Render, NativeUi, Census, Parity,

    // Detail slots. These are nested INSIDE a top-level slot, so they are reported
    // separately and are deliberately NOT added to the frame total, or the total
    // would count the same microsecond twice.
    DetailStart,
    MirrorBuild, SimStart, SimTick, OpeningSnapshot, Fade, MarkArtStamp, MarkArtTrace, FrameTraceRead,
    // The two halves of FrameTraceRead. It measured 138.86 ms a call on a turned relic frame and
    // the whole of that was attributed to one slot, which cannot say whether the cost is the
    // graphics stall or the rotated sampling on the processor. Those want opposite fixes, so the
    // split is measured before either is attempted.
    FramePixels, FrameContourScan,

    // MUST STAY LAST, and every array below is sized from it.
    //
    // It is here because the alternative bit, hard. SlotCount was written as
    // "(int)FrameTraceRead + 1", naming whichever slot happened to be last at the time, and adding
    // two slots after it made every Measure on them write past the end of the arrays. The throw
    // surfaced inside FrameTrace, was caught by ITS safety net, and was reported to the player as
    // "could not read a frame sprite" : four rarity frames silently fell back to a flat border for
    // a whole session, and the profiler that caused it was the last place anyone would look.
    //
    // A count restated by hand is a count that drifts. Derive it.
    Count
}

internal static class Perf
{
    private const int SlotCount = (int)PerfSlot.Count;
    private const int TopLevelCount = (int)PerfSlot.DetailStart;

    private static readonly long[] FrameTicks = new long[SlotCount];
    private static readonly long[] FrameCalls = new long[SlotCount];
    private static readonly long[] Calls = new long[SlotCount];
    private static readonly long[] TotalTicks = new long[SlotCount];
    // Cold calls are kept apart from every other frame, per slot and for the mod as a whole. They
    // can land on several frames as subsystems first become active, and pay for every scene search
    // and every classification exactly once, so folding them into a mean answers a question nobody
    // asked : an aggregate is not a population.
    private static readonly long[] FirstTicks = new long[SlotCount];
    private static readonly long[] FirstCalls = new long[SlotCount];
    private static readonly long[] FirstFrame = new long[SlotCount];
    private static readonly long[] WorstTicks = new long[SlotCount];
    private static readonly string[] SlotNames =
    {
        "Phase", "EnsureLayer", "Views", "Hover", "DragTrack", "Shadow", "Anchor", "LiveSnapshot",
        "PartGlow", "Aoe", "Render", "NativeUi", "Census", "Parity", "DetailStart",
        "MirrorBuild", "SimStart", "SimTick", "OpeningSnapshot", "Fade", "MarkArtStamp", "MarkArtTrace",
        "FrameTraceRead", "FramePixels", "FrameContourScan", "Count"
    };

    private static long _frames;
    private static long _totalModTicks;
    private static long _coldFrames;
    private static long _coldFrameModTicks;
    private static long _settledFrames;
    private static long _settledModTicks;
    private static long _worstFrameModTicks;
    private static long _worstFrameIndex;
    private static double _gameSeconds;
    private static bool _loggedSlowFrame;
    private static long _lastHeapBytes;
    private static long _allocatedBytes;
    private static long _worstAllocatedBytes;
    private static long _allocationFrames;
    private static long _collectionAffectedFrames;
    private static readonly int[] LastCollectionCounts = new int[3];
    private static readonly long[] Collections = new long[3];

    /// <summary>The mod's own cost on the frame that just ended. Read by the simulation budget.</summary>
    internal static double LastFrameModMs { get; private set; }

    /// <summary>Real time the frame that just ended took, the mod's share included.</summary>
    internal static double LastFrameTotalMs { get; private set; }

    internal static Scope Measure(PerfSlot slot) => new Scope(slot);

    /// <summary>
    /// What one call of a slot has actually cost on THIS machine, after its first call. Zero when
    /// it has never run.
    /// </summary>
    /// <remarks>
    /// This is what lets the mod schedule against the machine it is on rather than against a
    /// constant chosen on the machine it was written on. A job that cannot be split asks what it
    /// costs, compares that against the slack this frame has, and waits for a better frame if it
    /// does not fit.
    ///
    /// Zero for a slot that has never run is deliberate and is not a missing case: an unknown cost
    /// must never be treated as unaffordable, or the first call would be deferred forever and the
    /// cost would stay unknown forever. The first call is how the estimate comes to exist.
    ///
    /// The first call is excluded for the same reason the report excludes it. It pays for JIT
    /// compilation and for the interop layer resolving native addresses, neither of which is paid
    /// again, so including it would over-estimate every later call by an order of magnitude and the
    /// job would defer itself on frames that had ample room.
    /// </remarks>
    internal static double MeanCallMs(PerfSlot slot)
    {
        int index = (int)slot;
        if ((uint)index >= (uint)SlotCount) return 0.0;
        long calls = Calls[index] - FirstCalls[index];
        if (calls <= 0) return 0.0;
        return ToMs(TotalTicks[index] - FirstTicks[index]) / calls;
    }

    internal static void EndFrame()
    {
        long modTicks = 0;
        for (int i = 0; i < TopLevelCount; i++) modTicks += FrameTicks[i];
        LastFrameModMs = ToMs(modTicks);
        LastFrameTotalMs = Time.unscaledDeltaTime * 1000.0;

        _frames++;
        _totalModTicks += modTicks;
        _gameSeconds += Time.unscaledDeltaTime;
        bool coldFrame = false;
        for (int i = 0; i < SlotCount; i++)
        {
            if (FrameCalls[i] != 0 && FirstFrame[i] == 0)
            {
                coldFrame = true;
                break;
            }
        }
        if (coldFrame)
        {
            _coldFrames++;
            _coldFrameModTicks += modTicks;
        }
        else
        {
            _settledFrames++;
            _settledModTicks += modTicks;
            if (_worstFrameIndex == 0 || modTicks > _worstFrameModTicks)
            {
                _worstFrameModTicks = modTicks;
                _worstFrameIndex = _frames;
            }
            if (!_loggedSlowFrame)
            {
                double slowFrameMs = Math.Max(4.0, 0.30 * MachineProfile.TargetFrameMs);
                if (LastFrameModMs > slowFrameMs)
                {
                    _loggedSlowFrame = true;
                    WarnSlowFrame(modTicks);
                }
            }
        }

        for (int i = 0; i < SlotCount; i++)
        {
            long calls = FrameCalls[i];
            if (calls != 0)
            {
                long ticks = FrameTicks[i];
                Calls[i] += calls;
                TotalTicks[i] += ticks;
                if (FirstFrame[i] == 0)
                {
                    FirstFrame[i] = _frames;
                    FirstTicks[i] = ticks;
                    FirstCalls[i] = calls;
                }
                else if (ticks > WorstTicks[i]) WorstTicks[i] = ticks;
            }
            FrameCalls[i] = 0;
            FrameTicks[i] = 0;
        }
        SampleManagedHeap();
    }

    /// <summary>Starts a fresh window. Called when a phase is entered, so each phase reports its own.</summary>
    internal static void Reset()
    {
        Array.Clear(FrameTicks, 0, SlotCount);
        Array.Clear(FrameCalls, 0, SlotCount);
        Array.Clear(Calls, 0, SlotCount);
        Array.Clear(TotalTicks, 0, SlotCount);
        Array.Clear(FirstTicks, 0, SlotCount);
        Array.Clear(FirstCalls, 0, SlotCount);
        Array.Clear(FirstFrame, 0, SlotCount);
        Array.Clear(WorstTicks, 0, SlotCount);
        _frames = 0;
        _totalModTicks = 0;
        _coldFrames = 0;
        _coldFrameModTicks = 0;
        _settledFrames = 0;
        _settledModTicks = 0;
        _worstFrameModTicks = 0;
        _worstFrameIndex = 0;
        _gameSeconds = 0;
        _loggedSlowFrame = false;
        _lastHeapBytes = 0;
        _allocatedBytes = 0;
        _worstAllocatedBytes = 0;
        _allocationFrames = 0;
        _collectionAffectedFrames = 0;
        Array.Clear(Collections, 0, Collections.Length);
        for (int generation = 0; generation < LastCollectionCounts.Length; generation++)
        {
            try { LastCollectionCounts[generation] = GC.CollectionCount(generation); }
            catch { LastCollectionCounts[generation] = 0; }
        }
        LastFrameModMs = 0;
        LastFrameTotalMs = 0;
    }

    /// <summary>The whole window as one block of lines. Empty when nothing was measured.</summary>
    /// <remarks>
    /// Two means per slot, and both denominators are named in the header, because they answer two
    /// different questions and a rate whose denominator is unstated is a number that will be read
    /// wrong. Per FRAME is what the slot costs the player : a throttled scan that runs once every
    /// thirty frames is cheap per frame however heavy it is. Per CALL is what one run of it costs :
    /// the same scan can be thirty times worse than it looks. Telling those two apart is the whole
    /// diagnosis this instrument was built for, so it must not need arithmetic to read.
    /// </remarks>
    internal static string Report(string window)
    {
        if (_frames == 0) return "[TargetingMod] perf (" + window + "): no frames measured";

        double settledMeanMs = _settledFrames > 0 ? ToMs(_settledModTicks) / _settledFrames : 0;
        double gameFrameMeanMs = _gameSeconds * 1000.0 / _frames;
        double modShare = _gameSeconds > 0 ? ToMs(_totalModTicks) * 100.0 / (_gameSeconds * 1000.0) : 0;

        var report = new StringBuilder();
        report.Append("[TargetingMod] perf (").Append(window).Append("): ").Append(_frames)
            .Append(" frame(s), cold ").Append(_coldFrames).Append(" frame(s) total ").Append(Ms(_coldFrameModTicks))
            .Append(" ms, worst settled ").Append(Ms(_worstFrameModTicks)).Append(" ms at frame ")
            .Append(_worstFrameIndex).Append(", settled mean ").Append(Ms2(settledMeanMs))
            .Append(" ms; whole frame mean ").Append(Ms2(gameFrameMeanMs))
            .Append(" ms, the mod is ").Append(modShare.ToString("F1", CultureInfo.InvariantCulture))
            .Append("% of it. Columns after each slot's first call: mean/frame, mean/call.");
        double allocatedPerFrame = _allocationFrames > 0 ? _allocatedBytes / (double)_allocationFrames : 0.0;
        report.AppendLine();
        report.Append("[TargetingMod] perf   managed bytes/frame mean ")
            .Append(allocatedPerFrame.ToString("F0", CultureInfo.InvariantCulture))
            .Append(", worst ").Append(_worstAllocatedBytes)
            .Append(", across ").Append(_allocationFrames).Append(" sampled frame(s)")
            .Append("; collection-affected frames skipped ").Append(_collectionAffectedFrames)
            .Append("; collections gen0/gen1/gen2 ").Append(Collections[0]).Append('/')
            .Append(Collections[1]).Append('/').Append(Collections[2]);
        // What the budget controller settled on, because without it the report cannot show whether
        // the mod was given room or was starving itself. The first version of that controller read
        // a capped frame as having no room at all and throttled the prediction to its floor, and
        // nothing in this report said so: every number looked healthy while the drag felt slow.
        // A budget that cannot be seen in the log is a budget nobody can check.
        // Both figures, because the gap between them is what went wrong the first time. The game
        // asked for 200 frames a second while the machine was held at 60 from outside it, so
        // judging against the ASKED figure ruled every real frame a failure. The budget now judges
        // against the OBSERVED one, and printing them side by side makes a disagreement visible
        // instead of leaving it to be inferred from a slow drag.
        report.Append("; frame budget settled at ")
            .Append(Ms2(FrameBudget.AllowanceMs)).Append(" ms against an observed ")
            .Append(Ms2(FrameBudget.ReferenceMs)).Append(" ms frame (game asked for ")
            .Append(Ms2(MachineProfile.TargetFrameMs)).Append(" ms)");

        // Insertion sort by total time, descending. A handful of slots, once per placement, so the
        // simplest thing that cannot allocate is the right thing.
        var order = new int[SlotCount];
        int count = 0;
        for (int i = 0; i < SlotCount; i++)
        {
            if (i == (int)PerfSlot.DetailStart || Calls[i] == 0) continue;
            int at = count;
            while (at > 0 && TotalTicks[order[at - 1]] < TotalTicks[i]) { order[at] = order[at - 1]; at--; }
            order[at] = i;
            count++;
        }

        for (int position = 0; position < count; position++)
        {
            int slot = order[position];
            bool detail = slot > (int)PerfSlot.DetailStart;
            long afterTicks = TotalTicks[slot] - FirstTicks[slot];
            long afterFrames = _frames - FirstFrame[slot];
            long afterCalls = Calls[slot] - FirstCalls[slot];
            double perFrame = afterFrames > 0 ? ToMs(afterTicks) / afterFrames : 0;
            double perCall = afterCalls > 0 ? ToMs(afterTicks) / afterCalls : 0;
            report.AppendLine();
            report.Append("[TargetingMod] perf   ").Append(detail ? "  ." : "").Append(SlotNames[slot].PadRight(detail ? 15 : 17))
                .Append(Calls[slot].ToString(CultureInfo.InvariantCulture).PadLeft(6)).Append(" call(s)")
                .Append("  first ").Append(Ms(FirstTicks[slot]).PadLeft(7))
                .Append("  worst ").Append(Ms(WorstTicks[slot]).PadLeft(7))
                .Append("  ").Append(Ms2(perFrame).PadLeft(7))
                .Append("  ").Append(Ms2(perCall).PadLeft(7));
        }
        return report.ToString();
    }

    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    private static string Ms(long ticks) => ToMs(ticks).ToString("F2", CultureInfo.InvariantCulture);
    private static string Ms2(double ms) => ms.ToString("F2", CultureInfo.InvariantCulture);

    private static void SampleManagedHeap()
    {
        // Heap counters are diagnostic and must obey the same rule as every timed slot: losing a
        // reading is harmless, while letting an instrument throw into the frame it watches is not.
        try
        {
            long heapBytes = GC.GetTotalMemory(false);
            if (_lastHeapBytes != 0)
            {
                long delta = heapBytes - _lastHeapBytes;
                if (delta < 0)
                {
                    // A collection can make the live heap smaller than it was one frame ago. That
                    // is not a negative allocation, so keep the frame out of the allocation mean
                    // and count it separately rather than letting it cancel real garbage.
                    _collectionAffectedFrames++;
                }
                else
                {
                    _allocatedBytes += delta;
                    _allocationFrames++;
                    if (delta > _worstAllocatedBytes) _worstAllocatedBytes = delta;
                }
            }
            _lastHeapBytes = heapBytes;
            for (int generation = 0; generation < LastCollectionCounts.Length; generation++)
            {
                int count = GC.CollectionCount(generation);
                int delta = count - LastCollectionCounts[generation];
                if (delta > 0) Collections[generation] += delta;
                LastCollectionCounts[generation] = count;
            }
        }
        catch
        {
            // A missing heap sample must never be observable outside the report.
        }
    }

    // Said once per measurement window, without anyone having to switch a log on first, and it
    // names up to three slots responsible rather than only the total. The same shape and reason as the
    // warnings PositionalGlow and PartGlow already carry : a total on its own sends the report back
    // asking which part.
    private static void WarnSlowFrame(long modTicks)
    {
        int first = -1, second = -1, third = -1;
        for (int slot = 0; slot < TopLevelCount; slot++)
        {
            if (FrameTicks[slot] <= 0) continue;
            if (first < 0 || FrameTicks[slot] > FrameTicks[first]) { third = second; second = first; first = slot; }
            else if (second < 0 || FrameTicks[slot] > FrameTicks[second]) { third = second; second = slot; }
            else if (third < 0 || FrameTicks[slot] > FrameTicks[third]) third = slot;
        }
        var warning = new StringBuilder("[TargetingMod] the mod took ").Append(Ms(modTicks))
            .Append(" ms on a settled frame; the most expensive part").Append(second >= 0 ? "s were " : " was ")
            .Append(SlotNames[first]).Append(' ').Append(Ms(FrameTicks[first])).Append(" ms");
        if (second >= 0) warning.Append(", ").Append(SlotNames[second]).Append(' ').Append(Ms(FrameTicks[second])).Append(" ms");
        if (third >= 0) warning.Append(", ").Append(SlotNames[third]).Append(' ').Append(Ms(FrameTicks[third])).Append(" ms");
        warning.Append(". Please report this with the log if the game feels uneven during placement");
        MelonLogger.Warning(warning.ToString());
    }

    // A struct, so a scope per subsystem per frame allocates nothing at all. Used as a `using var`
    // declaration at the top of a method wherever the whole body is being measured, which keeps the
    // measurement from adding a brace level to code that is already written.
    internal readonly struct Scope : IDisposable
    {
        private readonly int _slot;
        private readonly long _start;

        internal Scope(PerfSlot slot)
        {
            _slot = (int)slot;
            _start = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            // A measurement must never be able to break the thing it measures, and this one already
            // did once : an out of range slot threw out of Dispose, into the middle of the frame
            // tracer, which caught it and told the player it could not read a sprite. Losing a
            // number is nothing. Losing a feature to the instrument watching it is the worst
            // possible failure for a profiler, so the bound is checked rather than assumed.
            if ((uint)_slot >= (uint)SlotCount) return;
            FrameTicks[_slot] += Stopwatch.GetTimestamp() - _start;
            FrameCalls[_slot]++;
        }
    }
}
