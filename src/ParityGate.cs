using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GuildrunTargetingMod.Interop;
using Il2CppEmber.Scopes.Battle.BattleSimulation.Data;
using Il2CppEmber.Scopes.GameRun.RunSession.Data;
using Il2CppEmber.Simulation.Core.Fsm;
using Il2CppEmber.Simulation.Core.State;
using Il2CppEmber.Simulation.Core.State.Entities;
using Il2Cppgg.leyline.core.Mvcs.Model;
using MelonLoader;
using UnityEngine;
using FP = Il2CppPhoton.Deterministic.FP;
using FPVector3 = Il2CppPhoton.Deterministic.FPVector3;

namespace GuildrunTargetingMod;

// Checks the mod's prediction against the fight that actually happens, every battle.
//
// This is what lets the mod survive a game update without anyone watching it. The preview is not
// trusted because the code looks right ; it is trusted because the last battle proved it. When
// the real fight starts, the gate samples it and compares : the seed, who each unit picked, and
// where everyone ended up. A match unlocks the preview for this build. A mismatch turns it off,
// says so on screen, and writes the disagreement down. A fresh install and the first launch after
// a patch both start in that off state and unlock themselves after one clean battle.
//
// The sampling is invisible. It reads the live battle and draws nothing.
internal sealed class ParityGate
{
    private const int ExistingSampleTickCap = 150;
    private const int SampleMargin = 30;
    private const int AbsoluteSampleTickCap = ShadowSim.Runner.TickCap;
    // How far apart two opening positions may be before the difference means anything.
    //
    // A quarter of a world unit, derived from the simulation's own definition of one rather than
    // written down as a raw number. A hex is about 2.6 units across, so this is a tenth of a hex :
    // far below anything that could put a unit on the wrong tile, and four orders of magnitude
    // above the rounding difference this check used to fail on.
    //
    // It used to demand exact equality on every fixed-point digit. The game does not compute these
    // positions, it reads them from a table baked from floating point in the editor, so the mod's
    // computed answer differed by two to eleven raw units out of half a million. That is one part
    // in fifteen thousand of a hex, it never once changed a target or a destination, and it
    // switched the whole preview off three battles running on 2026-08-18 while telling the player
    // the game had been updated. CellWorldTable now reads the same table, so these should agree
    // exactly ; this tolerance is what stops the next rounding difference costing a feature.
    private static readonly long PositionToleranceRaw = FP._1.RawValue / 4;
    private readonly Bindings _bindings;
    private readonly Capabilities _capabilities;
    private readonly string _logPath;
    private PredictionResult _prediction;
    private readonly SortedDictionary<int, Dictionary<string, string>> _targets = new();
    private readonly SortedDictionary<int, Dictionary<string, Vector2Int>> _cells = new();
    private readonly Dictionary<string, string> _targetMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector2Int> _cellMap = new(StringComparer.Ordinal);
    // Native objects do not move, and Begin clears this before another battle can reuse a pointer.
    private readonly Dictionary<IntPtr, string> _entityIds = new();
    private Dictionary<string, FPVector3> _openingPositions;
    private float _resolutionStartedAt;
    private int _sampleTickCap;
    private int _lastFrame = -1;
    private int? _seedActual;
    private int? _firstRealDeathTick;
    private bool _sawResolvableTarget;
    private bool _bootAssertionCompleted;
    private int _suspectStrikes;

    public ModNotice UserNotice { get; private set; }

    public ParityGate(Bindings bindings, Capabilities capabilities, string userDataRoot)
    {
        // The loader's own data folder. Nothing the mod writes goes into the game's install
        // folder : on a player's machine that would leave our folders inside their game.
        _logPath = Path.Combine(userDataRoot, "GuildrunTargetingMod", "parity_log.jsonl");
        _bindings = bindings;
        _capabilities = capabilities;
    }

