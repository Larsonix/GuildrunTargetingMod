using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GuildrunTargetingMod.Interop;
using MelonLoader;
using UnityEngine;

namespace GuildrunTargetingMod;

// Everything the mod reaches into the game for, checked once at startup and written to the log.
//
// A mod against a game it does not ship with has to assume the game will move underneath it. So
// rather than discovering a missing type deep inside a battle, every type and member is looked up
// at boot : a required one that has moved turns the prediction off before it can be wrong, and an
// optional one only costs the feature that uses it. The log then says exactly what was found,
// which turns "it stopped working after the update" into one readable line.
//
// The build the mod was last proven against is remembered, so a new build re-enters safe mode on
// its own and stays there until one battle proves the prediction against the real fight.
internal sealed class Bindings
{
    internal const string ModVersion = "2.2.0";

    // How many battles in a row have to disagree before the preview is taken away.
    //
    // Two, not one. A single battle can disagree without the mod being wrong : a run resumed in
    // the middle of a fight, a board read while the scene was still assembling, a sampling window
    // that closed before the board settled. The cost of acting on one is the whole feature, and
    // the cost of waiting for a second is one battle of a preview that might be slightly off.
    private const int MismatchesBeforeDisabling = 2;

    // Bumped whenever the gate's own rules change, and stored alongside the mod version in the
    // recorded failure. A verdict is only as meaningful as the rules that produced it, so a
    // recorded failure has to name both : "the mod disagreed" under rules that have since been
    // corrected is not evidence about anything.
    //
    // Revision 2 is this round. Revision 1 compared every unit's opening world position for exact
    // equality against a number the game does not compute the way the mod did, and counted missing
    // evidence as contradiction. It failed a hundred percent of the battles it ever judged. Without
    // this, a machine carrying a revision 1 failure would start the corrected mod with the preview
    // switched off, on the strength of a verdict the correction exists to retract.
    private const int ParityRulesRevision = 2;

    private static string FailureToken => ModVersion + "#r" + ParityRulesRevision;

    private readonly Capabilities _capabilities;
    private readonly MelonPreferences_Entry<string> _testedBuildGuid;
    private readonly MelonPreferences_Entry<string> _parityFailureVersion;
    private readonly MelonPreferences_Entry<int> _mismatchStreak;
    private readonly List<string> _rows = new();

    public string BuildGuid { get; private set; }
    public bool PredictionBindingsValid { get; private set; } = true;
    public bool HasPersistedFailure { get; private set; }
    // Read into the per-battle parity row. A bug report is answered from that file alone, so it
    // has to say how close the mod was to switching itself off, not just what this battle proved.
    public int MismatchStreak => _mismatchStreak?.Value ?? 0;

    public Bindings(
        Capabilities capabilities,
        MelonPreferences_Entry<string> testedBuildGuid,
        MelonPreferences_Entry<string> parityFailureVersion,
        MelonPreferences_Entry<int> mismatchStreak)
    {
        _capabilities = capabilities;
        _testedBuildGuid = testedBuildGuid;
        _parityFailureVersion = parityFailureVersion;
        _mismatchStreak = mismatchStreak;
    }

