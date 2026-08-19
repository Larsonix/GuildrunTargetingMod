using System;
using GuildrunTargetingMod.Interop;
using GuildrunTargetingMod.Ui;
using Il2CppEmber.Balancing;
using Il2CppEmber.Balancing.Difficulty;
using Il2CppEmber.Balancing.Sheets.Characters.Classes;
using Il2CppEmber.Balancing.Sheets.Items;
using Il2CppEmber.Balancing.SimulationBridge;
using Il2CppEmber.Scopes.GameRun.Challenge.Data;
using Il2CppEmber.Scopes.GameRun.Effects.Data;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data.Characters;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data.Items;
using Il2Cppgg.leyline.balancing.Data;
using Il2Cppgg.leyline.core.Mvcs.Model;
using MelonLoader;
using FP = Il2CppPhoton.Deterministic.FP;
using ClassList = Il2CppSystem.Collections.Generic.List<Il2Cppgg.leyline.balancing.Data.BalancingRef<Il2CppEmber.Balancing.Sheets.Characters.Classes.IHeroClassEntry>>;

namespace GuildrunTargetingMod;

// Marks the Rift Seal when it is not paying and moving it would fix that.
//
// In a Red Rift run the Seal is charged by equipping it to Heroes of different classes and
// winning. The classes are grouped in threes, each group needs several wins, and a group that is
// already full gains nothing from another one. The holder still pays the Seal's stat penalty
// either way, so a win on a full group is a fight fought for nothing.
//
// The game warns when the Seal is not equipped at all, and refuses to start the battle until it
// is. It says nothing about it being worn by the wrong Hero, which is the one a playtester said
// he gets wrong most.
//
// It says it with the red line on the Seal's own icon, and it never says it in words. That is an
// owner ruling from play, 2026-08-18, and it is the right one : the mark already means "this is
// not paying and rearranging fixes it" everywhere else in the mod, which is exactly what a Seal on
// a fully charged Hero is. A sentence floating over the board said the same thing in a different
// language, in a different place, about a thing whose icon was sitting there unmarked.
//
// The corollary is the rule this class is built around : NO MARK WHEN NOTHING WOULD FIX IT. If
// every fielded Hero's groups are already full, moving the Seal gains nothing, so the icon says
// nothing. A mark that points at something the player cannot act on is worse than silence.
//
// Everything here reads state the game keeps anyway, and the rule it applies is the game's own,
// taken from the action that does the charging. The two places it could drift are both closed :
// the holder's classes come from the same property the charging code reads, and the number of
// wins a group needs is read from the singleton rather than written down here.
internal sealed class AnchorWatch
{
    // The answer changes when the player moves the Seal between Heroes or benches somebody, both of
    // which happen at the speed of a click, and the check walks the run's items and the holder's
    // classes. Four frames is about fifteen times a second : fast enough that the mark appears with
    // the click that caused it, slow enough that the walk is not repeated per frame.
    //
    // It was fifteen frames, which is a quarter of a second of the icon disagreeing with the board.
    //
    // A DURATION now, and it used to be four FRAMES. "About fifteen times a second" was only ever
    // true at sixty frames a second: the same four frames are five checks a second at twenty and
    // fifty at two hundred, so the rate this comment promises held on exactly one machine. Written
    // as the interval it was always meant to be.
    private const float SecondsBetweenChecks = 1f / 15f;

    // A reader caught mid setup, or balancing asked for before it is ready, is a passing condition
    // and not a broken binding. Treating one of those as permanent has already cost this mod a
    // whole session once, so a fault only turns the feature off after it has repeated this many
    // times in a row, and a single success clears the count.
    private const int FaultsBeforeDisabling = 30;

    private readonly Capabilities _capabilities;
    private string _lastFault;
    private string _lastState;
    private bool _lastStateWasChargeOutcome;
    private string _lastChargeWhere;
    private int _lastChargeCount;
    private bool _lastChargeMarked;
    private float _nextCheckAt;
    private int _consecutiveFaults;

    private string _markedSealItemId;

    public bool HasBlocked => _capabilities.SealMark && _markedSealItemId != null;