    public void Begin(PredictionResult prediction)
    {
        _prediction = prediction;
        _targets.Clear();
        _cells.Clear();
        _targetMap.Clear();
        _cellMap.Clear();
        _entityIds.Clear();
        _openingPositions = null;
        _resolutionStartedAt = Time.realtimeSinceStartup;
        _sampleTickCap = Math.Min(AbsoluteSampleTickCap,
            Math.Max(ExistingSampleTickCap, (prediction?.Ticks ?? 0) + SampleMargin));
        _lastFrame = -1;
        _seedActual = null;
        _firstRealDeathTick = null;
        _sawResolvableTarget = false;
        // After a strike the check has to actually run again next battle.
        if (_suspectStrikes > 0) _bootAssertionCompleted = false;
    }

    public void SampleResolution()
    {
        if (!_capabilities.CoreRead) return;
        try
        {
            if (!DataReaders.TryGet<BattleSimulationDataReader>(out var battle) || battle?.CurrentFrame?.Frame == null) return;
            // Once the fight is running, the run's current seed is the seed this battle was built
            // with. Reading it turns the check from "the openings looked the same" into a direct
            // comparison against the number the prediction computed.
            if (_seedActual == null && DataReaders.TryGet<RunSessionDataReader>(out var run) && run != null)
                _seedActual = run.CurrentSeed.CurrentValue;
            ReadOnlyFrame frame = battle.CurrentFrame;
            int tick = frame.Frame.FrameNumber;
            if (tick == _lastFrame) return;
            // Start the clock at the first sample that worked, not at the phase change. The phase
            // can turn over before any frame is readable, and on a resumed run it may not happen
            // at all.
            bool firstSample = _lastFrame == -1;
            if (firstSample) _resolutionStartedAt = Time.realtimeSinceStartup;
            _lastFrame = tick;
            // Past the sampling window nothing the walk below produces is read by any verdict, and
            // a battle runs far longer than the window, so it is skipped rather than computed and
            // discarded. The self-validation underneath still runs on every sample : it is gated on
            // elapsed SECONDS, and a battle watched at speed reaches the cap before five of them
            // have passed, so returning early here would leave it to the end-of-battle fallback.
            // The window is now decided ONCE, here, instead of by four copies of the same test
            // scattered through the walk. Everything below this brace is inside the window by
            // construction, which is what lets those copies go.
            if (tick <= _sampleTickCap)
            {
                _targetMap.Clear();
                _cellMap.Clear();
                // Only frame zero, never merely the first frame this happened to read. The frame
                // number starts at zero and is incremented by ticking, so frame zero is the board
                // after the game's combat-start abilities and before anyone has taken a step, which
                // is exactly the moment the prediction recorded. Sampling runs in LateUpdate and the
                // first readable frame is not guaranteed to be frame zero ; comparing a later one
                // against the prediction's opening would report a mismatch on a correct board and
                // fail the whole feature closed.
                Dictionary<string, FPVector3> positionMap = tick == 0
                    ? new Dictionary<string, FPVector3>(StringComparer.Ordinal)
                    : null;
                for (int i = 0; i < frame.Entities.Count; i++)
                {
                    Entity entity = frame.Entities[i];
                    if (entity == null) continue;
                    string id = EntityIdString(entity);
                    if (positionMap != null) positionMap[id] = entity.Transform.Position;
                    if (!_firstRealDeathTick.HasValue &&
                        (entity.Fsm.CurrentState == EntityState.Dead || !entity.IsAlive()))
                        _firstRealDeathTick = tick;
                    _cellMap[id] = entity.CellPosition;
                    if (!NullableRaw.TryReadEffectiveTarget(entity, out var target))
                    {
                        _targetMap[id] = null;
                        continue;
                    }
                    if (!frame.TryGetEntity(target, out Entity resolved) || resolved == null)
                    {
                        string reason = $"NullableRaw live decode fault: {target} from {id} did not resolve";
                        _capabilities.DisableCoreRead(reason);
                        _capabilities.DisablePrediction(reason);
                        UserNotice = ModNotice.ReadFailure;
                        return;
                    }
                    _sawResolvableTarget = true;
                    _targetMap[id] = EntityIdString(resolved);
                }
                if (positionMap != null) _openingPositions = positionMap;
                _targets[tick] = new Dictionary<string, string>(_targetMap, StringComparer.Ordinal);
                _cells[tick] = new Dictionary<string, Vector2Int>(_cellMap, StringComparer.Ordinal);
            }

            // A separate check on the raw memory reads themselves. If they were decoding
            // garbage, no unit would ever report a target that resolves to a real unit. But a
            // battle can genuinely open with nobody having picked yet, so this waits for real
            // evidence, sixty frames across five seconds with nothing resolvable, and even then
            // only counts a strike. An earlier version fired after two seconds on a single
            // sample and shut a whole session down over a resumed battle's opening frame.
            if (!_bootAssertionCompleted && _targets.Count >= 60 &&
                Time.realtimeSinceStartup - _resolutionStartedAt >= 5f)
            {
                _bootAssertionCompleted = true;
                if (_sawResolvableTarget)
                {
                    _suspectStrikes = 0;
                    MelonLogger.Msg("[TargetingMod] NullableRaw live self-validation PASS");
                }
                else RegisterSuspectStrike("no resolvable non-null live target in 60 samples over 5 s");
            }
        }
        catch (Exception e)
        {
            _capabilities.DisableCoreRead("live parity sampling failed: " + e);
        }
    }

