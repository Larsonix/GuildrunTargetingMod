using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Il2CppEmber.Balancing;
using Il2CppEmber.Balancing.SimulationBridge;
using Il2CppEmber.Balancing.SimulationBridge.Context;
using Il2CppEmber.Balancing.SimulationBridge.Effects;
using Il2CppEmber.Balancing.SimulationBridge.Effects.Conditions;
using Il2CppEmber.Balancing.Sheets.Abilities.ActiveAbilities;
using Il2CppEmber.Balancing.Sheets.Abilities.PassiveAbilities;
using Il2CppEmber.Balancing.Sheets.Characters;
using Il2CppEmber.Balancing.Sheets.Characters.Specializations;
using Il2CppEmber.Balancing.Sheets.RankModifiers;
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

    /// <summary>
    /// The role words both halves of this feature use in place of an entry id they cannot both read.
    /// </summary>
    /// <remarks>
    /// A Hero-owned part is named by WHERE IT CAME FROM rather than by which entry it is: the
    /// starting ability, the specialization, or a particular rank modifier. Two of those three are
    /// pure roles with no id at all.
    ///
    /// That is not a shortcut, it is the only scheme that works. Naming the specialization requires
    /// reading `Specialization`, a nullable of a BalancingRef whose id carries a byte array and a
    /// string, so it is not blittable and cannot cross the interop boundary by any route. A rank
    /// modifier CAN be named, because its ref arrives as a plain dictionary key rather than inside a
    /// nullable, so that one keeps its id and stays distinguishable from its siblings.
    ///
    /// Spelled here, once, so the two ends cannot drift apart.
    /// </remarks>
    public const string StartingAbilityRole = "start";
    public const string SpecializationRole = "spec";
    public static string RankModifierRole(string entryId) => "rank/" + entryId;

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

    /// <summary>Every ability key this snapshot holds, with its state, for the log.</summary>
    /// <remarks>
    /// The WHOLE set, not a sample. The one-shot "first key filed" line this replaces fired on the
    /// first refresh of a session, which is the opening battle of a run and has nothing
    /// placement-dependent in it, so it reported an empty board and never spoke again. A sampled
    /// instrument answers about the moment it happened to fire rather than the moment being asked
    /// about, and there are at most a handful of these.
    /// </remarks>
    public string DescribeAbilities()
    {
        if (_byAbility.Count == 0) return "none";
        var parts = new List<string>(_byAbility.Count);
        foreach (KeyValuePair<string, PartState> pair in _byAbility) parts.Add(pair.Key + "=" + pair.Value);
        parts.Sort(StringComparer.Ordinal);
        return string.Join(" | ", parts);
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
    // The longest the marks may stand without being recomputed while the board looks unchanged.
    // A quarter of a second is under the threshold at which a stale mark reads as wrong, and it is
    // what catches the changes occupancy cannot see, such as an item moved between two Heroes.
    private const float UnchangedRefreshSeconds = 0.25f;

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

    // The subset of the list above whose answer depends on WHO is being asked about, and not only on
    // where the owner is standing. Classified from the game's own condition bodies rather than from
    // their names: every one of these reads BOTH the target and the owner, so what it really asks is
    // "is SOMEBODY ELSE in this relation to the owner".
    //
    // ASKING ONE OF THESE ABOUT THE OWNER IS NOT A SLIGHTLY WRONG QUESTION. IT IS A QUESTION WITH A
    // CONSTANT ANSWER, and that is what this list exists to stop.
    //
    // IsInFrontOfOwner and IsBehindOwner open with `if (targetId == owner.Id) return false;`, so Sal's
    // The Lover and Pimenta's The Lover reported "not paying" on every board ever drawn and no
    // placement could ever clear it. IsAdjacentToOwner needs no special case to be just as constant:
    // the distance from a Hero to itself is zero and the test is "== 1". The two that fail the other
    // way are worse for being invisible, IsOwnerOrAdjacentToOwner and IsInRangeOfOwner both answer
    // true for the owner by construction, so the fourteen entries carrying them could never report
    // anything at all and the feature looked like it simply had nothing to say about them.
    //
    // Note what is NOT here. IsOwnerInBackRow and IsOwnerAdjacentToExactlyOneAlly ignore the target
    // entirely and read the owner's own hex, which is why those have always worked. And the whole
    // IsEffectTarget family is left out on purpose: those ask where ONE named Hero is standing, with
    // no reference to an owner, so asking about the wearer of an item is exactly right and asking
    // about the board at large would light Sentinel's Plate for a row its wearer is not in.
    private static readonly HashSet<string> RelationalConditions = new(StringComparer.Ordinal)
    {
        "IsAdjacentToOwnerCondition",
        "IsBehindOwnerCondition",
        "IsEffectTargetInSameRowAsOwnerCondition",
        "IsInFrontOfOwnerCondition",
        "IsInRangeOfOwnerCondition",
        "IsOnlyAllyInFrontOfOwnerCondition",
        "IsOwnerOrAdjacentToOwnerCondition",
        "IsOwnerOrDirectlyInFrontOfOwnerCondition",
        "IsOwnerOrOnlyAllyInFrontOfOwnerCondition"
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
    // Whether an effect must be judged against the board rather than against its owner. Authored
    // data like the line above, cached the same way and for the same reason, and keyed by ID for the
    // same reason again : an effect object freed when an item is unequipped must not hand its answer
    // to whatever lands on that address next.
    private readonly Dictionary<string, bool> _relationalByEffect = new(StringComparer.Ordinal);
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
    // Frames where the board had not moved and the floor had not expired, so nothing was read.
    private int _skippedUnchanged;
    private ulong _lastSignature;
    private bool _signatureSeen;
    private float _nextForcedRefreshAt;
    private double _firstRefreshMs;
    private double _worstAfterFirstMs;
    private double _totalAfterFirstMs;
    private bool _loggedEffectFault;
    private bool _loggedAbilityKey;
    private bool _loggedPreviewResult;
    // Every distinct condition class the run actually contains, gathered until the first result
    // is reported. If nothing matches the allowlist, this is what says whether the names are
    // being read correctly or simply do not appear on this board.
    private static readonly HashSet<string> SeenConditionNames = new(StringComparer.Ordinal);
    private static readonly Dictionary<IntPtr, bool> AllowlistedConditionClasses = new();

    public PositionalGlow(Capabilities capabilities) => _capabilities = capabilities;

    /// <summary>
    /// The player's verbose-log switch. The key trace below is diagnostic scaffolding, not something
    /// a player needs in their log, and it stays behind this.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted after it served its purpose. Those two lines, one from each half of
    /// this feature, are what ended a hunt that five careful source reads had each got wrong: when
    /// two components must agree on a composed key, the only cheap way to see a disagreement is to
    /// print both spellings. The next time they drift this is the tool, and rebuilding it from
    /// scratch under pressure is how a lesson gets paid for twice.
    /// </remarks>
    public Func<bool> DevLog { get; set; }

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
        // The POPULATION first, because the timings were all this reported and the population is the
        // question anybody actually brings to this log. It was written once per session by
        // LogFirstResult, at the first refresh, which on every shipped log is the opening battle of a
        // run : no items, no specializations, no rank modifiers. So every log this mod has ever
        // produced says "0 hero(es), 0 item(s)" and none of them meant it. Printed per placement, it
        // says what THAT board held.
        $"{_live.Heroes} hero(es), {_live.Items} item(s), {_live.Relics} relic(s), " +
        $"{_live.Abilities} ability/rank-modifier(s) placement-dependent on this board; " +
        $"{_live.Positional} positional of {_live.Scanned} scanned, {_live.Unreadable} unreadable, " +
        $"{_live.UnattributedAbilities} hero-owned effect(s) not traced to their entry; " +
        $"{_refreshes} refresh(es), first {_firstRefreshMs:F2} ms, " +
        $"worst after that {_worstAfterFirstMs:F2} ms, mean after that {MeanAfterFirstMs:F2} ms; " +
        $"{_skipped} frame(s) not read because a drag preview was answering, " +
        $"{_skippedUnchanged} not read because nobody had moved";

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
    public void Update(bool liveNeeded, ulong boardSignature)
    {
        if (!_capabilities.PositionalGlow) return;
        if (!liveNeeded) { _skipped++; return; }
        // THE ANSWER ONLY CHANGES WHEN THE BOARD DOES, SO STOP RECOMPUTING IT SIXTY TIMES A SECOND.
        //
        // This walked every fielded Hero, every equipped item and every owned relic on EVERY frame,
        // evaluating the game's own condition code for each positional effect. Measured in play
        // 2026-08-20: a mean of 0.12 ms but a worst of 8.82 ms on a settled frame, which tripped the
        // mod's own slow-frame warning. The cost also grew when relational conditions started being
        // asked of every Hero rather than one, which is correct and is not free.
        //
        // None of that work can produce a different answer while nobody has moved. The occupancy
        // signature the drag tracker already computes says exactly that, and it costs nothing
        // because that walk happens anyway.
        //
        // The floor is what keeps this honest. Occupancy does NOT see an item moved between two
        // Heroes, so anything the cheap test cannot notice is corrected within a quarter of a
        // second. Same shape as the hover raycast's own floor and the area scan's interval, and the
        // same reasoning: a cheap test may be less than exhaustive only when something bounded
        // catches what it misses.
        float now;
        try { now = UnityEngine.Time.realtimeSinceStartup; }
        catch { now = 0f; }
        bool boardMoved = !_signatureSeen || boardSignature != _lastSignature;
        if (!boardMoved && now < _nextForcedRefreshAt) { _skippedUnchanged++; return; }
        _lastSignature = boardSignature;
        _signatureSeen = true;
        _nextForcedRefreshAt = now + UnchangedRefreshSeconds;
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
        _relationalByEffect.Clear();
        _unreadable.Clear();
        SeenConditionNames.Clear();
        _tooltipController = null; // belongs to the battle scene that is being taken apart.
        _tooltipControllerSearch.Reset();
        _refreshes = 0;
        _skipped = 0;
        _skippedUnchanged = 0;
        _signatureSeen = false;
        _lastSignature = 0UL;
        _nextForcedRefreshAt = 0f;
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

    private bool IsRelationalCached(EffectId effectId, ModularEffect modular)
    {
        string key = EffectKey(modular.Pointer, effectId);
        if (_relationalByEffect.TryGetValue(key, out bool known)) return known;
        bool relational = HasRelational(modular.Condition);
        _relationalByEffect[key] = relational;
        return relational;
    }

    // Does this effect's condition tree contain anything that has to be judged against the board.
    // Walked exactly like IsPositional, including reading a compound through its backing array
    // rather than the interface list, which is a known way to bring the process down.
    private static bool HasRelational(IEffectCondition condition)
    {
        if (condition == null) return false;
        try
        {
            CompoundCondition compound = condition.TryCast<CompoundCondition>();
            if (compound != null)
            {
                var inner = NullableRaw.ReadReferenceArrayAt<IEffectCondition>(compound, "_conditions");
                if (inner == null) return false;
                for (int i = 0; i < inner.Length; i++)
                    if (HasRelational(inner[i])) return true;
                return false;
            }
            return RelationalConditions.Contains(RuntimeDiscovery.NativeClassName(condition));
        }
        catch
        {
            // Unclassifiable means "judge it the way it was judged before", which is the behaviour
            // that has shipped for four versions rather than a new guess.
            return false;
        }
    }

    // Whether ANY Hero on the board satisfies this effect, asked one Hero at a time.
    //
    // Per EFFECT and never per condition, and that is the part worth stating. A compound has to be
    // judged against ONE Hero at a time or a rule reading "in front of the owner AND in the front
    // row" would be answered about two different people and reported as a single fact. Asking each
    // Hero the whole question and taking the best answer is the only form that cannot do that.
    //
    // The same walk EvaluateRelic already makes, deliberately left as a second copy rather than
    // shared with it : a relic asks this because it HAS no owner, and this asks it because the owner
    // is the wrong Hero to ask about. Same shape, different reasons, and folding them together would
    // hide that a change to one is not automatically right for the other.
    private static bool MeetsForAnyHero(ModularEffect modular, IBattleContextReader reader,
        IBattleContextWriter writer, GameRegistryDataReader registry)
    {
        foreach (HeroData hero in registry.Data.Heroes.Values)
        {
            if (hero == null || registry.IsHeroInReserve(hero.HeroId)) continue;
            if (MeetsFor(modular, reader, writer, hero.HeroId.ToEntityId())) return true;
        }
        return false;
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
                Dictionary<string, string> abilityEntries = AbilityEntriesFor(data, hero);
                EvaluateOwned(data.EffectsByHero, hero.HeroId, target, heroKey, null,
                    reader, writer, effects, registry, snapshot, abilityEntries);
                EvaluateOwned(data.EffectsByHeroSpecialization, hero.HeroId, target, heroKey, null,
                    reader, writer, effects, registry, snapshot, abilityEntries);
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

    // Names hero-owned effects from the game's indexes, whose keys carry the same balancing refs
    // the icon layer reads. The effect's OriginEntryId cannot be used here: BalancingEntryId is not
    // blittable, so the generated nullable getter returns its default value and files every effect
    // under the empty id.
    private static Dictionary<string, string> AbilityEntriesFor(EffectsData data, HeroData heroData)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        bool rankModifiersMapped = MapRankModifiers(data, heroData.HeroId, entries);
        try { MapSpecialization(data, heroData, entries); }
        catch { /* Attribution must never cost the Hero tile its already-correct answer. */ }

        // EffectsByHero double-files rank modifiers with the starting ability. Without a complete
        // rank-modifier exclusion, attributing its leftovers would put a blocked rank modifier on
        // the starting ability's innocent icon. Keep evaluating those effects for MergeHero, but
        // leave their ability entries unattributed if the tuple-keyed index could not be walked.
        if (rankModifiersMapped)
        {
            try { MapStartingAbility(data, heroData, entries); }
            catch { /* Leave the effect unattributed instead of skipping its Hero evaluation. */ }
        }
        return entries;
    }

    private static bool MapRankModifiers(EffectsData data, HeroId heroId,
        Dictionary<string, string> entries)
    {
        try
        {
            var rankEffects = data.RankModifierEffects;
            if (rankEffects == null) return false;
            foreach (var rankKey in rankEffects.Keys)
            {
                if (rankKey == null) continue;
                HeroId indexedHero = rankKey.Item1;
                if (!NullableRaw.SameRawId(in indexedHero, in heroId)) continue;
                BalancingRef<IRankModifierEntry> rankRef = rankKey.Item2;
                if (rankRef == null) continue;
                var ids = rankEffects[rankKey];
                if (ids == null) continue;
                string entryKey = PlacementSnapshot.RankModifierRole(rankRef.Id.ToString());
                for (int i = 0; i < ids.Count; i++)
                    entries[ids[i].ToString()] = entryKey;
            }
            return true;
        }
        catch
        {
            // A partial walk is not enough to distinguish leftovers in EffectsByHero. Remove it so
            // the caller cannot mistake an unseen rank modifier for the starting ability.
            entries.Clear();
            return false;
        }
    }

    private static void MapSpecialization(EffectsData data, HeroData heroData,
        Dictionary<string, string> entries)
    {
        // THE SPECIALIZATION IS NAMED BY ITS ROLE AND NEVER BY ITS ENTRY, BECAUSE ITS ENTRY CANNOT
        // BE READ AT ALL.
        //
        // `IHeroTagsSource.Specialization` is a nullable of BalancingRef, and BalancingRef carries a
        // BalancingEntryId, which carries a byte array and a string. That is not blittable, so there
        // is no way across the interop boundary: the generated getter throws while constructing the
        // value, and a raw offset read cannot rebuild a struct made of references either. This is the
        // third field of that exact family to defeat this feature, after the effect's OriginEntryId
        // and the bar slot's owner id.
        //
        // It does not need to be read. Being IN this index already says which part the effect came
        // from, and the icon layer can work out which of its slots is the specialization from the
        // rank modifiers alone, which ARE readable. So both ends agree on the word "spec" and neither
        // one has to name the entry.
        if (!data.EffectsByHeroSpecialization.ContainsKey(heroData.HeroId)) return;
        var ids = data.EffectsByHeroSpecialization[heroData.HeroId];
        if (ids == null) return;
        for (int i = 0; i < ids.Count; i++)
            entries[ids[i].ToString()] = PlacementSnapshot.SpecializationRole;
    }

    private static void MapStartingAbility(EffectsData data, HeroData heroData,
        Dictionary<string, string> entries)
    {
        if (!data.EffectsByHero.ContainsKey(heroData.HeroId)) return;
        ICharacterEntry heroEntry = heroData.HeroRef.ToEntry()?.TryCast<ICharacterEntry>();
        if (heroEntry == null) return;
        // The starting ability is a role too. Its entry IS readable, unlike the specialization's,
        // but naming it by id would leave one of the three hero-owned parts spelled differently from
        // the other two, and one scheme with an exception in it is two schemes.
        string entryKey = PlacementSnapshot.StartingAbilityRole;
        var ids = data.EffectsByHero[heroData.HeroId];
        if (ids == null) return;
        for (int i = 0; i < ids.Count; i++)
        {
            string effectKey = ids[i].ToString();
            if (!entries.ContainsKey(effectKey)) entries[effectKey] = entryKey;
        }
    }

    private void EvaluateOwned<TKey>(Il2CppSystem.Collections.Generic.Dictionary<TKey, Il2CppSystem.Collections.Generic.List<EffectId>> index,
        TKey key, EntityId target, string heroKey, string itemKey,
        IBattleContextReader reader, IBattleContextWriter writer, EffectsReader effects,
        GameRegistryDataReader registry,
        PlacementSnapshot snapshot, Dictionary<string, string> abilityEntries = null)
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
                // Who to ask about. An effect whose rule is about where its OWNER stands is asked
                // about that owner, which is what this has always done and is right for an item : an
                // item's rule is about the Hero wearing it and nobody else. An effect whose rule is
                // about somebody else's position RELATIVE to the owner cannot be answered that way at
                // all, because the owner is never in front of, behind, or one hex from itself. See
                // RelationalConditions for what that cost.
                bool met = IsRelationalCached(ids[i], modular)
                    ? MeetsForAnyHero(modular, reader, writer, registry)
                    : MeetsFor(modular, reader, writer, target);
                PartState state = met ? PartState.Live : PartState.Blocked;
                snapshot.MergeHero(heroKey, state);
                if (itemKey != null) { snapshot.MergeItem(itemKey, state); continue; }
                // Hero-owned, so it came from a rank modifier, a passive, or the specialization.
                // The per-Hero map names it from the game's indexes. An effect those indexes cannot
                // name is COUNTED rather than guessed at: the tile under the Hero still says
                // something is switched off, and no icon is told a story that might not be true.
                // Keyed by the HERO AND the entry, never the entry alone. Two Heroes can carry the
                // same rank modifier in opposite states, and a key that named only the modifier
                // would merge them : one Hero out of position would put the mark on everybody
                // else's copy of it too, which is the feature telling a story that is not true.
                if (abilityEntries != null &&
                    abilityEntries.TryGetValue(ids[i].ToString(), out string entryKey))
                {
                    string abilityKey = PlacementSnapshot.AbilityKey(heroKey, entryKey);
                    snapshot.MergeAbility(abilityKey, state);
                    // Said once, with the key EXACTLY as it is filed. The icon half composes the same
                    // key from the other end, off the ability ref the card is showing, and if the two
                    // spellings differ by so much as a prefix the lookup silently answers "nothing to
                    // say" and no icon can ever be marked. That is indistinguishable from a part that
                    // is simply paying, which is a play session per guess. Print both ends and the
                    // comparison is free.
                    if (!_loggedAbilityKey && DevLog?.Invoke() == true)
                    {
                        _loggedAbilityKey = true;
                        MelonLogger.Msg($"[TargetingMod] ability state FILED under key '{abilityKey}' = {state}");
                    }
                }
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
