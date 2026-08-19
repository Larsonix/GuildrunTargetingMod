using System;
using System.Globalization;
using System.Text;
using GuildrunTargetingMod.Ui;
using MelonLoader;
using UnityEngine;

namespace GuildrunTargetingMod;

// Measures the engine-side work which begins only after the mod has returned from OnLateUpdate.
// Every CPU path still runs on both sides; only the root handed to Unity for drawing is changed.
internal sealed class DrawCostProbe
{
    // A block is a DURATION, not a frame count, and that distinction is the whole of this comment.
    //
    // It was twelve frames first, which is the same defect this cycle catalogued everywhere else in
    // the mod: a frame count is a different length of time on every machine, and on ONE machine it
    // is a different length of time from second to second. Lifting the frame rate limit took the
    // game from 60 to about 300 fps, so twelve frames went from 200 ms to 40 ms, and the alternation
    // sped up as the prediction finished and the frame rate climbed, then slowed again whenever a
    // hero was picked up and the simulation restarted. Reported from play, exactly like that.
    //
    // Half a second per block also puts the alternation at one per second. That is deliberate and it
    // is a safety limit, not a comfort one: at twelve frames and 300 fps the board was flashing
    // between eight and thirteen times a second, which is inside the band that flashing guidance
    // exists to keep content out of. One per second is below the three per second that guidance
    // allows, and this file already reasons that way about the marks' travelling lights.
    private const double BlockMs = 500.0;
    private const int DiscardFrames = 3;
    private const int Capacity = 512;

    // Enough for a stable median and a stable quartile, and no more. The first run collected until
    // placement ended and wrapped both ring buffers, so it flashed the board for a minute and a half
    // to answer a question that was already answered in the first few seconds. Stopping on evidence
    // ends the flashing after roughly ten alternations and restores the frame rate limit with it.
    private const int TargetSamples = 256;

    // How close to the frame rate cap a median has to sit before the run counts as pinned to it.
    // One millisecond is comfortably inside the jitter of a vsynced frame and comfortably below
    // the cost this probe is looking for.
    private const double CapToleranceMs = 1.0;

    private readonly double[] _drawn = new double[Capacity];
    private readonly double[] _hidden = new double[Capacity];
    private int _drawnCount;
    private int _hiddenCount;
    private int _drawnAt;
    private int _hiddenAt;
    private int _frameInBlock;
    private double _blockStartedAt;
    private bool _drawing = true;
    private bool _running;
    private bool _complete;
    private bool _placementSeen;
    private bool _faulted;
    private bool _loggedFault;
    private bool _capLifted;
    private int _cappedFrameRate;
    private int _cappedVSync;

    internal void Update(OverlayRenderer overlay, bool enabled)
    {
        if (_faulted)
            return;
        try
        {
            if (!enabled)
            {
                if (_running) Stop(overlay);
                return;
            }
            _placementSeen = true;
            if (_complete) return; // measured already; the board is left alone for the rest of it.
            if (!_running)
            {
                _drawing = true;
                _frameInBlock = 0;
                _blockStartedAt = Time.unscaledTime;
                overlay?.SetWorldDrawing(true);
                LiftFrameRateCap();
                _running = true;
                return;
            }

            // The flip itself costs a frame, and GPU work is pipelined so a frame's cost can land
            // on the next one, so the first few frames of every block are discarded. That discard
            // stays a FRAME count on purpose while the block is a duration: it is paying for a
            // pipeline that is some number of frames deep, not some number of milliseconds deep.
            //
            // Alternating blocks, rather than one long run of each, keep a slow patch of the
            // session from landing entirely on one side. The frame rate climbs steadily here as
            // the prediction finishes, which is exactly the drift that would bias a blocked run.
            if (_frameInBlock >= DiscardFrames)
                AddSample(_drawing, Time.unscaledDeltaTime * 1000.0);
            _frameInBlock++;

            if (_drawnCount >= TargetSamples && _hiddenCount >= TargetSamples)
            {
                Stop(overlay); // restores the drawing and the frame rate limit together.
                _complete = true;
                MelonLogger.Msg("[TargetingMod] draw-cost probe: enough samples, board and frame rate limit back to normal");
                return;
            }

            if (Time.unscaledTime - _blockStartedAt < BlockMs / 1000.0) return;

            _drawing = !_drawing;
            _frameInBlock = 0;
            _blockStartedAt = Time.unscaledTime;
            overlay?.SetWorldDrawing(_drawing);
        }
        catch (Exception e)
        {
            Disable(overlay, e);
        }
    }

