using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using GuildrunTargetingMod.ShadowSim;
using GuildrunTargetingMod.Ui;
using Il2CppEmber.Scopes.Battle.Board.Controllers;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(GuildrunTargetingMod.Mod), "GuildrunTargetingMod", GuildrunTargetingMod.Bindings.ModVersion, "Larsonix")]
[assembly: MelonGame("Leyline", "Guildrun")]

namespace GuildrunTargetingMod;

// The mod's entry point, and the only place that decides what runs when.
//
// Everything happens during placement and nothing survives into the fight. Each frame reads the
// board, plays a slice of the prediction, and draws the result ; the moment the fight starts,
// every visual is destroyed and only the invisible self-check keeps reading.
public sealed class Mod : MelonMod
{
    private Capabilities _capabilities;
    private Bindings _bindings;
    private PhaseGate _phase;
    private ShadowSimController _shadow;
    private ParityGate _parity;
    private AnchorWatch _anchor;
    private PositionalGlow _glow;
    private PartGlow _partGlow;
    private AoeShapes _aoe;
    private MelonPreferences_Entry<bool> _enabled;
    private MelonPreferences_Entry<bool> _devLog;
    private MelonPreferences_Entry<bool> _measureDrawCost;
    private MelonPreferences_Entry<bool> _arrowsFromGhosts;
    private MelonPreferences_Entry<bool> _midlineHeads;
    private MelonPreferences_Entry<bool> _transparentUnits;
    private MelonPreferences_Entry<bool> _previewStartsOn;
    private MelonPreferences_Entry<bool> _dragLivePreview;
    private MelonPreferences_Entry<string> _previewHotkey;
    private MelonPreferences_Entry<string> _originHotkey;
    private MelonPreferences_Entry<string> _transparencyHotkey;
    private MelonPreferences_Entry<float> _tickBudgetMs;
    private MelonPreferences_Entry<float> _dragTickBudgetMs;
    private MelonPreferences_Entry<float> _markLapSeconds;
    private DragPreviewService _dragPreview;
    private BoardController _board;
    private UnitViewRegistry _views;
    private HoverService _hover;
    private OverlayRenderer _overlay;
    private NativeUI _ui;
    private MenuUI _menu;
    private LeaderboardGuard _guard;
    private UiCensus _census;
    private DrawCostProbe _drawCostProbe;
    private string _viewCacheKey;
    private bool _inert;
    // Whether the feature layer ran on the previous frame. Only a change in this drives work, so
    // a switched-off mod pays one comparison per frame and nothing else.
    private bool _featuresActive;
    private bool _visualPlacementReady;
    private bool _finalViewRebuilt;
    private bool _uiPlacementEntered;
    private float _placementWarmupElapsed;
    private bool _placementEnteredThisFrame;
    private readonly SearchBackoff _boardSearch = new("battle board controller");
    private SearchBackoff _visualResolutionSearch;
    private readonly SearchBackoff _staleViewSearch = new("a unit view under the pointer");
    private bool _staleViewRebuildPending;
    private float _staleViewRetryAt;

    // Resuming a saved run puts the game into placement while the battle scene is still starting
    // up. Predicting on that half-restored state crashed the game outright, from inside the
    // simulation's own loading code, which no amount of error handling on our side can catch.
    // So placement has to have been stable for a moment before the mod touches anything.
    // The visual-only prewarm is the exception: it performs no prediction or simulation work.
    private const float PlacementWarmupSeconds = 0.75f;

    // A fifth of a second of pointing at a model the map cannot name. Long enough that a single
    // frame caught while the board is being rebuilt never triggers it, short enough that a player
    // coming back to the window does not notice the gap.
    //
    // Written as the fifth of a second this comment always claimed, instead of as the twelve FRAMES
    // it used to be. Twelve frames was a fifth of a second on one machine, six hundredths at two
    // hundred frames a second, and well over half a second at twenty, so the promise here held
    // nowhere except where it was written.
    private const float StaleViewSeconds = 0.2f;

