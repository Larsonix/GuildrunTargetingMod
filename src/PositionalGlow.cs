using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Il2CppEmber.Balancing.SimulationBridge;
using Il2CppEmber.Balancing.SimulationBridge.Context;
using Il2CppEmber.Balancing.SimulationBridge.Effects;
using Il2CppEmber.Balancing.SimulationBridge.Effects.Conditions;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data.Characters;
using Il2CppEmber.Simulation.Core.Bridge;
using Il2CppEmber.Simulation.Core.State;
using Il2CppEmber.Simulation.Core.State.Effects;
using Il2CppEmber.Simulation.Core.State.Effects.Conditions;
using GuildrunTargetingMod.Interop;
using Il2CppEmber.Scopes.Application.UI.Tooltips;
using Il2CppEmber.Scopes.GameRun.Effects.Data;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data.Items;
using Il2CppEmber.Scopes.GameRun.Player.Data;
using Il2CppEmber.Scopes.GameRun.Utilities.Tooltips;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2Cppgg.leyline.balancing.Data;
using Il2Cppgg.leyline.core.Mvcs.Model;
using MelonLoader;
using GuildrunTargetingMod.Ui;

namespace GuildrunTargetingMod;

/// <summary>Whether a part's placement condition is satisfied on the board as it stands.</summary>
internal enum PartState
{
    /// <summary>Nothing about this part depends on where anyone is standing.</summary>
    NotPositional,
    /// <summary>It depends on placement, and the current placement satisfies it.</summary>
    Live,
    /// <summary>It depends on placement, and the current placement does not satisfy it.</summary>
    Blocked
}

/// <summary>One board's worth of answers : every Hero, item and relic whose parts care where units stand.</summary>
/// <remarks>
/// There are two of these in flight during a drag. One describes the board as it really is, read
/// off the game's own out-of-battle evaluation. The other describes the board the player is about
/// to make, read off the throwaway frame the preview already builds for the hex under the cursor.
/// They answer the same question about the same instant, so nothing mixes : one is the placement
/// that exists, the other the placement that would.
/// </remarks>
internal sealed class PlacementSnapshot
{
    private readonly Dictionary<string, PartState> _byHero = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PartState> _byItem = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PartState> _byRelic = new(StringComparer.Ordinal);
    // Rank modifiers, passives and the specialization passive, each under its OWN entry rather than
    // under the Hero carrying it. Eighteen of the thirty-one entries this feature can ever light up
    // are hero-owned, and until this existed the only thing that could show them was the tile under
    // the Hero, which says "something here is switched off" and not WHICH of up to a dozen things.
    private readonly Dictionary<string, PartState> _byAbility = new(StringComparer.Ordinal);

    public int Scanned;
    public int Positional;
    public int Unreadable;
    /// <summary>Hero-owned effects that could not be traced back to the entry they came from.</summary>
    public int UnattributedAbilities;

    public int Heroes => _byHero.Count;
    public int Items => _byItem.Count;
    public int Relics => _byRelic.Count;
    public int Abilities => _byAbility.Count;
    public bool Answered => _byHero.Count != 0 || _byItem.Count != 0 || _byRelic.Count != 0
                            || _byAbility.Count != 0;
    public bool HasBlocked { get; private set; }

    public PartState ForHero(string id) => Lookup(_byHero, id);
    public PartState ForItem(string id) => Lookup(_byItem, id);
    public PartState ForRelic(string id) => Lookup(_byRelic, id);
    public PartState ForAbility(string id) => Lookup(_byAbility, id);

    /// <summary>The one place the hero-and-entry key is spelled, so both ends cannot drift apart.</summary>
    public static string AbilityKey(string heroKey, string entryKey) =>
        string.IsNullOrEmpty(heroKey) || string.IsNullOrEmpty(entryKey) ? null : heroKey + "/" + entryKey;

    public void MergeHero(string id, PartState state) { Merge(_byHero, id, state); Note(state); }
    public void MergeItem(string id, PartState state) { Merge(_byItem, id, state); Note(state); }
    public void MergeRelic(string id, PartState state) { Merge(_byRelic, id, state); Note(state); }
    public void MergeAbility(string id, PartState state) { Merge(_byAbility, id, state); Note(state); }

    public void Reset()
    {
        _byHero.Clear();
        _byItem.Clear();
        _byRelic.Clear();
        _byAbility.Clear();
        Scanned = 0;
        Positional = 0;
        Unreadable = 0;
        UnattributedAbilities = 0;
        HasBlocked = false;
    }

    private void Note(PartState state)
    {
        if (state == PartState.Blocked) HasBlocked = true;
    }

    /// <summary>
    /// Whether this snapshot says the same thing about HEROES as another one.
    /// </summary>
    /// <remarks>
    /// Only the heroes, because only the hero map reaches the world overlay : the item, relic and
    /// ability maps are read by the icon marks, which are refreshed on their own. Narrow on
    /// purpose, and named for what it actually compares rather than for what it is used for.
    ///
    /// Exact rather than a count or a hash. This decides whether the overlay is allowed to skip a
    /// redraw, so a test that is merely usually right would show the player a stale board, and a
    /// speed change that alters what the mod draws is a defect rather than a bonus. There are about
    /// eight heroes, so exact costs nothing worth naming.
    /// </remarks>
    public bool SameHeroesAs(PlacementSnapshot other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other == null || _byHero.Count != other._byHero.Count) return false;
        foreach (KeyValuePair<string, PartState> pair in _byHero)
        {
            if (!other._byHero.TryGetValue(pair.Key, out PartState state) || state != pair.Value)
                return false;
        }
        return true;
    }

    private static PartState Lookup(Dictionary<string, PartState> from, string id) =>
        id != null && from.TryGetValue(id, out PartState state) ? state : PartState.NotPositional;

    private static void Merge(Dictionary<string, PartState> into, string key, PartState state)
    {
        if (string.IsNullOrEmpty(key)) return;
        // Blocked wins : if any part of this thing is switched off by where it stands, that is the
        // thing the player wants pointed out.
        if (into.TryGetValue(key, out PartState existing) && existing == PartState.Blocked) return;
        into[key] = state;
    }
}

