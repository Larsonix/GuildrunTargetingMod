using System;
using System.Collections.Generic;
using System.Diagnostics;
using GuildrunTargetingMod.Interop;
using Il2CppEmber.Balancing;
using Il2CppEmber.Balancing.Sheets.Characters.Attacks;
using Il2CppEmber.Balancing.Sheets.Items;
using Il2CppEmber.Balancing.SimulationBridge;
using Il2CppEmber.Balancing.SimulationBridge.Context;
using Il2CppEmber.Simulation.Core;
using Il2CppEmber.Simulation.Core.Config;
using Il2CppEmber.Simulation.Core.Fsm;
using Il2CppEmber.Simulation.Core.State;
using Il2CppEmber.Simulation.Core.State.Entities;
using Il2CppEmber.Simulation.Core.Utilities;
using Il2Cppgg.leyline.netcode.Utilities;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;
using FP = Il2CppPhoton.Deterministic.FP;
using FPVector3 = Il2CppPhoton.Deterministic.FPVector3;

namespace GuildrunTargetingMod.ShadowSim;

// Plays one board out in the game's own simulation, a slice of ticks per frame, until every
// living unit has reached the hex it will fight from. The mod never reimplements a targeting
// rule : it hands the board to the code that will run the fight and reads back the result.
//
// The simulation runs in a frame of its own that is thrown away afterwards, so nothing here can
// reach the live battle.
internal sealed class Runner
{
    internal const int TickCap = 600;
    // Two replays per unit that started the fight alive, and a small margin. That bound is not a
    // guess : a unit costs one replay to protect from the tick it died on, and at most one more to
    // escalate to protection from the opening, after which no later board can kill it. See AddGrant.
    //
    // It replaced a flat 32 that was reached thirteen times in one session, each time after
    // thirty-three whole playouts, each time producing nothing at all.
    //
    // [2026-08-19] The two-step escalation the paragraph above describes is GONE : the first death
    // now grants protection from the opening directly, so a unit costs at most ONE replay and this
    // cap stops being the mechanism. It is deliberately left at the same value as a pure backstop.
    // The reasoning is kept in full because it is why the number is what it is, and because the
    // failure it records is the one that comes back if anyone ever lowers the grant to a tick again.
    private const int ReplaysPerUnit = 2;
    private const int ReplayMargin = 4;
    private const int StaleOffsetTicks = 30;
    // The simulation's own fixed step, in its fixed-point format : 2184 / 65536 second, which is
    // the 30 ticks per second the battle runs at. Feeding any other value would play out a fight
    // the game never runs.
    private static readonly FP TickDelta = FP.FromRaw(2184);
    // This only has to outlast the 600-tick playout. Keeping it far above that also makes the
    // intent obvious if the cap changes later.
    private static readonly FP ImmunityDuration = FP.FromRaw(100000L * 65536L);
    private readonly Capabilities _capabilities;
    private readonly Dictionary<string, int> _immunityGrants = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Dictionary<string, Vector2Int>> _replayCheckpoints = new();
    private readonly Dictionary<string, OffsetObservation> _offsetObservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector2Int> _previousTickCells = new(StringComparer.Ordinal);
    // BeginRun clears this because every replay owns a newly initialized frame and entity set.
    private readonly Dictionary<IntPtr, string> _entityIds = new();
    private string _lastExtraFault;
    private NoOpErrorReporter _reporterRoot;
    private IErrorReporting _reporter;
    private SimulationReferences _references;
    private BattleConfig _config;
    private BattleSimulation _simulation;
    private HashSet<string> _initiallyAlive;
    private string _configHash;
    private string _cacheKey;
    private int _seed;
    private int _ticks;
    private int _replays;
    private bool _settled;
    private bool _immunityWriteFailed;
    private bool _replayDiverged;
    // Raised when a tick threw the playout away and rebuilt it. Read by TickSlice, which stops for
    // the frame rather than starting another restart it has no time left for.
    private bool _restartedThisSlice;
    private bool _deathsUnprevented;
    private PlacementSnapshot _placement;
    private bool _placementFaulted;
    private Dictionary<string, Vector2Int> _openingCells;
    private Dictionary<string, FPVector3> _openingPositions;
    private Dictionary<string, string> _tick0Targets;