    private string EntityIdString(Entity entity)
    {
        IntPtr pointer = entity.Pointer;
        if (_entityIds.TryGetValue(pointer, out string id)) return id;
        id = entity.Id.ToString();
        _entityIds[pointer] = id;
        return id;
    }

    public void FinishBattle()
    {
        // Two lists, and which one a disagreement lands in is the whole design of this gate.
        //
        // HARD is for the things the mod actually draws : the seed the fight runs on, who each unit
        // picks, and where everyone ends up. Those are claims made on screen, so a disagreement
        // there means the picture was wrong and the feature has to stop making it.
        //
        // NOTES are for everything else : evidence that was not gathered, a board that could not be
        // observed, and differences too small to mean anything. All of it is written to the log,
        // because a bug report is answered from that file alone, and none of it withholds anything
        // from the player.
        //
        // They used to be one list, and that is the defect this whole round is about. Missing
        // evidence and sub-hex rounding both read as "the mod disagreed with the game", which
        // turned the preview off and blamed a game update that had not happened.
        var hard = new List<string>();
        var notes = new List<string>();
        int matchedTicks = 0;
        string verdict;

        if (_prediction == null)
        {
            notes.Add("unavailable: no completed placement prediction at Resolution start");
            verdict = "unavailable";
        }
        else
        {
            // These two are the mod misbehaving rather than the game moving, and both mean the
            // playout that produced the picture was not the playout the code intended.
            if (_prediction.ImmunityWriteFailed)
                hard.Add("engine: damage immunity write failed during prediction");
            if (_prediction.ReplayDiverged)
                hard.Add("engine: death-prevention replay diverged");

            if (_targets.Count == 0)
            {
                notes.Add("unavailable: no live Resolution frames were sampled");
                verdict = hard.Count > 0 ? "mismatch" : "unavailable";
            }
            else
            {
                if (_seedActual.HasValue && _seedActual.Value != _prediction.Seed)
                    hard.Add($"seed: predicted {_prediction.Seed}, actual {_seedActual.Value}");
                // Absence of a frame-zero sample is not evidence of disagreement. Say so rather
                // than comparing against nothing, which would read every predicted unit as
                // missing from the live battle and fail a board that was in fact correct.
                if (_openingPositions != null)
                    CompareOpeningPositions(_prediction.OpeningPositions, _openingPositions, hard, notes);
                else
                    notes.Add("unsampled: frame zero was never read, opening positions not compared");
                Dictionary<string, string> first = FirstAcquiredTargetMap(_targets);
                CompareMaps("tick-1", _prediction.Tick0Targets, first, hard);
                foreach (var sample in _targets.Values)
                    if (MapsEqual(_prediction.Tick0Targets, sample)) matchedTicks++;

                bool deathWindowOpen = !_firstRealDeathTick.HasValue ||
                    _prediction.Ticks < _firstRealDeathTick.Value;
                bool settledComparable = _prediction.PreventedDeaths == 0 &&
                    !_prediction.DeathsUnprevented && deathWindowOpen &&
                    (!_firstRealDeathTick.HasValue || HasPreDeathSettleFrame());
                if (settledComparable)
                {
                    bool cellsSampled = CompareSettledCells(hard, notes);
                    bool targetsSampled = CompareSettledTargets(hard, notes);
                    // Sampled and agreeing is a full pass. Sampled and disagreeing is the one thing
                    // this gate exists to catch. Not sampled at all is neither, and calling it a
                    // disagreement is how a correct board used to fail : a playout that settles
                    // later than the sampling window reaches has simply not been checked.
                    verdict = hard.Count > 0 ? "mismatch"
                        : cellsSampled && targetsSampled ? "behavioral-pass"
                        : "partial-pass";
                }
                else
                {
                    string deathTick = _firstRealDeathTick?.ToString() ?? "null";
                    notes.Add(
                        $"not-comparable: settle tick {_prediction.Ticks}, first real death tick {deathTick}, " +
                        $"prevented deaths {_prediction.PreventedDeaths}, deaths allowed {_prediction.DeathsUnprevented}");
                    verdict = hard.Count == 0 ? "partial-pass" : "mismatch";
                }
            }
        }

        if (!_bootAssertionCompleted)
        {
            _bootAssertionCompleted = true;
            if (_sawResolvableTarget) _suspectStrikes = 0;
            else if (_targets.Count >= 30)
                RegisterSuspectStrike("Resolution ended without one resolvable non-null live target");
            // Too few samples is no evidence either way, so it is never a strike.
        }

        AppendVerdict(new
        {
            buildGuid = _bindings.BuildGuid,
            stage = StageIdentity.Read(),
            seedPredicted = _prediction?.Seed,
            seedActual = _seedActual,
            seedComparison = _seedActual.HasValue ? "direct" : "unavailable",
            predictionTicks = _prediction?.Ticks,
            preventedDeaths = _prediction?.PreventedDeaths,
            firstRealDeathTick = _firstRealDeathTick,
            stillMovingAtCap = _prediction?.StillMovingAtCap,
            immunityWriteFailed = _prediction?.ImmunityWriteFailed,
            replayDiverged = _prediction?.ReplayDiverged,
            matchedTicks,
            deathsUnprevented = _prediction?.DeathsUnprevented,
            mismatches = hard,
            notes,
            verdict,
            // State as this battle began, so a log read on its own shows whether the preview was
            // off and how close the mod was to switching it off.
            persistedFailure = _bindings.HasPersistedFailure,
            mismatchStreakBefore = _bindings.MismatchStreak,
            computedAt = DateTime.UtcNow
        });

        if (verdict == "behavioral-pass")
        {
            _bindings.RecordCleanBattle(fullyComparable: true);
            UserNotice = ModNotice.None;
            MelonLogger.Msg(_seedActual.HasValue
                ? "[TargetingMod] PARITY PASS: seed exact + opening matched"
                : "[TargetingMod] PARITY PASS: behavioral opening matched (seed unread)");
        }
        else if (verdict == "mismatch")
        {
            // One disagreement is not enough to take a working feature away.
            //
            // A single battle can disagree for reasons that are not the mod being wrong : a run
            // resumed mid fight, a board caught while the scene was still assembling, a sample
            // window that closed early. Two in a row is a pattern, and it is the same two-strike
            // rule the raw-read self-validation below already uses. Until the second one the
            // player keeps the preview and the log carries the warning.
            string reason = "shadow/live parity mismatch: " + string.Join(" | ", hard);
            if (_bindings.RecordParityFailure())
            {
                _capabilities.DisablePrediction(reason);
                UserNotice = ModNotice.Disagreement;
                MelonLogger.Error("[TargetingMod] !!! prediction disagreed with the live battle twice running, preview off: " + reason);
            }
            else MelonLogger.Warning("[TargetingMod] prediction disagreed with the live battle (strike 1, re-checking next battle): " + reason);
        }
        else if (verdict == "partial-pass")
        {
            // Everything observable agreed and nothing disagreed ; the settled board simply could
            // not be observed, because a unit died before it settled, or the prediction had to
            // prevent a death, or the board settled past the sampling window. That is missing
            // evidence, not contradicting evidence, so it clears a pending strike and puts a
            // switched-off preview back on.
            _bindings.RecordCleanBattle(fullyComparable: false);
            UserNotice = ModNotice.None;
            MelonLogger.Msg("[TargetingMod] PARITY PARTIAL PASS: opening matched, settled state not comparable");
        }

        _prediction = null;
        _targets.Clear();
        _cells.Clear();
        _openingPositions = null;
    }