// Works out which equipped items and owned relics care about where units stand, and whether the
// board as arranged is currently satisfying them.
//
// This is the thing playtesters asked for most : "sometimes you have a character with the
// defensive legs, and forget about it when positioning", and relics that pay the front row or the
// back row which get forgotten two fights later.
//
// The rules are never ours. The game evaluates its own effects out of battle already, for every
// item tooltip it draws, and this runs the same evaluation : the same conditions, the same
// out-of-battle reader, and the same sacrificial writer. So the mod never learns what
// "is in the front row" means, and a balance patch or a brand new condition needs no change here.
//
// Two rules that are not obvious and must not be undone :
//
//   The evaluation RUNS THE EFFECT'S ACTIONS. That is how the game's own tooltip code works, and
//   what makes it safe is the writer it is handed, not the evaluation itself. So the loop below
//   deliberately stops at the condition and never calls an action at all, which is stricter than
//   the game is with itself.
//
//   It only ever runs during Placement. Evaluating touches shared authored effect objects, which
//   is harmless while no simulation is running and is not something to find out about otherwise.
internal sealed class PositionalGlow
{
    private const int FaultsBeforeDisabling = 30;
    // A frame at sixty per second is about sixteen milliseconds, so three is a fifth of one. Below
    // that this is not the thing anyone would feel.
    private const double SlowRefreshMs = 3.0;

    // Conditions that depend on where units stand AND that the game is willing to judge outside a
    // battle. Both halves matter.
    //
    // The first half is why there is a list at all. The game's own filter admits plenty of
    // conditions that have nothing to do with placement, such as how many Shards you hold, and
    // reporting one of those as blocked would be telling the player to fix something no amount of
    // moving a Hero can fix.
    //
    // The second half is why the list is shorter than every positional condition in the game.
    // ITooltipSource.IsValidCondition refuses a set of them by name, and the ones it refuses that
    // would otherwise belong here are the "nearest ally", "nearest enemy", grid-distance and
    // adjacent-to-effect-target family. Those need a live fight to mean anything, so the game will
    // not judge them out of battle and neither will this. Anything not on this list is simply not
    // annotated : the feature shows less rather than something untrue.
    private static readonly HashSet<string> PositionalConditions = new(StringComparer.Ordinal)
    {
        "IsAdjacentToAllyCondition",
        "IsAdjacentToAllyWithGreaterRangeCondition",
        "IsAdjacentToExactlyOneAllyCondition",
        "IsAdjacentToOwnerCondition",
        "IsBehindOwnerCondition",
        "DoesTargetHasExactlyOneAllyBehindCondition",
        "DoesTargetHasExactlyOneAllyInFrontCondition",
        "IsEffectTargetAloneInRowCondition",
        "IsEffectTargetInBackRowCondition",
        "IsEffectTargetInBackTwoRowsCondition",
        "IsEffectTargetInFrontRowCondition",
        "IsEffectTargetInFrontTwoRowsCondition",
        "IsEffectTargetOnlyAllyInBackTwoRowsCondition",
        "IsEffectTargetOnlyAllyInXHexRangeCondition",
        "IsInFrontOfOwnerCondition",
        "IsInRangeOfOwnerCondition",
        "IsOwnerAdjacentToExactlyOneAllyCondition",
        "IsOwnerInBackRowCondition",
        // Found by censusing the build rather than by reading names : five uses, plainly about
        // where units stand, and the game is willing to judge it outside a fight. It was missing
        // from the hand-written list purely because nobody had checked the list against the data.
        "IsOwnerOrAdjacentToOwnerCondition",
        "IsOwnerOrOnlyAllyInFrontOfOwnerCondition",
        // Authored in the game but used by nothing in the current build. Listed anyway : they are
        // plainly about where units stand, the game is willing to judge them outside a fight, and
        // costing nothing today means a patch that starts using one is covered on arrival rather
        // than silently missed.
        "IsEffectTargetInSameRowAsOwnerCondition",
        "IsEffectTargetOnlyAllyInFirstTwoRowsCondition",
        "IsOnlyAllyInFrontOfOwnerCondition",
        "IsOwnerOrDirectlyInFrontOfOwnerCondition"
    };

    private readonly Capabilities _capabilities;
    private PlacementSnapshot _live = new();
    private PlacementSnapshot _spare = new();
    private readonly Dictionary<IntPtr, string> _heroIdStrings = new();
    private readonly Dictionary<IntPtr, EntityId> _lastHeroIds = new();
    private readonly Dictionary<int, string> _itemIdStrings = new();
    private readonly Dictionary<int, ItemId> _lastItemIds = new();
    private readonly Dictionary<int, string> _relicIdStrings = new();
    private readonly Dictionary<int, RelicId> _lastRelicIds = new();
    private readonly Dictionary<IntPtr, string> _effectIdStrings = new();
    private readonly Dictionary<IntPtr, EffectId> _lastEffectIds = new();
    // Whether an effect is placement-dependent never changes : its condition tree is authored
    // data. Deciding it means asking the runtime for a class name, twice per condition, and a run
    // carries hundreds of effects, so without this the filter alone would be the most expensive
    // thing the mod does. Answers are kept for the run and thrown away with it.
    // Deliberately keyed by the effect's ID rather than by its object pointer, even though the
    // pointer would save a string per effect per frame. An effect object can be freed when an item
    // is unequipped mid-placement, and a later effect landing on that address would inherit this
    // answer, which is a mark that is silently wrong rather than merely late. The per-frame cost
    // this would have saved is removed properly by refreshing on board change instead of on frame.
    private readonly Dictionary<string, bool> _positionalByEffect = new(StringComparer.Ordinal);
    // Effects the game's own condition code refuses to judge. Measured in play : one entry on one
    // board threw out of IsTargetConditionMet, and without this it would throw again on every
    // check for the rest of the placement.
    //
    // Also by ID, and for a second reason on top of the one above : the throw can happen before
    // there IS an object to point at, and a set that could not record those would let exactly the
    // repeat it exists to stop happen again. It is empty on every board but one measured so far,
    // so the lookup is guarded on the count and costs nothing to consult.
    private readonly HashSet<string> _unreadable = new(StringComparer.Ordinal);
    private TooltipContextWriter _writer;
    private GameRunTooltipController _tooltipController;
    private readonly SearchBackoff _tooltipControllerSearch = new("out-of-battle tooltip controller");
    private string _lastFault;
    private int _consecutiveFaults;
    private bool _loggedFirstResult;
    private bool _loggedSlowRefresh;
    private int _refreshes;
    private int _skipped;
    private double _firstRefreshMs;
    private double _worstAfterFirstMs;
    private double _totalAfterFirstMs;
    private bool _loggedEffectFault;
    private bool _loggedPreviewResult;
    // Every distinct condition class the run actually contains, gathered until the first result
    // is reported. If nothing matches the allowlist, this is what says whether the names are
    // being read correctly or simply do not appear on this board.
    private static readonly HashSet<string> SeenConditionNames = new(StringComparer.Ordinal);
    private static readonly Dictionary<IntPtr, bool> AllowlistedConditionClasses = new();

