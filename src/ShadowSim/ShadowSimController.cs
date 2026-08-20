using System;
using System.Collections.Generic;
using System.Linq;
using GuildrunTargetingMod.Ui;
using Il2CppEmber.Simulation.Core.Config;
using Il2CppEmber.Simulation.Core.State;
using MelonLoader;
using UnityEngine;

namespace GuildrunTargetingMod.ShadowSim;

// Owns the prediction for the current board : decides when it is worth rebuilding, spends a
// slice of each frame ticking it, and hands the finished playout to the renderer.
internal sealed class ShadowSimController
{
    // How many finished playouts to keep. Sized for a whole placement's worth of dragging a hero
    // around the board. Keys are derived from the board's own contents, so an old entry can never
    // be served for a different layout. Cleared when placement ends.
    private const int CacheCap = 64;
    private const double BudgetFloorMs = 0.5;
    // How long a candidate hex has to be held before it is worth building a battle for. See the
    // long note at the dwell test itself.
    // Reported from play as too long at eighty milliseconds, and halved. The wait the owner felt
    // was mostly NOT this: the budget was misreading a capped frame as having no room and deferring
    // the rebuild for up to a quarter of a second on top. That is fixed in FrameBudget. This is
    // shortened as well because forty milliseconds still filters a hex the cursor is only crossing,
    // and the whole point of the dwell is to skip work nobody asked for rather than to make anybody
    // wait for work they did.
    private const float DebounceSeconds = 0.04f;
    // The unconditional re-check, whatever the cheap change tests believe. Twice a second costs
    // about a millisecond of processor time per second and is what makes it safe for the tests
    // above to be cheap rather than exhaustive: anything they miss is corrected within this.
    // Was half a second, and half a second is what the owner felt. It is the LAST resort now rather
    // than the main way a board change is noticed, since the occupancy signal above catches the
    // case that was reaching it, so the remaining job is only to catch whatever neither signal
    // describes, such as an item moved between two heroes. Shorter because it is gated by the
    // budget like everything else: a machine with no room defers it, so this bounds the wait
    // without imposing the cost on a machine that cannot pay it.
    private const float IdleRecheckSeconds = 0.2f;
    // THERE IS NO LONGER A STALE-PICTURE THRESHOLD, AND THE REASON IS KEPT SO IT IS NOT REINVENTED.
    //
    // A constant here used to say how long the picture from the hex a player had just left was
    // allowed to stand while the new hex was worked out. It was added to stop a sweep across the
    // board reading as a strobe, and it was 120 ms.
    //
    // Measured 2026-08-20 across three sessions: it fired on 81%, 41% and 29% of drags, and the
    // owner reported the result as flashing and unpolished. The mistake was not the number. It was
    // that ANY hold means showing an answer about a hex the hero has already left, and this mod
    // refuses a wrong answer everywhere else it can give one. The strobe the hold was avoiding was
    // real, but the cure showed a falsehood to hide a flicker, and it produced two transitions per
    // hex where an honest blank produces one.
    //
    // The picture is now tied to the candidate it was built for and disappears the instant they
    // disagree. See the state test in UpdatePlacement. Nothing here is scaled to the machine any
    // more, because there is no longer a duration to scale.