    public PredictionResult Result { get; private set; }
    public bool IsRunning => _simulation != null;

    /// <summary>
    /// What this board does for the parts that care where units stand, available from the moment
    /// the opening frame exists rather than when the playout ends.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the marks feel instant. The answer is taken at the opening frame,
    /// before a single tick has run, and it used to be carried out only inside the finished result,
    /// so a question already answered waited for a hundred and fifty ticks of walking about before
    /// anyone could read it. On a board where the playout produced nothing at all it was never
    /// readable, and the icons kept showing the previous hex.
    ///
    /// Null while no run is in flight, which is what makes the reader fall through to the last
    /// finished playout's copy of the same answer.
    /// </remarks>
    public PlacementSnapshot OpeningPlacement { get; private set; }

    public Runner(Capabilities capabilities) => _capabilities = capabilities;

    /// <summary>
    /// How many units each unit's attack lands on. Set by the mod, like the overlay's own answers
    /// are ; null leaves every unit single-target, which is what it was before this existed and is
    /// still correct for almost all of them.
    /// </summary>
    public Func<string, MultiHit.Rule> Rules { get; set; }

    /// <summary>
    /// Asked once per playout, at the opening frame, what this board does for the parts that care
    /// where units stand. Set by the mod ; null leaves the answer off the result entirely.
    /// </summary>
    /// <remarks>
    /// This is the one thing the runner is asked that is not about the prediction itself, and it is
    /// here rather than outside because this is the only place the hypothetical board exists as a
    /// frame. It must never be allowed to affect the playout : it is called after the opening has
    /// been captured, it is wrapped so a fault cannot escape into the run, and what it returns is
    /// carried out on the result and nothing more.
    /// </remarks>
    public Func<Frame, PlacementSnapshot> OpeningEvaluator { get; set; }

    public void Start(BattleConfig config, string configHash, string cacheKey)
    {
        EnsureReporter();
        var guidFactory = new GuidFactory(null).TryCast<IGuidFactory>();
        if (guidFactory == null) throw new InvalidOperationException("GuidFactory(null) did not cast to IGuidFactory");

        // Without a network session this factory falls back to Guid.NewGuid, so ids minted during
        // the playout are not reproducible run to run. That is safe here : the ids minted once a
        // fight is under way belong to spawned effects, and unit against unit targeting never
        // reads them.
        _references = new SimulationReferences
        {
            Balancing = EmberBalancing.Instance,
            ErrorReporting = _reporter,
            GuidFactory = guidFactory
        };
        _config = config;
        _configHash = configHash;
        _cacheKey = cacheKey;
        _seed = config.Seed;
        _immunityGrants.Clear();
        _replayCheckpoints.Clear();
        _initiallyAlive = null;
        _replays = 0;
        _immunityWriteFailed = false;
        _replayDiverged = false;
        _deathsUnprevented = false;
        _placement = null;
        OpeningPlacement = null;
        Result = null;
        BeginRun(captureInitialEntities: true);
    }