    public PositionalGlow(Capabilities capabilities) => _capabilities = capabilities;

    /// <summary>
    /// The board the player is about to make, or null when they are not making one. Set from the
    /// drag preview's own playout ; while it is set it answers everything, on its own.
    /// </summary>
    /// <remarks>
    /// It answers ON ITS OWN, never falling back to the real board for something it does not list.
    /// A part missing from it is a part that would not be lit if the hero were dropped here, which
    /// is a real answer and the whole point. Falling through to the live board for those would show
    /// the player the arrangement they are leaving rather than the one they are choosing.
    /// </remarks>
    public PlacementSnapshot Preview
    {
        get => _preview;
        set
        {
            if (ReferenceEquals(_preview, value)) return;
            _preview = value;
            HeroStateVersion++;
        }
    }

    /// <summary>
    /// Bumped whenever what this would answer about a HERO changes, and never otherwise.
    /// </summary>
    /// <remarks>
    /// The world overlay reads hero state on every unit tile it draws, so it cannot know whether a
    /// redraw is needed without this. Named for the hero map alone because that is all it tracks:
    /// the item, relic and ability answers can change without this moving, and the icon marks that
    /// read those refresh themselves rather than watching this.
    /// </remarks>
    public int HeroStateVersion { get; private set; }

    private PlacementSnapshot _preview;

    private PlacementSnapshot Current => _preview ?? _live;
    public bool HasBlocked => _capabilities.PositionalGlow && Current.HasBlocked;

    // All four answer "nothing to say" once the feature has switched itself off. The refresh stops
    // running at that point but does not empty what it last read, and a reader that kept serving
    // that would keep drawing a picture of a board that has since been rearranged.
    /// <summary>State for a Hero, keyed by its entity id string, as the overlay keys everything.</summary>
    public PartState ForHero(string entityId) =>
        _capabilities.PositionalGlow ? Current.ForHero(entityId) : PartState.NotPositional;

    /// <summary>State for a relic, keyed by its relic id string.</summary>
    public PartState ForRelic(string relicId) =>
        _capabilities.PositionalGlow ? Current.ForRelic(relicId) : PartState.NotPositional;

    /// <summary>State for one equipped item, keyed by its item id string, as the item row keys it.</summary>
    public PartState ForItem(string itemId) =>
        _capabilities.PositionalGlow ? Current.ForItem(itemId) : PartState.NotPositional;

    /// <summary>
    /// State for one rank modifier, passive or specialization, keyed by its authored entry id.
    /// </summary>
    /// <remarks>
    /// Keyed by the ENTRY rather than by the Hero, which is the whole point of it existing : a Hero
    /// can carry a dozen of these at once and the tile underneath can only say that one of them is
    /// switched off.
    /// </remarks>
    public PartState ForAbility(string heroKey, string entryId) =>
        _capabilities.PositionalGlow
            ? Current.ForAbility(PlacementSnapshot.AbilityKey(heroKey, entryId))
            : PartState.NotPositional;

    /// <summary>What running this every frame actually cost, for the log at the end of a placement.</summary>
    /// <remarks>
    /// The first version of this reported the worst frame and nothing else, and the worst frame
    /// read 9.4 ms, which is most of a frame at sixty per second and looks alarming. It could not
    /// distinguish "every frame costs that" from "the first one did", and the first one does : it
    /// pays for finding the tooltip controller and for classifying every effect in the run, both of
    /// which are then cached for the placement. An aggregate is not a population : report the first
    /// separately from the rest, or the number cannot answer the question it was taken to answer.
    /// </remarks>
    public string Diagnostic =>
        $"{_refreshes} refresh(es), first {_firstRefreshMs:F2} ms, " +
        $"worst after that {_worstAfterFirstMs:F2} ms, mean after that {MeanAfterFirstMs:F2} ms; " +
        $"{_skipped} frame(s) not read because a drag preview was answering";

    private double MeanAfterFirstMs => _refreshes > 1 ? _totalAfterFirstMs / (_refreshes - 1) : 0;