    public void RunBootSelfCheck()
    {
        BuildGuid = ReadBuildGuid(out string guidSource);
        CheckCoreInventory();
        CheckManifestInventory();

        try
        {
            NullableRaw.Initialize();
            _rows.Add("OK   NullableRaw native layouts: " + NullableRaw.LayoutDiagnostic);
        }
        catch (Exception e)
        {
            FailCore("NullableRaw layout resolution failed: " + e.Message);
        }

        bool versionChanged = !string.IsNullOrEmpty(_parityFailureVersion.Value) &&
                              !string.Equals(_parityFailureVersion.Value, FailureToken, StringComparison.Ordinal);
        if (versionChanged)
        {
            _parityFailureVersion.Value = string.Empty;
            _mismatchStreak.Value = 0;
            MelonPreferences.Save();
        }

        bool knownBuild = string.Equals(_testedBuildGuid.Value, BuildGuid, StringComparison.OrdinalIgnoreCase);
        // A recorded disagreement belongs to one mod version AND one game build. A game update
        // makes it evidence about a game that is no longer installed, so it is thrown away rather
        // than carried across, and the player meets the new build with the preview working.
        //
        // This is the whole shape of the change, and it replaces the opposite rule. An unrecognised
        // build used to switch the preview off at boot and keep it off until a battle proved it, so
        // every player lost the feature after every patch and read "Self-check in progress" while
        // nothing was wrong. What actually establishes whether the mod can work is the inventory
        // above : sixty members looked up before a board is ever drawn. If those resolved, the mod
        // runs, and the battle afterwards is what would take it away.
        bool persistedFailure = knownBuild &&
            string.Equals(_parityFailureVersion.Value, FailureToken, StringComparison.Ordinal);
        if (!knownBuild && (_mismatchStreak.Value != 0 || _parityFailureVersion.Value.Length != 0))
        {
            _mismatchStreak.Value = 0;
            _parityFailureVersion.Value = string.Empty;
            MelonPreferences.Save();
        }
        HasPersistedFailure = persistedFailure;
        if (PredictionBindingsValid && persistedFailure)
        {
            _capabilities.DisablePrediction(
                "the prediction disagreed with two fights running on this game build; " +
                "one battle that agrees turns it back on");
        }

        MelonLogger.Msg("[TargetingMod] ================= BOOT SELF-CHECK =================");
        MelonLogger.Msg($"[TargetingMod] mod={ModVersion} game={Application.version} unity={Application.unityVersion}");
        MelonLogger.Msg($"[TargetingMod] buildGuid={BuildGuid} source={guidSource} tested={_testedBuildGuid.Value}");
        MelonLogger.Msg($"[TargetingMod] parity state: knownBuild={knownBuild} persistedFailure={HasPersistedFailure} mismatchStreak={_mismatchStreak.Value}/{MismatchesBeforeDisabling}");
        foreach (string row in _rows) MelonLogger.Msg("[TargetingMod] " + row);
        MelonLogger.Msg($"[TargetingMod] capabilities: CoreRead={_capabilities.CoreRead}, Prediction={_capabilities.Prediction}, verificationCandidate={PredictionBindingsValid}");
        MelonLogger.Msg("[TargetingMod] =====================================================");
    }

    /// <summary>
    /// A battle where nothing disagreed. Clears any pending strike and puts a switched-off preview
    /// back on.
    /// </summary>
    /// <remarks>
    /// A battle that could not observe the settled board counts too, and that is deliberate. It is
    /// missing evidence, not contradicting evidence, and requiring a fully comparable battle is how
    /// the preview used to be withheld forever from players whose fights all had somebody die
    /// early : two playtesters were told the preview would turn on after this battle, fight after
    /// fight, and it never did. What such a battle does prove is not small : the seed matched the
    /// real one exactly, every unit opened where the mod said, and the opening picks matched.
    /// </remarks>
    public void RecordCleanBattle(bool fullyComparable)
    {
        bool changed = _mismatchStreak.Value != 0 ||
                       _parityFailureVersion.Value.Length != 0 ||
                       !string.Equals(_testedBuildGuid.Value, BuildGuid, StringComparison.OrdinalIgnoreCase);
        _testedBuildGuid.Value = BuildGuid;
        _parityFailureVersion.Value = string.Empty;
        _mismatchStreak.Value = 0;
        if (changed) MelonPreferences.Save();
        bool wasOff = HasPersistedFailure;
        HasPersistedFailure = false;
        _capabilities.EnablePredictionAfterParity();
        if (wasOff)
            MelonLogger.Msg("[TargetingMod] the prediction agreed with this fight" +
                (fullyComparable ? "" : " as far as it could be checked") + "; preview back on");
    }

    /// <summary>
    /// A battle that disagreed on something the mod draws. Returns true when this was the second in
    /// a row and the preview has to come off.
    /// </summary>
    public bool RecordParityFailure()
    {
        int streak = _mismatchStreak.Value + 1;
        _mismatchStreak.Value = streak;
        if (streak < MismatchesBeforeDisabling)
        {
            MelonPreferences.Save();
            return false;
        }
        _parityFailureVersion.Value = FailureToken;
        _testedBuildGuid.Value = BuildGuid; // scopes the failure to this game build, and only this one.
        MelonPreferences.Save();
        HasPersistedFailure = true;
        return true;
    }