    private readonly Bindings _bindings;
    private readonly Capabilities _capabilities;
    private readonly ConfigMirror _mirror;
    private readonly Runner _runner;
    private readonly Func<float> _tickBudgetMs;
    private readonly Func<float> _dragTickBudgetMs;
    private readonly Func<bool> _devLog;
    private readonly Dictionary<string, PredictionResult> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private string _activeKey;
    private bool _lastOverridePresent;
    private int _lastOverrideFromX;
    private int _lastOverrideFromY;
    private int _lastOverrideToX;
    private int _lastOverrideToY;
    // The candidate the picture currently on screen was built for. Compared by VALUE against the
    // candidate the player is on, every frame, which is what turns "hide it the instant the hero
    // leaves the hex" into a state the mod can always answer rather than a stopwatch it has to run.
    // Deliberately not the cache key : that costs a whole config build and a hash, and this question
    // has to be cheap enough to ask on every frame of a drag.
    private bool _builtOverridePresent;
    private int _builtFromX;
    private int _builtFromY;
    private int _builtToX;
    private int _builtToY;
    private bool _wasDragging;
    private PredictionResult _lastAnnounced;
    // The last playout taken off the runner, shown or not. Held separately from LastResult because a
    // result that was cached without being shown must not be offered again on every following frame,
    // and LastResult can no longer answer "have I already dealt with this one".
    private PredictionResult _lastTakenFromRunner;
    private bool _rebuildPending;
    private bool _decision;
    private float _candidateSince;
    private float _nextRecheckAt;
    private float _deferringSince;
    private ulong _lastBoardSignature;
    private bool _boardSignatureSeen;
    // The opening answer for boards whose playout never finished, so re-crossing a hex during a
    // drag is not wasted. The full result cache below only ever holds FINISHED playouts, so before
    // this a hex the player crossed twice was built twice and answered neither time.
    private readonly Dictionary<string, PlacementSnapshot> _openingCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _openingOrder = new();
    // How long the player waits between moving onto a hex and seeing that hex answered.
    //
    // This is the number the owner reports on and it is the one thing nothing here measured. Every
    // slot in the profiler is a COST, and a mod can be cheap on every frame while taking most of a
    // second to answer, which is exactly what happened: the report read 4.9 percent of frame and
    // healthy while the drag was waiting about two thirds of a second. Cost and latency are
    // different questions and only one of them was being asked.
    private float _previewRequestedAt = -1f;
    private int _previewSamples;
    private int _previewCleared;
    private double _previewTotalMs;
    private double _previewWorstMs;

    public PredictionResult LastResult { get; private set; }

    /// <summary>
    /// What the board currently being played out says about the parts that care where units stand,
    /// as soon as it says it.
    /// </summary>
    /// <remarks>
    /// The run in flight wins over the last finished one, because during a drag the run in flight
    /// is the hex under the cursor and the finished one is the hex the player has left. It answers
    /// from the opening frame, so it is ready the same frame the board changes instead of when the
    /// playout ends, and it exists even on a board whose playout produces nothing.
    /// </remarks>
    public PlacementSnapshot ActivePlacement
    {
        get
        {
            PlacementSnapshot live = _runner.OpeningPlacement;
            if (live != null) return live;
            // A hex crossed before, whose playout was cancelled by the next hex before it finished.
            // The full result cache holds finished playouts only, so without this the marks went
            // blank every time a hero was dragged back over ground it had already covered, which is
            // the most ordinary thing a player does while deciding.
            if (_activeKey != null && _openingCache.TryGetValue(_activeKey, out PlacementSnapshot remembered))
                return remembered;
            return LastResult?.Placement;
        }
    }

    /// <summary>Passed straight to the runner ; see <see cref="Runner.Rules"/>.</summary>
    public Func<string, MultiHit.Rule> Rules
    {
        set => _runner.Rules = value;
    }

    /// <summary>Passed straight to the runner ; see <see cref="Runner.OpeningEvaluator"/>.</summary>
    public Func<Frame, PlacementSnapshot> OpeningEvaluator
    {
        set => _runner.OpeningEvaluator = value;
    }

    public ShadowSimController(Bindings bindings, Capabilities capabilities,
        Func<float> tickBudgetMs, Func<float> dragTickBudgetMs, Func<bool> devLog)
    {
        _bindings = bindings;
        _capabilities = capabilities;
        _mirror = new ConfigMirror(capabilities);
        _runner = new Runner(capabilities);
        _tickBudgetMs = tickBudgetMs;
        _dragTickBudgetMs = dragTickBudgetMs;
        _devLog = devLog;
        MachineProfile.SetDevLog(devLog);
    }