    public void TickSlice(float budgetMs)
    {
        if (_simulation == null) return;
        double budget = Math.Max(0.1, budgetMs);
        long start = Stopwatch.GetTimestamp();
        _restartedThisSlice = false;
        while (_simulation != null && !_restartedThisSlice &&
               (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency < budget)
            TickOne();
    }

    public void Cancel()
    {
        _simulation = null;
        _offsetObservations.Clear();
        // Belongs to the board that was abandoned. A later run that finishes early reads this
        // field, and without clearing it here that run would carry the previous board's answer.
        _placement = null;
        OpeningPlacement = null;
        // A cancelled run yields nothing. Without this, switching to a cached layout, which never
        // calls Start, would read the previous run's result as if it were fresh and overwrite the
        // newer cached prediction one frame later.
        Result = null;
    }

    private void BeginRun(bool captureInitialEntities)
    {
        _entityIds.Clear();
        _simulation = new BattleSimulation();
        _simulation.Initialize(_references, _config);
        Frame frame = _simulation.CurrentFrame;
        if (captureInitialEntities)
        {
            _initiallyAlive = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < frame.Entities.Count; i++)
            {
                Entity entity = frame.Entities[i];
                if (entity != null && entity.IsAlive()) _initiallyAlive.Add(entity.Id.ToString());
            }
        }

        _ticks = 0;
        _settled = false;
        _offsetObservations.Clear();
        _tick0Targets = null;
        // No grant can exist at tick 0, but keeping this symmetrical with the per-tick path
        // makes every run start by applying any grants scheduled for its current boundary.
        if (!ApplyGrants(frame, 0))
        {
            if (captureInitialEntities)
            {
                _openingCells = CaptureCells(frame.Entities);
                _openingPositions = CapturePositions(frame.Entities);
            }
            Finish(false);
            return;
        }
        // Initialize has already run the game's combat-start abilities, so this records where
        // anyone thrown across the board before the first tick landed.
        if (captureInitialEntities)
        {
            _openingCells = CaptureCells(frame.Entities);
            _openingPositions = CapturePositions(frame.Entities);
        }
        // Only on the real run. A replay re-plays the same board to hold a unit alive, so asking
        // again would recompute an identical answer several times for one drag.
        if (captureInitialEntities) AskPlacement(frame);
    }

    // The placement answer for THIS board, taken at the opening and never later. A rule judged
    // where a unit ran to is one the player cannot fix by moving anything, which is the one thing
    // the marks refuse to report.
    //
    // Wrapped tightly on purpose. The prediction is the thing this class exists for, and a fault in
    // an annotation on top of it must not cost the playout it is annotating.
    private void AskPlacement(Frame frame)
    {
        if (OpeningEvaluator == null) return;
        try
        {
            using (Perf.Measure(PerfSlot.OpeningSnapshot))
                _placement = OpeningEvaluator(frame);
            // Published here rather than at the end of the playout. It is already the final answer
            // for this board : it was taken at the opening frame and nothing later changes it.
            OpeningPlacement = _placement;
        }
        catch (Exception e)
        {
            _placement = null;
            OpeningPlacement = null;
            if (_placementFaulted) return;
            _placementFaulted = true;
            MelonLogger.Warning("[TargetingMod] placement marks could not read a predicted board; " +
                                "the prediction itself is unaffected. Full fault follows:\n" + e);
        }
    }

