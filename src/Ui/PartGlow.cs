using System;
using System.Collections.Generic;
using System.Diagnostics;
using GuildrunTargetingMod.Interop;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2Cppgg.leyline.balancing.Data;
using Il2CppEmber.Balancing;
using Il2CppEmber.Balancing.Sheets.Abilities.ActiveAbilities;
using Il2CppEmber.Balancing.Sheets.Abilities.PassiveAbilities;
using Il2CppEmber.Balancing.Sheets.Characters;
using Il2CppEmber.Balancing.Sheets.RankModifiers;
using Il2CppEmber.Balancing.SimulationBridge;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data.Characters;
using Il2CppEmber.Scopes.GameRun.UI.HeroCard;
using Il2CppEmber.Scopes.GameRun.UI.HeroCard.Elements;
using Il2CppEmber.Scopes.GameRun.UI.Relics;
using Il2CppEmber.Scopes.GameRun.UI.Slots;
using Il2CppEmber.Scopes.GameRun.UI.Slots.HeroPanel;
using Il2Cppgg.leyline.core.Mvcs.Model;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace GuildrunTargetingMod.Ui;

// Marks the equipped items and owned relics whose rules care about where units are standing, and
// which are currently NOT paying because of where they are standing.
//
// The board half of this lives in the overlay, on the tile under the unit. This is the other half :
// the icons themselves, in a Hero's item row and in the run's relic bar. Being told "this Hero's
// placement matters" still leaves the player working out WHICH of the four things they are wearing
// meant it, and the report this feature came from is about exactly that gap : "sometimes you have a
// character with the defensive legs, and forget about it when positioning".
//
// Four rules hold the whole design up.
//
//   A part that is WORKING looks exactly like vanilla. Nothing is drawn on it, ever. Only a part
//   switched off by placement is marked. That single ruling removed three problems at once : a mark
//   on a working item would have had to survive five rarity colours, a full working row would have
//   been a wall of decoration, and the earlier plan needed the game's own frame dimmed and later
//   put back, which is a ledger of borrowed state and a way to leave an inventory wrong.
//
//   Nothing the game owns is ever written to. Not a colour, not a sprite, not a flag. The game
//   rewrites an item slot's colours every time it repopulates one, so tinting its image would be a
//   fight lost silently, and a failure mid-frame would leave a player's inventory recoloured with
//   nothing to put it back. Every mark here is an object the mod made and parented under the view,
//   exactly as the world-space ghosts already are. Teardown destroys ours, and the game's own UI is
//   untouched because it always was.
//
//   The shape is measured, never invented, and never borrowed either. The border is traced off the
//   frame sprite's own rarity band and redrawn in our red as a picture of ours, so it follows the
//   centre ornament's V and survives an art change with no code change. Borrowing the sprite and
//   tinting it was the previous build and was measured wrong : the UI shader multiplies, so every
//   mark came out rarity colour times our colour. See MarkArt and FrameTrace.
//
//   It is its own failure domain. The game's UI is the most update-fragile surface this mod
//   reaches for, so when it moves, this goes quiet on its own and the board half, the arrows and
//   the prediction never notice.
internal sealed class PartGlow
{
    // Once a second, and it used to be three times a second.
    //
    // This timer covers ONE case : a slot object that did not exist in the scene at all and has
    // just been instantiated. Everything else, a pooled slot being switched on, a different item
    // appearing in a slot, a slot being switched off, is now noticed the frame it happens, because
    // the per-frame pass reads the live slots rather than a list frozen at search time.
    //
    // Making it rarer is not a micro-optimisation. The search behind it walks every loaded object
    // in the game : at three times a second that is a hitch three times a second, spent almost
    // entirely on rediscovering objects that had not moved. `Diagnostic` reports what it costs.
    // Evidence now requests a search, and this is only the floor between requests, not a schedule.
    // One second is the floor rather than a frame count, so fast and slow machines ask at the same
    // rate. When a search adds nothing, the shared backoff stretches later attempts as far as five
    // seconds; a search which does discover a newly created view resets that failure history.
    private const float SecondsBetweenSearches = 1f;
    // How long the sweep for newly shown slots takes to cover the whole set. A DURATION, not a
    // frame count, so the cost per second is the same on every machine and only the slice per frame
    // differs. A tenth of a second is about the threshold at which a mark appearing reads as
    // immediate, and the previous cycle's "the red line takes half a second to arrive" report is
    // the reason it is not set any looser.
    private const float DiscoverySeconds = 0.120f;
    private const int FaultsBeforeDisabling = 10;
    // A fifth of a frame at sixty per second, the same threshold the placement marks use.
    private const double SlowFrameMs = 3.0;
    // How the strike across a marked icon is proportioned, as shares of the icon so they hold at
    // any UI scale. The border's own weight is deliberately NOT here : it belongs to the picture
    // MarkArt draws, in that sprite's own pixels and per axis, and a second copy of it here would
    // be a second number to keep in step with the first.
    private const float SlashLengthFactor = 1.3f;
    // Matched to the border so that neither outweighs the other. This was more than twice as heavy
    // before, from when the strike was the only red on the icon and had to carry the whole message
    // on its own. It is now one of two marks and is proportioned as one of two.
    private const float SlashThicknessFactor = 0.050f;
    private const float SlashMinThickness = 3f;
    // How far out of an ability view the search for its owning Hero may climb before giving up. A
    // card is a handful of levels deep and the bar that holds every card sits just above it, so this
    // is a backstop against a deep hierarchy costing a full subtree walk per level per view, not a
    // number anyone should need to tune. The ambiguity test below is what actually stops the climb.
    private const int MaxCardClimb = 8;

    private readonly Capabilities _capabilities;
    private readonly Func<string, PartState> _forItem;
    private readonly Func<string, PartState> _forRelic;
    private readonly Func<string, string, PartState> _forAbility;
    private readonly Func<bool> _anythingBlocked;
    // Read every frame rather than captured once, so editing the settings file and reloading it
    // takes effect on the next frame instead of on the next launch.
    private readonly Func<float> _markLapSeconds;
    // Keyed by the view's instance id rather than by the view : a pooled slot is reused for a
    // different item, and the mark under it should be reused with it.
    private readonly Dictionary<int, Mark> _marks = new();
    private readonly List<int> _stale = new();
    private readonly List<Tracked> _tracked = new();
    // Every equipment slot object in the scene, switched on or not. Found rarely ; filtered every
    // frame. Keeping the switched-off ones is the whole point : a pooled slot that the game turns
    // on when a panel opens is already in here, so its mark appears with the panel instead of up to
    // a third of a second later.
    private readonly List<PlaceholderSlotView> _slots = new();
    // Which entries of _slots were on screen last time, kept sorted so membership is a binary
    // search. Maintained across frames rather than rebuilt, which is what lets the expensive walk
    // above be amortised instead of repeated.
    private readonly List<int> _visibleSlotIndices = new();
    private readonly Dictionary<int, string> _itemIdStrings = new();
    private readonly Dictionary<int, ItemId> _lastItemIds = new();
    private int _sweepCursor;
    private bool _sawDestroyedSlot;
    // Set whenever nothing is known about what is on screen, so the next pass looks at everything
    // once rather than discovering the board a slice at a time.
    private bool _fullSweepPending = true;
    // The Hero cards, which carry the largest of the three surfaces : rank modifiers and passives.
    private readonly List<HeroRankModifiersView> _abilityViews = new();
    // Kept separate because sharing the rank-card packing rule would point at innocent abilities.
    private readonly List<HeroPanelAbilitiesView> _panelAbilityViews = new();
    // The bottom Hero bar uses the full-card tiles but packs them by a third, incompatible rule.
    private readonly List<HeroCardAbilitiesView> _bottomAbilityViews = new();
    private readonly Dictionary<IntPtr, string> _heroKeysByCard = new();
    private readonly Dictionary<IntPtr, EntityId> _lastHeroIds = new();
    private readonly Dictionary<IntPtr, Image> _platesByContainer = new();
    private readonly HashSet<IntPtr> _containersWithoutPlate = new();
    // What ANSWERED the question "which Hero is this card about", per ability view. Never the answer
    // itself. A pooled view handed to a different Hero would go on reporting the old one, and
    // proving the old Hero is still alive does not prove it is still THIS view's Hero. Holding the
    // source and reading through it each frame keeps the answer current while still paying for the
    // search only once. The surfaces keep separate caches so one native pointer lifetime cannot be
    // mistaken for another, and a MiniHeroCard can answer through a PlaceholderSlotView instead.
    private readonly Dictionary<IntPtr, HeroCardView> _cardsByAbilityView = new();
    private readonly Dictionary<IntPtr, HeroCardView> _cardsByPanelAbilityView = new();
    private readonly Dictionary<IntPtr, BottomHeroView> _bottomHeroesByAbilityView = new();
    private readonly Dictionary<IntPtr, HeroCardView> _cardsByBottomAbilityView = new();
    private readonly Dictionary<IntPtr, PlaceholderSlotView> _ownedSlotByAbilityView = new();
    private NullableRaw.FieldRef _itemIdField;
    private bool _itemIdFieldResolved;
    private RelicUIController _relicUi;
    private float _nextSearchAt;
    private readonly SearchBackoff _sceneSearchBackoff = new("item, relic, and ability UI views");
    private bool _searched;
    private int _scanStamp;
    private int _consecutiveFaults;
    private string _lastFault;
    private double _worstSearchMs;
    private int _searches;
    private int _frames;
    private double _firstFrameMs;
    private double _worstFrameMs;
    private double _totalFrameMs;
    private bool _loggedSlowFrame;
    private int _abilityMismatches;
    private int _abilityFaults;
    private string _lastAbilityFault;
    private bool _loggedAbilityFault;
    private bool _loggedAbilityLookup;
    /// <summary>The player's verbose-log switch; the key trace above is diagnostic only.</summary>
    public Func<bool> DevLog { get; set; }
    private bool _loggedAbilityBlocked;
    // How the ability half of the last pass went. Counted because the report could not previously
    // tell "this board had no ability worth marking" from "the ability walk never ran at all", and
    // those two look identical from outside : both are silence. They were NOT the same thing. Every
    // shipped log said 0 mismatches and 0 faults while the walk was in fact returning before it
    // reached a single slot, because the owning Hero could not be identified, and nothing counted
    // that. An instrument has to be able to say which of its zeroes it means.
    private int _cardsResolved;
    private int _cardsUnresolved;
    // The most any single pass of this placement saw, and these are the REPORTED numbers.
    //
    // The per-pass values above answered nothing on their own, for the same reason the area counters
    // did not: the report prints when placement ENDS, and by then the placement bar is being torn
    // down, so a pass that saw four cards all placement long reports zero. Measured 2026-08-20, on
    // a run where 32 ability panels had been found and the counters still both read zero. "Did this
    // ever happen" needs a peak, not a snapshot of the last moment.
    private int _peakCardsResolved;
    private int _peakCardsUnresolved;
    private int _peakAbilityIcons;
    // Skipped slots are counted PER PASS and reported as a peak, like everything else here.
    //
    // They used to accumulate over the whole placement, which was harmless only while the ability
    // walk never ran. It runs now, and one of the skips is entirely legitimate and happens on every
    // frame: the specialization slot is written twice by the game, active then passive, so whichever
    // of the two is not on screen is refused every single pass. Accumulated, that reports several
    // thousand skipped slots on a healthy board and buries a real packing failure in noise. A peak
    // says "at most N slots were refused in any one pass", which is the number worth reading.
    private int _peakAbilityMismatches;
    private int _peakAbilityFaults;