    public void UpdatePlacement(DragSnapshot drag, ulong boardSignature)
    {
        if (!_bindings.PredictionBindingsValid || _mirror.Faulted) return;
        float now;
        try { now = Time.realtimeSinceStartup; }
        catch { now = 0f; }
        bool dragging = drag != null;
        // On a hex where releasing would work, predict the board that release would produce. On a
        // hex where it would not, predict the board unchanged, because that is what the player
        // gets if they let go there.
        //
        // A hero held over its OWN hex is "no change", spelled that way rather than as a hex
        // exchanged with itself. ConfigMirror swaps the two cells, so an override of a hex onto
        // itself already produced a byte-identical config and therefore an identical cache key; the
        // two forms were always the same board and only one of them said so. Normalising it here is
        // what lets everything below compare candidates by value instead of having to know that.
        BoardOverride? over = dragging && drag.CandidateValid && drag.CandidateCell != drag.StartCell
            ? new BoardOverride(drag.StartCell, drag.CandidateCell)
            : null;
        // Asking whether the board changed means rebuilding the whole config and hashing it, so the
        // question costs as much as the answer. That is why nothing below asks it on a schedule any
        // more. It is asked when something the mod can see cheaply has moved, and otherwise twice a
        // second so that anything cheap tests cannot see is still caught.
        //
        // It used to be every ten FRAMES, described in this comment as "about six checks a second".
        // Ten frames is six a second at sixty, thirty a second at two hundred and two a second at
        // twenty, so the mod worked hardest at exactly the moment it had the least reason to and
        // eased off on the machines that were struggling. Ticking an already started playout still
        // happens every frame, further down, and that part was always budgeted.
        bool overrideChanged = over.HasValue != _lastOverridePresent;
        if (over.HasValue)
        {
            BoardOverride value = over.Value;
            overrideChanged = overrideChanged || value.FromCell.x != _lastOverrideFromX ||
                value.FromCell.y != _lastOverrideFromY || value.ToCell.x != _lastOverrideToX ||
                value.ToCell.y != _lastOverrideToY;
            _lastOverrideFromX = value.FromCell.x;
            _lastOverrideFromY = value.FromCell.y;
            _lastOverrideToX = value.ToCell.x;
            _lastOverrideToY = value.ToCell.y;
        }
        // The board's own occupancy, and it is the signal that was missing. Everything below reacts
        // to the DRAG snapshot, so a board change that never produced one was invisible: bringing a
        // hero off the bench, or sending one to it, yields no usable drag at all, and the board
        // silently changed with nothing to notice until the poll further down. Measured from play:
        // drag response mean 36 ms, worst 498 ms, and that worst is the poll interval exactly.
        //
        // Treated as a decision rather than as a candidate hex. The board really did change, so
        // there is nothing speculative to wait out and nothing to be gained by deferring it.
        bool boardChanged = _boardSignatureSeen && boardSignature != _lastBoardSignature;
        _lastBoardSignature = boardSignature;
        _boardSignatureSeen = true;

        bool dragStateChanged = dragging != _wasDragging;
        if (boardChanged)
        {
            _rebuildPending = true;
            _decision = true;
            _candidateSince = now;
        }
        if (overrideChanged || dragStateChanged)
        {
            _rebuildPending = true;
            _candidateSince = now;
            // Picking a hero up and putting one down are decisions, not hexes the cursor happened
            // to pass over, so neither waits for the dwell below and neither is ever deferred. The
            // drop in particular is what puts the prediction back on the real board, and a fight
            // can start the moment it lands.
            if (dragStateChanged) _decision = true;
            // Restarted rather than kept on every change, because a player who has moved on is
            // waiting for the NEW hex. Timing from the first of a run of changes would report the
            // whole sweep as one wait and flatter every hex after the first.
            if (dragging) _previewRequestedAt = now;
        }
        _lastOverridePresent = over.HasValue;
        _wasDragging = dragging;

        // THE PICTURE STOPS THE INSTANT THE HERO LEAVES THE HEX IT DESCRIBES.
        //
        // Owner ruling, 2026-08-20: "either you show the answer instantly, and if we cannot because
        // it would cause performance problems then we hide the targeting from the previous hex the
        // instant it leaves it."
        //
        // This replaced a timer, and the timer was the thing that looked broken. It held the old
        // hex's arrows for 120 ms before blanking, so one hex change produced THREE pictures: the
        // right one, then 120 ms of an answer about a hex the hero had already left, then nothing,
        // then the new one. The middle picture is the only one of the four that was ever wrong, and
        // it is the one the player reported as flashing. Two transitions where there should be one.
        //
        // Asked as a STATE rather than as a timer, which is what makes it exact: the picture belongs
        // to the candidate it was built for, so the moment the player is on a different candidate it
        // is a wrong answer and goes. No threshold to tune, and nothing behaves differently on a
        // slower machine, which the timer plainly did.
        //
        // What clears is the board overlay alone : the arcs, the ghost copies, the tiles and the
        // footprints. The see-through bodies, the hidden unit panels and the placement marks are all
        // held on purpose, because none of them waits for a playout: the marks are taken from the new
        // board's opening frame and are already correct the same frame the hex changes, and the other
        // two follow their toggles rather than the prediction.
        bool pictureDescribesThisCandidate = _builtOverridePresent == over.HasValue &&
            (!over.HasValue || (over.Value.FromCell.x == _builtFromX && over.Value.FromCell.y == _builtFromY &&
                                over.Value.ToCell.x == _builtToX && over.Value.ToCell.y == _builtToY));
        if (dragging && !pictureDescribesThisCandidate && LastResult != null)
        {
            LastResult = null;
            _previewCleared++;
        }

        // THE DWELL. A hex the cursor is crossing on the way somewhere is not a hex the player is
        // choosing, and building for it costs the two most expensive calls the mod makes. Measured
        // on the reference machine: about 0.6 ms to describe the board and about 2.6 ms to
        // construct the battle, each of which is one call into the game's own code and therefore
        // cannot be split across frames however much slack there is. Crossing five hexes quickly
        // used to start five playouts and throw four away.
        //
        // Eighty milliseconds is a HUMAN dwell time, so unlike everything else in this cycle it is
        // deliberately NOT adapted to the machine: how long a person rests on a hex before they
        // mean it does not depend on their graphics card. It is also why the previous cycle's
        // one-FRAME version was reverted and this one is not the same change. A frame is 4 ms on
        // this machine and 50 ms on a slow one, so that debounce was invisible where it was tested
        // and a real delay everywhere else.
        //
        // AND IT ONLY APPLIES TO A MACHINE THAT CANNOT AFFORD TO BE WRONG. If there is room for the
        // rebuild right now, building for a hex the cursor is merely crossing costs a few
        // milliseconds nobody reads, and in exchange the preview follows the hero with no wait at
        // all. If there is not, the same speculation is exactly what makes a slow machine stutter,
        // so the wait comes back. That is the conditional form: a machine with headroom behaves as
        // though the dwell did not exist, and one without it stops thrashing rebuilds it cannot pay
        // for. A flat wait would have taxed every machine for the benefit of the slowest.
        double rebuildEstimateMs = Perf.MeanCallMs(PerfSlot.MirrorBuild) + Perf.MeanCallMs(PerfSlot.SimStart);
        bool roomRightNow = FrameBudget.CanAfford(rebuildEstimateMs);
        bool dwellSatisfied = _decision || !dragging || roomRightNow ||
                              now - _candidateSince >= DebounceSeconds;
        // The unconditional floor. Anything that changes the board without moving a hero, such as
        // an item being moved between two of them, is caught here rather than by a signal for it,
        // and it is what makes the cheap tests above safe to be less than exhaustive.
        //
        // It also bounds the dwell rather than being defeated by it. A player dragging steadily so
        // that no hex is ever held for the full dwell would otherwise never get a rebuild at all;
        // this gives them twice a second, against the thirty a second the frame counter used to
        // produce on a fast machine.
        bool due = (_rebuildPending && dwellSatisfied) || now >= _nextRecheckAt;

        if (due && WillingToSpend(rebuildEstimateMs))
        {
            _rebuildPending = false;
            _decision = false;
            _nextRecheckAt = now + IdleRecheckSeconds;
            // Booked against this frame's allowance BEFORE the work, so the ticking below sees a
            // frame that has already been spent and yields to its floor instead of the two of them
            // together overrunning. Booking it afterwards, or not at all, would have left the
            // budget describing a frame that no longer existed, which is the whole failure this
            // cycle is about: a limit on one part that says nothing about the total.
            FrameBudget.Spend(rebuildEstimateMs);
            bool mirrorBuilt;
            BattleConfig config;
            string hash;
            string key;
            using (Perf.Measure(PerfSlot.MirrorBuild))
                mirrorBuilt = _mirror.TryBuild(_bindings.BuildGuid, over, out config, out hash, out key);
            if (!mirrorBuilt) return;
            // Whatever this rebuild settles on, it settles it FOR this candidate. Recorded before the
            // branch below rather than inside it, because the branch is skipped when the key has not
            // moved, and "the key did not move" is precisely the case where the picture already on
            // screen is the right answer for this candidate and must not be blanked for it.
            _builtOverridePresent = over.HasValue;
            if (over.HasValue)
            {
                _builtFromX = over.Value.FromCell.x;
                _builtFromY = over.Value.FromCell.y;
                _builtToX = over.Value.ToCell.x;
                _builtToY = over.Value.ToCell.y;
            }
            // The key changed, OR the key is the same and there is nothing left to show for it.
            //
            // THE SECOND HALF IS NOT BELT AND BRACES. IT IS THE WHOLE OF A LATCH.
            //
            // _activeKey is a claim: this board has an answer, either finished in LastResult or
            // being played out right now. Two places break that claim without touching the key.
            // The stale-picture clear above sets LastResult to null, and Runner.Cancel sets the
            // runner's Result to null, which the cache-hit branch below does WITHOUT starting a
            // run after it. Once both have happened the controller holds a key it cannot draw, and
            // a test on the key alone then refuses to rebuild the one board that needs it: the
            // idle re-check computes the same key twice a second and skips, and the accept test at
            // the end of this method has a null result to offer. The board stays empty for the
            // rest of the placement.
            //
            // Only an edit that really moves a hero breaks that tie, because only that changes the
            // key. Picking a hero up and putting it back down does NOT, and that is the part that
            // makes it look like the mod has died rather than paused: an override of a hex onto
            // itself exchanges a tile with itself, so ConfigMirror serializes the identical config
            // and hands back the identical key.
            //
            // Asked as "have I an answer" rather than by clearing the key at each of the two
            // places that can lose one, so that a third way of losing one cannot reopen this.
            bool haveAnswer = LastResult != null || _runner.IsRunning;
            if (!string.Equals(key, _activeKey, StringComparison.Ordinal) || !haveAnswer)
            {
                // Before the run is discarded, keep what it already answered. The opening answer is
                // final the moment it exists, so a playout abandoned halfway still produced a real
                // result for the marks even though it never produced one for the arrows.
                RememberOpening();
                _runner.Cancel();
                if (_cache.TryGetValue(key, out PredictionResult cached))
                {
                    // This exact layout has already been played out, either a hex the hero was
                    // dragged across before or the board the drag preview computed just before
                    // the drop. Nothing to compute, so it renders on the spot.
                    _activeKey = key;
                    LastResult = cached;
                    // A cache hit is an answer like any other, and the fastest one there is. Left
                    // out, the latency figure would only ever describe the slow half.
                    NotePreviewAnswered();
                }
                else
                {
                    try
                    {
                        using (Perf.Measure(PerfSlot.SimStart))
                            _runner.Start(config, hash, key);
                        _activeKey = key;
                        // Outside a drag, an out of date prediction is never shown, and this is the
                        // line that says so.
                        //
                        // It reads as though a drag were the exception that keeps the old picture,
                        // and that is no longer true : the candidate test near the top of this
                        // method has already cleared it on the frame the hero left the hex this
                        // picture describes. By the time a new playout starts for a NEW candidate
                        // there is nothing left to hold, so the branch below is reached during a
                        // drag only when the candidate has not moved, which is exactly when holding
                        // is right. Kept as a guard rather than removed, because "not dragging" and
                        // "the candidate changed" are two different reasons and only one of them
                        // lives up there.
                        if (!dragging) LastResult = null;
                    }
                    catch (Exception e)
                    {
                        _runner.Cancel();
                        _capabilities.DisablePrediction("shadow runner failed to start: " + e);
                        return;
                    }
                }
            }
            else if (LastResult != null)
            {
                // Same board, and its answer is already on screen. That IS this hex's answer and
                // it arrived in no time at all, so the wait is over and is recorded as over, the
                // same way a cache hit is.
                //
                // It was not recorded before, and the omission was doing two things. It left the
                // latency figure describing only the hexes that had to be computed, so the fastest
                // answers the mod gives were the ones missing from its own report. And it left
                // _previewRequestedAt running against a request nothing would ever clear, so the
                // stale-picture clear above eventually blanked a picture that was correct for the
                // board in front of the player. Deliberately NOT extended to the case where a run
                // is still in flight for this key: nothing is on screen then, the wait is real,
                // and AcceptResult will record it when the playout lands.
                NotePreviewAnswered();
            }
        }
        try
        {
            using (Perf.Measure(PerfSlot.SimTick))
                _runner.TickSlice(BudgetForFrame(dragging));
        }
        catch (Exception e)
        {
            _runner.Cancel();
            _capabilities.DisablePrediction("shadow runner failed: " + e);
            return;
        }
        // A FINISHED PLAYOUT IS ALWAYS WORTH KEEPING. IT IS NOT ALWAYS WORTH SHOWING.
        //
        // These were one decision and had to become two. The clear at the top of this method drops
        // the picture the moment the hero leaves the hex it describes, and this line then handed the
        // just-finished playout for that same abandoned hex straight back, in the SAME frame, after
        // the clear had already run. The board therefore went on showing the hex the player had left,
        // which is precisely the thing the clear exists to prevent and precisely the thing that was
        // reported. Measured before the split: 851 clears against 110 answered previews.
        //
        // Caching it regardless is not a consolation prize, it is the point: the player sweeping back
        // across that hex gets it instantly, and throwing the work away would make a re-crossing pay
        // for a whole playout twice.
        if (_runner.Result == null || ReferenceEquals(_runner.Result, _lastTakenFromRunner)) return;
        _lastTakenFromRunner = _runner.Result;
        AcceptResult(_runner.Result, showIt: pictureDescribesThisCandidate);
    }