    public AnchorWatch(Capabilities capabilities) => _capabilities = capabilities;

    /// <summary>
    /// True for the Rift Seal when winning this battle would charge nothing AND moving the Seal to
    /// a Hero on the board would charge something.
    /// </summary>
    /// <remarks>
    /// Two situations, one answer, because they are the same situation from the player's side :
    /// the Seal is in the bag, or the Seal is on a Hero whose groups are all full. Either way the
    /// fight about to be fought charges nothing and putting the Seal on somebody else fixes it.
    ///
    /// Silent whenever nothing would fix it, and silent in every uncertain case : a read that
    /// fails, a class the mapping does not know, a holder whose classes cannot be read. A mark that
    /// fires when the win would in fact have counted is worse than no mark at all.
    /// </remarks>
    public bool IsSealNotPaying(string itemId) =>
        _capabilities.SealMark && _markedSealItemId != null && itemId != null &&
        string.Equals(_markedSealItemId, itemId, StringComparison.Ordinal);

    /// <summary>Recomputes the cached answer a dozen times a second. Safe to call every frame.</summary>
    public void Update()
    {
        if (!_capabilities.SealMark) return;
        // Fully qualified rather than adding a using for UnityEngine to a file that reaches into a
        // dozen generated Il2Cpp namespaces, several of which carry their own Time.
        float now;
        try { now = UnityEngine.Time.realtimeSinceStartup; }
        catch { now = 0f; }
        if (_nextCheckAt > 0f && now < _nextCheckAt) return;
        _nextCheckAt = now + SecondsBetweenChecks;
        Evaluate();
    }

    /// <summary>Forgets the cached answer, so the next battle starts from a fresh read.</summary>
    public void Reset()
    {
        _nextCheckAt = 0f;
        _markedSealItemId = null;
    }

    // Which global counter a class feeds. Mirrors the charging action's own table, which pairs
    // seven class ids onto three counters : Tank and Vanguard, then Warrior, Assassin and Duelist,
    // then Mystic and Mage.
    //
    // The keys are strings the game stores in the save, which is what makes this the most durable
    // thing the mod reads : renaming one would break every player's run, so they do not move.
    //
    // A class this does not know about returns null and is treated as still chargeable, so the
    // warning stays silent. If Leyline adds an eighth class, the worst this does is say nothing.
    private static string CounterKeyForClass(int classId) => classId switch
    {
        2 or 3 => "riftAnchorTankVanguardCount",                // Tank, Vanguard
        1 or 4 or 5 => "riftAnchorAssassinDuelistWarriorCount",  // Warrior, Assassin, Duelist
        6 or 7 => "riftAnchorMageMysticCount",                   // Mystic, Mage
        _ => null
    };