    private void CheckCoreInventory()
    {
        Check("DataReaders", "gg.leyline.core.Mvcs.Model.DataReaders", true, "Get", "Has", "TryGet", "ReaderDictionary");
        Check("BattleSimulationDataReader", "Ember.Scopes.Battle.BattleSimulation.Data.BattleSimulationDataReader", false, "CurrentFrame", "SimulationState", "BattleEnded");
        Check("ReadOnlyFrame", "Ember.Simulation.Core.State.ReadOnlyFrame", true, "Entities", "TryGetEntity", "TimeSinceStart");
        Check("Entity", "Ember.Simulation.Core.State.Entities.Entity", true, "Id", "Character", "Transform", "CellPosition", "IsDisplacing", "IsAlive", "GetStatusStackCount", "Fsm", "Stats");
        Check("TransformComponent", "Ember.Simulation.Core.State.Components.TransformComponent", true, "Position");
        Check("CharacterComponent", "Ember.Simulation.Core.State.Components.CharacterComponent", true, "TargetId", "PreparedAttack");
        Check("PreparedAttack", "Ember.Simulation.Core.State.Components.PreparedAttack", true, "TargetId");
        Check("Frame", "Ember.Simulation.Core.State.Frame", true, "Entities", "TryGetEntity", "PathfindingDataCache", "BattleContext");
        Check("PathfindingData", "Ember.Simulation.Core.State.Components.PathfindingData", true, "HasRemainingPath");
        Check("EntityState", "Ember.Simulation.Core.Fsm.EntityState", true, "MoveToTarget", "Displaced", "Dead");
        Check("StatusType", "Ember.Balancing.Sheets.Characters.Attacks.StatusType", true, "DamageImmunity", "Stun");
        Check("SimulationBattleContext", "Ember.Simulation.Core.Bridge.SimulationBattleContext", true, "ApplyAdditionalStatusStack");
        Check("BattleSimulation", "Ember.Simulation.Core.BattleSimulation", false, "Initialize", "Tick", "FinishFrameExecution", "CurrentFrame");
        Check("SimulationReferences", "Ember.Simulation.Core.Config.SimulationReferences", false, "Balancing", "ErrorReporting", "GuidFactory");
        Check("BattleConfig", "Ember.Simulation.Core.Config.BattleConfig", false, "Seed", "BoardWidth", "BoardHeight", "CellSize", "BattleDuration", "HeroDtos", "ReserveHeroDtos", "EnemyDtos", "EffectDtos", "ActiveRelics", "GlobalPermanentCustomData", "CurrentPlayerShards", "CurrentChunkIndex", "IsBossFloor", "FightMode", "FightModeThresholds");
        Check("CharacterDto", "Ember.Simulation.Core.Dto.CharacterDto", true, "EntityId");
        Check("RunSessionDataReader", "Ember.Scopes.GameRun.RunSession.Data.RunSessionDataReader", true, "BattleFlowState", "RunSeed", "CurrentFloorIndex", "CurrentChunkIndex", "IsBossFloor", "CurrentFightMode", "IsEndlessMode", "CurrentEndlessFloor", "CurrentSeed", "GetCurrentEncounter");
        Check("BattleFlowState", "Ember.Scopes.GameRun.RunSession.Data.BattleFlowState", true, "Placement", "Resolution");
        Check("BoardDataReader", "Ember.Scopes.Battle.Board.Data.BoardDataReader", true, "BoardWidth", "BoardHeight", "Data");
        // The two tests the game itself uses to decide whether a hero can be dropped on a hex.
        // Optional : losing them costs the live drag preview and nothing else.
        Check("BoardDataReader (drag preview)", "Ember.Scopes.Battle.Board.Data.BoardDataReader", false, "IsInPlayableRange", "IsPositionFree");
        Check("TileInfo", "Ember.Scopes.Battle.Board.Data.TileInfo", true, "HeroId", "EnemyId");
        Check("GameRegistryDataReader", "Ember.Scopes.GameRun.GameRegistry.Data.GameRegistryDataReader", true, "GetEnemyData", "Data", "GlobalPermanentCustomData", "IsHeroInReserve");
        Check("HeroData", "Ember.Scopes.GameRun.GameRegistry.Data.Characters.HeroData", false, "HeroId", "CellPosition", "Stats", "ToDto");
        Check("EnemyData", "Ember.Scopes.GameRun.GameRegistry.Data.Characters.EnemyData", false, "EnemyId", "CellPosition", "Stats", "ToDto");
        Check("EffectsReader", "Ember.Scopes.GameRun.Effects.Data.EffectsReader", true, "GetActiveEffects", "IsQuestEffectCompleted", "Data");
        Check("PlayerReader", "Ember.Scopes.GameRun.Player.Data.PlayerReader", true, "CurrentShards", "Data");
        Check("EmberBalancing", "Ember.Balancing.EmberBalancing", false, "Instance");
        Check("GuidFactory", "gg.leyline.netcode.Utilities.GuidFactory", false, "NewGuid", "ResetSessionSeed");
        Check("IGuidFactory", "gg.leyline.netcode.Utilities.IGuidFactory", false, "NewGuid", "ResetSessionSeed");
        Check("IErrorReporting", "Ember.Balancing.SimulationBridge.Context.IErrorReporting", false, "SetEffectTags", "ClearEffectTags", "CaptureException", "CaptureMessage", "AddBreadcrumb");
    }

