using System;
using System.Collections.Generic;
using GuildrunTargetingMod.Interop;
using GuildrunTargetingMod.Ui;
using Il2CppEmber.Balancing;
using Il2CppEmber.Balancing.Aoe;
using Il2CppEmber.Balancing.Sheets.Abilities.ActiveAbilities;
using Il2CppEmber.Balancing.SimulationBridge.AbilityActions;
using Il2CppEmber.Balancing.SimulationBridge.Collisors;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data.Characters;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2Cppgg.leyline.balancing.Data;
using Il2Cppgg.leyline.core.Mvcs.Model;
using MelonLoader;
using UnityEngine;

namespace GuildrunTargetingMod;

/// <summary>The footprint one unit's ability covers, in board units, around wherever it is drawn.</summary>
internal sealed class AoeOutline
{
    /// <summary>One closed loop per shape the area is made of. Almost every ability has exactly one.</summary>
    public readonly Vector2[][] Loops;

    /// <summary>
    /// How far the shape reaches from its own origin, in world units. Used to tell a footprint a
    /// player can step out of from one that covers the board whatever they do.
    /// </summary>
    public readonly float MaxReach;

    /// <summary>
    /// Whether turning the unit turns the shape. A circle does not care, and 690 of the 696 shapes
    /// this build ships are circles, so this is false nearly everywhere.
    /// </summary>
    public readonly bool RotatesWithFacing;

    /// <summary>
    /// Whether every part of this footprint is a circle, which is what makes <see cref="MaxReach"/>
    /// mean "how much ground this covers" rather than merely "how far its furthest point is".
    /// </summary>
    /// <remarks>
    /// For a circle the two are the same number and the reach is a fair measure of coverage. For a
    /// rectangle they are not related at all: a long narrow beam has a distant furthest corner and
    /// covers almost no ground, and a player steps out of it sideways.
    ///
    /// Measured 2026-08-20 across all 27 authored areas in this build. Four were being refused as
    /// covering the whole board. Three were the Dragons' Flame, Frost and Poison Breath, circles of
    /// reach 12.00, and refusing those is the feature working. The fourth was Requiem Barrage, a
    /// RECTANGLE of reach 24.83, which is dodgeable and was being hidden on every board it appeared
    /// on. That was the reported "ability areas do not work at all", and no threshold could have
    /// fixed it: the next rectangles down sit at 11.36, just under the cutoff, so moving the line
    /// either keeps hiding a dodgeable shape or starts showing an undodgeable one.
    /// </remarks>
    public readonly bool AllCircles;

    public AoeOutline(Vector2[][] loops, bool rotatesWithFacing, bool allCircles)
    {
        Loops = loops;
        RotatesWithFacing = rotatesWithFacing;
        AllCircles = allCircles;
        float reach = 0f;
        for (int i = 0; i < loops.Length; i++)
            for (int j = 0; j < loops[i].Length; j++)
            {
                float d = loops[i][j].magnitude;
                if (d > reach) reach = d;
            }
        MaxReach = reach;
    }
}

// Works out the shape of the area each unit's ability covers, so the board can show it where the
// unit will be standing rather than leaving the player to learn it by being caught in one.
//
// The point of this file: there is no hero named anywhere in it, and that is the whole design.
//
// The area an ability places is NOT on the ability. It is a serialized field on the ability's own
// action, and every hero that has one names that field differently : _aoeEntry on the shared
// action, _regularAoeEntry on most of the hero-specific ones, and _stallAoeEntry or
// _protectorAoeEntry or _seasonedAoeEntry beside it on three of them. Reading those by name would
// be ten bindings into hero-specific classes, which is the most fragile surface in the game and
// precisely what this mod is built not to depend on.
//
// So the question asked here is not "what is this hero's area field called". It is "which field on
// this action holds one of these", which has no hero in it at all. A hero added in a patch is
// covered without a line changing here, and an action shaped in a way this cannot read draws
// nothing rather than guessing.
//
// The geometry is never ours either. The shapes come off the game's own collisors, in the units the
// board is already drawn in.
internal sealed class AoeShapes
{
    // Twice a second. Which ability a unit has can change during a placement, since a Hero can be
    // moved off the bench, but it changes at the speed of a person clicking.
    //
    // A DURATION, and it used to be thirty FRAMES. Thirty frames is half a second at sixty frames a
    // second, a third of a second at ninety, and a second and a half at twenty : the rate this
    // comment claims was only ever true on one machine. Every throttle in this mod is now a real
    // interval, so the scan happens twice a second everywhere and only the number of frames between
    // scans differs.
    private const float SecondsBetweenScans = 0.5f;
    private const int FaultsBeforeDisabling = 10;
    // Enough segments that a circle reads as a circle at board scale rather than as a polygon.
    private const int CircleSegments = 32;
    // Photon's fixed point, which every number in the simulation is stored in : the raw integer is
    // the value times two to the sixteenth. Converting through the raw value rather than through a
    // helper keeps this exact and independent of what the runtime chooses to expose.
    private const float FixedPointScale = 65536f;