    public PredictionResult BuildFinalPrediction()
    {
        PredictionResult fallback = LastResult;
        if (!_bindings.PredictionBindingsValid || _mirror.Faulted) return fallback;
        try
        {
            bool mirrorBuilt;
            BattleConfig config;
            string hash;
            string key;
            using (Perf.Measure(PerfSlot.MirrorBuild))
                mirrorBuilt = _mirror.TryBuild(_bindings.BuildGuid, null, out config, out hash, out key);
            if (!mirrorBuilt)
                return fallback;
            _runner.Cancel();
            using (Perf.Measure(PerfSlot.SimStart))
                _runner.Start(config, hash, key);
            while (_runner.IsRunning)
            {
                using (Perf.Measure(PerfSlot.SimTick))
                    _runner.TickSlice(1000f);
            }
            if (_runner.Result == null) return fallback;
            _activeKey = key;
            // Always shown. This is the final prediction for the board the fight is about to run on,
            // built with no drag override at all, so there is no candidate hex left to disagree with.
            AcceptResult(_runner.Result, showIt: true);
            return LastResult;
        }
        catch (Exception e)
        {
            _runner.Cancel();
            _capabilities.DisablePrediction("final shadow prediction failed: " + e);
            return fallback;
        }
    }

    public void LeavePlacement()
    {
        _runner.Cancel();
        _mirror.LeavePlacement();
        _cache.Clear();
        _cacheOrder.Clear();
        _activeKey = null;
        LastResult = null;
        _lastOverridePresent = false;
        _lastOverrideFromX = 0;
        _lastOverrideFromY = 0;
        _lastOverrideToX = 0;
        _lastOverrideToY = 0;
        _builtOverridePresent = false;
        _builtFromX = 0;
        _builtFromY = 0;
        _builtToX = 0;
        _builtToY = 0;
        _wasDragging = false;
        _lastAnnounced = null;
        _lastTakenFromRunner = null;
        _rebuildPending = false;
        _decision = false;
        _candidateSince = 0f;
        _nextRecheckAt = 0f;
        _deferringSince = 0f;
        _openingCache.Clear();
        _openingOrder.Clear();
        _previewRequestedAt = -1f;
        // The drag response TOTALS are deliberately NOT cleared here. This runs on the way out of
        // placement, and the report that prints them runs after it, so clearing them here printed
        // "no drag preview was answered this placement" on a placement with thirty three of them.
        // They are cleared on the way IN instead, which is where Perf clears its own window and for
        // the same reason. An instrument that resets before it reports measures nothing and says so
        // confidently.
        // Re-read next battle rather than once per session, so a player who changes their frame
        // rate cap or turns vertical sync on does not keep being budgeted against the old one.
        MachineProfile.Reset();
        // FrameBudget is NOT reset here, for the reason above: the placement report prints what it
        // settled on, and this runs first, so it printed 0.00 ms every single time.
    }