    public PartGlow(Capabilities capabilities, Func<string, PartState> forItem, Func<string, PartState> forRelic,
        Func<string, string, PartState> forAbility, Func<float> markLapSeconds, Func<bool> anythingBlocked)
    {
        _capabilities = capabilities;
        _forItem = forItem;
        _forRelic = forRelic;
        _forAbility = forAbility;
        _markLapSeconds = markLapSeconds;
        _anythingBlocked = anythingBlocked;
    }

    /// <summary>Safe to call every frame ; searches the scene rarely, reads and marks always.</summary>
    /// <remarks>
    /// Three rates, not two, and getting the middle one wrong is what a player reported as "the red
    /// line still takes half a second to arrive".
    ///
    /// SEARCHING the scene for slot objects walks every loaded object in the game, which is the
    /// expensive thing here and is worth milliseconds rather than microseconds. It only ever finds
    /// something new when the game instantiates a slot that did not exist, so once a second is
    /// generous. What it actually costs is measured and reported rather than written down here.
    ///
    /// READING them, which slots are switched on and what item each is showing, is a handful of
    /// field reads and happens every frame. This is the half that used to ride on the search timer :
    /// the tracked list was built from the slots that were switched on AT SEARCH TIME, so opening a
    /// panel meant waiting out the rest of the timer before anything in it could be marked. That
    /// wait is what "arrive" meant, and no amount of speeding up the answer behind the mark could
    /// have fixed it.
    ///
    /// MARKING them is a dictionary lookup and also happens every frame, because while a hero is
    /// being dragged the answer changes on every hex crossed.
    /// </remarks>
    public void Update()
    {
        // Riding on the data layer's switch as well as its own is deliberate. With the data gone
        // every answer is "nothing to say", and marks left over from before it went would be a
        // picture of a board that has since been rearranged.
        if (!_capabilities.GlowIcons || !_capabilities.PositionalGlow)
        {
            if (_marks.Count != 0) Clear(); // once, not every frame for the rest of the placement.
            return;
        }
        try
        {
            // Before anything asks for a picture, because this is what moves the lap on and what
            // hands out the one frame trace a frame is allowed to pay for.
            MarkArt.Advance(_markLapSeconds != null ? _markLapSeconds() : 0f);
            // The timer only covers slot objects the game has newly instantiated. A destroyed one
            // is visible from here, so that does not wait for the timer at all.
            // Evidence now replaces the periodic timer, and a shared floor limits every trigger.
            // Everything from here down runs every frame, so it is the half that has to be cheap
            // and the half worth measuring. Reading which slots are on screen was assumed cheap
            // once already this round and was not ; assume nothing twice.
            long start = Stopwatch.GetTimestamp();
            CollectVisible();
            // The last clause asks whether the SEARCH found nothing, never whether nothing is
            // currently on screen. Those are different questions and the difference is the whole
            // safety of this trigger : a closed hero card leaves plenty of slot objects found and
            // simply none of them visible, so a trigger reading `_tracked` would fire on every frame
            // a panel is shut and turn an evidence-driven search back into a periodic one, twice as
            // often as the timer it replaced. Only "the search itself came up empty" is the case the
            // old timer existed for.
            bool foundNothing = _slots.Count == 0 && _abilityViews.Count == 0 &&
                                _panelAbilityViews.Count == 0 && _bottomAbilityViews.Count == 0;
            bool searchNeeded = !_searched || AnySlotDestroyed() || AnyAbilityViewDestroyed() ||
                                AnyPanelAbilityViewDestroyed() || AnyBottomAbilityViewDestroyed() ||
                                (foundNothing && _anythingBlocked != null && _anythingBlocked());
            if (searchNeeded && (!_searched ||
                (Time.realtimeSinceStartup >= _nextSearchAt && _sceneSearchBackoff.ShouldTry())))
            {
                bool foundNew = SearchScene();
                _nextSearchAt = Time.realtimeSinceStartup + SecondsBetweenSearches;
                if (foundNew) _sceneSearchBackoff.Found();
                else _sceneSearchBackoff.Missed();
                CollectVisible();
            }
            _scanStamp++;
            for (int i = 0; i < _tracked.Count; i++)
            {
                Tracked view = _tracked[i];
                MarkView(view.Host, view.Key, StateOf(view), view.Frame);
            }
            SweepUnmarked();
            RecordFrame((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
            _consecutiveFaults = 0;
        }
        catch (Exception e)
        {
            // Not latched on the first throw. A view caught halfway through being rebuilt is a
            // passing condition, and treating one as permanent has cost this mod a session before.
            if (_lastFault != e.Message)
            {
                _lastFault = e.Message;
                MelonLogger.Warning("[TargetingMod] part glow marking failed (will retry). Full fault follows:\n" + e);
            }
            if (++_consecutiveFaults >= FaultsBeforeDisabling)
            {
                _capabilities.DisableGlowIcons(
                    $"item and relic icons unmarkable {_consecutiveFaults} scans in a row: {e.Message}");
                Clear();
            }
        }
    }

    /// <summary>Destroys every mark the mod made. The game's own UI is left exactly as it was.</summary>
    public void Clear()
    {
        foreach (Mark mark in _marks.Values) Mark.Destroy(mark);
        MarkArt.Clear();
        _marks.Clear();
        _tracked.Clear();
        _slots.Clear();
        _visibleSlotIndices.Clear();
        _itemIdStrings.Clear();
        _lastItemIds.Clear();
        _sweepCursor = 0;
        _sawDestroyedSlot = false;
        _fullSweepPending = true;
        _abilityViews.Clear();
        _panelAbilityViews.Clear();
        _bottomAbilityViews.Clear();
        ClearAbilityIdMemos();
        _platesByContainer.Clear();
        _containersWithoutPlate.Clear();
        _cardsByAbilityView.Clear();
        _cardsByPanelAbilityView.Clear();
        _bottomHeroesByAbilityView.Clear();
        _cardsByBottomAbilityView.Clear();
        _ownedSlotByAbilityView.Clear();
        _itemIdField = default;
        _itemIdFieldResolved = false;
        _abilityMismatches = 0;
        _abilityFaults = 0;
        _cardsResolved = 0;
        _cardsUnresolved = 0;
        _peakCardsResolved = 0;
        _peakCardsUnresolved = 0;
        _peakAbilityIcons = 0;
        _peakAbilityMismatches = 0;
        _peakAbilityFaults = 0;
        _relicUi = null;
        _nextSearchAt = 0f;
        _sceneSearchBackoff.Reset();
        _searched = false;
        _worstSearchMs = 0;
        _searches = 0;
        _frames = 0;
        _firstFrameMs = 0;
        _worstFrameMs = 0;
        _totalFrameMs = 0;
    }

    /// <summary>Which family a tracked icon belongs to, because each is keyed differently.</summary>
    private enum PartKind { Item, Relic, Ability }

    // One icon the mod is watching : what it is, where it is, and which frame sprite to trace.
    private readonly struct Tracked
    {
        public readonly Transform Host;
        public readonly Image Frame;
        public readonly string Id;
        // Only an ability uses this : its state is keyed by the HERO as well as by the entry,
        // because two Heroes can carry one rank modifier in opposite states.
        public readonly string OwnerId;
        public readonly int Key;
        public readonly PartKind Kind;

        public Tracked(Transform host, Image frame, string id, string ownerId, int key, PartKind kind)
        {
            Host = host;
            Frame = frame;
            Id = id;
            OwnerId = ownerId;
            Key = key;
            Kind = kind;
        }
    }

    // Whether a slot object the mod is holding has been destroyed. Being switched off is NOT a
    // reason to search again : that is the ordinary state of a pooled slot and the per-frame read
    // below already handles it.
    //
    // Answered from what the sweep saw rather than by walking the list, because walking the list is
    // exactly the 249 interop calls this pass exists to stop paying, and it was being paid twice a
    // frame : once here and once to find the visible slots. The sweep reports a destroyed object as
    // it reaches it, so the answer costs nothing and arrives at most one discovery window late,
    // which the one second floor between searches already dwarfs.
    private bool AnySlotDestroyed() => _sawDestroyedSlot;

    private bool AnyAbilityViewDestroyed()
    {
        for (int i = 0; i < _abilityViews.Count; i++)
            if (_abilityViews[i] == null) return true;
        return false;
    }

    private bool AnyPanelAbilityViewDestroyed()
    {
        for (int i = 0; i < _panelAbilityViews.Count; i++)
            if (_panelAbilityViews[i] == null) return true;
        return false;
    }

    private bool AnyBottomAbilityViewDestroyed()
    {
        for (int i = 0; i < _bottomAbilityViews.Count; i++)
            if (_bottomAbilityViews[i] == null) return true;
        return false;
    }

    public bool SceneSearched => _searched;

    public bool PrewarmSearch()
    {
        if (_searched) return true;
        bool foundNew = SearchScene();
        _nextSearchAt = Time.realtimeSinceStartup + SecondsBetweenSearches;
        if (foundNew) _sceneSearchBackoff.Found();
        else _sceneSearchBackoff.Missed();
        return true;
    }

    // The expensive half, and the only half that is rare.
    //
    // Every equipment slot object in the scene is asked directly, rather than finding the panel
    // that owns them and walking its children. A slot carries its own item id, so it can answer for
    // itself wherever the game happens to be showing it, and the mod never has to know which panel
    // is up. That is one binding instead of one per panel, and a panel the game adds later is
    // covered without a change here.
    private bool SearchScene()
    {
        long start = Stopwatch.GetTimestamp();
        bool foundNew = false;
        if (_slots.Count == 0 || AnySlotDestroyed())
        {
            int liveBefore = 0;
            for (int i = 0; i < _slots.Count; i++)
                if (_slots[i] != null) liveBefore++;
            _slots.Clear();
            _visibleSlotIndices.Clear();
            _itemIdStrings.Clear();
            _lastItemIds.Clear();
            _sweepCursor = 0;
            _sawDestroyedSlot = false;
            // The set of slots just changed underneath the sweep, so nothing is known to be on
            // screen any more. Look at all of them once rather than fading the marks in.
            _fullSweepPending = true;
            List<PlaceholderSlotView> found = RuntimeDiscovery.FindAll<PlaceholderSlotView>();
            for (int i = 0; i < found.Count; i++)
            {
                PlaceholderSlotView slot = found[i];
            // Switched-off slots are KEPT. The search deliberately includes objects that are loaded
            // but not shown, which is what makes it the right search for a UI built out of pools.
            // What must be excluded is a PREFAB : the same search hands those back, and parenting a
            // mark under one would write the mod into an asset the game instantiates from.
                if (slot == null || !slot.gameObject.scene.IsValid()) continue;
                _slots.Add(slot);
            }
            if (_slots.Count > liveBefore) foundNew = true;
        }
        if (_abilityViews.Count == 0 || AnyAbilityViewDestroyed())
        {
            int liveBefore = 0;
            for (int i = 0; i < _abilityViews.Count; i++)
                if (_abilityViews[i] != null) liveBefore++;
            _abilityViews.Clear();
            ClearAbilityIdMemos();
            List<HeroRankModifiersView> cards = RuntimeDiscovery.FindAll<HeroRankModifiersView>();
            for (int i = 0; i < cards.Count; i++)
            {
                HeroRankModifiersView card = cards[i];
            // A prefab answers this search too, and parenting a mark under one would write the mod
            // into an asset the game instantiates from. Same guard as the item slots above.
                if (card == null || !card.gameObject.scene.IsValid()) continue;
                _abilityViews.Add(card);
            }
            if (_abilityViews.Count > liveBefore) foundNew = true;
        }
        if (_panelAbilityViews.Count == 0 || AnyPanelAbilityViewDestroyed())
        {
            int liveBefore = 0;
            for (int i = 0; i < _panelAbilityViews.Count; i++)
                if (_panelAbilityViews[i] != null) liveBefore++;
            _panelAbilityViews.Clear();
            _cardsByPanelAbilityView.Clear();
            List<HeroPanelAbilitiesView> panels = RuntimeDiscovery.FindAll<HeroPanelAbilitiesView>();
            for (int i = 0; i < panels.Count; i++)
            {
                HeroPanelAbilitiesView panel = panels[i];
                // A prefab here would receive marks that every later placement bar inherits.
                if (panel == null || !panel.gameObject.scene.IsValid()) continue;
                _panelAbilityViews.Add(panel);
            }
            if (_panelAbilityViews.Count > liveBefore) foundNew = true;
        }
        if (_bottomAbilityViews.Count == 0 || AnyBottomAbilityViewDestroyed())
        {
            int liveBefore = 0;
            for (int i = 0; i < _bottomAbilityViews.Count; i++)
                if (_bottomAbilityViews[i] != null) liveBefore++;
            _bottomAbilityViews.Clear();
            _bottomHeroesByAbilityView.Clear();
            _cardsByBottomAbilityView.Clear();
            List<HeroCardAbilitiesView> views = RuntimeDiscovery.FindAll<HeroCardAbilitiesView>();
            for (int i = 0; i < views.Count; i++)
            {
                HeroCardAbilitiesView view = views[i];
                // A prefab here would receive marks that every later bottom Hero bar inherits.
                if (view == null || !view.gameObject.scene.IsValid()) continue;
                _bottomAbilityViews.Add(view);
            }
            if (_bottomAbilityViews.Count > liveBefore) foundNew = true;
        }
        if (_relicUi == null)
        {
            _relicUi = RuntimeDiscovery.FindLive<RelicUIController>();
            if (_relicUi != null) foundNew = true;
        }
        _searched = true;
        _searches++;
        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        if (ms > _worstSearchMs) _worstSearchMs = ms;
        return foundNew;
    }

    // The cheap half, every frame : which of the slots found above are on screen right now, and
    // what each is showing. This is what makes a mark appear with the panel that carries it.
    private void CollectVisible()
    {
        // The slots already on screen, re-checked EVERY call. There are about nine of them, so this
        // is the cheap half, and it is deliberately not amortised : a panel closing has to be
        // noticed at once, because a mark left behind on a hidden icon is a wrong statement about
        // the board. A mark that arrives a moment late is not. The asymmetry is the whole design.
        for (int i = _visibleSlotIndices.Count - 1; i >= 0; i--)
        {
            int index = _visibleSlotIndices[i];
            PlaceholderSlotView slot = _slots[index];
            if (slot == null)
            {
                _sawDestroyedSlot = true;
                _visibleSlotIndices.RemoveAt(i);
            }
            else if (!slot.gameObject.activeInHierarchy)
            {
                _visibleSlotIndices.RemoveAt(i);
            }
        }

        // The slots NOT on screen, which is nearly all of them : 249 objects in a real placement,
        // to find the 9 that are showing. Asking all of them every frame is an interop call per
        // object per frame and was the single most expensive thing the mod did.
        //
        // So the sweep covers the whole set every DiscoverySeconds instead, however many frames
        // that takes. Per second the cost is fixed ; per frame it adapts, which is the property a
        // frame count can never have. At 60 frames a second this looks at about a sixth of the set
        // per frame, at 20 it looks at half, at 200 it looks at a twentieth, and in all three cases
        // a panel that opens is picked up within the same tenth of a second.
        //
        // A full pass is forced after a scene search, because then NOTHING is known to be visible
        // and amortising would fade the marks in over the window instead of drawing them at once.
        int slice;
        if (_fullSweepPending)
        {
            slice = _slots.Count;
            _fullSweepPending = false;
        }
        else
        {
            slice = Math.Max(1, (int)Math.Ceiling(_slots.Count * Time.unscaledDeltaTime / DiscoverySeconds));
            slice = Math.Min(slice, _slots.Count);
        }
        int examined = 0;
        int visited = 0;
        while (examined < slice && visited < _slots.Count)
        {
            if (_sweepCursor >= _slots.Count) _sweepCursor = 0;
            int index = _sweepCursor++;
            visited++;
            // Kept sorted, so membership is a binary search rather than a scan. The loop above is
            // the only thing that removes, and it removes back to front, so both stay ordered.
            int found = _visibleSlotIndices.BinarySearch(index);
            if (found >= 0) continue; // already known to be showing, and re-checked above.
            examined++;

            PlaceholderSlotView slot = _slots[index];
            if (slot == null)
            {
                _sawDestroyedSlot = true;
                continue;
            }
            if (!slot.gameObject.activeInHierarchy) continue;
            _visibleSlotIndices.Insert(~found, index);
        }

        _tracked.Clear();
        for (int i = 0; i < _visibleSlotIndices.Count; i++)
        {
            PlaceholderSlotView slot = _slots[_visibleSlotIndices[i]];
            // Through the field rather than the property it belongs to. The generated getter for a
            // nullable boxes it first, and boxing an empty one hands back a null pointer : an empty
            // slot is the ordinary case here, not the exception.
            if (!_itemIdFieldResolved)
            {
                _itemIdField = NullableRaw.ResolveField<ItemId>(slot, "<ItemId>k__BackingField");
                _itemIdFieldResolved = true;
            }
            if (!NullableRaw.TryRead(slot, _itemIdField, out ItemId itemId)) continue;
            int key = slot.GetInstanceID();
            // Turning an id into text is an interop call that allocates, and it was being paid for
            // every showing slot on every frame to produce the same answer. Kept until the slot's
            // item actually changes, which a pooled slot reused for a different item does do, so
            // the last id is stored beside the text rather than the text being trusted on its own.
            if (!_lastItemIds.TryGetValue(key, out ItemId lastItemId) ||
                !NullableRaw.SameRawId(in lastItemId, in itemId))
            {
                _lastItemIds[key] = itemId;
                _itemIdStrings[key] = itemId.ToString();
            }
            _tracked.Add(new Tracked(slot.transform, slot._frameImage, _itemIdStrings[key], null,
                key, PartKind.Item));
        }
        CollectRelics();
        CollectAbilities();
    }

    /// <summary>The rank modifiers, passives and specialization shown on a Hero's card.</summary>
    /// <remarks>
    /// Eighteen of the thirty-one entries this feature can light up are hero-owned, which makes this
    /// the LARGEST of the three surfaces and the one that went longest without an icon to point at.
    ///
    /// The view does not record which entry each of its slots is showing. It packs them into one
    /// flat array in a fixed order, the active ability, then the passives, then the rank modifiers,
    /// keeping only the sprite. That order is replayed here from the Hero's own data.
    ///
    /// Replaying an order is a guess about someone else's code, so it is CHECKED rather than
    /// trusted : the entry this walk believes a slot holds must be the entry whose icon that slot is
    /// actually displaying, and a slot that disagrees is skipped. A mark on the wrong ability is
    /// worse than no mark at all, because the whole contract of this feature is that it shows less
    /// rather than something untrue. If the game ever repacks that array, this goes quiet and says
    /// so in the log instead of pointing at innocent icons.
    /// </remarks>
    private void CollectAbilities()
    {
        // Per pass, like _tracked itself, so the report describes the last frame rather than
        // accumulating a number that only grows with how long the placement lasted.
        _cardsResolved = 0;
        _cardsUnresolved = 0;
        _abilityMismatches = 0;
        _abilityFaults = 0;
        for (int i = 0; i < _abilityViews.Count; i++)
        {
            HeroRankModifiersView view = _abilityViews[i];
            if (view == null || !view.gameObject.activeInHierarchy) continue;
            // Per view, so one card that is mid-rebuild cannot cost the other cards on screen.
            try { CollectOneCard(view); }
            catch (Exception e) { NoteAbilityFault(e); }
        }
        for (int i = 0; i < _panelAbilityViews.Count; i++)
        {
            HeroPanelAbilitiesView view = _panelAbilityViews[i];
            if (view == null) continue;
            // One panel can be handed new data while the other cards remain safe to read.
            try
            {
                if (!view.gameObject.activeInHierarchy) continue;
                CollectOnePanelCard(view);
            }
            catch (Exception e) { NoteAbilityFault(e); }
        }
        for (int i = 0; i < _bottomAbilityViews.Count; i++)
        {
            HeroCardAbilitiesView view = _bottomAbilityViews[i];
            if (view == null) continue;
            // One empty or rebuilding bar slot must not cost the other Heroes on screen.
            try
            {
                if (!view.gameObject.activeInHierarchy) continue;
                CollectOneBottomCard(view);
            }
            catch (Exception e) { NoteAbilityFault(e); }
        }
        if (_cardsResolved > _peakCardsResolved) _peakCardsResolved = _cardsResolved;
        if (_cardsUnresolved > _peakCardsUnresolved) _peakCardsUnresolved = _cardsUnresolved;
        if (_abilityMismatches > _peakAbilityMismatches) _peakAbilityMismatches = _abilityMismatches;
        if (_abilityFaults > _peakAbilityFaults) _peakAbilityFaults = _abilityFaults;
        int abilityIcons = AbilityIconsNow;
        if (abilityIcons > _peakAbilityIcons) _peakAbilityIcons = abilityIcons;
    }

    private void CollectOnePanelCard(HeroPanelAbilitiesView view)
    {
        var slots = view._cachedAbilityViews;
        if (slots == null || slots.Count == 0) return;
        IReadOnlyHeroData data = ResolvePanelHero(view);
        if (data == null) { _cardsUnresolved++; return; }
        _cardsResolved++;

        IntPtr viewKey = view.Pointer;
        EntityId heroId = data.HeroId.ToEntityId();
        if (!_lastHeroIds.TryGetValue(viewKey, out EntityId lastHeroId) ||
            !NullableRaw.SameRawId(in lastHeroId, in heroId))
        {
            _lastHeroIds[viewKey] = heroId;
            _heroKeysByCard[viewKey] = heroId.ToString();
        }
        string heroKey = _heroKeysByCard[viewKey];

        ICharacterEntry heroEntry = data.HeroRef.ToEntry()?.TryCast<ICharacterEntry>();
        if (heroEntry == null) return;
        ICharacterData characterData = data.TryCast<ICharacterData>();
        if (characterData == null) return;

        // Read the concrete rank dictionary before naming fixed slots. Rank modifiers replace those
        // slots, and missing the collision would mark a rank icon as the starting or specialization
        // role.
        var ranks = characterData.AppliedRankModifiers?
            .TryCast<Il2CppSystem.Collections.Generic.Dictionary<int, BalancingRef<IRankModifierEntry>>>();
        if (ranks == null) { _abilityMismatches++; return; }
        HashSet<int> rankIndices = new();
        foreach (int key in ranks.Keys) rankIndices.Add(key + 1);

        Sprite startingIcon = null;
        if (heroEntry.StartWithActiveAbility)
        {
            BalancingRef<IActiveAbilityEntry> abilityRef = heroEntry.ActiveAbilityRef;
            IActiveAbilityEntry entry = abilityRef.ToEntry();
            if (entry != null) startingIcon = entry.Icon;
        }
        else
        {
            BalancingRef<IPassiveAbilityEntry> abilityRef = heroEntry.PassiveAbilityRef;
            IPassiveAbilityEntry entry = abilityRef.ToEntry();
            if (entry != null) startingIcon = entry.Icon;
        }
        if (!rankIndices.Contains(0) && startingIcon != null)
            TrackPanelAbilitySlot(slots, 0, heroKey, PlacementSnapshot.StartingAbilityRole, startingIcon);

        List<Sprite> rankIcons = new();
        foreach (int key in ranks.Keys)
        {
            BalancingRef<IRankModifierEntry> rankRef = ranks[key];
            IRankModifierEntry entry = rankRef.ToEntry();
            if (entry != null)
            {
                if (entry.Icon != null) rankIcons.Add(entry.Icon);
                TrackPanelAbilitySlot(slots, key + 1, heroKey,
                    PlacementSnapshot.RankModifierRole(rankRef.Id.ToString()), entry.Icon);
            }
        }

        // Specialization is a nullable BalancingRef and cannot be read safely. Elimination avoids
        // that interop fault, while rejecting a known icon prevents a colliding start or rank slot
        // from being mislabeled as the specialization.
        if (!rankIndices.Contains(1) && slots.Count > 1)
        {
            HeroPanelAbilityView abilityView = slots[1];
            Image icon = abilityView?._icon;
            if (abilityView != null && abilityView.gameObject.activeInHierarchy && icon != null &&
                icon.sprite != null)
            {
                bool knownIcon = icon.sprite == startingIcon;
                for (int i = 0; !knownIcon && i < rankIcons.Count; i++)
                    knownIcon = icon.sprite == rankIcons[i];
                if (knownIcon) _abilityMismatches++;
                else TrackPanelAbilitySlot(slots, 1, heroKey,
                    PlacementSnapshot.SpecializationRole, icon.sprite);
            }
        }
    }

    private void TrackPanelAbilitySlot(Il2CppSystem.Collections.Generic.List<HeroPanelAbilityView> slots,
        int index, string heroKey, string entryKey, Sprite expected)
    {
        if (index < 0 || index >= slots.Count) return;
        HeroPanelAbilityView abilityView = slots[index];
        if (abilityView == null || !abilityView.gameObject.activeInHierarchy) return;
        Image icon = abilityView._icon;
        if (icon == null) return;

        // A changed packing rule must silence this slot, not accuse a different ability.
        if (expected == null || icon.sprite != expected) { _abilityMismatches++; return; }

        // Use the filing side's role verbatim. Entry ids cannot be recovered from its non-blittable
        // nullable origin data, so using an entry id here would split lookup from filing again.
        _tracked.Add(new Tracked(abilityView.transform, FindPanelPlate(abilityView, icon), entryKey,
            heroKey, abilityView.GetInstanceID(), PartKind.Ability));
    }

    // One card the ability walk could not read. Counted for the report, and the WHOLE exception is
    // said once per session.
    //
    // The message on its own is not a diagnosis. "Object reference not set to an instance of an
    // object" is what four bottom-bar cards reported on 2026-08-20 and it names no line, no surface
    // and no field, which is a play session spent learning what one stack trace carries for free.
    // The mod ships its symbol file beside itself precisely so this names a line. Same reasoning, and
    // the same shape, as ReportEffectFault in PositionalGlow.
    private void NoteAbilityFault(Exception e)
    {
        _abilityFaults++;
        _lastAbilityFault = e.Message;
        if (_loggedAbilityFault) return;
        _loggedAbilityFault = true;
        MelonLogger.Warning("[TargetingMod] an ability card could not be read; the rest of the board " +
                            "is unaffected. Full fault follows:\n" + e);
    }

    private void CollectOneBottomCard(HeroCardAbilitiesView view)
    {
        var slots = view._abilities;
        if (slots == null || slots.Length == 0) return;
        IReadOnlyHeroData data = ResolveBottomHero(view);
        if (data == null) { _cardsUnresolved++; return; }
        _cardsResolved++;

        IntPtr viewKey = view.Pointer;
        EntityId heroId = data.HeroId.ToEntityId();
        if (!_lastHeroIds.TryGetValue(viewKey, out EntityId lastHeroId) ||
            !NullableRaw.SameRawId(in lastHeroId, in heroId))
        {
            _lastHeroIds[viewKey] = heroId;
            _heroKeysByCard[viewKey] = heroId.ToString();
        }
        string heroKey = _heroKeysByCard[viewKey];

        ICharacterEntry heroEntry = data.HeroRef.ToEntry()?.TryCast<ICharacterEntry>();
        if (heroEntry == null) return;
        ICharacterData characterData = data.TryCast<ICharacterData>();
        if (characterData == null) return;

        // This view puts a rank at key - 1. Reserve those indices first or a rank collision can be
        // falsely named as the starting ability or specialization.
        var ranks = characterData.AppliedRankModifiers?
            .TryCast<Il2CppSystem.Collections.Generic.Dictionary<int, BalancingRef<IRankModifierEntry>>>();
        if (ranks == null) { _abilityMismatches++; return; }
        HashSet<int> rankIndices = new();
        foreach (int key in ranks.Keys) rankIndices.Add(key - 1);

        Sprite startingIcon = null;
        if (heroEntry.StartWithActiveAbility)
        {
            BalancingRef<IActiveAbilityEntry> abilityRef = heroEntry.ActiveAbilityRef;
            IActiveAbilityEntry entry = abilityRef.ToEntry();
            if (entry != null) startingIcon = entry.Icon;
        }
        else
        {
            BalancingRef<IPassiveAbilityEntry> abilityRef = heroEntry.PassiveAbilityRef;
            IPassiveAbilityEntry entry = abilityRef.ToEntry();
            if (entry != null) startingIcon = entry.Icon;
        }
        if (!rankIndices.Contains(0) && startingIcon != null)
            TrackBottomAbilitySlot(slots, 0, heroKey, PlacementSnapshot.StartingAbilityRole, startingIcon);

        List<Sprite> rankIcons = new();
        foreach (int key in ranks.Keys)
        {
            BalancingRef<IRankModifierEntry> rankRef = ranks[key];
            IRankModifierEntry entry = rankRef.ToEntry();
            if (entry != null)
            {
                if (entry.Icon != null) rankIcons.Add(entry.Icon);
                TrackBottomAbilitySlot(slots, key - 1, heroKey,
                    PlacementSnapshot.RankModifierRole(rankRef.Id.ToString()), entry.Icon);
            }
        }

        // Specialization cannot be read because its nullable BalancingRef is non-blittable. Accept
        // only an otherwise unknown visible icon, so a packing collision cannot mark a known part
        // under the specialization role.
        if (!rankIndices.Contains(1) && slots.Length > 1)
        {
            HeroCardAbilityView abilityView = slots[1];
            Image icon = abilityView?._abilityIcon;
            if (abilityView != null && abilityView.gameObject.activeInHierarchy && icon != null &&
                icon.sprite != null)
            {
                bool knownIcon = icon.sprite == startingIcon;
                for (int i = 0; !knownIcon && i < rankIcons.Count; i++)
                    knownIcon = icon.sprite == rankIcons[i];
                if (knownIcon) _abilityMismatches++;
                else TrackBottomAbilitySlot(slots, 1, heroKey,
                    PlacementSnapshot.SpecializationRole, icon.sprite);
            }
        }
    }

    private void TrackBottomAbilitySlot(Il2CppReferenceArray<HeroCardAbilityView> slots, int index,
        string heroKey, string entryKey, Sprite expected)
    {
        if (index < 0 || index >= slots.Length) return;
        HeroCardAbilityView abilityView = slots[index];
        if (abilityView == null || !abilityView.gameObject.activeInHierarchy) return;
        Image icon = abilityView._abilityIcon;
        if (icon == null) return;

        // A packing collision or changed rule must silence this guess, not mark another ability.
        if (expected == null || icon.sprite != expected) { _abilityMismatches++; return; }

        // Store the semantic role unchanged. The filing side cannot recover an entry id from its
        // non-blittable nullable origin data, so an entry-id lookup could never match its role key.
        _tracked.Add(new Tracked(abilityView.transform, abilityView._frameImage, entryKey, heroKey,
            abilityView.GetInstanceID(), PartKind.Ability));
    }

    private void CollectOneCard(HeroRankModifiersView view)
    {
        Il2CppReferenceArray<HeroRankModifiersView.Modifier> slots = view._modifiers;
        if (slots == null || slots.Length == 0) return;
        // A full card carries the Hero directly, but the placement bar's MiniHeroCard does not.
        // Its owned equipment slot is the only identity kept inside that card's hierarchy.
        IReadOnlyHeroData data = ResolveHero(view);
        if (data == null) { _cardsUnresolved++; return; }
        _cardsResolved++;
        IntPtr viewKey = view.Pointer;
        EntityId heroId = data.HeroId.ToEntityId();
        if (!_lastHeroIds.TryGetValue(viewKey, out EntityId lastHeroId) ||
            !NullableRaw.SameRawId(in lastHeroId, in heroId))
        {
            _lastHeroIds[viewKey] = heroId;
            _heroKeysByCard[viewKey] = heroId.ToString();
        }
        string heroKey = _heroKeysByCard[viewKey];
        // The card is handed the read-only face of a Hero, which carries identity but not the
        // ability lists ; those live on the character face of the same object. Cast rather than
        // assume : a card showing something that is not a full character simply gets no marks.
        ICharacterData hero = data.TryCast<ICharacterData>();
        if (hero == null) return;
        ICharacterEntry heroEntry = data.HeroRef.ToEntry()?.TryCast<ICharacterEntry>();
        if (heroEntry == null) return;

        int slot = 0;
        // The active slot can belong to a specialization. Only a Hero that starts active proves this
        // slot has the starting role; otherwise tracking it would name an indistinguishable ability.
        if (hero.ActiveAbility.HasValue())
        {
            IActiveAbilityEntry active = hero.ActiveAbility.ToEntry();
            if (heroEntry.StartWithActiveAbility && active != null)
                TrackAbilitySlot(slots, ref slot, viewKey, heroKey,
                    PlacementSnapshot.StartingAbilityRole, active.Icon);
            else slot++;
        }

        // Cast to the concrete collections. The read-only interfaces the game hands out expose an
        // indexer but no count, and their Keys/Values are il2cpp enumerables that C# cannot walk
        // directly. A cast that comes back null costs only the marks on this card, which is the
        // right way for a guess about someone else's container to fail.
        var passives = hero.PassiveAbilities?
            .TryCast<Il2CppSystem.Collections.Generic.List<BalancingRef<IPassiveAbilityEntry>>>();
        // PassiveAbilities folds the specialization passive into the starting passives, so none of
        // them has a provable role. Skip every passive but advance across them; failing to advance
        // would shift rank roles onto innocent passive icons.
        if (passives != null) slot += passives.Count;
        else { _abilityMismatches++; return; }

        var ranks = hero.AppliedRankModifiers?
            .TryCast<Il2CppSystem.Collections.Generic.Dictionary<int, BalancingRef<IRankModifierEntry>>>();
        if (ranks != null)
            foreach (BalancingRef<IRankModifierEntry> rankRef in ranks.Values)
            {
                IRankModifierEntry entry = rankRef.ToEntry();
                if (entry != null) TrackAbilitySlot(slots, ref slot, viewKey, heroKey,
                    PlacementSnapshot.RankModifierRole(rankRef.Id.ToString()), entry.Icon);
            }
        else _abilityMismatches++;
    }

    private void TrackAbilitySlot(Il2CppReferenceArray<HeroRankModifiersView.Modifier> slots, ref int slot,
        IntPtr viewKey, string heroKey, string entryKey, Sprite expected)
    {
        if (slot >= slots.Length) return;
        int slotIndex = slot;
        HeroRankModifiersView.Modifier modifier = slots[slot];
        slot++;
        if (modifier == null || modifier.Container == null || modifier.Image == null) return;
        if (!modifier.Container.activeInHierarchy) return;

        // THE CHECK. If the icon on screen is not the icon this entry owns, the packing order is not
        // what this walk assumes and nothing here can be trusted, so nothing is marked.
        if (expected == null || modifier.Image.sprite != expected) { _abilityMismatches++; return; }

        // Keep the role key verbatim. The filing side's non-blittable nullable entry id became an
        // all-zero guid, so an entry-id lookup made a correctly blocked filing invisible here.
        _tracked.Add(new Tracked(modifier.Container.transform, FindPlate(modifier), entryKey, heroKey,
            modifier.Container.GetInstanceID(), PartKind.Ability));
    }

    private void ClearAbilityIdMemos()
    {
        _heroKeysByCard.Clear();
        _lastHeroIds.Clear();
    }

    private IReadOnlyHeroData ResolveHero(HeroRankModifiersView view)
    {
        IntPtr key;
        try { key = view.Pointer; }
        catch { return null; }
        if (key == IntPtr.Zero) return null;

        // Read THROUGH the remembered source rather than trusting a remembered answer. Reaching
        // through a wrapper whose native object the scene destroyed throws, so a dead source drops
        // out of the cache here and the search runs again.
        if (_cardsByAbilityView.TryGetValue(key, out HeroCardView cachedCard))
        {
            IReadOnlyHeroData hero = HeroOf(cachedCard);
            if (hero != null) return hero;
            _cardsByAbilityView.Remove(key);
        }
        if (_ownedSlotByAbilityView.TryGetValue(key, out PlaceholderSlotView cachedSlot))
        {
            IReadOnlyHeroData hero = HeroOf(cachedSlot);
            if (hero != null) return hero;
            _ownedSlotByAbilityView.Remove(key);
        }

        HeroCardView card = FindFullCard(view);
        IReadOnlyHeroData fromCard = HeroOf(card);
        if (fromCard != null)
        {
            _cardsByAbilityView[key] = card;
            return fromCard;
        }
        PlaceholderSlotView slot = FindOwnedSlot(view);
        IReadOnlyHeroData fromSlot = HeroOf(slot);
        if (fromSlot != null)
        {
            _ownedSlotByAbilityView[key] = slot;
            return fromSlot;
        }
        // Missing is deliberately NOT remembered. A MiniHeroCard is filled in after the scene
        // appears, and the negative cache this replaced turned that passing state into a card that
        // stayed unmarked for the rest of the placement.
        return null;
    }

    private IReadOnlyHeroData ResolvePanelHero(HeroPanelAbilitiesView view)
    {
        IntPtr key;
        try { key = view.Pointer; }
        catch { return null; }
        if (key == IntPtr.Zero) return null;

        if (_cardsByPanelAbilityView.TryGetValue(key, out HeroCardView cachedCard))
        {
            IReadOnlyHeroData hero = HeroOf(cachedCard);
            if (hero != null) return hero;
            _cardsByPanelAbilityView.Remove(key);
        }

        HeroCardView card;
        try { card = view.GetComponentInParent<HeroCardView>(); }
        catch { return null; }
        IReadOnlyHeroData fromCard = HeroOf(card);
        if (fromCard == null) return null;
        _cardsByPanelAbilityView[key] = card;
        return fromCard;
    }

    private IReadOnlyHeroData ResolveBottomHero(HeroCardAbilitiesView view)
    {
        IntPtr key;
        try { key = view.Pointer; }
        catch { return null; }
        if (key == IntPtr.Zero) return null;

        // Cache the source, not the answer. A pooled bottom slot can be handed to another Hero.
        if (_bottomHeroesByAbilityView.TryGetValue(key, out BottomHeroView cachedBottom))
        {
            IReadOnlyHeroData hero = HeroOf(cachedBottom);
            if (hero != null) return hero;
            _bottomHeroesByAbilityView.Remove(key);
        }
        if (_cardsByBottomAbilityView.TryGetValue(key, out HeroCardView cachedCard))
        {
            IReadOnlyHeroData hero = HeroOf(cachedCard);
            if (hero != null) return hero;
            _cardsByBottomAbilityView.Remove(key);
        }

        BottomHeroView bottom;
        try { bottom = view.GetComponentInParent<BottomHeroView>(); }
        catch { return null; }
        IReadOnlyHeroData fromBottom = HeroOf(bottom);
        if (fromBottom != null)
        {
            _bottomHeroesByAbilityView[key] = bottom;
            return fromBottom;
        }

        HeroCardView card;
        try { card = view.GetComponentInParent<HeroCardView>(); }
        catch { return null; }
        IReadOnlyHeroData fromCard = HeroOf(card);
        if (fromCard == null) return null;
        _cardsByBottomAbilityView[key] = card;
        return fromCard;
    }

    private static IReadOnlyHeroData HeroOf(HeroCardView card)
    {
        try { return card != null ? card._heroData : null; }
        catch { return null; }
    }

    private static IReadOnlyHeroData HeroOf(PlaceholderSlotView slot)
    {
        try
        {
            if (slot == null) return null;
            if (!NullableRaw.TryReadNullableAt(slot, "<OwnerHeroId>k__BackingField", out HeroId id))
                return null;
            return HeroFromRegistry(id);
        }
        catch { return null; }
    }

    private static IReadOnlyHeroData HeroOf(BottomHeroView view)
    {
        try
        {
            if (view == null) return null;
            // The getter boxes HeroId?, and an empty bar slot then becomes a null native pointer.
            if (!NullableRaw.TryReadNullableAt(view, "<HeroId>k__BackingField", out HeroId id))
                return null;
            return HeroFromRegistry(id);
        }
        catch { return null; }
    }

    private static HeroCardView FindFullCard(HeroRankModifiersView view)
    {
        try { return view.GetComponentInParent<HeroCardView>(); }
        catch { return null; }
    }

    // Which Hero a card belongs to when the card is not a full HeroCardView.
    //
    // A MiniHeroCard keeps no Hero of its own. What it does keep is the equipment view sitting
    // beside the ability icons, and the game stamps every equipment slot with its owner
    // (HeroEquipmentView.UpdateEquipmentSlots -> PlaceholderSlotView.Initialize(index, HeroId)). So
    // the identity is one level out from the icons and nowhere else inside that hierarchy.
    //
    // TWO RULES HOLD THIS UP AND BOTH ARE THE DIFFERENCE BETWEEN A MARK AND A WRONG MARK.
    //
    // The climb goes ONE parent at a time and stops the moment an ancestor covers more than one
    // Hero. The placement bar puts every card under a single shared parent, so a walk that just kept
    // climbing would eventually find SOMEBODY's slot and mark this card with another Hero's answers.
    // An ancestor holding two owners is proof the walk has left the card, and nothing at all is the
    // honest answer there. That also settles a case the game creates by itself: a slot is only built
    // when `_showEmpty || item.HasValue`, so a Hero carrying no items owns no slot, and this returns
    // null for them instead of borrowing the neighbour they happen to sit beside.
    //
    // The owner is read through NullableRaw and never through the generated getter. HeroId? is a
    // nullable value type, the getter boxes it first, and boxing an empty one hands back a null
    // pointer : an unowned slot is the ordinary case here rather than the exception. It is the same
    // trap the item id further up is read around, for the same reason, and it was walked into again
    // on the first pass at this method.
    private static PlaceholderSlotView FindOwnedSlot(HeroRankModifiersView view)
    {
        Transform ancestor;
        try { ancestor = view.transform.parent; }
        catch { return null; }

        for (int level = 0; level < MaxCardClimb; level++)
        {
            try { if (ancestor == null) return null; }
            catch { return null; }

            PlaceholderSlotView ownerSlot = null;
            HeroId ownerHeroId = default;
            bool foundOwner = false;
            bool ambiguous = false;
            try
            {
                var slots = ancestor.GetComponentsInChildren<PlaceholderSlotView>(true);
                if (slots != null)
                    for (int i = 0; i < slots.Length; i++)
                    {
                        PlaceholderSlotView slot = slots[i];
                        if (slot == null) continue;
                        if (!NullableRaw.TryReadNullableAt(slot, "<OwnerHeroId>k__BackingField",
                                out HeroId id))
                            continue;
                        if (!foundOwner)
                        {
                            ownerSlot = slot;
                            ownerHeroId = id;
                            foundOwner = true;
                            continue;
                        }
                        // A second, different owner under the same ancestor means this is no longer
                        // one card. Stop rather than pick whichever came back first.
                        if (NullableRaw.SameRawId(in ownerHeroId, in id)) continue;
                        ambiguous = true;
                        break;
                    }
            }
            catch
            {
                // Climbing past an unreadable level could reach the shared bottom-bar parent and
                // borrow another Hero's slot, which is worse than leaving this card unmarked.
                return null;
            }

            if (ambiguous) return null;
            if (foundOwner) return ownerSlot;
            try { ancestor = ancestor.parent; }
            catch { return null; }
        }
        return null;
    }

    private static IReadOnlyHeroData HeroFromRegistry(HeroId heroId)
    {
        try
        {
            if (!DataReaders.TryGet<GameRegistryDataReader>(out var registry) || registry == null)
                return null;
            if (registry.Data == null || registry.Data.Heroes == null) return null;
            if (!registry.Data.Heroes.TryGetValue(heroId, out HeroData hero) || hero == null) return null;
            return hero.TryCast<IReadOnlyHeroData>();
        }
        catch { return null; }
    }

    // The diamond plate behind an ability icon. It is not a field on anything : the view names the
    // icon and the container and nothing else, so the plate is found rather than asked for.
    //
    // Guessing wrong here is SAFE, which is why a guess is acceptable. Whatever is handed to MarkArt
    // is measured before it is used, and a sprite that is not a band of four straight edges is
    // refused and the icon keeps the strike alone. The shape gate is what makes this honest.
    private Image FindPlate(HeroRankModifiersView.Modifier modifier)
    {
        IntPtr key = modifier.Container.Pointer;
        if (_platesByContainer.TryGetValue(key, out Image cached))
        {
            if (_containersWithoutPlate.Contains(key)) return null;
            if (cached != null) return cached;
        }
        var images = modifier.Container.GetComponentsInChildren<Image>(true);
        if (images != null)
        {
            for (int i = 0; i < images.Length; i++)
            {
                Image candidate = images[i];
                if (candidate == null || candidate == modifier.Image) continue;
                if (candidate.sprite == null) continue;
                _platesByContainer[key] = candidate;
                _containersWithoutPlate.Remove(key);
                return candidate;      // hierarchy order, so the one drawn behind comes first
            }
        }
        _platesByContainer[key] = null;
        _containersWithoutPlate.Add(key);
        return null;
    }

    private Image FindPanelPlate(HeroPanelAbilityView abilityView, Image icon)
    {
        IntPtr key = abilityView.Pointer;
        if (_platesByContainer.TryGetValue(key, out Image cached))
        {
            if (_containersWithoutPlate.Contains(key)) return null;
            if (cached != null) return cached;
        }
        var images = abilityView.GetComponentsInChildren<Image>(true);
        if (images != null)
        {
            for (int i = 0; i < images.Length; i++)
            {
                Image candidate = images[i];
                if (candidate == null || candidate == icon) continue;
                if (candidate.sprite == null) continue;
                _platesByContainer[key] = candidate;
                _containersWithoutPlate.Remove(key);
                return candidate;
            }
        }
        _platesByContainer[key] = null;
        _containersWithoutPlate.Add(key);
        return null;
    }

    private PartState StateOf(Tracked view)
    {
        switch (view.Kind)
        {
            case PartKind.Relic: return _forRelic(view.Id);
            case PartKind.Ability:
                PartState abilityState = _forAbility != null
                    ? _forAbility(view.OwnerId, view.Id)
                    : PartState.NotPositional;
                // The other end of the key, said once. PositionalGlow prints the key it FILED; this
                // prints the key being LOOKED UP and what came back. Two lines in one log answer a
                // question that is otherwise invisible: an icon that is tracked, matched to its
                // entry, and still never marked looks exactly like an icon whose part is simply
                // paying, and only the spelling of the key tells them apart.
                if (!_loggedAbilityLookup && DevLog?.Invoke() == true)
                {
                    _loggedAbilityLookup = true;
                    MelonLogger.Msg($"[TargetingMod] ability state LOOKED UP with key " +
                                    $"'{PlacementSnapshot.AbilityKey(view.OwnerId, view.Id)}' = {abilityState}");
                }
                // And the event that actually matters, said the first time it happens. If this line
                // appears and no border does, the lookup is fine and the fault is in the drawing;
                // if it never appears, nothing was ever asking for a mark. Those are two different
                // repairs and nothing in the log could tell them apart.
                if (abilityState == PartState.Blocked && !_loggedAbilityBlocked && DevLog?.Invoke() == true)
                {
                    _loggedAbilityBlocked = true;
                    MelonLogger.Msg("[TargetingMod] an ability came back BLOCKED and should now be marked: " +
                                    PlacementSnapshot.AbilityKey(view.OwnerId, view.Id));
                }
                return abilityState;
            default: return _forItem(view.Id);
        }
    }

    private void CollectRelics()
    {
        if (_relicUi == null) return;
        try
        {
            // Unlike an item slot, a relic's icon does not know which relic it is showing. The map
            // from one to the other is the controller's own, and it is the only thing that has it.
            Il2CppSystem.Collections.Generic.Dictionary<RelicId, RelicView> views = _relicUi._relicViews;
            if (views == null) return;
            foreach (RelicId relicId in views.Keys)
            {
                RelicView view = views[relicId];
                if (view == null || !view.gameObject.activeInHierarchy) continue;
                _tracked.Add(new Tracked(view.transform, view._frameImage, relicId.ToString(), null,
                    view.GetInstanceID(), PartKind.Relic));
            }
        }
        catch
        {
            // The wrapper outlived the object behind it, which stays non-null and throws when
            // reached through. Look for the controller again on the next search.
            _relicUi = null;
        }
    }

    /// <summary>What this found and what each half cost, for the log at the end of a placement.</summary>
    public string Diagnostic =>
        $"{_tracked.Count} icon(s) read from {_slots.Count} slot(s) and {_abilityViews.Count} hero card(s) " +
        $"and {_panelAbilityViews.Count} placement-bar ability panel(s) and " +
        $"{_bottomAbilityViews.Count} bottom-bar ability view(s) in the scene, {_marks.Count} marked; " +
        $"peak this placement: {_peakAbilityIcons} ability icon(s) tracked, off {_peakCardsResolved} " +
        $"card(s) whose Hero was identified and {_peakCardsUnresolved} that could not be; " +
        (_peakAbilityMismatches != 0 || _peakAbilityFaults != 0
            ? $"ability slots skipped, worst pass: {_peakAbilityMismatches} whose icon did not match " +
              $"the entry the walk expected, {_peakAbilityFaults} card(s) faulted ({_lastAbilityFault}); "
            : "") +
        $"{_searches} scene search(es), worst {_worstSearchMs:F1} ms; " +
        $"{_frames} frame(s), first {_firstFrameMs:F2} ms, worst after that {_worstFrameMs:F2} ms, " +
        $"mean after that {MeanFrameMs:F2} ms; " + MarkArt.Diagnostic;

    private double MeanFrameMs => _frames > 1 ? _totalFrameMs / (_frames - 1) : 0;

    /// <summary>How many of the icons being watched are hero abilities rather than items or relics.</summary>
    /// <remarks>
    /// Counted at report time off the live list rather than kept as a running total, so it says what
    /// the LAST pass saw and cannot drift away from the icon count printed beside it.
    /// </remarks>
    private int AbilityIconsNow
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _tracked.Count; i++)
                if (_tracked[i].Kind == PartKind.Ability) n++;
            return n;
        }
    }

    private void RecordFrame(double ms)
    {
        _frames++;
        if (_frames == 1) { _firstFrameMs = ms; return; }
        _totalFrameMs += ms;
        if (ms <= _worstFrameMs) return;
        _worstFrameMs = ms;
        if (ms <= SlowFrameMs || _loggedSlowFrame) return;
        _loggedSlowFrame = true;
        MelonLogger.Warning($"[TargetingMod] marking the item and relic icons took {ms:F1} ms on a settled frame; " +
                            "please report this with the log if the game feels uneven during placement");
    }

    private void MarkView(Transform host, int key, PartState state, Image frame)
    {
        // ONLY a part switched off by where units are standing is marked. A part that is working
        // looks exactly like vanilla, and nothing at all is drawn on it, ever.
        //
        // That one rule is worth more than it looks. It is why the mark can be a single colour
        // without ever clashing with the five rarity colours it sits on ; it is why a full row of
        // working items stays a row of items rather than a wall of decoration ; and it is why
        // nothing the game owns has to be dimmed, hidden or put back, so there is no ledger of
        // borrowed UI state to get wrong when a panel closes at the wrong moment.
        //
        // A part that is not positional at all, and a part that is positional and currently paying,
        // are the same thing from here : no mark, and any mark either had is swept at the end of
        // the scan. Silence is the contract. The feature shows less rather than something untrue.
        if (state != PartState.Blocked || host == null) return;
        RectTransform hostRect = host.TryCast<RectTransform>();
        if (hostRect == null) return;
        if (!_marks.TryGetValue(key, out Mark mark) || !mark.Alive)
        {
            mark = Mark.Create(host, frame, UiSolid.Sprite);
            _marks[key] = mark;
        }
        mark.Stamp = _scanStamp;
        mark.Apply(hostRect, frame, UiSolid.Sprite);
    }

    private void SweepUnmarked()
    {
        _stale.Clear();
        foreach (KeyValuePair<int, Mark> pair in _marks)
            if (pair.Value.Stamp != _scanStamp) _stale.Add(pair.Key);
        for (int i = 0; i < _stale.Count; i++)
        {
            Mark.Destroy(_marks[_stale[i]]);
            _marks.Remove(_stale[i]);
        }
    }

    // The two objects the mod puts under one marked icon : the border, which is a picture of that
    // frame's own band redrawn in our red, and the strike across it. Both are ours, both are
    // destroyed together, and neither ever touches the icon itself.
    private sealed class Mark
    {
        public Image Border;
        public Image Slash;
        public int Stamp;
        private bool _warnedNoFrame;
        private bool _reportedGeometry;

        public bool Alive => Border != null && Slash != null;

        public static Mark Create(Transform host, Image frame, Sprite fallback)
        {
            // BOTH go under the SLOT, in this order, and the order is the whole point.
            //
            // The border used to be parented under the frame image and stretched to fill it, which
            // registered our pixels against the frame's for free. It also put the border at the
            // FRAME's place in the draw order, and this interface draws depth first : the slot holds
            // the frame and a separate ItemVisuals carrying the icon, so everything inside the icon
            // draws after everything inside the frame. The border went straight behind the icon.
            // Reported from play, 2026-08-18. The strike never had the problem because it was
            // already a last sibling of the slot, which is exactly what is copied here.
            //
            // Registration is no longer free, so it is done explicitly : MatchRect copies the frame
            // image's rectangle into the slot's own space every time it moves.
            return new Mark
            {
                Border = NewLayer(host, "TargetingMarkBorder", null, first: false),
                // Created second and therefore last, so the strike sits over the border, which is
                // the order the reference render draws them in.
                Slash = NewLayer(host, "TargetingMarkStrike", fallback, first: false)
            };
        }

        /// <summary>Puts a rect exactly where another one is, across any depth of hierarchy.</summary>
        /// <remarks>
        /// Through world space rather than by copying anchors, because copying anchors is only
        /// correct while the source is a direct child of the destination's parent, and nothing
        /// guarantees the frame image is. Two corners is enough : neither rect is ever rotated
        /// relative to the other here.
        /// </remarks>
        private static void PlaceBorder(RectTransform target, RectTransform source, RectTransform host,
            MarkPlacement placement)
        {
            Rect r = source.rect;
            Vector3 min = host.InverseTransformPoint(source.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
            Vector3 max = host.InverseTransformPoint(source.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
            var frameSize = new Vector2(Mathf.Abs(max.x - min.x), Mathf.Abs(max.y - min.y));
            var frameCentre = new Vector2((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f) - host.rect.center;

            var size = frameSize;
            Vector2 centre = frameCentre;
            float turn = 0f;
            if (placement.Valid)
            {
                // The picture is measured in the SPRITE's pixels, so it is scaled by however much
                // the panel is showing that sprite at. For a square frame this comes out as exactly
                // the frame's own rect, which is why the two paths are one piece of code.
                float scaleX = frameSize.x / placement.SourceWidth;
                float scaleY = frameSize.y / placement.SourceHeight;
                if (placement.Turned)
                {
                    // A turn and a non-uniform scale do not commute : scaling x and y differently
                    // and THEN turning shears the picture off the band. One scale for a turned
                    // frame, and the relic sprites are square, so this is exact rather than a
                    // compromise. It is averaged rather than picked so a small disagreement costs
                    // half as much either way.
                    float uniform = (scaleX + scaleY) * 0.5f;
                    scaleX = uniform;
                    scaleY = uniform;
                }
                size = new Vector2(placement.Width * scaleX, placement.Height * scaleY);
                centre = frameCentre + new Vector2(placement.OffsetX * scaleX, placement.OffsetY * scaleY);
                // Negated : the trace measures its turn in pixel coordinates, where y runs DOWN,
                // and the interface turns things in a world where y runs up.
                turn = -placement.AngleDegrees;
            }

            // Written only when it actually moved. Every one of these is a canvas rebuild, and this
            // runs per marked icon per frame ; a slot that is sitting still must cost nothing.
            if ((target.sizeDelta - size).sqrMagnitude > 0.01f ||
                (target.anchoredPosition - centre).sqrMagnitude > 0.01f ||
                Mathf.Abs(target.localEulerAngles.z - (turn < 0f ? turn + 360f : turn)) > 0.01f)
            {
                target.anchorMin = Center;
                target.anchorMax = Center;
                target.pivot = Center;
                target.sizeDelta = size;
                target.anchoredPosition = centre;
                target.localRotation = Quaternion.Euler(0f, 0f, turn);
            }
        }

        // Something the game added after we did has pushed us down the order. Re-asserted only when
        // that is true, and only from the strike, because two objects both demanding last place
        // every frame would simply swap forever and rebuild the canvas doing it.
        private void EnsureOnTop()
        {
            Transform parent = Slash.transform.parent;
            if (parent == null || Slash.transform.GetSiblingIndex() == parent.childCount - 1) return;
            Border.rectTransform.SetAsLastSibling();
            Slash.rectTransform.SetAsLastSibling();
        }

        /// <summary>The frame image's rectangle, expressed in the host's own space.</summary>
        /// <remarks>
        /// The strike used to be sized and centred from the HOST, and on an item slot that is right
        /// because the slot IS the icon. It is not right anywhere else. A bottom-bar ability view's
        /// rectangle covers the icon AND the description text beside it, so a strike scaled to that
        /// came out about twice the width of the thing it was striking and hung off the tile.
        /// Reported on sight, 2026-08-20, the first time an ability was ever successfully marked.
        ///
        /// The border never had the fault because it was already placed from this rectangle. Both
        /// marks now measure the same box, which is the only way they can stay agreeing on surfaces
        /// nobody has met yet.
        /// </remarks>
        private static bool TryFrameRect(RectTransform host, Image frame, out Vector2 size, out Vector2 centre)
        {
            size = default;
            centre = default;
            if (frame == null || host == null) return false;
            try
            {
                RectTransform source = frame.rectTransform;
                Rect r = source.rect;
                if (r.width <= 0f || r.height <= 0f) return false;
                Vector3 min = host.InverseTransformPoint(source.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
                Vector3 max = host.InverseTransformPoint(source.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
                size = new Vector2(Mathf.Abs(max.x - min.x), Mathf.Abs(max.y - min.y));
                centre = new Vector2((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f) - host.rect.center;
                return size.x > 0f && size.y > 0f;
            }
            catch { return false; }
        }

        public void Apply(RectTransform hostRect, Image frame, Sprite fallback)
        {
            Rect rect = hostRect.rect;
            // Before the game's layout has run once there is no size to work from, and a mark shown
            // at that moment is not merely misplaced : a fresh RectTransform is a hundred by a
            // hundred at the centre of its parent, so it would be a block across the panel for a
            // frame. Nothing is shown until there is an icon-sized icon to sit on.
            bool sized = rect.width > 0f && rect.height > 0f;

            Sprite border = MarkArt.BorderFor(frame != null ? frame.sprite : null, out MarkPlacement placement);
            if (border != null)
            {
                PlaceBorder(Border.rectTransform, frame.rectTransform, hostRect, placement);
                ReportGeometryOnce(hostRect, frame, placement);
                // Copied, not assumed. An image that preserves its aspect letterboxes its sprite
                // inside the rect rather than filling it, so if the frame does that and we do not,
                // the two are drawn at different sizes in the same box and the border sits off the
                // band by however much the letterboxing moved it.
                Border.preserveAspect = frame.preserveAspect;
                Border.sprite = border;
                // WHITE, and this is the load bearing line of the whole feature. Unity's UI shader
                // MULTIPLIES the tint into the sprite, so the previous version of this mark, which
                // borrowed the game's own frame sprite and tinted it red, rendered as rarity colour
                // times our colour : a different colour on every rarity, and four of the five some
                // shade of green. Our sprite already carries the red and the per pixel alpha, so
                // the tint must be the identity. Never put a colour here.
                Border.color = Color.white;
            }
            else if (frame == null && !_warnedNoFrame)
            {
                // Not fatal : the strike alone still says blocked. Said once so that a surface the
                // mod meets later without a frame image is discovered rather than silently thinner.
                _warnedNoFrame = true;
                MelonLogger.Warning("[TargetingMod] an icon was marked with no frame image to trace, " +
                                    "so it shows the strike without the border");
            }
            // The frame's own visibility has to be checked explicitly now, and it did not before.
            // While the border was a CHILD of the frame image it was hidden whenever the frame was,
            // for free. A sibling of the slot is not, so a slot that is on screen with its frame
            // switched off would otherwise get a border drawn around nothing.
            bool frameShown = frame != null && frame.enabled && frame.gameObject.activeInHierarchy;
            SetActive(Border, border != null && sized && frameShown);

            if (sized)
            {
                // Measured off the FRAME, falling back to the host only where there is no frame to
                // measure. See TryFrameRect for what the host measured instead.
                Vector2 markSize = rect.size;
                Vector2 markCentre = Vector2.zero;
                if (TryFrameRect(hostRect, frame, out Vector2 frameSize, out Vector2 frameCentre))
                {
                    markSize = frameSize;
                    markCentre = frameCentre;
                }
                RectTransform slash = Slash.rectTransform;
                slash.anchorMin = Center;
                slash.anchorMax = Center;
                slash.pivot = Center;
                slash.anchoredPosition = markCentre;
                slash.sizeDelta = new Vector2(markSize.x * SlashLengthFactor,
                    Mathf.Max(SlashMinThickness, markSize.x * SlashThicknessFactor));
                // Down to the right, the way a "not this" stroke is drawn everywhere else.
                //
                // A rotated quad, where the reference renderer had to build a polygon. That is not
                // a shortcut : the renderer's reason was that rotating a bitmap with a bicubic
                // resample rings at the corners and leaves bright specks at the strike's tips.
                // Unity samples a rotated quad bilinearly, and bilinear cannot overshoot, so the
                // artefact that constraint existed to avoid has no way to happen here. Porting the
                // constraint rather than its reason would have cost a mesh for nothing.
                slash.localRotation = Quaternion.Euler(0f, 0f, -45f);
            }
            Slash.sprite = fallback;
            Slash.color = StrikeColor;
            SetActive(Slash, sized);
            EnsureOnTop();
        }

        // Said once, with both rectangles, because the border's placement now depends on the frame
        // image's rect rather than on the slot's and the two are not required to agree. If a border
        // is ever reported sitting off the band, this line is what separates "the trace is wrong"
        // from "the rectangle it was drawn into is wrong", which is two play sessions of guessing.
        private void ReportGeometryOnce(RectTransform hostRect, Image frame, MarkPlacement placement)
        {
            if (_reportedGeometry) return;
            _reportedGeometry = true;
            Rect slot = hostRect.rect, band = frame.rectTransform.rect;
            MelonLogger.Msg($"[TargetingMod] mark geometry: slot {slot.width:F0}x{slot.height:F0}, " +
                            $"frame {band.width:F0}x{band.height:F0}, " +
                            $"preserveAspect={frame.preserveAspect}" +
                            (placement.Turned
                                ? $", picture {placement.Width:F0}x{placement.Height:F0} of a " +
                                  $"{placement.SourceWidth:F0}x{placement.SourceHeight:F0} sprite, " +
                                  $"turned {placement.AngleDegrees:F0} degrees"
                                : ""));
        }

        public static void Destroy(Mark mark)
        {
            if (mark == null) return;
            // Each on its own. A wrapper stays non-null after the scene has destroyed the object
            // behind it, and reaching through one throws ; a throw here used to abandon the rest of
            // a teardown and leave the mod running against objects that were already gone.
            DestroyLayer(mark.Border);
            DestroyLayer(mark.Slash);
            mark.Border = null;
            mark.Slash = null;
        }

        private static void DestroyLayer(Image layer)
        {
            try { if (layer != null) UnityEngine.Object.Destroy(layer.gameObject); }
            catch (Exception e) { MelonLogger.Warning("[TargetingMod] part glow layer teardown skipped: " + e.Message); }
        }

        private static void SetActive(Image layer, bool active)
        {
            if (layer == null) return;
            GameObject go = layer.gameObject;
            if (go.activeSelf != active) go.SetActive(active);
        }

        private static Image NewLayer(Transform host, string name, Sprite sprite, bool first)
        {
            var go = new GameObject(name);
            // The rect goes on before anything else. A fresh object is born with a plain Transform,
            // and everything below here, the anchors, the offsets and Image.rectTransform itself,
            // needs a RectTransform to exist first. Letting the Image pull one in as a dependency
            // would leave the order to Unity, and there is no reason to find out how that goes.
            RectTransform rect = go.AddComponent<RectTransform>();
            rect.SetParent(host, false);
            if (first) rect.SetAsFirstSibling();
            else rect.SetAsLastSibling();
            Image image = go.AddComponent<Image>();
            image.sprite = sprite;
            // Never in the way of a click or a tooltip. This is an annotation on the game's UI, and
            // the game's UI has to keep behaving exactly as it did without the mod.
            image.raycastTarget = false;
            go.SetActive(false);
            return image;
        }

        private static readonly Vector2 Center = new(0.5f, 0.5f);
        // The same red the board's own blocked strike uses, so one glance reads the same thing in
        // both places. Read from the overlay rather than restated here : two copies of a colour is
        // two colours as soon as one of them is adjusted.
        //
        // There is only one colour left in this class. The drained blue that used to ring a working
        // icon is gone with the rule that a working part looks exactly like vanilla, and the border
        // carries its own red inside the picture MarkArt draws.
        private static readonly Color StrikeColor = Alpha(OverlayRenderer.GlowBlockedMarkColor, 0.97f);

        private static Color Alpha(Color color, float alpha) { color.a = alpha; return color; }
    }
}
