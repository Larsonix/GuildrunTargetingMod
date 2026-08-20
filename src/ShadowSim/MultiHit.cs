using System;
using System.Collections.Generic;
using GuildrunTargetingMod.Ui;
using Il2CppEmber.Balancing;
using Il2CppEmber.Balancing.Sheets.Abilities.ActiveAbilities;
using Il2CppEmber.Balancing.Sheets.Abilities.PassiveAbilities;
using Il2CppEmber.Balancing.SimulationBridge.AbilityActions;
using Il2CppEmber.Balancing.SimulationBridge.Effects;
using Il2CppEmber.Balancing.SimulationBridge.Effects.Conditions;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data.Characters;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2Cppgg.leyline.balancing.Data;
using Il2Cppgg.leyline.core.Mvcs.Model;
using MelonLoader;

namespace GuildrunTargetingMod.ShadowSim;

/// <summary>How many things a unit's attack lands on, and where.</summary>
/// <remarks>
/// Almost every attack in this game hits exactly one unit, which is why the mod drew exactly one
/// arrow for four versions and nobody minded. A handful do not, and every one of them is decided by
/// where units are standing, which makes them the most placement-relevant facts on the board and
/// the ones a placement tool has least excuse for hiding.
///
/// Two separate mechanisms, and they are not variants of each other :
///
///  - **Reaching several units at once.** Three ability actions loop an attack over units chosen by
///    hex distance. The rules are read from the simulation's own code, restated below beside each,
///    and evaluated here with the game's own <c>HexGridUtils.Distance</c> so the geometry cannot
///    drift from the geometry the fight uses.
///  - **Splashing around whatever is being hit.** Authored as a CONDITION on a passive
///    (<c>IsAdjacentToTriggerTargetCondition</c>), not on the active ability, which is exactly why
///    it was invisible : nothing in this mod had ever read a passive.
///
/// No capability switch of its own, deliberately. Losing this degrades to the single arrow every
/// unit had before it existed, which is still the correct picture for almost all of them, so there
/// is nothing for a switch to protect and nothing a player would need told.
/// </remarks>
internal sealed class MultiHit
{
    /// <summary>How a unit picks the extra units it hits, beyond the one it is paired with.</summary>
    internal enum Reach
    {
        /// <summary>One target, like almost everything in the game.</summary>
        Single,
        /// <summary>Every enemy within the unit's own live Attack Range, measured from the unit.</summary>
        WithinOwnRange,
        /// <summary>Every enemy exactly one hex away.</summary>
        Neighbours
    }

    internal readonly struct Rule
    {
        public readonly Reach Reach;
        /// <summary>Ordinary attacks also strike everything standing next to the unit being hit.</summary>
        public readonly bool SplashesAroundTarget;

        public Rule(Reach reach, bool splashes)
        {
            Reach = reach;
            SplashesAroundTarget = splashes;
        }

        public bool IsPlain => Reach == Reach.Single && !SplashesAroundTarget;
    }

    // The three ability actions in this build that hit more than the unit they are aimed at, named
    // by the class that implements the behaviour rather than by the hero who happens to own it. A
    // hero can be renamed, respecialised or handed a different ability ; the action either runs or
    // it does not.
    //
    // Each rule is the simulation's own loop, restated. Read them beside the source before editing:
    //   FunkeAbilityAction : every enemy with Distance(enemy, SOURCE) <= source Attack Range, on
    //                        top of the primary target. Measured from the CASTER, not the target,
    //                        and against the LIVE stat, so a range rank modifier is already in it.
    //   TillyAction        : the same shape, its own range value.
    //   MingAbilityAction  : every living enemy with Distance == 1 exactly.
    // Anything not in this table keeps the single arrow the mod has always drawn, which is the
    // correct answer for every other unit in the game.
    private static readonly Dictionary<string, Reach> ReachByAction = new(StringComparer.Ordinal)
    {
        { "FunkeAbilityAction", Reach.WithinOwnRange },
        { "TillyAction", Reach.WithinOwnRange },
        { "MingAbilityAction", Reach.Neighbours }
    };

    // The condition that means "and everything standing next to it". Five effects in this build use
    // it; three are the Fire, Frost and Poison Dragons' auto attacks, which is the whole reason a
    // player fighting a Final Boss could not see why a second hero was dying.
    private const string AdjacentCondition = "IsAdjacentToTriggerTargetCondition";

    private readonly Dictionary<string, Rule> _byUnit = new(StringComparer.Ordinal);
    // Keyed by ability id, because the answer is authored data and identical for every unit sharing
    // an ability. Three dragons ask the same question three times otherwise.
    private readonly Dictionary<string, Reach> _reachByAbility = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _splashByAbility = new(StringComparer.Ordinal);
    private bool _logged;
    private string _lastFault;

    /// <summary>The rule for a unit, or a plain single-target one when it has nothing special.</summary>
    public Rule For(string entityId) =>
        entityId != null && _byUnit.TryGetValue(entityId, out Rule rule) ? rule : default;

    public void Clear() => _byUnit.Clear();