    private void TickOne()
    {
        Frame frame = _simulation.CurrentFrame;
        if (frame == null || !frame.IsRunning || _ticks >= TickCap)
        {
            Finish(_ticks >= TickCap && !_settled);
            return;
        }

        int nextTick = _ticks + 1;
        CaptureCells(frame.Entities, _previousTickCells);
        if (_replayCheckpoints.TryGetValue(nextTick, out Dictionary<string, Vector2Int> expected) &&
            !CellsEqual(expected, _previousTickCells))
        {
            _replayDiverged = true;
            MelonLogger.Error($"[TargetingMod] shadow replay diverged before tick {nextTick}");
        }
        if (!ApplyGrants(frame, nextTick))
        {
            // The frame is still exactly T - 1 because the lethal tick has not run yet.
            Finish(false);
            return;
        }

        _simulation.Tick(TickDelta);
        _simulation.FinishFrameExecution();
        _ticks++;
        frame = _simulation.CurrentFrame;

        // Ask only the dead. Turning an entity id into a string is a native call that allocates,
        // and on the overwhelming majority of ticks nobody has died, so paying it per unit per
        // tick was buying an answer we almost always already knew. Reading IsAlive is a plain
        // field test, so the normal path now costs no allocation at all.
        List<string> deaths = null;
        for (int i = 0; i < frame.Entities.Count; i++)
        {
            Entity entity = frame.Entities[i];
            if (entity == null || entity.IsAlive()) continue;
            string id = EntityIdString(entity);
            if (!_initiallyAlive.Contains(id)) continue;
            deaths ??= new List<string>();
            if (!deaths.Contains(id)) deaths.Add(id);
        }
        if (deaths != null && !_deathsUnprevented)
        {
            if (!_replayCheckpoints.ContainsKey(_ticks))
                _replayCheckpoints[_ticks] = new Dictionary<string, Vector2Int>(
                    _previousTickCells, StringComparer.Ordinal);
            for (int i = 0; i < deaths.Count; i++) AddGrant(deaths[i], _ticks);
            _replays++;
            if (_replays > ReplayCap)
            {
                // Out of budget. Stop holding anyone up and play the board out the way it really
                // goes, which is still a real answer : the marks and the arrows describe the
                // opening, which is all they ever claimed, and the settled half is reported as not
                // comparable rather than not produced.
                //
                // Returning nothing was the worse choice and it is what used to happen. Thirteen
                // times in one session this point was reached, and each time the board being
                // dragged onto got no answer at all, so the icons went on describing the hex the
                // player had already left.
                _deathsUnprevented = true;
                _immunityGrants.Clear();
                _replayCheckpoints.Clear();
                MelonLogger.Warning(
                    $"[TargetingMod] shadow replay budget of {ReplayCap} spent on config {_configHash[..12]}; " +
                    "playing this board out with deaths allowed");
            }
            // Ends this frame's slice, whatever is left of it. A replay is not a tick: it throws the
            // playout away and builds the whole battle again, which is one call into the game's own
            // code and about as expensive as starting the prediction from scratch. The slice checks
            // its clock BEFORE each tick, so a replay begun with a fraction of a millisecond left
            // still runs to completion and overshoots by all of it, and several in a row overshoot
            // by all of them. Measured on the reference machine: 14.16 ms spent against a 6 ms
            // budget. Bounding it to one restart per frame spreads a board that keeps killing
            // somebody across frames instead of stalling one, which is the trade this whole cycle
            // is built on: longer is acceptable, a dropped frame is not.
            _restartedThisSlice = true;
            BeginRun(captureInitialEntities: false);
            return;
        }

        if (_ticks == 1) _tick0Targets = CaptureTargets(frame);
        if (_ticks >= 1 && BoardArrived(frame))
        {
            _settled = true;
            Finish(false);
        }
        else if (!frame.IsRunning) Finish(false);
    }

    private bool BoardArrived(Frame frame)
    {
        bool arrived = true;
        for (int i = 0; i < frame.Entities.Count; i++)
        {
            Entity entity = frame.Entities[i];
            if (entity == null || !entity.IsAlive()) continue;
            bool offsetSettled = VisualOffsetSettled(entity);
            EntityState state = entity.Fsm.CurrentState;
            bool pathMissingOrRemaining = !frame.PathfindingDataCache.TryGetValue(
                entity.Id, out var pathfindingData) || pathfindingData.HasRemainingPath;
            if (entity.IsDisplacing || state == EntityState.Displaced ||
                state == EntityState.MoveToTarget ||
                entity.GetStatusStackCount(StatusType.Stun) > 0 ||
                pathMissingOrRemaining || !offsetSettled)
                arrived = false;
        }
        return arrived;
    }

    private bool VisualOffsetSettled(Entity entity)
    {
        string id = EntityIdString(entity);
        if (!NullableRaw.TryReadVisualOffset(entity, out var offset) ||
            (offset.X.RawValue == 0 && offset.Y.RawValue == 0 && offset.Z.RawValue == 0))
        {
            _offsetObservations.Remove(id);
            return true;
        }
        if (entity.IsDisplacing)
        {
            _offsetObservations.Remove(id);
            return false;
        }

        var current = new OffsetObservation(offset.X.RawValue, offset.Y.RawValue, offset.Z.RawValue, 1);
        if (_offsetObservations.TryGetValue(id, out OffsetObservation previous) &&
            previous.X == current.X && previous.Y == current.Y && previous.Z == current.Z)
            current.Count = previous.Count + 1;
        _offsetObservations[id] = current;
        return current.Count >= StaleOffsetTicks;
    }