    private readonly Capabilities _capabilities;
    private readonly Dictionary<string, AoeOutline> _byUnit = new(StringComparer.Ordinal);
    // Worked out once per ability and kept for the run. An ability's shape is authored data, and a
    // miss is cached as readily as a hit : "this one has no area to draw" is an answer.
    private readonly Dictionary<string, AoeOutline> _byAbility = new(StringComparer.Ordinal);
    private readonly HashSet<string> _abilitiesWithoutShape = new(StringComparer.Ordinal);
    // Every distinct action class the board actually contains, gathered until the first result is
    // reported. A count of zero footprints has two completely different causes : this board has no
    // ability that places an area, or the walk is reading nothing at all. Only naming what it saw
    // separates them, and the alternative is a play session per guess. That lesson is already
    // written down once in this codebase, in the feature that had to learn it.
    private readonly HashSet<string> _seenActionNames = new(StringComparer.Ordinal);
    private int _actionsWalked;
    private float _nextScanAt;
    // Whether the last Update was refused, either by the player's switch or by a fault. Kept so
    // that going quiet is an event rather than a state polled every frame ; see Update.
    private bool _quiet;
    private int _consecutiveFaults;
    private bool _loggedFirstResult;
    private string _lastFault;
    private int _ambiguous;
    // Abilities the balancing lookup could not read on the last scan. NOT cached as "has no area",
    // and counted so that a board where this is persistently non-zero is visible rather than just
    // quiet. Reset per scan, because it describes the scan and not the session.
    private int _unreadable;

    public AoeShapes(Capabilities capabilities) => _capabilities = capabilities;

    /// <summary>What the last scan found, for the log at the end of a placement.</summary>
    /// <remarks>
    /// Printed per placement rather than once per session. LogFirstResult below fires on the FIRST
    /// scan of a session, which is the opening battle of a run: no specializations, no rank
    /// modifiers, often nothing with an area at all. Every log this mod has produced therefore
    /// reports the emptiest board of the session and nothing afterwards, which is how a feature can
    /// stop working for a whole session without a single line saying so.
    /// </remarks>
    public string Diagnostic =>
        $"{_byUnit.Count} unit(s) with a footprint, {_abilitiesWithoutShape.Count} ability(ies) " +
        $"known to place none, {_ambiguous} refused as ambiguous, {_unreadable} unreadable this scan" +
        (_quiet ? "; the player's switch or a fault has this feature off" : "");

    /// <summary>The footprint for a unit, or null when there is nothing honest to draw.</summary>
    public AoeOutline For(string entityId) =>
        _capabilities.AoeOutline && entityId != null && _byUnit.TryGetValue(entityId, out AoeOutline outline)
            ? outline : null;

    /// <summary>
    /// Bumped whenever the footprints actually change, so the renderer can tell a frame that needs
    /// redrawing from one that would draw the same picture again.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>Safe to call every frame ; recomputes a couple of times a second.</summary>
    /// <param name="wanted">
    /// The player's own switch, from the ability areas button. False stops the scan as well as the
    /// drawing : this walk is the entire cost of the feature, and a display option that keeps
    /// paying for a picture nobody is drawing is the wrong shape.
    /// </param>
    /// <remarks>
    /// Kept apart from <see cref="Capabilities.AoeOutline"/> on purpose, and both are honoured
    /// here. That one means the feature broke and said so in the log ; this one means the player
    /// asked for it to stop. Reading them as one switch would let a fault silently flip a button
    /// the player owns, and would make a deliberate choice look like a defect in the log.
    /// </remarks>
    public void Update(bool wanted)
    {
        if (!_capabilities.AoeOutline || !wanted)
        {
            // Once on the way down, not once per frame. Clear bumps the version, the renderer
            // redraws whenever the version moves, and a switch whose whole purpose is to save work
            // would then have cost a full redraw sixty times a second for as long as it was off.
            if (!_quiet)
            {
                _quiet = true;
                Clear();
                Version++;
            }
            return;
        }
        // Clear leaves the next scan due immediately, so switching the areas back on shows them on
        // the next frame rather than up to half a second later.
        _quiet = false;
        float now;
        try { now = Time.realtimeSinceStartup; }
        catch { now = 0f; }
        if (_nextScanAt > 0f && now < _nextScanAt) return;
        _nextScanAt = now + SecondsBetweenScans;
        Refresh();
        // Bumped on EVERY refresh rather than only when the shapes are seen to differ. Comparing
        // would be exact and would save the renderer a redraw, and it is still the wrong trade: a
        // cheap test that is merely usually right, guarding a picture, is how a stale overlay
        // ships, and a speed change that alters what the mod draws is a defect rather than a
        // bonus. Refreshing twice a second costs the renderer two redraws a second, which is about
        // a hundredth of a millisecond per frame at sixty. Correct is affordable here.
        Version++;
    }