    // Works out whether the Seal should be marked, and files the answer for the icons to read.
    //
    // Every early return here leaves the Seal unmarked, which is the honest answer to every
    // uncertain case as well as to every case where nothing is wrong.
    private void Evaluate()
    {
        _markedSealItemId = null; // nothing to point at until this read says otherwise.
        try
        {
            // Only during the challenge run this belongs to, and only while the Seal still has
            // something to prove. The game gates its own anchor handling on the same two reads.
            if (!DataReaders.TryGet<ChallengeReader>(out var challenge) || challenge == null) return;
            if (!challenge.IsChallengeRun) { Report("not a Red Rift run, nothing to mark"); return; }
            if (challenge.GetChallengeState(ChallengeType.ChargeTheAnchor) != ChallengeState.Pending)
            { Report("the Seal challenge is not pending, nothing to mark"); return; }

            if (!DataReaders.TryGet<GameRegistryDataReader>(out var registry) || registry == null) return;

            IDifficultiesSingleton difficulties = EmberBalancing.Instance.GetSingleton<IDifficultiesSingleton>();
            if (difficulties == null) return;
            int winsPerClass = difficulties.ChallengeWinsPerClass;
            if (winsPerClass <= 0) return;

            if (!TryFindSeal(registry, difficulties.AnchorItemRef, out ItemId sealId, out HeroData holder))
            { Report("this run has no Seal"); return; }

            _consecutiveFaults = 0; // a clean read clears whatever went wrong before it.

            // Worn by somebody. There is only something to point at when that somebody charges
            // nothing, and even then only when moving it to another fielded Hero would charge
            // something. CountHeroesThatWouldCharge answers the second question and needs no
            // exclusion for the holder, since a holder who charges nothing is one it discounts.
            string where = "in the bag";
            if (holder != null)
            {
                // A benched holder is the game's problem, not ours : it refuses to start the battle
                // at all until the Seal is on a fielded Hero, so a mark would only crowd that.
                if (registry.IsHeroInReserve(holder.HeroId))
                { Report("worn by a benched Hero, which the game refuses to start the fight over"); return; }
                if (!EveryClassAlreadyCharged(holder, registry, winsPerClass))
                { Report("worn, and winning this fight still charges it : no mark"); return; }
                where = "worn by a Hero whose groups are all charged";
            }
            // Reported from play : the Seal was in the inventory, a Hero on the board could have
            // worn it and gained a stage, and nothing said so. The game refuses to start the battle
            // without it equipped, so it does eventually stop you, but it never points at the thing
            // in your bag.
            int couldCharge = CountHeroesThatWouldCharge(registry, winsPerClass);
            if (couldCharge > 0)
            {
                _markedSealItemId = sealId.ToString();
                ReportChargeOutcome(where, couldCharge, true);
            }
            else ReportChargeOutcome(where, couldCharge, false);
        }
        catch (Exception e)
        {
            // Its own failure domain : losing this must never cost the arrows, the preview or the
            // prediction. It is also not latched on the first throw, because the ordinary reason
            // to land here is a reader or the balancing data being asked for a moment too early,
            // which fixes itself on the next check.
            if (_lastFault != e.Message)
            {
                _lastFault = e.Message;
                MelonLogger.Warning("[TargetingMod] anchor check failed (will retry): " + e.Message);
            }
            if (++_consecutiveFaults >= FaultsBeforeDisabling)
                _capabilities.DisableSealMark(
                    $"anchor state unreadable {_consecutiveFaults} checks in a row: {e.Message}");
        }
    }

    // Finds the Seal and who is wearing it.
    //
    // Returns false only when this run has no Seal at all. A Seal that exists but is worn by
    // nobody is a real answer, and the one the player most needs pointing out, so it comes back
    // true with a null holder rather than being lumped in with "nothing to say".
    private static bool TryFindSeal(GameRegistryDataReader registry,
        BalancingRef<IItemEntry> anchorRef, out ItemId sealId, out HeroData holder)
    {
        holder = null;
        sealId = default;
        if (!anchorRef.HasValue()) return false;

        // Which ItemId is the Seal in this run.
        bool found = false;
        foreach (ItemData item in registry.Data.Items.Values)
        {
            if (item == null || item.ItemRef != anchorRef) continue;
            sealId = item.Id;
            found = true;
            break;
        }
        if (!found) return false; // no Seal in this run.

        // Who is wearing it, asked of the HEROES.
        //
        // Careful here : not of the item. `ItemData.OwnerEntityId` is written to return null
        // unconditionally, so asking the item who holds it always answers nobody, silently.
        //
        // This used to go the long way round instead, walking every effect in the run, matching one
        // to the Seal and reading an owner id out of the effect object at a raw field offset. That
        // works, but it is three interop reads and a hand-resolved offset deep to answer a question
        // the game answers directly, and every one of those is a place to silently return nothing.
        // `IsItemEquipped` is the game's own predicate and is what the placement marks already use
        // to find an item's wearer.
        foreach (HeroData hero in registry.Data.Heroes.Values)
        {
            if (hero == null || !hero.IsItemEquipped(sealId)) continue;
            holder = hero;
            return true;
        }
        return true; // the Seal exists and nobody is wearing it.
    }