    /// <summary>
    /// Rebuilds the per-unit rules from the run's own registry. Cheap after the first placement of a
    /// session : the per-ability answers are authored data and are kept.
    /// </summary>
    public void Refresh()
    {
        try
        {
            if (!DataReaders.TryGet<GameRegistryDataReader>(out var registry) || registry == null) return;
            _byUnit.Clear();
            foreach (HeroData hero in registry.Data.Heroes.Values)
                if (hero != null)
                    Record(hero.HeroId.ToEntityId().ToString(), hero.HasActiveAbility ? hero.ActiveAbility : default,
                        hero.HasActiveAbility, hero.PassiveAbilities);
            foreach (EnemyData enemy in registry.Data.Enemies.Values)
                if (enemy != null)
                    Record(enemy.EnemyId.ToEntityId().ToString(), enemy.HasActiveAbility ? enemy.ActiveAbility : default,
                        enemy.HasActiveAbility, enemy.PassiveAbilities);
            LogOnce();
        }
        catch (Exception e)
        {
            // Losing this must cost the extra arrows and nothing else. The single-target picture is
            // what every unit falls back to, and it is still correct for almost all of them.
            if (_lastFault != e.Message)
            {
                _lastFault = e.Message;
                MelonLogger.Warning("[TargetingMod] multi-hit rules unreadable (single arrows only): " + e.Message);
            }
            _byUnit.Clear();
        }
    }

    private void Record(string entityId, BalancingRef<IActiveAbilityEntry> abilityRef, bool hasActive,
        Il2CppSystem.Collections.Generic.IReadOnlyList<BalancingRef<IPassiveAbilityEntry>> passives)
    {
        Reach reach = hasActive ? ReachOf(abilityRef) : Reach.Single;
        bool splash = SplashOf(passives);
        var rule = new Rule(reach, splash);
        // Only units that actually differ are stored, so the lookup answers "plain" by absence and
        // the common case costs one failed dictionary probe.
        if (!rule.IsPlain) _byUnit[entityId] = rule;
    }

    private Reach ReachOf(BalancingRef<IActiveAbilityEntry> abilityRef)
    {
        string key = abilityRef.Id.ToString();
        if (_reachByAbility.TryGetValue(key, out Reach cached)) return cached;
        Reach found = Reach.Single;
        IActiveAbilityEntry entry = EmberBalancing.Instance.Get<IActiveAbilityEntry>(abilityRef);
        Il2CppReferenceArray<IAbilityAction> actions = entry?.AbilityActions;
        if (actions != null)
            for (int i = 0; i < actions.Length && found == Reach.Single; i++)
            {
                if (actions[i] == null) continue;
                // The object's own native class, so an action arriving as an interface still answers
                // for what it really is. Same rule the area outlines are read by.
                if (ReachByAction.TryGetValue(RuntimeDiscovery.NativeClassName(actions[i]), out Reach r)) found = r;
            }
        _reachByAbility[key] = found;
        return found;
    }

    private bool SplashOf(Il2CppSystem.Collections.Generic.IReadOnlyList<BalancingRef<IPassiveAbilityEntry>> passives)
    {
        if (passives == null) return false;
        // Cast to the concrete list before walking it, because the interop wrapper for the game's
        // read-only list interface surfaces ONLY an indexer : no Count and no enumerator, so neither
        // a for loop nor a foreach compiles against it. The object behind it really is the list, and
        // a build where it is not simply yields no splash rather than throwing.
        var list = passives.TryCast<Il2CppSystem.Collections.Generic.List<BalancingRef<IPassiveAbilityEntry>>>();
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
        {
            BalancingRef<IPassiveAbilityEntry> passive = list[i];
            string key = passive.Id.ToString();
            if (_splashByAbility.TryGetValue(key, out bool cached))
            {
                if (cached) return true;
                continue;
            }
            bool found = PassiveSplashes(passive);
            _splashByAbility[key] = found;
            if (found) return true;
        }
        return false;
    }

    private static bool PassiveSplashes(BalancingRef<IPassiveAbilityEntry> passiveRef)
    {
        IPassiveAbilityEntry entry = EmberBalancing.Instance.Get<IPassiveAbilityEntry>(passiveRef);
        // The effects live on the concrete entry, not on the interface the registry hands back.
        PassiveAbilityEntry concrete = entry?.TryCast<PassiveAbilityEntry>();
        Il2CppReferenceArray<IEffect> effects = concrete?.GetAllEffects();
        if (effects == null) return false;
        for (int i = 0; i < effects.Length; i++)
        {
            ModularEffect modular = effects[i]?.TryCast<ModularEffect>();
            IEffectCondition condition = modular?.Condition;
            if (condition == null) continue;
            if (string.Equals(RuntimeDiscovery.NativeClassName(condition), AdjacentCondition, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private void LogOnce()
    {
        if (_logged) return;
        _logged = true;
        int reaching = 0, splashing = 0;
        foreach (Rule r in _byUnit.Values)
        {
            if (r.Reach != Reach.Single) reaching++;
            if (r.SplashesAroundTarget) splashing++;
        }
        // Counts rather than a bare "ready", so a board where this silently found nothing is
        // distinguishable in the log from one that had nothing to find.
        MelonLogger.Msg($"[TargetingMod] multi-hit rules: {reaching} unit(s) hit several at once, " +
                        $"{splashing} splash around their target, of {_byUnit.Count} that differ from single-target");
    }
}