    public void Clear()
    {
        _byUnit.Clear();
        // The per-ability shapes are kept. They are authored data, identical from one battle to the
        // next, and rebuilding them costs a walk of every action on every ability on the board.
        _nextScanAt = 0f;
    }

    private void Refresh()
    {
        try
        {
            if (!DataReaders.TryGet<GameRegistryDataReader>(out var registry) || registry == null) return;
            _byUnit.Clear();
            _unreadable = 0;
            foreach (HeroData hero in registry.Data.Heroes.Values)
            {
                if (hero == null || !hero.HasActiveAbility) continue;
                Record(hero.HeroId.ToEntityId().ToString(), hero.ActiveAbility);
            }
            // Enemies get the same treatment through the same code, because they carry the ability
            // reference on exactly the same shape of field. Where an enemy's area will land is if
            // anything the more useful half : it is the one the player can still move out of.
            foreach (EnemyData enemy in registry.Data.Enemies.Values)
            {
                if (enemy == null || !enemy.HasActiveAbility) continue;
                Record(enemy.EnemyId.ToEntityId().ToString(), enemy.ActiveAbility);
            }
            _consecutiveFaults = 0;
            LogFirstResult();
        }
        catch (Exception e)
        {
            if (_lastFault != e.Message)
            {
                _lastFault = e.Message;
                MelonLogger.Warning("[TargetingMod] area outline read failed (will retry). Full fault follows:\n" + e);
            }
            if (++_consecutiveFaults >= FaultsBeforeDisabling)
                _capabilities.DisableAoeOutline(
                    $"ability areas unreadable {_consecutiveFaults} scans in a row: {e.Message}");
        }
    }

    private void Record(string entityId, BalancingRef<IActiveAbilityEntry> abilityRef)
    {
        string key = abilityRef.Id.ToString();
        if (_abilitiesWithoutShape.Contains(key)) return;
        if (!_byAbility.TryGetValue(key, out AoeOutline outline))
        {
            outline = Build(abilityRef, out bool readable);
            if (outline == null)
            {
                // ONLY AN ABILITY THAT WAS ACTUALLY READ MAY BE REMEMBERED AS HAVING NO AREA.
                //
                // This set is never cleared, on purpose : whether an ability places an area is
                // authored data and does not change during a run. That is only true of an answer,
                // and until now a FAILURE to read one was filed here as though it were an answer.
                // The first scan runs about three quarters of a second into a placement, while the
                // scene is still assembling, which is exactly when the balancing lookup can hand
                // back nothing. One such moment silently retired that ability's footprint for the
                // rest of the session, with no log line and no way back short of a relaunch.
                //
                // The lesson is already written three times in this codebase, in ConfigMirror,
                // PositionalGlow and PartGlow : a reader caught mid setup is a passing condition,
                // and treating one as permanent has cost this mod a session before. This was the
                // one place it had not been applied.
                if (readable) _abilitiesWithoutShape.Add(key);
                else _unreadable++;
                return;
            }
            _byAbility[key] = outline;
        }
        _byUnit[entityId] = outline;
    }

    /// <param name="readable">
    /// Whether the ability could be read at all. False means try again next scan; true means the
    /// answer is "this ability places no area" and is safe to remember for the run.
    /// </param>
    private AoeOutline Build(BalancingRef<IActiveAbilityEntry> abilityRef, out bool readable)
    {
        readable = false;
        IActiveAbilityEntry entry = EmberBalancing.Instance.Get<IActiveAbilityEntry>(abilityRef);
        Il2CppReferenceArray<IAbilityAction> actions = entry?.AbilityActions;
        if (actions == null) return null;
        // Past this point the ability was read. Everything below is a real answer about it,
        // including the deliberate refusal in the ambiguous case, so all of it may be remembered.
        readable = true;

        AoeOutline chosen = null;
        for (int i = 0; i < actions.Length; i++)
        {
            IAbilityAction action = actions[i];
            if (action == null) continue;
            _actionsWalked++;
            if (_seenActionNames.Count < 60)
            {
                string actionName = RuntimeDiscovery.NativeClassName(action);
                if (actionName.Length != 0) _seenActionNames.Add(actionName);
            }
            // By type, never by name. The class this asks is the object's own native class, so an
            // action arriving as an interface answers for what it really is.
            List<AoeEntry> areas = NullableRaw.ReadFieldsOfType<AoeEntry>(action);
            for (int j = 0; j < areas.Count; j++)
            {
                AoeOutline candidate = FromEntry(areas[j]);
                if (candidate == null) continue;
                if (chosen == null) { chosen = candidate; continue; }
                if (SameShape(chosen, candidate)) continue;
                // The ability carries more than one shape and nothing here can say which one this
                // unit will place : the choice is made in the hero's own compiled logic, off a
                // specialization it may or may not have. Three abilities in the whole game are like
                // this. Naming those three would mean binding three hero classes by name, and
                // drawing the wrong footprint is worse than drawing none, so it draws none.
                _ambiguous++;
                return null;
            }
        }
        return chosen;
    }