    /// <summary>Recomputes now. Called once per frame.</summary>
    /// <remarks>
    /// It used to recompute every fifteenth frame, which is a quarter of a second of the marks
    /// disagreeing with the board, and the item icons ran on their own clock on top of that. The
    /// player reported the two together as half a second to a second of lag, which is exactly what
    /// two independent waits of that size add up to.
    ///
    /// Running it per frame is affordable because almost none of the work is per frame. Whether an
    /// effect cares where units stand is authored data and is cached for the run after the first
    /// look ; what is left is a walk over the fielded Heroes, the equipped items and the owned
    /// relics, evaluating only the handful of conditions that are positional at all. A real run
    /// measured thirteen effects scanned and three of them positional. WorstRefreshMs is here so
    /// that claim can be checked rather than believed.
    /// While a drag preview is answering, the live board is nobody's answer, so it is not read at
    /// all. Those frames are counted apart from the refreshes rather than folded in as cheap ones :
    /// averaging over frames that did no work would quietly halve the very number this exists to
    /// report, and the point of the number is to be comparable.
    /// </remarks>
    public void Update(bool liveNeeded)
    {
        if (!_capabilities.PositionalGlow) return;
        if (!liveNeeded) { _skipped++; return; }
        long start = Stopwatch.GetTimestamp();
        Refresh();
        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        _refreshes++;
        if (_refreshes == 1)
        {
            // The cold one, and it is expected to be the expensive one : it finds the tooltip
            // controller and classifies every effect in the run, and both answers are then kept.
            // Warning about it would be crying wolf about the one frame that is supposed to cost.
            _firstRefreshMs = ms;
            return;
        }
        _totalAfterFirstMs += ms;
        if (ms <= _worstAfterFirstMs) return;
        _worstAfterFirstMs = ms;
        // Said out loud once, without anyone having to switch a log on first. Running this every
        // frame is what makes the marks keep up with the board, and the one thing that could make
        // that a bad trade is a board where the walk stays expensive after the first frame. If that
        // ever happens it belongs in the log of the player it happened to.
        if (ms <= SlowRefreshMs || _loggedSlowRefresh) return;
        _loggedSlowRefresh = true;
        MelonLogger.Warning($"[TargetingMod] the placement marks took {ms:F1} ms on a settled frame; " +
                            "please report this with the log if the game feels uneven during placement");
    }

    public void Clear()
    {
        _live.Reset();
        _spare.Reset();
        Preview = null;
        _heroIdStrings.Clear();
        _lastHeroIds.Clear();
        _itemIdStrings.Clear();
        _lastItemIds.Clear();
        _relicIdStrings.Clear();
        _lastRelicIds.Clear();
        _effectIdStrings.Clear();
        _lastEffectIds.Clear();
        _positionalByEffect.Clear();
        _unreadable.Clear();
        SeenConditionNames.Clear();
        _tooltipController = null; // belongs to the battle scene that is being taken apart.
        _tooltipControllerSearch.Reset();
        _refreshes = 0;
        _skipped = 0;
        _firstRefreshMs = 0;
        _worstAfterFirstMs = 0;
        _totalAfterFirstMs = 0;
    }

    // One line the first time a dragged board is answered, so "it does not follow the hero" can be
    // told from "it follows and this hero has nothing to follow" without a second play session.
    private void LogFirstPreviewResult(PlacementSnapshot snapshot)
    {
        if (_loggedPreviewResult || snapshot == null) return;
        _loggedPreviewResult = true;
        MelonLogger.Msg($"[TargetingMod] positional glow follows the drag: {snapshot.Heroes} hero(es), " +
                        $"{snapshot.Items} item(s), {snapshot.Relics} relic(s) on the board being dragged to; " +
                        $"{snapshot.Positional} positional, {snapshot.Unreadable} unreadable");
    }

    private bool IsPositionalCached(EffectId effectId, ModularEffect modular)
    {
        string key = EffectKey(modular.Pointer, effectId);
        if (_positionalByEffect.TryGetValue(key, out bool known)) return known;
        bool positional = IsPositional(modular.Condition);
        _positionalByEffect[key] = positional;
        return positional;
    }

    private string HeroKey(IntPtr owner, EntityId id)
    {
        if (!_lastHeroIds.TryGetValue(owner, out EntityId lastId) ||
            !NullableRaw.SameRawId(in lastId, in id))
        {
            _lastHeroIds[owner] = id;
            _heroIdStrings[owner] = id.ToString();
        }
        return _heroIdStrings[owner];
    }

    private string ItemKey(int slot, ItemId id)
    {
        if (!_lastItemIds.TryGetValue(slot, out ItemId lastId) ||
            !NullableRaw.SameRawId(in lastId, in id))
        {
            _lastItemIds[slot] = id;
            _itemIdStrings[slot] = id.ToString();
        }
        return _itemIdStrings[slot];
    }

    private string RelicKey(int slot, RelicId id)
    {
        if (!_lastRelicIds.TryGetValue(slot, out RelicId lastId) ||
            !NullableRaw.SameRawId(in lastId, in id))
        {
            _lastRelicIds[slot] = id;
            _relicIdStrings[slot] = id.ToString();
        }
        return _relicIdStrings[slot];
    }

    private string EffectKey(IntPtr owner, EffectId id)
    {
        if (!_lastEffectIds.TryGetValue(owner, out EffectId lastId) ||
            !NullableRaw.SameRawId(in lastId, in id))
        {
            _lastEffectIds[owner] = id;
            _effectIdStrings[owner] = id.ToString();
        }
        return _effectIdStrings[owner];
    }

    private void Refresh()
    {
        // Names the last thing this got as far as. An IL2CPP stack can be thin, and a bare "object
        // reference not set" that names no line is a play session spent learning what one word
        // would have said. This costs a string assignment per check.
        string stage = "start";
        try
        {
            IBattleContextReader reader = FindOutOfBattleReader();
            if (reader == null) return;
            stage = "writer";
            IBattleContextWriter writer = SacrificialWriter();

            stage = "readers";
            if (!DataReaders.TryGet<EffectsReader>(out var effects) || effects == null) return;
            if (!DataReaders.TryGet<GameRegistryDataReader>(out var registry) || registry == null) return;
            // Both of these are the reader's own data and both can be absent while a scene is still
            // assembling itself. Reaching through either without asking was the one unguarded
            // dereference left in this method, and an unguarded dereference outside the per-effect
            // guard below takes the whole feature down rather than one entry.
            stage = "reader data";
            if (effects.Data == null || effects.Data.Effects == null) return;
            if (registry.Data == null || registry.Data.Heroes == null) return;

            stage = "walk";
            _spare.Reset();
            PlacementSnapshot rebuilt = BuildSnapshot(reader, writer, effects, registry, _spare);
            // Always adopted, because the item, relic and ability halves are read by the icon marks
            // whether or not the heroes moved. The VERSION only moves when the hero half really
            // changed, so the world overlay can skip a redraw it would have spent on an identical
            // picture. Two different questions, deliberately not folded into one flag.
            bool heroesUnchanged = _live != null && rebuilt != null && rebuilt.SameHeroesAs(_live);
            PlacementSnapshot previous = _live;
            _live = rebuilt;
            _spare = previous;
            if (!heroesUnchanged) HeroStateVersion++;
            _consecutiveFaults = 0;
            LogFirstResult();
        }
        catch (Exception e)
        {
            // Not latched on the first throw : a reader caught mid setup is a passing condition,
            // and treating one as permanent has cost this mod a session before.
            if (_lastFault != e.Message)
            {
                _lastFault = e.Message;
                MelonLogger.Warning($"[TargetingMod] positional glow read failed at stage '{stage}' (will retry). Full fault follows:\n" + e);
            }
            if (++_consecutiveFaults >= FaultsBeforeDisabling)
                _capabilities.DisablePositionalGlow(
                    $"positional state unreadable {_consecutiveFaults} checks in a row at stage '{stage}': {e.Message}");
        }
    }