    /// <summary>Starts a fresh placement window. Called when placement is entered, like Perf.</summary>
    public void EnterPlacement()
    {
        FrameBudget.Reset();
        // A fresh board. Comparing this placement's first reading against the last battle's would
        // report a change that is really just a different fight.
        _lastBoardSignature = 0UL;
        _boardSignatureSeen = false;
        _previewRequestedAt = -1f;
        _previewSamples = 0;
        _previewCleared = 0;
        _previewTotalMs = 0.0;
        _previewWorstMs = 0.0;
    }

    /// <summary>
    /// Whether this frame can pay for the one thing in the mod that cannot be split.
    /// </summary>
    /// <remarks>
    /// Describing the board and constructing the battle are one call each into code the mod does
    /// not own. No budget can make either of them shorter, so the only thing left to decide is
    /// WHICH frame pays. Waiting for a frame with room turns a guaranteed stutter into a little
    /// latency, which is exactly the trade this cycle was given permission to make.
    ///
    /// The estimate is measured, not configured: Perf knows what these two calls have cost on this
    /// machine, so a slow machine defers on a real number rather than on a constant somebody chose
    /// on a fast one. A call that has never run estimates zero and therefore always runs, which is
    /// how the estimate comes to exist at all.
    ///
    /// A decision, meaning a hero picked up or put down, is never deferred. The half of a
    /// millisecond it might save is not worth the picture disagreeing with the board the player is
    /// looking at, and a fight can begin the frame after a drop.
    /// </remarks>
    private bool WillingToSpend(double estimateMs)
    {
        if (_decision)
        {
            _deferringSince = 0f;
            return true;
        }
        return FrameBudget.RunNowOrDefer(estimateMs, ref _deferringSince);
    }