    private static AoeOutline FromEntry(AoeEntry entry)
    {
        Il2CppReferenceArray<ICollisor> collisors = entry?.Collisors;
        if (collisors == null || collisors.Length == 0) return null;
        var loops = new List<Vector2[]>(collisors.Length);
        bool rotates = false;
        bool allCircles = true;
        for (int i = 0; i < collisors.Length; i++)
        {
            ICollisor collisor = collisors[i];
            if (collisor == null) continue;
            // Asked as a cast rather than by reading the shape's own type field. A wrapper handed
            // back from an array of interfaces reports the interface it was declared as, so the
            // ordinary test for what something is answers the wrong question here.
            CircleCollisor circle = collisor.TryCast<CircleCollisor>();
            if (circle != null)
            {
                loops.Add(CircleLoop(ToVector(circle.Center), ToFloat(circle.Radius)));
                continue;
            }
            RectCollisor rect = collisor.TryCast<RectCollisor>();
            if (rect == null) continue; // a kind of shape this build does not contain ; draw nothing for it.
            // The four corners are already where they belong relative to the origin. Unlike a
            // circle, the shape's own centre is carried separately and is not added to them.
            loops.Add(new[] { ToVector(rect.P1), ToVector(rect.P2), ToVector(rect.P3), ToVector(rect.P4) });
            rotates = true;
            allCircles = false;
        }
        return loops.Count == 0 ? null : new AoeOutline(loops.ToArray(), rotates, allCircles);
    }

    private static Vector2[] CircleLoop(Vector2 center, float radius)
    {
        var points = new Vector2[CircleSegments];
        for (int i = 0; i < CircleSegments; i++)
        {
            float angle = i * (Mathf.PI * 2f / CircleSegments);
            points[i] = new Vector2(center.x + Mathf.Cos(angle) * radius, center.y + Mathf.Sin(angle) * radius);
        }
        return points;
    }

    // Two shapes are the same shape if every point of them is. Compared rather than trusted,
    // because an ability carrying the same area twice under two names is common and an ability
    // carrying two genuinely different ones is the case that must not be drawn.
    private static bool SameShape(AoeOutline a, AoeOutline b)
    {
        if (a.Loops.Length != b.Loops.Length) return false;
        for (int i = 0; i < a.Loops.Length; i++)
        {
            if (a.Loops[i].Length != b.Loops[i].Length) return false;
            for (int j = 0; j < a.Loops[i].Length; j++)
                if ((a.Loops[i][j] - b.Loops[i][j]).sqrMagnitude > 0.0001f) return false;
        }
        return true;
    }

    private static float ToFloat(Il2CppPhoton.Deterministic.FP value) => value.RawValue / FixedPointScale;

    // The simulation's flat plane is X across and Y along, and the board's is X across and Z along.
    // Every collisor in the game is authored on the first and drawn on the second.
    private static Vector2 ToVector(Il2CppPhoton.Deterministic.FPVector2 value) =>
        new(ToFloat(value.X), ToFloat(value.Y));

    private void LogFirstResult()
    {
        if (_loggedFirstResult) return;
        _loggedFirstResult = true;
        MelonLogger.Msg($"[TargetingMod] ability areas: {_byUnit.Count} unit(s) with a footprint to draw, " +
                        $"{_abilitiesWithoutShape.Count} ability(ies) with none, {_ambiguous} left undrawn as ambiguous");
        if (_byUnit.Count != 0) return;
        // Nothing to draw. Say what the board actually contained, so one log line answers whether
        // the abilities are being read at all or simply do not place an area. Real class names here
        // mean the walk works and this board has none ; an empty list means it does not.
        var sample = new List<string>(_seenActionNames);
        sample.Sort(StringComparer.Ordinal);
        MelonLogger.Msg($"[TargetingMod] ability areas walked {_actionsWalked} action(s), " +
                        $"{sample.Count} distinct: {(sample.Count == 0 ? "NONE READ" : string.Join(", ", sample))}");
    }
}