    /// <summary>
    /// The same question asked of a board that does not exist yet : the throwaway frame the drag
    /// preview already built for the hex under the cursor.
    /// </summary>
    /// <remarks>
    /// This is why the answer can follow a hero being dragged at all. The preview already mirrors
    /// the board with the drop applied and hands it to the game's own simulation, so the frame it
    /// produces IS the arrangement the player is considering. Nothing about the real game is
    /// touched, and nothing about the rules is ours : the same conditions are asked the same
    /// question against a different board.
    ///
    /// Safer than the live path, not riskier. A condition is handed the frame's own context as its
    /// writer, and that frame is thrown away a moment later, so a condition that writes cannot
    /// reach anything real. The live path has to keep a sacrificial writer switched off by hand to
    /// get the same guarantee.
    ///
    /// Asked at the OPENING frame, never the settled one. A rule judged where a unit ran to is one
    /// no amount of moving can fix, and reporting those is the one thing this feature refuses to
    /// do. The opening frame still carries the placement the player chose.
    /// </remarks>
    public PlacementSnapshot ForOpeningFrame(Frame frame)
    {
        if (!_capabilities.PositionalGlow || frame == null) return null;
        try
        {
            SimulationBattleContext context = frame.BattleContext;
            if (context == null) return null;
            if (!DataReaders.TryGet<EffectsReader>(out var effects) || effects == null) return null;
            if (!DataReaders.TryGet<GameRegistryDataReader>(out var registry) || registry == null) return null;
            if (effects.Data == null || effects.Data.Effects == null) return null;
            if (registry.Data == null || registry.Data.Heroes == null) return null;
            // The frame supplies the READER and nothing else. It is both halves and it would have
            // been the obvious thing to hand over, and it would have been wrong : this frame is not
            // thrown away yet, it is about to be played out into the prediction the whole mod rests
            // on. A condition that wrote to it would quietly change the fight being predicted, and
            // the self-check would then report the mod disagreeing with the game. The writer is the
            // same switched-off sacrificial one the live path uses, so evaluation can touch nothing
            // at all.
            var snapshot = new PlacementSnapshot();
            BuildSnapshot(context.TryCast<IBattleContextReader>(), SacrificialWriter(), effects, registry,
                snapshot);
            LogFirstPreviewResult(snapshot);
            return snapshot;
        }
        catch (Exception e)
        {
            // The preview answer is a nicety on top of an answer that already exists. Losing it
            // must never cost the real board's marks, so this reports and hands back nothing at
            // all, and the caller falls back to the board as it stands.
            ReportEffectFault(e);
            return null;
        }
    }

    // Somewhere for an evaluation to write that is nowhere at all.
    //
    // Never allowed to write. The game turns this on for its own tooltips because it wants the stat
    // numbers to put in the text ; all this wants is whether the condition holds, so nothing is
    // ever permitted through. That is the containment, and it is the whole reason running the
    // game's own evaluation is safe. Both boards use this one, the real and the hypothetical.
    private IBattleContextWriter SacrificialWriter()
    {
        _writer ??= new TooltipContextWriter();
        _writer.Clear();
        _writer.AllowWrite = false;
        return _writer.TryCast<IBattleContextWriter>();
    }