    // How many Heroes on the board would gain a stage by wearing the Seal. Asked of every fielded
    // Hero, because the player can choose freely between them and one who would charge something is
    // enough to make the Seal worth moving.
    //
    // Counted rather than answered yes or no purely so the log can say how many. A count of zero
    // and a count of three are the difference between "the mark is correctly silent" and "the mark
    // should be there and is not", and without the number that is a play session to find out.
    private static int CountHeroesThatWouldCharge(GameRegistryDataReader registry, int winsPerClass)
    {
        int count = 0;
        foreach (HeroData hero in registry.Data.Heroes.Values)
        {
            if (hero == null || registry.IsHeroInReserve(hero.HeroId)) continue;
            // EveryClassAlreadyCharged answers false when it cannot read a Hero, which would make
            // an unreadable Hero look like a reason to move the Seal. Read the classes first and
            // skip the ones that cannot answer, so silence stays the uncertain case here too.
            ClassList classes = NullableRaw.ReadObjectAt<ClassList>(hero, "_allClasses");
            if (classes == null || classes.Count == 0) continue;
            if (!EveryClassAlreadyCharged(hero, registry, winsPerClass)) count++;
        }
        return count;
    }

    // One line whenever the answer changes, and nothing at all while it holds.
    //
    // The Seal was reported as "buggy and/or inconsistent", and there was no way to tell from the
    // log whether it had decided not to mark, decided to mark something that was not on screen, or
    // never run. A feature whose whole job is to make one judgement has to be able to say what it
    // judged. Kept across placements on purpose, so an unchanged answer stays quiet all session.
    private void Report(string state)
    {
        if (!_lastStateWasChargeOutcome && string.Equals(_lastState, state, StringComparison.Ordinal)) return;
        _lastStateWasChargeOutcome = false;
        _lastState = state;
        MelonLogger.Msg("[TargetingMod] Rift Seal: " + state);
    }

    // These are the only reports whose state contains values found during the check. Compare those
    // values first, so an unchanged answer does not build a sentence merely for Report to discard.
    private void ReportChargeOutcome(string where, int couldCharge, bool marked)
    {
        if (_lastStateWasChargeOutcome && string.Equals(_lastChargeWhere, where, StringComparison.Ordinal) &&
            _lastChargeCount == couldCharge && _lastChargeMarked == marked)
            return;
        _lastStateWasChargeOutcome = true;
        _lastChargeWhere = where;
        _lastChargeCount = couldCharge;
        _lastChargeMarked = marked;
        _lastState = marked
            ? $"{where}, {couldCharge} fielded Hero(es) would charge it : MARKED"
            : $"{where}, but nobody on the board would charge it either : no mark";
        MelonLogger.Msg("[TargetingMod] Rift Seal: " + _lastState);
    }

    // The charging action walks the holder's classes and charges the first group that is not yet
    // full, so a Hero of two classes whose first group is full still charges the second. Nothing
    // is charged only when every group the Hero belongs to is full, and that is what this asks.
    // Being a question about all of them, it does not depend on the order the action walks them
    // in, which is one less thing that can drift.
    //
    // It reads the backing list behind Classes, which is the Hero's own and item-granted classes
    // together and is the property the charging action itself reads. A Crest or a Glyph therefore
    // counts here exactly as it counts there. The concrete list is read rather than the interface
    // the property is typed as, because walking an interface-typed generic through interop is a
    // known way to bring the process down.
    private static bool EveryClassAlreadyCharged(HeroData holder, GameRegistryDataReader registry, int winsPerClass)
    {
        ClassList classes = NullableRaw.ReadObjectAt<ClassList>(holder, "_allClasses");
        if (classes == null || classes.Count == 0) return false; // unreadable proves nothing.

        // Derive the fixed-point scale from the game's own definition of one rather than writing
        // the shift down here, so comparing against a plain integer stays exact.
        long required = FP._1.RawValue * winsPerClass;
        for (int i = 0; i < classes.Count; i++)
        {
            string key = CounterKeyForClass(classes[i].Id.SequentialId);
            if (key == null) return false; // a class the mapping does not know : say nothing.
            // A counter the run has never written is simply absent, which is not full.
            if (!registry.TryGetGlobalPermanentCustomData(key, out FP charged)) return false;
            if (charged.RawValue < required) return false; // this group still has room.
        }
        return true;
    }
}