    private bool ApplyGrants(Frame frame, int tick)
    {
        foreach (var grant in _immunityGrants)
        {
            if (grant.Value != tick) continue;
            Entity entity = null;
            for (int i = 0; i < frame.Entities.Count; i++)
            {
                Entity candidate = frame.Entities[i];
                if (candidate != null && string.Equals(
                        candidate.Id.ToString(), grant.Key, StringComparison.Ordinal))
                {
                    entity = candidate;
                    break;
                }
            }
            if (entity == null)
                return ImmunityWriteFailure(grant.Key, tick, "entity was missing");
            EntityId id = entity.Id;
            // "No source" cannot be written as null here, and writing it as null is what has been
            // switching the preview off.
            //
            // A nullable parameter crosses the interop boundary as a BOXED value, and the generated
            // call unboxes it without checking : it reads
            // il2cpp_object_unbox(Il2CppObjectBaseToPtrNotNull(sourceId)), so a null argument
            // reaches a method whose whole job is to reject null. An EMPTY nullable says the same
            // thing and is a real object to unbox. Native code has no null nullable to begin with :
            // there it is a value type carrying a flag.
            //
            // It stayed hidden because a grant only exists on a board where somebody would die
            // before it settles. So it threw rarely, deep into a placement, and surfaced as
            // "shadow runner failed" with no line and a notice blaming a game update.
            frame.BattleContext.ApplyAdditionalStatusStack(
                default(EffectId), new Il2CppSystem.Nullable<EntityId>(), id,
                StatusType.DamageImmunity, 1, ImmunityDuration);
            if (entity.GetStatusStackCount(StatusType.DamageImmunity) <= 0)
                return ImmunityWriteFailure(grant.Key, tick, "status did not read back");
        }
        return true;
    }

    private bool ImmunityWriteFailure(string id, int tick, string detail)
    {
        _immunityWriteFailed = true;
        string reason = $"damage immunity write failed for {id} before tick {tick}: {detail}";
        MelonLogger.Error("[TargetingMod] " + reason);
        _capabilities.DisablePrediction(reason);
        return false;
    }

    // Protect a unit from the tick it died on, and if that was not enough, from the opening.
    //
    // The obvious rule is to keep the earliest death tick seen, and that is what this did. It does
    // not converge : each lowering is a whole replay, the next board can kill the same unit earlier
    // again, and one board reached thirty-three playouts that way. Escalating instead bounds the
    // whole thing, because protection from the opening is something no later board can undo. Two
    // replays per unit, and then the algorithm is out of moves by construction rather than by cap.
    // [2026-08-19] The paragraph above describes how this USED to work, and is kept because its
    // reasoning is still the reason this method exists at all. What changed : it no longer grants
    // from the death tick and then escalates on a repeat. It grants from the opening the first
    // time, which is where the escalation was always going to end up anyway.
    //
    // The escalation was correct and it was slow. Each step was a WHOLE replay, and a replay is the
    // game's complete battle construction, so a board where three units died in three different
    // rounds paid for three playouts to reach a state one playout could have produced. Granting
    // from the opening immediately makes the number of replays the number of ROUNDS in which new
    // units die, which is one or two, instead of the number of death events.
    //
    // Nothing about the resulting picture changes : protection from the opening is exactly what the
    // old rule converged to, and the parity gate compares the finished prediction against the real
    // battle on every fight, which is what proves it.
    private void AddGrant(string id, int tick)
    {
        // The death tick remains in the signature and at the call site deliberately, but is no
        // longer read: the first death now grants protection from the opening.
        if (!_immunityGrants.ContainsKey(id)) _immunityGrants[id] = 0;
    }

    private int ReplayCap => Math.Max(8, (_initiallyAlive?.Count ?? 8) * ReplaysPerUnit + ReplayMargin);

    private int CountGrants() => _immunityGrants.Count;