    private float BudgetForFrame(bool dragging)
    {
        float configured = dragging ? _dragTickBudgetMs() : _tickBudgetMs();
        // Until the machine has been observed, nothing is withheld from it.
        if (!FrameBudget.Calibrated) return configured;
        // What is left AFTER the rebuild above has taken its share, so on the frame a new board is
        // built the ticking yields to it instead of the two of them together overrunning. The floor
        // is what guarantees a playout always finishes eventually rather than never.
        return (float)Math.Max(BudgetFloorMs, Math.Min(configured, FrameBudget.RemainingMs));
    }

    // The opening answer belongs to the board it was taken on, so it is filed under that board's
    // key before the run holding it is thrown away.
    private void RememberOpening()
    {
        PlacementSnapshot opening = _runner.OpeningPlacement;
        if (opening == null || _activeKey == null) return;
        if (!_openingCache.ContainsKey(_activeKey)) _openingOrder.Enqueue(_activeKey);
        _openingCache[_activeKey] = opening;
        while (_openingCache.Count > CacheCap && _openingOrder.Count > 0)
            _openingCache.Remove(_openingOrder.Dequeue());
    }

    private void CacheResult(PredictionResult result)
    {
        if (result?.CacheKey == null) return;
        if (_cache.ContainsKey(result.CacheKey))
        {
            _cache[result.CacheKey] = result;
            return;
        }
        _cache[result.CacheKey] = result;
        _cacheOrder.Enqueue(result.CacheKey);
        while (_cache.Count > CacheCap && _cacheOrder.Count > 0)
            _cache.Remove(_cacheOrder.Dequeue());
    }