    // The one walk, shared by the board that exists and the board that would. Only the context
    // differs : the same effects, the same conditions, the same attribution.
    //
    // ASKED OF THE OWNERS, not of the effects. That direction is the whole correctness of this
    // method, and getting it the other way round is what made all but one kind of part silently
    // dead.
    //
    // The first version walked every effect and asked each one who owned it, by reading an
    // OwnerEntityId off the effect object. That field is not filled in. The game writes it by hand
    // immediately before it evaluates one for a tooltip, which is exactly the tell that it is not
    // already there to be read, and it is the reason every log said 0 heroes and 0 items on every
    // board while relics worked perfectly : a relic is the one kind whose owner lives on the effect
    // RECORD rather than on the object. Twenty-four of the thirty-one entries in the game could
    // never have lit, and the seven that could were the ones being seen.
    //
    // The game keeps the answer already indexed the right way round : effects BY hero, BY
    // specialization, BY item, BY relic, all plain dictionaries. Asking those gives the owner for
    // free and, far more importantly, gives the right unit to evaluate against. An item's rule is
    // about the Hero wearing it and nobody else.
    private PlacementSnapshot BuildSnapshot(IBattleContextReader reader, IBattleContextWriter writer,
        EffectsReader effects, GameRegistryDataReader registry, PlacementSnapshot snapshot)
    {
        if (reader == null) return snapshot;
        EffectsData data = effects.Data;

        // What each Hero carries in its own right : rank modifiers, and the passive behind a
        // specialization. Judged against that Hero, because its own placement is the only one that
        // can satisfy it.
        // Every owner is read on its own, so one that cannot be read costs only itself.
        //
        // This is the same lesson as the per-entry guard inside, learned again one level up and at
        // the player's expense : an unguarded read out here escapes the whole walk, and thirty of
        // those in a row switch off a feature that was working for everything else on the board.
        // Anything that reaches into the game gets a guard around it, at every level it is done.
        foreach (HeroData hero in registry.Data.Heroes.Values)
        {
            try
            {
                if (hero == null || registry.IsHeroInReserve(hero.HeroId)) continue;
                EntityId target = hero.HeroId.ToEntityId();
                string heroKey = HeroKey(hero.Pointer, target);
                EvaluateOwned(data.EffectsByHero, hero.HeroId, target, heroKey, null, reader, writer, effects, registry, snapshot);
                EvaluateOwned(data.EffectsByHeroSpecialization, hero.HeroId, target, heroKey, null, reader, writer, effects, registry, snapshot);
            }
            catch (Exception e) { SkipOwner(snapshot, e); }
        }

        // What a Hero is wearing. Filed against the item as well as the wearer, because the item row
        // is keyed by the item and that row is where the report this feature came from lives.
        int itemSlot = 0;
        foreach (ItemId itemId in data.EffectsByItem.Keys)
        {
            try
            {
                HeroData wearer = FindWearer(registry, itemId);
                if (wearer == null || registry.IsHeroInReserve(wearer.HeroId)) continue;
                EntityId target = wearer.HeroId.ToEntityId();
                EvaluateOwned(data.EffectsByItem, itemId, target, HeroKey(wearer.Pointer, target),
                    ItemKey(itemSlot, itemId),
                    reader, writer, effects, registry, snapshot);
            }
            catch (Exception e) { SkipOwner(snapshot, e); }
            finally { itemSlot++; }
        }

        // A relic belongs to the run rather than to anyone, and pays out to whichever Heroes
        // qualify, so it is doing something as soon as one of them does.
        int relicSlot = 0;
        foreach (RelicId relicId in data.EffectsByRelic.Keys)
        {
            try { EvaluateRelic(data, relicId, RelicKey(relicSlot, relicId), reader, writer, effects, registry, snapshot); }
            catch (Exception e) { SkipOwner(snapshot, e); }
            finally { relicSlot++; }
        }

        return snapshot;
    }

    private void EvaluateOwned<TKey>(Il2CppSystem.Collections.Generic.Dictionary<TKey, Il2CppSystem.Collections.Generic.List<EffectId>> index,
        TKey key, EntityId target, string heroKey, string itemKey,
        IBattleContextReader reader, IBattleContextWriter writer, EffectsReader effects,
        GameRegistryDataReader registry,
        PlacementSnapshot snapshot)
    {
        // Asked before it is indexed. Most Heroes own no effects of a given kind at all, and a
        // Hero with no specialization has no entry in that index whatsoever, so reaching straight
        // for one throws rather than answering nothing. That threw for every Hero on every board
        // and took the whole feature down with it, which is a plain mistake and not a subtle one :
        // an indexer on a dictionary is a claim that the key is there.
        if (!index.ContainsKey(key)) return;
        var ids = index[key];
        if (ids == null) return;
        for (int i = 0; i < ids.Count; i++)
        {
            if (!TryPositional(ids[i], effects, registry, snapshot, out ModularEffect modular)) continue;
            try
            {
                snapshot.Positional++;
                PartState state = MeetsFor(modular, reader, writer, target) ? PartState.Live : PartState.Blocked;
                snapshot.MergeHero(heroKey, state);
                if (itemKey != null) { snapshot.MergeItem(itemKey, state); continue; }
                // Hero-owned, so it came from a rank modifier, a passive, or the specialization.
                // Which one is stamped on the effect itself, so the answer can name the entry
                // instead of only the Hero. An effect that cannot be traced back is COUNTED rather
                // than guessed at : the tile under the Hero still says something is switched off,
                // and no icon is told a story about itself that might not be true.
                // Keyed by the HERO AND the entry, never the entry alone. Two Heroes can carry the
                // same rank modifier in opposite states, and a key that named only the modifier
                // would merge them : one Hero out of position would put the mark on everybody
                // else's copy of it too, which is the feature telling a story that is not true.
                if (TryAbilityKey(ids[i], effects, target, heroKey, out string abilityKey))
                    snapshot.MergeAbility(abilityKey, state);
                else snapshot.UnattributedAbilities++;
            }
            catch (Exception e) { Skip(ids[i], snapshot, e); }
        }
    }

    private void EvaluateRelic(EffectsData data, RelicId relicId, string key,
        IBattleContextReader reader, IBattleContextWriter writer, EffectsReader effects,
        GameRegistryDataReader registry,
        PlacementSnapshot snapshot)
    {
        if (!data.EffectsByRelic.ContainsKey(relicId)) return;
        var ids = data.EffectsByRelic[relicId];
        if (ids == null) return;
        for (int i = 0; i < ids.Count; i++)
        {
            if (!TryPositional(ids[i], effects, registry, snapshot, out ModularEffect modular)) continue;
            try
            {
                snapshot.Positional++;
                bool met = false;
                foreach (HeroData hero in registry.Data.Heroes.Values)
                {
                    if (hero == null || registry.IsHeroInReserve(hero.HeroId)) continue;
                    if (!MeetsFor(modular, reader, writer, hero.HeroId.ToEntityId())) continue;
                    met = true;
                    break;
                }
                snapshot.MergeRelic(key, met ? PartState.Live : PartState.Blocked);
            }
            catch (Exception e) { Skip(ids[i], snapshot, e); }
        }
    }