    private void Finish(bool movingAtCap)
    {
        Frame frame = _simulation?.CurrentFrame;
        var settled = new Dictionary<string, SettledEntity>(StringComparer.Ordinal);
        if (frame != null)
        {
            var targets = CaptureTargets(frame);
            for (int i = 0; i < frame.Entities.Count; i++)
            {
                Entity entity = frame.Entities[i];
                if (entity == null) continue;
                string id = entity.Id.ToString();
                targets.TryGetValue(id, out string pairing);
                MultiHit.Rule rule = Rules != null ? Rules(id) : default;
                settled[id] = new SettledEntity
                {
                    Cell = entity.CellPosition,
                    TargetPairing = pairing,
                    Alive = entity.IsAlive(),
                    ExtraTargets = CaptureExtraTargets(frame, entity, rule, pairing),
                    SplashesAroundTarget = rule.SplashesAroundTarget
                };
            }
        }
        Result = new PredictionResult
        {
            ConfigHash = _configHash,
            CacheKey = _cacheKey,
            Seed = _seed,
            OpeningCells = _openingCells ?? new Dictionary<string, Vector2Int>(StringComparer.Ordinal),
            OpeningPositions = _openingPositions ?? new Dictionary<string, FPVector3>(StringComparer.Ordinal),
            Tick0Targets = _tick0Targets ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Settled = settled,
            BoardWidth = _config != null ? _config.BoardWidth : 0,
            BoardHeight = _config != null ? _config.BoardHeight : 0,
            StillMovingAtCap = movingAtCap,
            Ticks = _ticks,
            PreventedDeaths = CountGrants(),
            ImmunityWriteFailed = _immunityWriteFailed,
            ReplayDiverged = _replayDiverged,
            DeathsUnprevented = _deathsUnprevented,
            Placement = _placement
        };
        _placement = null;
        OpeningPlacement = null;
        _simulation = null;
        _offsetObservations.Clear();
    }

    /// <summary>
    /// Everyone else this unit's attack lands on, at the settled instant, or null when it lands on
    /// one thing like almost every attack in this game.
    /// </summary>
    /// <remarks>
    /// The membership test is the simulation's OWN <c>HexGridUtils.Distance</c>, not a distance
    /// written here that happens to agree today. Hex distance on an offset grid is not the obvious
    /// formula, the game already ships the answer, and a second implementation is a second thing to
    /// drift.
    ///
    /// Range is read as the LIVE stat rather than the hero's authored one, because a rank modifier
    /// or a specialization that grants Attack Range is exactly the case where a static number would
    /// quietly under-report the picture. Ming's rule is a distance of exactly one and does not read
    /// range at all, which is why the two are separate cases rather than one with a parameter.
    /// </remarks>
    private IReadOnlyList<string> CaptureExtraTargets(Frame frame, Entity entity, MultiHit.Rule rule, string primary)
    {
        if (rule.IsPlain) return null;
        List<string> extras = null;
        try
        {
            bool sourceIsHero = entity.IsHero;
            // Two independent mechanisms, and a unit could in principle carry both, so both are
            // gathered into one list rather than one being treated as a variant of the other.
            //
            // Reach is measured from the UNIT : that is what FunkeAbilityAction, TillyAction and
            // MingAbilityAction each do. Splash is measured from the unit it is ATTACKING, at a
            // distance of exactly one, which is IsAdjacentToTriggerTargetCondition's own test read
            // literally. Exactly one, not within one : the thing being hit is already hit by the
            // ordinary attack and is not part of the splash.
            bool hasReach = rule.Reach != MultiHit.Reach.Single;
            bool neighboursOnly = rule.Reach == MultiHit.Reach.Neighbours;
            int range = !hasReach || neighboursOnly ? 1 : entity.GetStat(TargetStatType.AttackRange).IntValue;

            bool hasSplashOrigin = false;
            Vector2Int splashOrigin = default;
            if (rule.SplashesAroundTarget && primary != null)
                hasSplashOrigin = TryCellOf(frame, primary, out splashOrigin);

            for (int i = 0; i < frame.Entities.Count; i++)
            {
                Entity other = frame.Entities[i];
                if (other == null || other.Pointer == entity.Pointer) continue;
                // The other side only. Every rule behind this walks the caster's enemies.
                if (other.IsHero == sourceIsHero || !other.IsAlive()) continue;
                string id = EntityIdString(other);
                // The paired target already has an arrow of its own ; this list is the ones that
                // would otherwise be invisible.
                if (string.Equals(id, primary, StringComparison.Ordinal)) continue;

                bool hit = false;
                if (hasReach)
                {
                    int fromUnit = HexGridUtils.Distance(other.CellPosition, entity.CellPosition);
                    hit = neighboursOnly ? fromUnit == 1 : fromUnit <= range;
                }
                if (!hit && hasSplashOrigin)
                    hit = HexGridUtils.Distance(other.CellPosition, splashOrigin) == 1;
                if (!hit) continue;
                (extras ??= new List<string>()).Add(id);
            }
        }
        catch (Exception e)
        {
            // A unit whose extra reach cannot be read falls back to its single arrow, which is what
            // it had before. Nothing about the rest of the picture depends on this.
            if (_lastExtraFault != e.Message)
            {
                _lastExtraFault = e.Message;
                MelonLogger.Warning("[TargetingMod] extra targets unreadable for one unit: " + e.Message);
            }
            return null;
        }
        return extras;
    }