    /// <summary>What the player waits, from moving onto a hex to that hex being answered.</summary>
    public string Diagnostic => _previewSamples == 0
        ? "no drag preview was answered this placement"
        : $"{_previewSamples} drag preview(s) answered, mean {_previewTotalMs / _previewSamples:F0} ms, " +
          $"worst {_previewWorstMs:F0} ms, {_previewCleared} hidden on a hex change. " +
          // The mean is now the only number that decides how this feels. With the hold gone, every
          // hex change blanks until its answer lands, so the wait IS the experience and there is no
          // threshold left to blame for a flicker.
          "The mean is what a player waits between moving onto a hex and seeing it answered";

    private void NotePreviewAnswered()
    {
        if (_previewRequestedAt < 0f) return;
        double ms;
        try { ms = (Time.realtimeSinceStartup - _previewRequestedAt) * 1000.0; }
        catch { _previewRequestedAt = -1f; return; }
        _previewRequestedAt = -1f;
        if (ms < 0.0) return;
        _previewSamples++;
        _previewTotalMs += ms;
        if (ms > _previewWorstMs) _previewWorstMs = ms;
    }

    /// <param name="showIt">
    /// Whether this playout answers the hex the hero is on NOW. False means keep it and say nothing:
    /// it is a correct answer to a question the player has stopped asking.
    /// </param>
    private void AcceptResult(PredictionResult result, bool showIt)
    {
        // Cached whatever the cursor has done since, because a cache entry is about a BOARD and not
        // about where the pointer happens to be.
        CacheResult(result);
        if (!showIt) return;
        NotePreviewAnswered();
        LastResult = result;
        if (!_devLog() || ReferenceEquals(_lastAnnounced, result)) return;
        _lastAnnounced = result;
        string targets = string.Join(", ", result.Tick0Targets.Select(x => Short(x.Key) + "->" + Short(x.Value)));
        string cells = string.Join(", ", result.Settled.Select(x => Short(x.Key) + "@" + x.Value.Cell.x + "," + x.Value.Cell.y));
        MelonLogger.Msg($"[TargetingMod] PREDICTION stage={StageIdentity.Read()} seed={result.Seed} ticks={result.Ticks} capMoving={result.StillMovingAtCap} prevented={result.PreventedDeaths} hash={result.ConfigHash[..12]}");
        MelonLogger.Msg("[TargetingMod] tick-0 targets: " + targets);
        MelonLogger.Msg("[TargetingMod] settled cells: " + cells);
    }

    private static string Short(string id) => string.IsNullOrEmpty(id) ? "none" : id.Substring(0, Math.Min(8, id.Length));
}