    internal void FinishPlacement(OverlayRenderer overlay)
    {
        try
        {
            Stop(overlay);
            if (_placementSeen && (_drawnCount != 0 || _hiddenCount != 0))
                MelonLogger.Msg(BuildReport());
        }
        catch (Exception e)
        {
            Disable(overlay, e);
        }
        finally
        {
            RestoreDrawing(overlay);
            RestoreFrameRateCap();
            _placementSeen = false;
            _complete = false; // the next placement is a different board and gets its own measurement.
            _drawnCount = 0;
            _hiddenCount = 0;
            _drawnAt = 0;
            _hiddenAt = 0;
        }
    }

    private void Stop(OverlayRenderer overlay)
    {
        _running = false;
        _drawing = true;
        _frameInBlock = 0;
        overlay?.SetWorldDrawing(true);
        RestoreFrameRateCap();
    }

    // The probe lifts the game's own frame rate limit while it is measuring, and puts it back.
    //
    // This is not a convenience, it is what makes the measurement exist at all. The sample is the
    // whole frame, wait for the display included. Under a limit the machine is comfortably meeting,
    // a frame with the overlay drawn and a frame without it both finish inside the same interval
    // and both measure it, so the difference is the limit rather than the cost, however expensive
    // the drawing is. This machine is the case in point: a 200 Hz display held at a 60 fps limit,
    // which is 16.67 ms of which the game uses a fraction.
    //
    // Doing it here rather than asking the player to change a setting is deliberate. It is scoped
    // to the measurement, it restores itself, and it cannot be half done. The alternative is a
    // procedure that produces a confident zero whenever it is not followed exactly.
    private void LiftFrameRateCap()
    {
        if (_capLifted) return;
        try
        {
            _cappedFrameRate = Application.targetFrameRate;
            _cappedVSync = QualitySettings.vSyncCount;
            if (_cappedFrameRate <= 0 && _cappedVSync <= 0) return; // nothing is limiting it already.
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            _capLifted = true;
            // The cached profile still describes the limited machine, and the report's own
            // cannot-measure check reads it. Re-resolving keeps that check honest about what is
            // true while the probe is running.
            MachineProfile.Reset();
            MelonLogger.Msg("[TargetingMod] draw-cost probe: frame rate limit lifted for the measurement, restored when it ends");
        }
        catch
        {
            // A platform that refuses the change leaves the limit in place. That is not fatal:
            // the report's cannot-measure check is what catches it, and it is still correct.
            _capLifted = false;
        }
    }

    private void RestoreFrameRateCap()
    {
        if (!_capLifted) return;
        _capLifted = false;
        try
        {
            Application.targetFrameRate = _cappedFrameRate;
            QualitySettings.vSyncCount = _cappedVSync;
            MachineProfile.Reset();
        }
        catch
        {
            // Shutting down, most likely. The setting belongs to the game and it re-applies its
            // own on the next settings change either way.
        }
    }

    private void AddSample(bool drawn, double milliseconds)
    {
        if (drawn)
        {
            _drawn[_drawnAt] = milliseconds;
            _drawnAt = (_drawnAt + 1) % Capacity;
            if (_drawnCount < Capacity) _drawnCount++;
        }
        else
        {
            _hidden[_hiddenAt] = milliseconds;
            _hiddenAt = (_hiddenAt + 1) % Capacity;
            if (_hiddenCount < Capacity) _hiddenCount++;
        }
    }