    /// <summary>Which authored entry this effect came from, as the key the icon row is read by.</summary>
    /// <remarks>
    /// The game stamps every effect it creates with where it came from : a rank modifier records
    /// the modifier's entry id, a passive or an active records the ability's. So the entry is on
    /// the effect and does not have to be reconstructed by walking indexes, which also avoids the
    /// double counting waiting there : a rank modifier's effects are filed under BOTH the hero
    /// index and the per-modifier one.
    ///
    /// Guarded rather than trusted. `OriginEntryId` is a NULLABLE game field, and reaching one
    /// through its generated getter is the shape that hands back a null pointer when it is empty
    /// (interop trap 1). Anything that throws or comes back empty is reported as unattributed, so a
    /// change in how the game stamps effects shows up as a number in the log rather than as marks
    /// appearing on the wrong icons.
    /// </remarks>
    private bool TryAbilityKey(EffectId effectId, EffectsReader effects, EntityId heroId,
        string heroKey, out string key)
    {
        key = null;
        try
        {
            if (effects?.Data == null) return false;
            if (!effects.Data.Effects.ContainsKey(effectId)) return false;
            EffectData effect = effects.Data.Effects[effectId];
            if (effect == null) return false;
            EffectOriginData origin = effect.OriginData;
            // `var` on purpose : this is a nullable of a balancing id whose namespace differs
            // between the generated assemblies, and naming it here would be one more thing to keep
            // in step with a game update for no benefit. What matters is that it has a value.
            var entryId = origin.OriginEntryId;
            if (!entryId.HasValue) return false;
            // NOT memoised, and deliberately. See the matching note in PartGlow.TrackAbilitySlot:
            // BalancingEntryId carries a byte array and a string beside its guid, so it is not
            // blittable, its bytes are references rather than values, and every cheap way to tell
            // two of them apart rests on an unverified assumption about the generated bindings. A
            // wrong answer marks an innocent ability. The hero half of this key IS memoised, by the
            // caller, because an entity id is a plain guid and can be compared safely.
            key = PlacementSnapshot.AbilityKey(heroKey, entryId.Value.ToString());
            return !string.IsNullOrEmpty(key);
        }
        catch
        {
            return false;
        }
    }

    // Is this one of ours, and can it be read at all. Counts the scan and keeps the per-entry
    // containment that stops one bad entry costing the rest of the board.
    private bool TryPositional(EffectId effectId, EffectsReader effects, GameRegistryDataReader registry,
        PlacementSnapshot snapshot, out ModularEffect modular)
    {
        modular = null;
        snapshot.Scanned++;
        try
        {
            if (effects?.Data == null) return false;
            if (!effects.Data.Effects.ContainsKey(effectId)) return false;
            EffectData effect = effects.Data.Effects[effectId];
            if (effect == null) return false;
            var instance = effect.EffectInstance as Il2CppObjectBase;
            modular = instance?.TryCast<ModularEffect>();
            if (modular == null || !IsPositionalCached(effectId, modular)) { modular = null; return false; }
            // One that has already proved unreadable is not tried again. The game's own condition
            // code throws on it, and it would throw identically four times a second for the rest of
            // the placement. Asking once is enough to know.
            //
            // The count is checked first so the ordinary board, where nothing has ever been
            // unreadable, never builds the string at all.
            if (_unreadable.Count != 0 && _unreadable.Contains(EffectKey(modular.Pointer, effectId)))
            { snapshot.Unreadable++; modular = null; return false; }
            return true;
        }
        catch (Exception e)
        {
            Skip(effectId, snapshot, e);
            modular = null;
            return false;
        }
    }

    // One Hero, item or relic that could not be read at all. Counted with the unreadable entries
    // because that is what it is from the player's side : something on the board the marks have
    // nothing to say about, while everything else carries on.
    private void SkipOwner(PlacementSnapshot snapshot, Exception e)
    {
        snapshot.Unreadable++;
        ReportEffectFault(e);
    }

    private void Skip(EffectId effectId, PlacementSnapshot snapshot, Exception e)
    {
        snapshot.Unreadable++;
        _unreadable.Add(effectId.ToString());
        ReportEffectFault(e);
    }

    // Which Hero is wearing this. Asked of the Heroes, never of the item : an item's own
    // OwnerEntityId is written to return null unconditionally, so it can never answer.
    private static HeroData FindWearer(GameRegistryDataReader registry, ItemId itemId)
    {
        foreach (HeroData hero in registry.Data.Heroes.Values)
            if (hero != null && hero.IsItemEquipped(itemId)) return hero;
        return null;
    }

    // Files the answer against everything the player can see and act on. That is deliberately more
    // than one place per part : the Hero says where to look on the board, and the item says which
    // of the things they are wearing is the one to look at. The item row is keyed by the item and
    // not by its wearer, so filing only against the Hero would leave the row unanswerable, and the
    // row is exactly where the report that started this feature lives ("you have a character with
    // the defensive legs, and forget about it when positioning").
    //
    // A Hero or an item with two positional parts is reported blocked if any one of them is,
    // because that is the one worth looking at.
    // The whole exception, once, and never again in this session.
    //
    // A message on its own is not a diagnosis. "Object reference not set to an instance of an
    // object" appeared thirty times in a row on the first board that reached this code and said
    // nothing at all about which line produced it, which is a play session spent to learn what one
    // stack trace carries for free. The mod ships its symbol file beside itself so this names a
    // line, not just a method.
    private void ReportEffectFault(Exception e)
    {
        if (_loggedEffectFault) return;
        _loggedEffectFault = true;
        MelonLogger.Warning("[TargetingMod] positional glow skipped an effect it could not read. " +
                            "The rest of the board is unaffected. Full fault follows:\n" + e);
    }

    // The condition tree, walked the way the game walks it. A compound holds several and any one
    // of them being positional makes the whole effect worth annotating.
    private static bool IsPositional(IEffectCondition condition)
    {
        if (condition == null) return false;
        try
        {
            CompoundCondition compound = condition.TryCast<CompoundCondition>();
            if (compound != null)
            {
                // The backing array, not the Conditions property. That property is typed as an
                // interface list, and walking one of those through interop is a known crash ; the
                // array behind it is concrete and safe. If the field cannot be reached at all, the
                // effect is simply not annotated rather than guessed at.
                var inner = NullableRaw.ReadReferenceArrayAt<IEffectCondition>(compound, "_conditions");
                if (inner == null) return false;
                for (int i = 0; i < inner.Length; i++)
                    if (IsPositional(inner[i])) return true;
                return false;
            }
            return IsAllowlisted(condition);
        }
        catch
        {
            // Classifying one condition is never worth taking the feature down for. An effect that
            // cannot be classified is annotated as nothing at all.
            return false;
        }
    }