    /// <summary>The hex a unit is standing on this frame, by its id.</summary>
    private bool TryCellOf(Frame frame, string id, out Vector2Int cell)
    {
        cell = default;
        for (int i = 0; i < frame.Entities.Count; i++)
        {
            Entity entity = frame.Entities[i];
            if (entity == null) continue;
            if (!string.Equals(EntityIdString(entity), id, StringComparison.Ordinal)) continue;
            cell = entity.CellPosition;
            return true;
        }
        return false;
    }

    private static bool CellsEqual(
        Dictionary<string, Vector2Int> left, Dictionary<string, Vector2Int> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var pair in left)
            if (!right.TryGetValue(pair.Key, out Vector2Int cell) || cell != pair.Value) return false;
        return true;
    }

    private Dictionary<string, string> CaptureTargets(Frame frame)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < frame.Entities.Count; i++)
        {
            Entity entity = frame.Entities[i];
            if (entity == null) continue;
            string id = EntityIdString(entity);
            if (!NullableRaw.TryReadEffectiveTarget(entity, out var target))
            {
                result[id] = null;
                continue;
            }
            if (!frame.TryGetEntity(target, out Entity resolved) || resolved == null)
            {
                string reason = $"NullableRaw decoded non-resolving target {target} for {id}";
                _capabilities.DisableCoreRead(reason);
                _capabilities.DisablePrediction(reason);
                throw new InvalidOperationException(reason);
            }
            result[id] = EntityIdString(resolved);
        }
        return result;
    }

    private Dictionary<string, Vector2Int> CaptureCells(Il2CppSystem.Collections.Generic.List<Entity> entities)
    {
        var result = new Dictionary<string, Vector2Int>(StringComparer.Ordinal);
        CaptureCells(entities, result);
        return result;
    }

    private void CaptureCells(
        Il2CppSystem.Collections.Generic.List<Entity> entities,
        Dictionary<string, Vector2Int> result)
    {
        result.Clear();
        for (int i = 0; i < entities.Count; i++)
            if (entities[i] is Entity entity) result[EntityIdString(entity)] = entity.CellPosition;
    }

    private Dictionary<string, FPVector3> CapturePositions(
        Il2CppSystem.Collections.Generic.List<Entity> entities)
    {
        var result = new Dictionary<string, FPVector3>(StringComparer.Ordinal);
        for (int i = 0; i < entities.Count; i++)
            if (entities[i] is Entity entity) result[EntityIdString(entity)] = entity.Transform.Position;
        return result;
    }

    private string EntityIdString(Entity entity)
    {
        IntPtr pointer = entity.Pointer;
        if (_entityIds.TryGetValue(pointer, out string id)) return id;
        id = entity.Id.ToString();
        _entityIds[pointer] = id;
        return id;
    }

    private void EnsureReporter()
    {
        if (_reporter != null) return;
        _reporterRoot = NoOpErrorReporter.CreateRegistered();
        _reporter = _reporterRoot.TryCast<IErrorReporting>();
        if (_reporter == null) throw new InvalidCastException("injected reporter did not cast to IErrorReporting");
        // Keep both wrappers alive. The garbage collector cannot see that native simulation code
        // holds this object through its interface pointer alone, so without a rooted reference it
        // is free to collect an object the game is still calling into.
        GC.KeepAlive(_reporterRoot);
    }

    private struct OffsetObservation
    {
        public long X;
        public long Y;
        public long Z;
        public int Count;

        public OffsetObservation(long x, long y, long z, int count)
        {
            X = x;
            Y = y;
            Z = z;
            Count = count;
        }
    }
}