    private string BuildReport()
    {
        GetQuartiles(_drawn, _drawnCount, out double drawnP25, out double drawnMedian, out double drawnP75);
        GetQuartiles(_hidden, _hiddenCount, out double hiddenP25, out double hiddenMedian, out double hiddenP75);
        var report = new StringBuilder();
        report.Append("[TargetingMod] draw-cost probe: drawn samples ").Append(_drawnCount)
            .Append(", hidden samples ").Append(_hiddenCount);
        report.AppendLine().Append("[TargetingMod] draw-cost probe   drawn whole-frame ms: p25 ")
            .Append(Ms(drawnP25)).Append(", median ").Append(Ms(drawnMedian)).Append(", p75 ").Append(Ms(drawnP75));
        report.AppendLine().Append("[TargetingMod] draw-cost probe   hidden whole-frame ms: p25 ")
            .Append(Ms(hiddenP25)).Append(", median ").Append(Ms(hiddenMedian)).Append(", p75 ").Append(Ms(hiddenP75));
        report.AppendLine().Append("[TargetingMod] draw-cost probe   median difference drawn minus hidden: ")
            .Append(Ms(drawnMedian - hiddenMedian)).Append(" ms");

        // A frame rate limit defeats this probe completely and silently, and that failure is the
        // exact one the probe exists to correct, so it is checked rather than hoped for.
        //
        // The sample is the WHOLE frame, which includes the wait for the display. Under vertical
        // sync at 60 Hz, a frame with the overlay drawn and a frame without it both finish inside
        // the same 16.67 ms and both measure 16.67 ms. The difference then comes out near zero
        // however expensive the drawing actually is. Reporting that as "drawing costs nothing" is
        // precisely how the previous cycle talked itself out of this work, from a number that
        // could not contain what it was being used to rule out.
        //
        // So when a cap is really in force and both sides are sitting inside it, this refuses to
        // answer instead of answering wrongly. An instrument that cannot tell "no cost" from
        // "cannot see" is worse than no instrument, because it produces a confident wrong number.
        //
        // A cap the machine is FAILING to meet is a different case and stays valid: both medians
        // then sit above the interval, the frame is genuinely over budget, and the difference is
        // real work rather than two equal waits.
        // Both medians have to sit AT the limit, not merely under it. A run whose frames land well
        // below the limit is not being held by it, which is exactly the state the probe arranges
        // for itself by lifting the limit, and reporting that as unmeasurable would refuse to
        // answer precisely when the answer became available.
        double target = MachineProfile.TargetFrameMs;
        if (MachineProfile.FrameRateCapped && target > 0.0
            && Math.Abs(drawnMedian - target) < CapToleranceMs
            && Math.Abs(hiddenMedian - target) < CapToleranceMs)
        {
            report.AppendLine().Append("[TargetingMod] draw-cost probe   NOT A MEASUREMENT: the frame rate is capped at ")
                .Append(Ms(target)).Append(" ms and both sides fit inside it, so the difference above is the cap, not the cost. ")
                .Append("Turn vertical sync and any frame rate limit off, or raise the resolution until the game misses the cap, then measure again.");
        }
        return report.ToString();
    }

    private static void GetQuartiles(double[] source, int count,
        out double p25, out double median, out double p75)
    {
        if (count <= 0)
        {
            p25 = median = p75 = 0.0;
            return;
        }
        var scratch = new double[count];
        Array.Copy(source, scratch, count);
        Array.Sort(scratch);
        p25 = Percentile(scratch, 0.25);
        median = Percentile(scratch, 0.50);
        p75 = Percentile(scratch, 0.75);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        double position = (sorted.Length - 1) * percentile;
        int below = (int)position;
        int above = Math.Min(below + 1, sorted.Length - 1);
        return sorted[below] + (sorted[above] - sorted[below]) * (position - below);
    }

    private void Disable(OverlayRenderer overlay, Exception error)
    {
        _faulted = true;
        _running = false;
        RestoreDrawing(overlay);
        // The limit belongs to the game, not to this probe, and a faulted probe never runs again
        // this session. Without this the player would keep an uncapped frame rate they did not ask
        // for, caused by a diagnostic that has already given up.
        RestoreFrameRateCap();
        if (_loggedFault) return;
        _loggedFault = true;
        try
        {
            MelonLogger.Warning("[TargetingMod] draw-cost probe disabled for this session: " + error.Message);
        }
        catch
        {
            // Logging is part of the instrument too, so even its failure stays inside the probe.
        }
    }

    private static void RestoreDrawing(OverlayRenderer overlay)
    {
        try { overlay?.SetWorldDrawing(true); }
        catch { /* teardown may already have destroyed the root; nothing remains to restore. */ }
    }

    private static string Ms(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
}