    public override void OnInitializeMelon()
    {
        // These are written to UserData/MelonPreferences.cfg on the first run, and the player
        // reads these descriptions there. They say the same thing as the readme's settings table,
        // in the same words.
        //
        // Built before the guard now, where they used to come after it. The guard owns the three
        // marks below and the player's own switch, so it cannot be constructed until they exist.
        var category = MelonPreferences.CreateCategory("GuildrunTargetingMod", "Guildrun Targeting Mod");
        _enabled = category.CreateEntry("Enabled", true,
            "Turns the whole mod on or off. The main menu button writes this one too, so the file and the button can never disagree.");
        _devLog = category.CreateEntry("DevLog", false, "Verbose logs, plus a dump of the resolved UI.");
        _measureDrawCost = category.CreateEntry("MeasureDrawCost", false,
            "Diagnostic. Briefly blinks the board overlay on and off to measure what drawing it costs your machine. Leave off for normal play.");
        // The two display choices behind the in-game toggles. Unlike the preview toggle, which
        // starts off in every battle, these are remembered across battles and sessions.
        _arrowsFromGhosts = category.CreateEntry("ArrowsFromGhosts", true,
            "Attack arrows start at the predicted positions instead of the current ones.");
        // No in-game button and no shortcut, by owner ruling from play : with see-through units
        // making the board readable, the direction marks were the least useful of the four, and a
        // button nobody presses is a button in the way of the three that matter. The setting stays
        // here for anyone who wants them.
        _midlineHeads = category.CreateEntry("MidlineArrowheads", false,
            "Adds a direction chevron in the middle of each line. No in-game button; set it here.");
        _transparentUnits = category.CreateEntry("TransparentUnits", true,
            "Fades the units for the whole placement so you can see the board through them.");
        // The preview deliberately starts off in every battle : a poll of players preferred
        // meeting the ordinary board first. This is for the player who lives in the preview and
        // does not want to switch it on every fight, so the default stands and the file decides.
        _previewStartsOn = category.CreateEntry("PreviewStartsOn", false,
            "Starts each battle with the opening preview already on.");
        _dragLivePreview = category.CreateEntry("DragLivePreview", true,
            "Computes the hex under a held hero. false hides the visuals while dragging.");
        // Any key name the Unity input system knows. The reasoning behind these three defaults is
        // beside the key parsing in NativeUI.
        _previewHotkey = category.CreateEntry("PreviewKey", "P", "Key that toggles the opening preview.");
        _originHotkey = category.CreateEntry("ArrowOriginKey", "F", "Key that toggles where attack arrows start.");
        _transparencyHotkey = category.CreateEntry("TransparencyKey", "T", "Key that toggles see-through units.");
        _tickBudgetMs = category.CreateEntry("TickBudgetMs", 2.0f,
            "Most simulation time allowed per frame during placement. Less is used when your machine has no time to spare.");
        // Dragging recomputes on every hex crossed, so it gets a bigger slice. A full playout was
        // measured at under 3 ms, so 6 ms usually finishes a new hex within the same frame while
        // still leaving most of a 60 frames per second budget to the game.
        _dragTickBudgetMs = category.CreateEntry("DragTickBudgetMs", 6.0f,
            "Most simulation time allowed per frame while dragging. Less is used when your machine has no time to spare.");
        // Seconds for one lap of the two lights that travel a marked item's border. Motion rather
        // than a pulse is a deliberate choice and not a decoration : a red flash has no luminance
        // threshold under the flashing guidance at all, so there is no such thing as a red pulse
        // subtle enough to be exempt, while smooth travel is outside that guidance entirely.
        // Slower is calmer, and zero stops it and leaves the border standing still.
        _markLapSeconds = category.CreateEntry("MarkLapSeconds", 3.6f,
            "Seconds for one lap of the lights on a marked item's border. 0 keeps the border and stops the motion.");
        var testedGuid = category.CreateEntry("TestedBuildGuid", string.Empty,
            "The last game build the mod checked itself against. Written by the mod.");
        var failureVersion = category.CreateEntry("ParityFailureVersion", string.Empty,
            "Set when two fights in a row disagreed with the mod. Cleared by one that agrees. Written by the mod.");
        var mismatchStreak = category.CreateEntry("ParityMismatchStreak", 0,
            "How many fights in a row have disagreed with the mod. Written by the mod.");
        // The three marks behind the one rule the player is told: a run counts for the
        // leaderboards only if the mod was switched off for the whole of it. They are here rather
        // than in a file of the mod's own so that a player can read the state of their own save.
        var moddedRunId = category.CreateEntry("ModdedRunId", string.Empty,
            "The run the mod has been active in. That run's scores are never submitted, even after the mod is switched off. Written by the mod.");
        var moddedStreak = category.CreateEntry("ModdedChallengeStreak", false,
            "Set while the Red Rift win streak contains a run played with the mod on. Clears when the streak restarts. Written by the mod.");
        var noticeShown = category.CreateEntry("LeaderboardNoticeShown", false,
            "Set once the leaderboard notice has been shown in the main menu. Written by the mod.");

        _capabilities = new Capabilities();
        _guard = new LeaderboardGuard(HarmonyInstance, _enabled, moddedRunId, moddedStreak, noticeShown);
        // Built even when the guard failed, and deliberately so. A mod that has switched itself
        // off still has to be able to say that somewhere the player will actually look, and the
        // menu layer reads nothing and changes nothing until it is clicked.
        _menu = new MenuUI(_capabilities, _guard);
        // One window opened here as well as on every scene load, for the case where the menu scene
        // is already up by the time the loader gets to this mod. If it is not, the window expires
        // costing nothing and the scene hook catches the menu when it really arrives.
        _menu.OnSceneInitialized(null);
        if (!_guard.Applied)
        {
            // Every other feature fails open because losing one visual should not cost the rest.
            // This guard fails closed because a preview with live score submission is the exact
            // outcome it exists to prevent, so nothing else may initialize when it cannot be sure.
            _inert = true;
            MelonLogger.Error("[TargetingMod] mod switched off because leaderboard submission could not be guaranteed disabled: " +
                _guard.UnavailableReason);
            return;
        }

        _bindings = new Bindings(_capabilities, testedGuid, failureVersion, mismatchStreak);
        _bindings.RunBootSelfCheck();
        // Beside the self-check and for the same reason. A report that a score did not appear on
        // Steam is answered from this line plus the block line the guard writes at the time.
        MelonLogger.Msg("[TargetingMod] leaderboards: " + _guard.Diagnostic);
        // Said out loud, once, with the whole path. The most asked-for thing in the feedback round
        // was a settings file that has existed since the first version : it is not discoverable
        // unless something tells the player where it is, and the log is the one place every player
        // ends up when they want to know what the mod is doing.
        MelonLogger.Msg("[TargetingMod] settings file: " +
            System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, "MelonPreferences.cfg") +
            "  (section [GuildrunTargetingMod])");
        _phase = new PhaseGate();
        _shadow = new ShadowSimController(_bindings, _capabilities, () => _tickBudgetMs.Value,
            () => Mathf.Max(_tickBudgetMs.Value, _dragTickBudgetMs.Value), () => _devLog.Value);
        _parity = new ParityGate(_bindings, _capabilities, MelonEnvironment.UserDataDirectory);
        _anchor = new AnchorWatch(_capabilities);
        _glow = new PositionalGlow(_capabilities);
        _partGlow = new PartGlow(_capabilities, ItemMarkState, _glow.ForRelic, _glow.ForAbility,
            () => _markLapSeconds.Value, () => _glow.HasBlocked || _anchor.HasBlocked);
        _aoe = new AoeShapes(_capabilities);
        _views = new UnitViewRegistry();
        _hover = new HoverService(_capabilities);
        _dragPreview = new DragPreviewService(_capabilities);
        _overlay = new OverlayRenderer(_capabilities);
        _drawCostProbe = new DrawCostProbe();
        // Bound once rather than per frame : a method group assigned every frame allocates a new
        // delegate every frame, and the answer behind it is throttled anyway.
        _overlay.HeroGlow = _glow.ForHero;
        _overlay.UnitAoe = _aoe.For;
        // Every playout is asked, at its opening frame, what that board does for the parts that
        // care where units stand. That is what lets the marks follow a hero being dragged : the
        // playout was built for the hex under the cursor, so its answer is about the board the
        // player is considering rather than the one they are leaving.
        _shadow.OpeningEvaluator = _glow.ForOpeningFrame;
        _ui = new NativeUI(_capabilities, value => _overlay.PreviewEnabled = value, _arrowsFromGhosts, _midlineHeads,
            _transparentUnits, _previewStartsOn,
            _previewHotkey, _originHotkey, _transparencyHotkey);
        _hover.OwnUiProbe = _ui.OwnsGameObject;
        _census = new UiCensus(MelonEnvironment.UserDataDirectory);
        _phase.Transitioned += OnPhaseTransition;
    }

    // Nothing here may cost anything while the mod is switched off, because a switch that only
    // hides the pictures is not the switch the player asked for.
    //
    // What that costs now: the menu layer's own single boolean, one comparison here, and nothing
    // else. No frame is opened, no budget is computed, no phase is polled and no teardown is
    // repeated. The old shape called DestroyVisualLayer from inside the frame, so a mod left off
    // for a session ran three backoff resets and a probe call on every frame of it.
    public override void OnLateUpdate()
    {
        // Runs whatever the switch says, because the main menu button is the only way back on.
        _menu?.Tick();

        bool active = !_inert && _enabled?.Value == true;
        if (active != _featuresActive)
        {
            _featuresActive = active;
            _guard?.SetFeaturesRunning(active);
            if (active) MelonLogger.Msg("[TargetingMod] switched on");
            else
            {
                MelonLogger.Msg("[TargetingMod] switched off; nothing of the mod runs per frame from here");
                // Once, on the way down. Teardown is already step isolated inside, and a throw
                // here would strand the visuals on screen with nothing left running to remove them.
                try { DestroyVisualLayer(); }
                catch (Exception e)
                {
                    MelonLogger.Warning("[TargetingMod] tearing down after being switched off failed: " + e.Message);
                }
            }
        }
        if (!active) return;

        // The frame is closed whatever happened inside it, including the frames the mod skips
        // entirely. A frame that cost nothing is still a frame, and dropping it from the count
        // would flatter every mean the instrument reports.
        try { UpdateFrame(); }
        finally { Perf.EndFrame(); }
    }

    // The main menu arrives as an additively loaded scene, so the loader says so for free and
    // nothing has to watch for it. The menu layer searches for nothing in here : it only opens a
    // bounded window in which it will look, because most scene loads are not the main menu.
    public override void OnSceneWasInitialized(int buildIndex, string sceneName)
    {
        _menu?.OnSceneInitialized(sceneName);
    }

    private void UpdateFrame()
    {
        // Opened once, before anything is allowed to spend. Everything the mod defers is measured
        // against what this works out is left of the frame after the game has had its share.
        FrameBudget.BeginFrame();
        using (Perf.Measure(PerfSlot.Phase))
            _phase.Poll();
        switch (_phase.Current)
        {
            case ModPhase.Placement:
                // Read, then predict, then draw, in that order. A hex the player has just dragged
                // onto reaches the prediction in the same frame it changes, so the new picture
                // usually lands without a frame of lag behind the hero.
                // A pause is not loading progress. In particular, the first enormous delta after
                // an alt-tab must not spend the whole safety warmup before any prewarm frame ran.
                //
                // A long frame is CLAMPED rather than discarded, and the difference matters. A
                // discarded one credits nothing, so a machine whose frames are all longer than the
                // clamp would never accumulate any warmup at all and the mod would simply never
                // start. Clamping keeps the pause protection, since a ten second alt-tab still
                // only buys half a second, while guaranteeing the wait always ends.
                if (_placementEnteredThisFrame)
                    _placementEnteredThisFrame = false;
                else if (Time.unscaledDeltaTime > 0f)
                    _placementWarmupElapsed += Mathf.Min(Time.unscaledDeltaTime, 0.5f);
                if (_placementWarmupElapsed >= PlacementWarmupSeconds)
                    UpdatePlacementVisuals();
                else
                    Prewarm();
                break;
            case ModPhase.Resolution:
                using (Perf.Measure(PerfSlot.Parity))
                    _parity.SampleResolution();
                break;
        }
    }

    public override void OnDeinitializeMelon()
    {
        // Before the inert check, because an inert mod still built the menu button and still owns
        // the object behind it.
        TearDown("main menu button", () => _menu?.Destroy());
        if (_inert) return;
        // Shutdown runs while the scene is already being taken apart, so an object the mod owns
        // may be gone and touching it throws. Anything escaping here is reported by the loader as
        // a failed shutdown, which reads like a real problem and is not one : the process is
        // exiting either way, and cleanup at this point is best effort by nature.
        try { DestroyVisualLayer(); }
        catch (Exception e) { MelonLogger.Warning("[TargetingMod] shutdown cleanup skipped: " + e.Message); }
        TearDown("session mark pictures", MarkArt.DropAll);
        TearDown("session overlay assets", OverlayRenderer.DropSessionAssets);
    }

    private void UpdatePlacementVisuals()
    {
        bool visualLayerReady;
        using (Perf.Measure(PerfSlot.EnsureLayer))
            visualLayerReady = EnsureVisualLayer();
        if (!visualLayerReady)
        {
            _drawCostProbe?.Update(_overlay, false);
            using (Perf.Measure(PerfSlot.Shadow))
                // Its last known value, not zero. The drag tracker has not run yet on this path, and
                // handing over a zero it never reported would look like the board had changed.
                _shadow.UpdatePlacement(null, _dragPreview.BoardSignature); // keep predicting while the scene loads.
            return;
        }
        string key = _shadow.LastResult?.CacheKey ?? "prediction-pending";
        // Measured, and it was not before. This test walks the board's own view list and then the
        // mod's, asking three things of each through interop, on every frame of every placement,
        // and it sat inside no slot at all: only the REBUILD it guards was ever timed. So the
        // headline "the mod costs 1.17 ms a frame" was an undercount by however much this is, and
        // nothing in the report said so. An instrument must contain what it claims to measure.
        bool boardMatches;
        using (Perf.Measure(PerfSlot.Views))
            boardMatches = _views.MatchesBoard(_board);
        if (!boardMatches)
        {
            // The board changed, so the map from unit ids to their on-screen models is rebuilt.
            using (Perf.Measure(PerfSlot.Views))
                _views.Rebuild(_board, key);
            _viewCacheKey = key;
        }
        using (Perf.Measure(PerfSlot.Hover))
            _hover.Update(_views, _devLog.Value, forceRefresh: false);
        // The map from models to units is rebuilt when the board changes, and clicking away to
        // another window and back does not change the board. Reported from play : after coming
        // back, hovering did nothing until a hero was picked up and put down, because picking one
        // up is what rebuilds the prediction and the map rides along with it.
        //
        // Rebuilt on the symptom rather than on a guess about the cause : the pointer sitting on a
        // model the map cannot name, for long enough that it is not a frame caught mid-rebuild.
        // Pointing at bare board never counts, so this stays quiet through ordinary play.
        if (_staleViewRebuildPending)
        {
            // The rebuild gets its same-frame retry below just as before, but only the next frame
            // can prove whether that repair held. Once it did not, a seconds-based floor prevents
            // a stationary pointer from turning the global object walk back into per-frame work.
            if (_hover.UnknownUnitSeconds >= StaleViewSeconds)
            {
                _staleViewSearch.Missed();
                _staleViewRetryAt = Time.realtimeSinceStartup + 0.25f;
            }
            else
            {
                _staleViewSearch.Found();
                _staleViewRetryAt = 0f;
            }
            _staleViewRebuildPending = false;
        }
        if (_hover.UnknownUnitSeconds >= StaleViewSeconds &&
            Time.realtimeSinceStartup >= _staleViewRetryAt && _staleViewSearch.ShouldTry())
        {
            using (Perf.Measure(PerfSlot.Views))
                _views.Rebuild(_board, _viewCacheKey);
            using (Perf.Measure(PerfSlot.Hover))
                // Forced, because the pointer has not moved : that is the whole condition that got
                // us here, and the ordinary skip would hand back the same stale answer again.
                _hover.Update(_views, false, forceRefresh: true);
            _staleViewRebuildPending = true;
            if (_devLog.Value) MelonLogger.Msg("[TargetingMod] unit view map rebuilt: the pointer was over a model it did not know");
        }
        // The drag tracker runs even when the live preview is switched off, because reading the
        // game's own state is how the mod knows a hero is in hand at all. It replaces watching
        // for the mouse press, which could miss an entire drag.
        DragSnapshot drag;
        using (Perf.Measure(PerfSlot.DragTrack))
            drag = _dragPreview.Update(_views, _board);
        // The tracker has the final say whenever it is reading the board. The mouse-press signal
        // is only consulted while it is not, which means disabled, faulted or still starting up.
        bool dragActive = drag != null || (!_dragPreview.Healthy && _hover.IsDragging);
        // A drag with nothing honest to predict, a hero held over the UI or taken from the bench,
        // hides the board visuals exactly as the mod did before the live preview existed.
        DragSnapshot visualDrag = _dragLivePreview.Value && drag != null && drag.Usable ? drag : null;
        using (Perf.Measure(PerfSlot.Shadow))
            // The drag tracker walked every tile a moment ago, so it already knows who is standing
            // where. Handing that on is what lets the prediction hear about a board change that
            // produced no usable drag, such as a hero arriving from the bench.
            _shadow.UpdatePlacement(visualDrag, _dragPreview.BoardSignature);
        PlacementSnapshot preview = visualDrag != null ? _shadow.ActivePlacement : null;
        PredictionResult prediction = _shadow.LastResult;
        // The saved preferences are the one place the two display toggles live. Copying them
        // across each frame is cheaper than wiring up change notifications for two booleans.
        _overlay.ArrowsFromGhosts = _arrowsFromGhosts.Value;
        _overlay.MidlineHeads = _midlineHeads.Value;
        _overlay.Transparent = _transparentUnits.Value;
        // Which equipped items and owned relics care about where units stand, and whether the
        // board as arranged is satisfying them. Both are asked before anything reads them : the
        // item icons ask the Seal watch and the placement marks what they think, and these two
        // calls are what make them think. Without them the icons would answer from the previous
        // frame's opinion.
        using (Perf.Measure(PerfSlot.Anchor))
            _anchor.Update();
        using (Perf.Measure(PerfSlot.LiveSnapshot))
            _glow.Update(preview == null);
        // While a hero is in hand, the marks answer for the board that dropping it would make,
        // taken from that board's own playout. The moment it is put down there is nothing
        // hypothetical left, so this goes back to null and the real board answers again.
        //
        // Only when the drag has something honest to show. A hero held over the interface or over
        // a hex where dropping would do nothing has no candidate board, and the marks should then
        // describe the board that is really there rather than an arrangement nobody is choosing.
        //
        // Read from the run in flight rather than from the finished prediction. The answer is taken
        // at that board's opening frame and is final the moment it exists, so waiting for the
        // playout to walk everybody to their hex was half a second of the icons describing the
        // previous hex, and on a board whose playout produced nothing they never caught up at all.
        _glow.Preview = preview;
        // The same answer on the icons themselves : the hero's item row and the run's relic bar,
        // which is where the thing being talked about actually is. Throttled inside like the data
        // it reads, and its own failure domain : the game's UI is the part most likely to move.
        using (Perf.Measure(PerfSlot.PartGlow))
            _partGlow.Update();
        // The ground each unit's ability will cover. Throttled inside like the two above : which
        // ability a unit has is authored data, and it changes at the speed of a person clicking.
        using (Perf.Measure(PerfSlot.Aoe))
            _aoe.Update();
        // While a hero is being dragged, it is the unit the visuals are about : with the preview
        // off, its own story at the hex it would land on ; with the preview on, the whole board
        // recomputed. Without a usable drag, holding a hero hides the board visuals as before.
        string hovered = visualDrag != null ? visualDrag.EntityId : _hover.HoveredEntityId;
        using (Perf.Measure(PerfSlot.Render))
            // The last two are what the renderer cannot see for itself. The tiles under units are
            // coloured from the placement marks and the footprints come from the area shapes, and
            // both of those are computed elsewhere on their own schedules, so without them the
            // renderer would skip a redraw on precisely the frame one of those answers changed.
            _overlay.Render(prediction, _views, _board, hovered, dragActive && visualDrag == null, visualDrag,
                _glow.HeroStateVersion, _aoe.Version);
        _drawCostProbe.Update(_overlay, _measureDrawCost.Value);
        // The game's overhead unit panels hide only while the preview is really showing. Asked
        // after rendering, so a rendering failure that switches the preview off puts the panels
        // back in the same frame. Dragging keeps them hidden, so picking a hero up does not flash
        // the whole HUD back on.
        using (Perf.Measure(PerfSlot.NativeUi))
            _ui.SetUnitHudHidden(_overlay.PreviewEnabled && _capabilities.Preview
                && _capabilities.Overlay && _capabilities.Prediction);
        ModNotice notice = _parity.UserNotice;
        if (notice == ModNotice.None && !_capabilities.Prediction)
        {
            // Two reasons the preview can be off, and they must not be confused. The mod once told
            // a player "turned off after a game update, a mod update will bring it back" when no
            // game had updated and no mod update was coming : it had disagreed with itself over a
            // rounding difference and blamed the game. A notice that names the wrong cause sends
            // the player looking in the wrong place and makes everything else it says worth less.
            //
            // There is deliberately no notice for a new game build any more, because a new game
            // build no longer withholds anything. Either the mod recorded a real disagreement, or
            // it could not read the battle at all.
            notice = _bindings.HasPersistedFailure ? ModNotice.Disagreement : ModNotice.ReadFailure;
        }
        using (Perf.Measure(PerfSlot.NativeUi))
            _ui.Update(_hover.HoveredEntityId, notice,
                prediction?.StillMovingAtCap == true, dragActive, _hover.OwnUiUnderPointer);
        using (Perf.Measure(PerfSlot.Census))
            _census.TryWrite(_devLog.Value, _bindings, _ui, _overlay, _hover, _views);
        // Compilation left over from the warmup, finished on frames that genuinely have room. A
        // slow machine gets fewer warmup frames than a fast one, so without this the machine that
        // benefits most from compiling early would be the one that finished the least of it. Gated
        // on real slack rather than run unconditionally, because the whole point of moving this
        // work was that it must never land on a frame the player is waiting for.
        if (!HotPathsCompiled && FrameBudget.RemainingMs > 3.0) CompileHotPaths();
    }

    // What an item icon should say, from the two things that can have an opinion about it.
    //
    // Kept here rather than inside either of them on purpose : the placement marks know nothing
    // about the Rift Seal, and the Seal watch knows nothing about placement. Composing the two
    // where they are already both in hand keeps each one a thing that does one job.
    //
    // The Seal wins when it applies. A Seal charging nothing while somebody on the board could be
    // charging it is a whole battle wasted, which outranks a stat bonus that is not currently
    // paying.
    private PartState ItemMarkState(string itemId) =>
        _anchor.IsSealNotPaying(itemId) ? PartState.Blocked : _glow.ForItem(itemId);

    // The authority on whether the visual layer is up. It stops asking once every available part
    // resolves, but a scene which is still assembling keeps getting bounded retries for the rest
    // of placement. Loading slowly is not a permanent capability verdict.
    //
    // The prewarm below can make every one of these a no-op, and that is the whole of its job. It
    // must never become the thing that decides, because a stager that also decides can decide never
    // to finish : one step per frame plus a step that never succeeds is a mod that quietly draws
    // nothing forever, with no notice, which is the opposite of how everything else here fails.
    private bool EnsureVisualLayer()
    {
        if (_visualPlacementReady) return true;
        try
        {
            if (_board == null && _boardSearch.ShouldTry())
            {
                _board = RuntimeDiscovery.FindLive<BoardController>();
                if (_board == null) _boardSearch.Missed();
                else _boardSearch.Found();
            }
            if (_board == null) return false; // the scene is still loading ; try again next frame.
            bool shouldResolve = _visualResolutionSearch == null || _visualResolutionSearch.ShouldTry();
            bool hoverReady = !_capabilities.Hover || _hover.Resolved || shouldResolve && _hover.Resolve(_board);
            bool overlayReady = !_capabilities.Overlay || _overlay.Resolved || shouldResolve && _overlay.Resolve(_board);
            bool uiReady = !_capabilities.NativeUi || _ui.Resolved || shouldResolve && _ui.Resolve(_overlay.PreviewIconSprite);
            // A resolve that comes back false without having thrown used to be a throw, and the
            // throw is what disabled the feature. The prewarm needs those to be retryable while the
            // scene is still assembling, so they answer false instead. A slow machine can still be
            // assembling after the warmup, so say what remains unresolved once and keep trying;
            // only the exception paths inside each resolver make a permanent verdict.
            if (!hoverReady || !overlayReady || !uiReady)
            {
                // Built only on a frame that is actually going to record a miss. Naming the parts
                // costs a comparison chain and two string joins, and this branch is precisely the
                // state a machine with a missing component sits in for the whole placement, so
                // building it unconditionally would leave a per-frame allocation behind inside the
                // one code path this entire change exists to bound.
                if (shouldResolve)
                {
                    string unresolved = !hoverReady && !overlayReady && !uiReady ? "battle camera, overlay, and placement UI" :
                        !hoverReady && !overlayReady ? "battle camera and overlay" :
                        !hoverReady && !uiReady ? "battle camera and placement UI" :
                        !overlayReady && !uiReady ? "overlay and placement UI" :
                        !hoverReady ? "battle camera" : !overlayReady ? "overlay" : "placement UI";
                    string named = unresolved + " after the placement warmup";
                    _visualResolutionSearch ??= new SearchBackoff(named);
                    _visualResolutionSearch.Name(named);
                    _visualResolutionSearch.Missed();
                }
            }
            else
                _visualResolutionSearch?.Found();
            // Always, even when the prewarm already built one. The prewarm's map is taken a few
            // frames into a three-quarter-second wait, and models can still be arriving after it :
            // enemies especially, which have no board list of their own and are found only by the
            // sweep inside Rebuild. Since the per-frame check now watches the HERO list and the
            // liveness of what it already holds, a unit that appeared late would otherwise never be
            // noticed at all, and would silently get no ghost and no arrow for the whole placement.
            //
            // It costs one sweep per placement, which is the one this whole item exists to stop
            // paying per hex.
            if (!_finalViewRebuilt)
            {
                _views.Rebuild(_board, "placement-enter");
                _viewCacheKey = "placement-enter";
                _finalViewRebuilt = true;
            }
            if (uiReady && _capabilities.NativeUi && !_uiPlacementEntered)
            {
                _ui.EnterPlacement(); // sets each battle's starting toggle state.
                _uiPlacementEntered = true;
            }
            _visualPlacementReady = hoverReady && overlayReady && uiReady;
            // Hover, the world overlay and the buttons stand or fall separately. A missing toggle
            // must not cost the player their arrows, and neither can stop the prediction.
            return hoverReady || overlayReady || uiReady;
        }
        catch (Exception e)
        {
            _capabilities.DisableOverlay("visual layer initialization failed: " + e.Message);
            DestroyVisualLayer();
            return false;
        }
    }

    // Setup, spread over the warmup the mod already waits out doing nothing.
    //
    // Measured before it existed: every one of these landed in the first frame after the warmup and
    // that frame cost 500.95 ms. None of them needs to be there. The warmup is dead time the player
    // cannot interact with, so one step per frame goes into it and the first interactive frame finds
    // the work already done.
    //
    // Best effort by construction. It resolves nothing it is not sure about, it disables nothing, it
    // reports nothing, and skipping it entirely would leave the mod behaving exactly as it did
    // before. Every step is idempotent and EnsureVisualLayer re-checks all of them anyway.
    //
    // It must never touch the simulation. The warmup exists because predicting on a half-restored
    // scene crashed the game from inside its own loading code, and that reason is untouched here.
    private void Prewarm()
    {
        if (_visualPlacementReady) return;
        try
        {
            if (_board == null)
            {
                if (!_boardSearch.ShouldTry()) return;
                _board = RuntimeDiscovery.FindLive<BoardController>();
                if (_board == null) _boardSearch.Missed();
                else _boardSearch.Found();
                return;
            }
            if (_capabilities.Overlay && !_overlay.Resolved) { _overlay.Resolve(_board); return; }
            if (_capabilities.Hover && !_hover.Resolved) { _hover.Resolve(_board); return; }
            if (_capabilities.NativeUi && !_ui.Resolved) { _ui.Resolve(_overlay.PreviewIconSprite); return; }
            if (_viewCacheKey == null)
            {
                if (_views.Rebuild(_board, "placement-enter")) _viewCacheKey = "placement-enter";
                return;
            }
            if (!_partGlow.SceneSearched) { _partGlow.PrewarmSearch(); return; }
            CompileHotPaths();
        }
        catch (Exception e)
        {
            // Never latched and never fatal : this is an optimization, and the real attempt happens
            // in EnsureVisualLayer a moment later with the failure handling that belongs there.
            if (_devLog?.Value == true)
                MelonLogger.Msg("[TargetingMod] a prewarm step was not ready yet: " + e.Message);
        }
    }

    // The types whose first call lands on the frame placement becomes interactive. Explicit rather
    // than "every type in the assembly", because compiling everything costs far more than the
    // warmup has to give and most of it is never on a hot path.
    private static readonly Type[] HotPathTypes =
    {
        typeof(ShadowSim.ConfigMirror), typeof(ShadowSim.Runner), typeof(ShadowSimController),
        typeof(ShadowSim.CellWorldTable), typeof(PositionalGlow), typeof(PlacementSnapshot),
        typeof(AoeShapes), typeof(AnchorWatch), typeof(Perf), typeof(FrameBudget),
        typeof(Interop.NullableRaw), typeof(OverlayRenderer), typeof(PartGlow), typeof(HoverService),
        typeof(DragPreviewService), typeof(UnitFader), typeof(UnitViewRegistry), typeof(MarkArt),
        typeof(RuntimeDiscovery), typeof(NativeUI), typeof(ParityGate)
    };

    private int _compiledTypes;
    private MethodInfo[] _compilingMethods;
    private int _compiledMethods;

    // Compiles the mod's own hot methods during the warmup, so the first interactive frame finds
    // them already compiled instead of paying for all of them at once.
    //
    // ZERO behavioural risk by construction, and that is the whole reason this is the half that
    // ships. PrepareMethod compiles a method body and does not execute it, so it cannot touch the
    // simulation, and the crash that created the warmup in the first place, predicting on a
    // half-restored scene, is untouched here by construction rather than by care.
    //
    // What it does NOT pay for: the interop layer resolves a native address on the first CALL, not
    // on the first compile, and the mod's cold cost is some unknown mixture of the two. Only real
    // calls would pay that half and only some of them are safe to make early, so the split is left
    // to be read off the shipped instrument rather than guessed at now.
    //
    // Bounded by a duration rather than by a count of types per frame, because a count would
    // compile fewer of them on exactly the machines that need it most.
    /// <summary>True once every listed type has been compiled. Compilation is per process.</summary>
    private bool HotPathsCompiled => _compiledTypes >= HotPathTypes.Length && _compilingMethods == null;

    private void CompileHotPaths()
    {
        if (HotPathsCompiled) return;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        // The clock is checked between METHODS rather than between types, and that is not a detail.
        // Checking only between types means one class the size of the overlay renderer compiles all
        // of itself before the limit is looked at again, so a two millisecond slice becomes twenty
        // on exactly one frame. Invisible during the warmup, and a stutter anywhere else.
        double limitTicks = System.Diagnostics.Stopwatch.Frequency * 0.002;
        while (System.Diagnostics.Stopwatch.GetTimestamp() - start < limitTicks)
        {
            if (_compilingMethods == null)
            {
                if (_compiledTypes >= HotPathTypes.Length) return;
                Type type = HotPathTypes[_compiledTypes++];
                if (type == null || type.ContainsGenericParameters) continue;
                try
                {
                    _compilingMethods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                }
                catch { _compilingMethods = null; continue; }
                _compiledMethods = 0;
            }
            if (_compiledMethods >= _compilingMethods.Length)
            {
                _compilingMethods = null;
                continue;
            }
            MethodInfo method = _compilingMethods[_compiledMethods++];
            // An abstract method has no body, and an uninstantiated generic has no single body to
            // compile. Asking for either throws rather than answering.
            if (method == null || method.IsAbstract || method.ContainsGenericParameters) continue;
            try { RuntimeHelpers.PrepareMethod(method.MethodHandle); }
            catch { /* one method that will not compile early simply compiles on first call. */ }
        }
    }

    // Teardown runs while the battle scene is being taken apart, so any single call into it can
    // throw on something the game has already destroyed. Each step therefore stands on its own : a
    // failure in one must never skip the ones after it, and the flags at the end have to be
    // cleared whatever happened.
    //
    // That is not a precaution, it is a fix. One throw in here used to abandon the rest, which
    // left the mod holding a destroyed board with its "already set up" flag still raised. It then
    // skipped its own setup on the next battle and drew nothing at all : the mod worked for the
    // first placement after a launch and was dead for the rest of the session.
    private void DestroyVisualLayer()
    {
        _drawCostProbe?.FinishPlacement(_overlay);
        _boardSearch.Reset();
        _visualResolutionSearch?.Reset();
        _staleViewSearch.Reset();
        if (!_visualPlacementReady && _board == null) return;
        TearDown("anchor watch", () => _anchor?.Reset());
        // What the two per-frame passes actually cost, measured rather than asserted, so the next
        // person to wonder whether per-frame was affordable reads it off a real placement instead
        // of arguing about it. Both split the cold frame out from the settled ones : the cold one
        // pays for every search and every classification and is not the number that matters.
        if (_devLog?.Value == true)
        {
            MelonLogger.Msg(Perf.Report("placement"));
            // The wait the player actually experiences, which every other line here misses: they
            // are all costs, and a mod can be cheap on every frame while taking most of a second
            // to answer. Printed before the marks so the first thing read is the thing felt.
            if (_shadow != null) MelonLogger.Msg("[TargetingMod] drag response: " + _shadow.Diagnostic);
            if (_glow != null) MelonLogger.Msg("[TargetingMod] placement marks: " + _glow.Diagnostic);
            if (_partGlow != null) MelonLogger.Msg("[TargetingMod] icon marks: " + _partGlow.Diagnostic);
        }
        TearDown("positional glow", () => _glow?.Clear());
        TearDown("part glow marks", () => _partGlow?.Clear());
        TearDown("area outlines", () => _aoe?.Clear());
        TearDown("drag tracker", () => _dragPreview?.Reset());
        TearDown("hover", () => _hover?.Clear());
        TearDown("placement UI", () => _ui?.LeavePlacement());
        TearDown("UI objects", () => _ui?.Destroy());
        TearDown("world overlay", () => _overlay?.Destroy());
        TearDown("unit views", () => _views?.Clear());
        _board = null;
        _viewCacheKey = null;
        _visualPlacementReady = false;
        _finalViewRebuilt = false;
        _uiPlacementEntered = false;
        _visualResolutionSearch = null;
        _staleViewRebuildPending = false;
        _staleViewRetryAt = 0f;
    }

    private static void TearDown(string what, Action step)
    {
        try { step(); }
        catch (Exception e) { MelonLogger.Warning($"[TargetingMod] tearing down the {what} failed: {e.Message}"); }
    }

    private void OnPhaseTransition(ModPhase previous, ModPhase current)
    {
        if (_devLog.Value) MelonLogger.Msg($"[TargetingMod] phase {previous} -> {current}");
        // Entering a fight with the mod running is the moment this run stops counting for the
        // leaderboards, and it stays not counting even if the player switches the mod off later.
        // Written here rather than per frame because it has to happen once per run and it writes
        // a file ; the guard itself only writes when the mark actually changes.
        if (current == ModPhase.Placement || current == ModPhase.Resolution) _guard?.NoteRunIsModded();
        if (current == ModPhase.Placement)
        {
            _capabilities.ResetForPlacement();
            _placementWarmupElapsed = 0f;
            _placementEnteredThisFrame = true;
            Perf.Reset();
            // Beside Perf.Reset and for its reason. Everything that reports per placement has to
            // clear its window on the way IN, because the report itself runs on the way out and
            // anything cleared there is cleared before it is printed.
            _shadow.EnterPlacement();
        }
        if (previous == ModPhase.Placement && current == ModPhase.Resolution)
        {
            PredictionResult held = _shadow.BuildFinalPrediction();
            _shadow.LeavePlacement(); // no prediction is ever left running once a fight starts.
            DestroyVisualLayer();     // every visual is gone before the fight shows itself.
            // After the placement report above, never before it, and the fight gets a window of
            // its own. Without this the battle's own per-frame cost is measured and then wiped by
            // the next placement without ever being printed, which is a measurement that answers
            // nothing.
            Perf.Reset();
            _parity.Begin(held);
            return;
        }
        if (current == ModPhase.Resolution)
        {
            // The fight started without a placement before it, which is what resuming a run in
            // the middle of a battle looks like. The gate's per-battle clock and samples still
            // have to reset : leaving a stale window behind once made its own sanity check fire
            // instantly and shut down reading the game for a whole session.
            Perf.Reset();
            _parity.Begin(null);
            return;
        }
        if (previous == ModPhase.Resolution && current != ModPhase.Resolution)
        {
            if (_devLog.Value) MelonLogger.Msg(Perf.Report("battle"));
            _parity.FinishBattle();
        }
        if (previous == ModPhase.Placement && current != ModPhase.Placement)
        {
            _shadow.LeavePlacement();
            DestroyVisualLayer();
        }
    }
}