    // The evaluation, and the one place this deliberately parts company with the game's own
    // wrapper : the wrapper applies the effect's actions once the condition passes, and this stops
    // at the condition. The condition test is the game's ; nothing is applied to anything.
    //
    // Who to test it against is worked out here rather than through the effect's own GetTargets,
    // which hands back a list typed as an interface, and walking one of those through interop is a
    // known way to bring the process down. The two cases that matter need no such list :
    //
    //   an effect with an owner belongs to that Hero, so its own placement is the only one that
    //   can satisfy it, and asking about anybody else would light an item up for standing
    //   somewhere its wearer is not ;
    //
    //   an effect with no owner is a relic, which pays out to whichever Heroes qualify, so it is
    //   doing something as soon as one of them does.
    private static bool Evaluate(ModularEffect modular, Il2CppObjectBase instance,
        IBattleContextReader reader, IBattleContextWriter writer, GameRegistryDataReader registry)
    {
        if (NullableRaw.TryReadEntityIdAt(instance, "<OwnerEntityId>k__BackingField", out EntityId owner))
            return MeetsFor(modular, reader, writer, owner);

        foreach (HeroData hero in registry.Data.Heroes.Values)
        {
            if (hero == null || registry.IsHeroInReserve(hero.HeroId)) continue;
            if (MeetsFor(modular, reader, writer, hero.HeroId.ToEntityId())) return true;
        }
        return false;
    }

    private static bool MeetsFor(ModularEffect modular, IBattleContextReader reader, IBattleContextWriter writer, EntityId target) =>
        ConditionMet(modular.Condition, modular.EffectId, reader, writer, target);

    // Walks a compound the way the game's own out-of-battle evaluation does : every child has to
    // hold, but a child it will not judge outside a fight counts as holding rather than failing.
    //
    // Judging the compound in one call instead would be wrong, and quietly so. A rule like
    // Sentinel's Plate is written as "alone in its row AND in the front row", and Lone Wolf's Bow
    // pairs a placement rule with one the game refuses to judge out of battle at all. Asking the
    // whole compound would fold those together and answer for reasons the player cannot act on.
    private static bool ConditionMet(IEffectCondition condition, EffectId effectId,
        IBattleContextReader reader, IBattleContextWriter writer, EntityId target)
    {
        if (condition == null) return true;
        CompoundCondition compound = condition.TryCast<CompoundCondition>();
        if (compound != null)
        {
            var inner = NullableRaw.ReadReferenceArrayAt<IEffectCondition>(compound, "_conditions");
            if (inner == null) return false;
            for (int i = 0; i < inner.Length; i++)
                if (!ConditionMet(inner[i], effectId, reader, writer, target)) return false;
            return true;
        }
        // Only the placement rules are judged. A status, a shard count or a trigger sitting in the
        // same compound is left alone : this is placement feedback, and reporting a part switched
        // off for a reason no amount of moving can fix is worse than saying nothing about it.
        if (!IsAllowlisted(condition)) return true;
        return condition.IsTargetConditionMet(effectId, reader, writer, target);
    }

    private static bool IsAllowlisted(IEffectCondition condition)
    {
        IntPtr klass = IL2CPP.il2cpp_object_get_class(condition.Pointer);
        if (AllowlistedConditionClasses.TryGetValue(klass, out bool allowlisted)) return allowlisted;
        string name = RuntimeDiscovery.NativeClassName(condition);
        if (name.Length != 0 && SeenConditionNames.Count < 400) SeenConditionNames.Add(name);
        allowlisted = PositionalConditions.Contains(name);
        AllowlistedConditionClasses[klass] = allowlisted;
        return allowlisted;
    }

    // The game's own out-of-battle reader, the one its tooltips evaluate against. It hangs off the
    // run's tooltip controller, which the mod already finds for its button tooltips.
    // The controller the game evaluates its own out-of-battle effects through. Found once and kept.
    //
    // Keeping it is not a micro-optimisation, it is what makes the per-frame refresh possible at
    // all. FindLive walks every loaded object in the game. That was affordable four times a second
    // and is not affordable sixty, and doing it per frame would have cost more in stutter than the
    // quarter-second of lag this round set out to remove : the fix would have been the regression.
    //
    // Re-found whenever it goes, and the read is still guarded, because a wrapper stays non-null
    // after the scene destroys the object behind it and reaching through one throws.
    private IBattleContextReader FindOutOfBattleReader()
    {
        if (_tooltipController == null)
        {
            if (!_tooltipControllerSearch.ShouldTry()) return null;
            _tooltipController = RuntimeDiscovery.FindLive<GameRunTooltipController>();
            if (_tooltipController == null) _tooltipControllerSearch.Missed();
            else _tooltipControllerSearch.Found();
        }
        if (_tooltipController == null) return null;
        try
        {
            GameRunTooltipContext context = _tooltipController.Context;
            return context?.BattleContextReader;
        }
        catch
        {
            _tooltipController = null; // the scene took it ; look again next frame.
            return null;
        }
    }

    // One line, once per session, so a report that nothing ever glows can be answered from the log
    // instead of a play session.
    private void LogFirstResult()
    {
        if (_loggedFirstResult) return;
        _loggedFirstResult = true;
        MelonLogger.Msg($"[TargetingMod] positional glow active: {_live.Heroes} hero(es), {_live.Items} item(s), {_live.Relics} relic(s), {_live.Abilities} ability/rank-modifier(s) with placement-dependent parts; scanned {_live.Scanned} effect(s), {_live.Positional} positional, {_live.Unreadable} unreadable, {_live.UnattributedAbilities} hero-owned effect(s) not traced to their entry");
        if (_live.Answered) return;
        // Nothing matched. Say what the run actually contains, so one log answers whether the
        // class names are being read at all or simply are not on this board.
        var sample = new List<string>(SeenConditionNames);
        sample.Sort(StringComparer.Ordinal);
        int shown = Math.Min(sample.Count, 24);
        MelonLogger.Msg($"[TargetingMod] positional glow saw {sample.Count} distinct condition(s); first {shown}: {string.Join(", ", sample.GetRange(0, shown))}");
    }
}