    private void CheckManifestInventory()
    {
        // Nothing here can stop the prediction ; these are the visual and UI surfaces. They are
        // still checked at boot so that after a game update the log names what moved, instead of
        // leaving a feature to fail quietly somewhere in the middle of a battle.
        Check("SimulationRunner", "Ember.Simulation.UnityRuntime.SimulationRunner", false, "Start", "Update", "FrameWrapper");
        Check("BattleSimulationService", "Ember.Scopes.Battle.BattleSimulation.Services.BattleSimulationService", false, "CreateBattleConfig", "StartSimulation");
        Check("RunSessionService", "Ember.Scopes.GameRun.RunSession.Services.RunSessionService", false, "CalculateCurrentSeed");
        // Where the game says each hex is. Optional on purpose : without it the mod falls back to
        // the hex formula, which predicts correctly and differs only in the last few fixed-point
        // digits. Checked here so that if it ever moves, the boot log names it instead of leaving
        // a silent difference in every opening position. See CellWorldTable for why it matters.
        Check("BattleScope (cell table)", "Ember.Scopes.Battle.BattleScope", false, "_debugConfig");
        Check("SimulationDebugConfig (cell table)", "Ember.Scopes.Battle.BattleSimulation.Data.SimulationDebugConfig",
            false, "GetCellToWorldLookup", "_cellToWorldPositions");
        Check("BattleInputActionHandler", "Ember.Scopes.Battle.InputHandling.BattleInputActionHandler", false, "PerformLiveHit");
        Check("UiHitResult", "Ember.Scopes.GameRun.InputHandling.UiHitResult", false, "View", "HoveredCharacter", "BoardPosition", "GroundPosition", "PointerPosition", "HoverType");
        Check("InputService", "gg.leyline.input.InputService", false, "TryRaycastUI", "TryRaycastComponent", "GetPointerPosition", "GetGroundPosition");
        Check("EntityUpdater", "Ember.Simulation.UnityRuntime.Entities.EntityUpdater", false, "TryGetEntityView");
        Check("EntityViewController", "Ember.Simulation.UnityRuntime.Entities.View.EntityViewController", false, "View", "EntityId");
        Check("EntityView", "Ember.Simulation.UnityRuntime.Entities.View.EntityView", false, "TryGetAttachmentSlot");
        Check("BattleUIController", "Ember.Scopes.Battle.UI.BattleUIController", false, "UpdateHealthBars", "UpdateHealthBarPosition", "SortHealthBars");
        Check("AttachmentSlotType", "gg.leyline.utilities.VFX.AttachmentSlotType", false, "Overhead");
        Check("TooltipRaycastTarget", "Ember.Scopes.Application.UI.Tooltips.TooltipRaycastTarget", false, "SetSource");
        Check("SimpleTooltipSource", "Ember.Scopes.Application.UI.Tooltips.Sources.SimpleTooltipSource", false, "SetData");
        Check("AppTooltipController", "Ember.Scopes.Application.UI.Tooltips.AppTooltipController", false, "ShowTooltip");
        Check("GameRunTooltipController", "Ember.Scopes.GameRun.Utilities.Tooltips.GameRunTooltipController", false, "Context", "HandleNotifications", "PopulateFromSource");
        Check("SpeedToggleView", "Ember.Scopes.GameRun.UI.BattleSpeed.SpeedToggleView", false, "Toggle", "SetActiveIndicator", "SetColor", "PlaySwitchVfx", "_backgroundImage", "_onIndicator");
        Check("BattleSpeedController", "Ember.Scopes.GameRun.UI.BattleSpeed.BattleSpeedController", false, "_speedViews", "_autoView");
        Check("IUiView", "Ember.Scopes.GameRun.UI.IUiView", false, "ElementGuid");
        Check("EmberAudioButtonHandler", "Ember.Scopes.Application.Audio.UI.EmberAudioButtonHandler", false, "Awake", "OnDestroy");
        Check("HealthBarView", "Ember.Scopes.Battle.UI.Hud.HealthBarView", false, "_playerPrimaryColor", "_enemyPrimaryColor");
        Check("IVisualFeedbackSingleton", "Ember.Balancing.VisualFeedback.IVisualFeedbackSingleton", false, "StatusIcons", "StatColors", "StatusIconTexts", "StatIconTexts");
        Check("PlacementFeedbackController", "Ember.Scopes.Battle.PlacementFeedback.PlacementFeedbackController", false, "ClearVFXs", "SetupVFXs");
        Check("BoardController", "Ember.Scopes.Battle.Board.Controllers.BoardController", false, "CharacterViewControllers", "PlacementGrid", "_gameRenderCamera", "_openTile", "_enemySideTile", "_invalidTile", "_allyTile");
        Check("BattleFlowUIStateController", "Ember.Scopes.Battle.UI.BattleFlow.BattleFlowUIStateController", false, "_placementParent");
        // The Rift Seal warning. Optional like everything else here : if any of it moved, that one
        // warning goes quiet and the rest of the mod is untouched.
        Check("ChallengeReader", "Ember.Scopes.GameRun.Challenge.Data.ChallengeReader", false, "IsChallengeRun", "GetChallengeState", "GetChallenge");
        Check("ChallengeType", "Ember.Scopes.GameRun.Challenge.Data.ChallengeType", false, "ChargeTheAnchor");
        Check("ChallengeState", "Ember.Scopes.GameRun.Challenge.Data.ChallengeState", false, "Pending");
        Check("IDifficultiesSingleton", "Ember.Balancing.Difficulty.IDifficultiesSingleton", false, "ChallengeWinsPerClass", "AnchorItemRef");
        Check("GameRegistryDataReader (anchor)", "Ember.Scopes.GameRun.GameRegistry.Data.GameRegistryDataReader", false, "TryGetGlobalPermanentCustomData", "IsHeroInReserve");
        // `_allClasses` and not just `Classes` : the property is what the charging code reads, but
        // the mod reads the concrete list behind it, because walking an interface-typed generic
        // through interop is a known way to bring the process down. Checking only the property left
        // the field the mod actually depends on unchecked, which is the boot log claiming to have
        // verified something it had not.
        Check("HeroData (anchor)", "Ember.Scopes.GameRun.GameRegistry.Data.Characters.HeroData", false,
            "Classes", "_allClasses", "IsItemEquipped");
        Check("ItemData (anchor)", "Ember.Scopes.GameRun.GameRegistry.Data.Items.ItemData", false, "ItemRef", "OwnerEntityId");
        // BoardController._anchorWarnText was checked here to borrow its font for a sentence the
        // mod no longer shows. Nothing reads it now, so nothing checks it : a binding kept for a
        // feature that is gone is a boot line that cannot fail usefully.
        // The positional glow, both halves. The data half reads the game's own out-of-battle
        // effect evaluation ; these are the icons it marks. All optional : if the game's UI moves,
        // the boot log names what moved and only the marks go quiet.
        Check("EffectsReader (glow)", "Ember.Scopes.GameRun.Effects.Data.EffectsReader", false, "TryGetOwnerItemId", "TryGetOwnerRelicId");
        Check("PlaceholderSlotView (glow)", "Ember.Scopes.GameRun.UI.Slots.PlaceholderSlotView", false, "ItemId", "OwnerHeroId", "_frameImage");
        Check("RelicUIController (glow)", "Ember.Scopes.GameRun.UI.Relics.RelicUIController", false, "_relicViews");
        Check("RelicView (glow)", "Ember.Scopes.GameRun.UI.Relics.RelicView", false, "_iconImage", "_frameImage");
        // The ability area outline. Nothing here is a hero-specific class on purpose : the shape is
        // found by asking which field on an action holds one of these, never by its name.
        Check("IActiveAbilityEntry (area)", "Ember.Balancing.Sheets.Abilities.ActiveAbilities.IActiveAbilityEntry", false, "AbilityActions");
        Check("AoeEntry (area)", "Ember.Balancing.Aoe.AoeEntry", false, "Collisors", "AoePrefab");
        Check("CircleCollisor (area)", "Ember.Balancing.SimulationBridge.Collisors.CircleCollisor", false, "Radius", "Center", "CollisorType");
        Check("RectCollisor (area)", "Ember.Balancing.SimulationBridge.Collisors.RectCollisor", false, "P1", "P2", "P3", "P4");
        Check("HeroData (area)", "Ember.Scopes.GameRun.GameRegistry.Data.Characters.HeroData", false, "ActiveAbility", "HasActiveAbility");
        Check("EnemyData (area)", "Ember.Scopes.GameRun.GameRegistry.Data.Characters.EnemyData", false, "ActiveAbility", "HasActiveAbility");
        // The main menu switch and the leaderboard guard's own reads. All optional, and that is
        // not a lowered standard : the gate itself does not depend on any of them. The two patched
        // methods are named directly in LeaderboardGuard, so if either of those moved the mod has
        // already switched itself off before this check ever runs. Everything below only decides
        // whether the button appears and how well the guard can explain itself, and each one that
        // moved is named here instead of failing somewhere the player would never see it.
        Check("SteamPlatformService", "Ember.Scopes.Application.GamePlatform.SteamPlatformService", false,
            "SubmitScore", "SubmitChallengeScore");
        Check("MainMenuUIController", "Ember.Scopes.MainMenu.UI.MainMenuUIController", false,
            "_settingsButton", "_communityButton", "_quitButton", "_startButton", "_persistenceReader");
        Check("DialogPanel", "Ember.System.UI.DialogPanel", false,
            "ShowConfirmationDialog", "ShowSimpleDialog", "ValidateState");
        Check("PersistenceReader", "Ember.Scopes.Application.Persistence.Data.PersistenceReader", false,
            "HasGameRunMetadata", "GameRunMetadata");
        // Checked apart from the required RunSessionDataReader row above, on purpose. Losing the
        // run's id must not take the preview down with it : the guard treats a run it cannot
        // identify as one to withhold from, which is the safe answer, and the preview is untouched.
        Check("RunSessionDataReader (leaderboard guard)", "Ember.Scopes.GameRun.RunSession.Data.RunSessionDataReader",
            false, "RunId");
        Check("ProgressionReader (leaderboard guard)", "Ember.Scopes.Application.Progression.Data.ProgressionReader",
            false, "CurrentChallengeWinstreak");
    }