    // Two strikes, not one. A single battle that never produced a readable target is weak
    // evidence, and acting on it costs the player the whole feature. Two in a row is a pattern.
    private void RegisterSuspectStrike(string what)
    {
        _suspectStrikes++;
        if (_suspectStrikes >= 2)
            _capabilities.DisableCoreRead("NullableRaw suspect in two consecutive battles: " + what);
        else
            MelonLogger.Warning("[TargetingMod] NullableRaw suspect (strike 1, re-checking next battle): " + what);
    }

    private void AppendVerdict(object row)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, JsonSerializer.Serialize(row) + Environment.NewLine);
        }
        catch (Exception e) { MelonLogger.Error("[TargetingMod] parity JSONL write failed: " + e); }
    }

    private bool CompareSettledCells(List<string> hard, List<string> notes)
    {
        Dictionary<string, Vector2Int> liveAtSettle = null;
        foreach (var pair in _cells)
            if (pair.Key >= _prediction.Ticks) { liveAtSettle = pair.Value; break; }
        if (liveAtSettle == null)
        {
            // Never checked, so nothing to disagree with. This used to count as a disagreement and
            // switch the preview off on a board whose only sin was settling slowly.
            notes.Add(
                $"unsampled: settle tick {_prediction.Ticks} is past the {_sampleTickCap}-tick cell sampling window");
            return false;
        }

        var keys = new HashSet<string>(_prediction.Settled.Keys, StringComparer.Ordinal);
        keys.UnionWith(liveAtSettle.Keys);
        foreach (string key in keys)
        {
            bool predicted = _prediction.Settled.TryGetValue(key, out SettledEntity expected);
            bool actual = liveAtSettle.TryGetValue(key, out Vector2Int value);
            if (!predicted)
                hard.Add($"settled cell @tick{_prediction.Ticks} {key}: missing from prediction, actual {value.x},{value.y}");
            else if (!actual)
                hard.Add($"settled cell @tick{_prediction.Ticks} {key}: predicted {expected.Cell.x},{expected.Cell.y}, missing from live battle");
            else if (expected.Cell != value)
                hard.Add($"settled cell @tick{_prediction.Ticks} {key}: predicted {expected.Cell.x},{expected.Cell.y}, actual {value.x},{value.y}");
        }
        return true;
    }

    private bool HasPreDeathSettleFrame()
    {
        foreach (int tick in _cells.Keys)
            if (tick >= _prediction.Ticks && tick < _firstRealDeathTick.Value) return true;
        return false;
    }

    // The settled pairings are what every arrow on screen actually claims. This comparison has
    // now passed repeatedly in real play, so a disagreement fails closed like every other claim.
    private bool CompareSettledTargets(List<string> hard, List<string> notes)
    {
        Dictionary<string, string> liveAtSettle = null;
        foreach (var pair in _targets)
            if (pair.Key >= _prediction.Ticks) { liveAtSettle = pair.Value; break; }
        if (liveAtSettle == null)
        {
            notes.Add(
                $"unsampled: settle tick {_prediction.Ticks} is past the {_sampleTickCap}-tick target sampling window");
            return false;
        }

        var keys = new HashSet<string>(_prediction.Settled.Keys, StringComparer.Ordinal);
        keys.UnionWith(liveAtSettle.Keys);
        foreach (string key in keys)
        {
            bool predicted = _prediction.Settled.TryGetValue(key, out SettledEntity expected);
            bool actualPresent = liveAtSettle.TryGetValue(key, out string actual);
            string note = null;
            if (!predicted)
                note = $"settled target @tick{_prediction.Ticks} {key}: missing from prediction, actual {actual ?? "null"}";
            else if (!actualPresent)
                note = $"settled target @tick{_prediction.Ticks} {key}: predicted {expected.TargetPairing ?? "null"}, missing from live battle";
            else if (!string.Equals(expected.TargetPairing, actual, StringComparison.Ordinal))
                note = $"settled target @tick{_prediction.Ticks} {key}: predicted {expected.TargetPairing ?? "null"}, actual {actual ?? "null"}";
            if (note == null) continue;
            notes.Add(note);
            hard.Add(note);
        }
        return true;
    }

    private static void CompareMaps(string label, Dictionary<string, string> expected, Dictionary<string, string> actual, List<string> mismatches)
    {
        var keys = new HashSet<string>(expected.Keys, StringComparer.Ordinal);
        keys.UnionWith(actual.Keys);
        foreach (string key in keys)
        {
            bool predicted = expected.TryGetValue(key, out string expectedValue);
            bool actualPresent = actual.TryGetValue(key, out string actualValue);
            if (!predicted)
                mismatches.Add($"{label} {key}: missing from prediction, actual {actualValue ?? "null"}");
            else if (!actualPresent)
                mismatches.Add($"{label} {key}: predicted {expectedValue ?? "null"}, missing from live battle");
            else if (!string.Equals(expectedValue, actualValue, StringComparison.Ordinal))
                mismatches.Add($"{label} {key}: predicted {expectedValue ?? "null"}, actual {actualValue ?? "null"}");
        }
    }

    // Whether every unit started the fight where the mod said it would.
    //
    // A unit present in one board and not the other is a real disagreement : the mirror built a
    // different fight. A unit in both, standing further apart than a tenth of a hex, is also real :
    // it is on the wrong tile. A unit in both and a few ten-thousandths of a unit apart is
    // arithmetic, is invisible, and is recorded rather than acted on. See PositionToleranceRaw.
    private static void CompareOpeningPositions(
        Dictionary<string, FPVector3> expected,
        Dictionary<string, FPVector3> actual,
        List<string> hard,
        List<string> notes)
    {
        expected ??= new Dictionary<string, FPVector3>(StringComparer.Ordinal);
        actual ??= new Dictionary<string, FPVector3>(StringComparer.Ordinal);
        var keys = new HashSet<string>(expected.Keys, StringComparer.Ordinal);
        keys.UnionWith(actual.Keys);
        foreach (string key in keys)
        {
            bool predicted = expected.TryGetValue(key, out FPVector3 expectedValue);
            bool actualPresent = actual.TryGetValue(key, out FPVector3 actualValue);
            if (!predicted)
            {
                hard.Add($"opening position {key}: missing from prediction, actual {FormatRaw(actualValue)}");
                continue;
            }
            if (!actualPresent)
            {
                hard.Add($"opening position {key}: predicted {FormatRaw(expectedValue)}, missing from live battle");
                continue;
            }
            long drift = LargestComponentDrift(expectedValue, actualValue);
            if (drift == 0) continue;
            string line = $"opening position {key}: predicted {FormatRaw(expectedValue)}, actual {FormatRaw(actualValue)}, drift {drift} raw";
            if (drift >= PositionToleranceRaw) hard.Add(line);
            else notes.Add("within tolerance: " + line);
        }
    }

    private static long LargestComponentDrift(FPVector3 left, FPVector3 right)
    {
        long x = Math.Abs(left.X.RawValue - right.X.RawValue);
        long y = Math.Abs(left.Y.RawValue - right.Y.RawValue);
        long z = Math.Abs(left.Z.RawValue - right.Z.RawValue);
        return Math.Max(x, Math.Max(y, z));
    }

    private static string FormatRaw(FPVector3 value) =>
        $"{value.X.RawValue},{value.Y.RawValue},{value.Z.RawValue}";

    private static Dictionary<string, string> FirstAcquiredTargetMap(
        SortedDictionary<int, Dictionary<string, string>> values)
    {
        Dictionary<string, string> first = null;
        foreach (var pair in values)
        {
            first ??= pair.Value;
            foreach (string target in pair.Value.Values)
                if (!string.IsNullOrEmpty(target)) return pair.Value;
        }
        return first ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static bool MapsEqual(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        if (expected.Count != actual.Count) return false;
        foreach (var pair in expected)
            if (!actual.TryGetValue(pair.Key, out string value) || !string.Equals(pair.Value, value, StringComparison.Ordinal))
                return false;
        return true;
    }
}