    private void Check(string label, string gameType, bool required, params string[] members)
    {
        Type type = FindProxyType(gameType);
        if (type == null)
        {
            _rows.Add($"FAIL {label}: type {gameType} missing");
            if (required) FailCore(label + " type missing");
            return;
        }

        var missing = new List<string>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (string member in members)
        {
            bool found = type.GetMember(member, flags).Length > 0 ||
                         type.GetMember("_" + member + "_k__BackingField", flags).Length > 0;
            if (!found) missing.Add(member);
        }
        if (missing.Count == 0) _rows.Add($"OK   {label}: {members.Length} member(s)");
        else
        {
            _rows.Add($"FAIL {label}: missing {string.Join(", ", missing)}");
            if (required) FailCore(label + " shape mismatch");
        }
    }

    private void FailCore(string reason)
    {
        PredictionBindingsValid = false;
        _capabilities.DisableCoreRead(reason);
        _capabilities.DisablePrediction(reason);
    }

    // Game types reach managed code under a generated name : the original full name with an
    // Il2Cpp prefix. Which assembly holds it is not fixed, so every loaded assembly is asked.
    private static Type FindProxyType(string gameType)
    {
        string proxyName = "Il2Cpp" + gameType;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType(proxyName, false);
            if (type != null) return type;
        }
        return null;
    }

    // Identifies the installed game build. Unity normally reports it, but it comes back empty or
    // all zeroes on some builds, and the boot config on disk carries the same value, so that is
    // the fallback. Which one answered is logged, since this is what safe mode keys off.
    private static string ReadBuildGuid(out string source)
    {
        source = "Application.buildGUID";
        string guid = null;
        try { guid = Application.buildGUID; } catch { }
        if (!string.IsNullOrWhiteSpace(guid) && guid.Trim('0').Length > 0) return guid;

        source = "Guildrun_Data/boot.config";
        try
        {
            foreach (string line in File.ReadAllLines(Path.Combine(Application.dataPath, "boot.config")))
                if (line.StartsWith("build-guid=", StringComparison.Ordinal))
                    return line.Substring("build-guid=".Length).Trim();
        }
        catch (Exception e) { source += " failed: " + e.Message; }
        return "unknown";
    }
}
