using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppEmber.Scopes.GameRun.Challenge.Data;
using Il2CppEmber.Scopes.GameRun.RunSession.Data;
using Il2Cppgg.leyline.core.Mvcs.Model;
using MelonLoader;

namespace GuildrunTargetingMod;

/// <summary>
/// What the main menu is allowed to ask of the mod, and nothing else.
/// </summary>
/// <remarks>
/// The menu builds a button and shows the game's dialog; it must not also know how a score is
/// judged. Keeping that behind an interface is what stops the two from growing into each other,
/// and it means the whole leaderboard policy stays readable in one file.
/// </remarks>
internal interface IModSwitch
{
    bool Available { get; }
    string UnavailableReason { get; }
    bool Enabled { get; }
    void SetEnabled(bool value);
    bool SavedRunIsModded { get; }
    bool ChallengeStreakIsModded { get; }
    bool NoticeShown { get; }
    void MarkNoticeShown();
    void NoteMainMenuState(bool hasSavedRun);
}

// The one place that decides whether a score reaches a leaderboard.
//
// A preview changes what the player knows before committing to a board, so a score from a run
// played with it on is not comparable with one from an unmodified run. Both of the game's
// submission paths are therefore closed before any part of the preview is initialized, and they
// stay closed for as long as this mod cannot prove the run in front of it was played without help.
//
// The rule the player is told, in one sentence, and everything below exists to make it true:
// a run counts for the leaderboards only if the mod was switched off for the whole of it.
//
// The patches are permanent for the session and only their ANSWER changes. Unpatching and
// repatching as the player flips the switch would make the guarantee depend on a race between
// Harmony and the game's own end-of-battle sequence; a patch that is always there and consults a
// flag cannot lose that race.
internal sealed class LeaderboardGuard : IModSwitch
{
    // Reached from the static prefixes. There is exactly one guard per process, built before
    // anything else, so this is set once and never cleared.
    private static LeaderboardGuard _live;

    // What the game can call to write a score, as read from the installed build's generated
    // interop assembly. The two Async ones are only ever reached through the two above them, so
    // closing the pair below closes all four. See the boot check for why this list exists.
    private static readonly string[] KnownSubmissionMethods =
    {
        "SubmitScore", "SubmitChallengeScore", "SubmitScoreAsync", "SubmitChallengeScoreAsync"
    };

    // Written into the mark when the mod ran inside a run whose identity could not be read. It can
    // never equal a real run id, so it blocks until either a run is identified or the saved run is
    // gone. Without it, a failed mark would be a silent hole: the run really was modded, nothing
    // recorded it, and switching the mod off afterwards would open the board.
    private const string UnidentifiedRun = "unidentified";

    private readonly MelonPreferences_Entry<bool> _enabled;
    private readonly MelonPreferences_Entry<string> _dirtyRunId;
    private readonly MelonPreferences_Entry<bool> _streakDirty;
    private readonly MelonPreferences_Entry<bool> _noticeShown;

    /// <summary>True once both submission paths are provably shut.</summary>
    public bool Applied { get; private set; }

    /// <summary>Why the mod switched itself off. Null while <see cref="Applied"/> is true.</summary>
    public string UnavailableReason { get; private set; }

    /// <summary>Whether the mod's per-frame feature layer is currently running.</summary>
    /// <remarks>
    /// Set by the mod on the frame the switch changes, rather than read from the preference here,
    /// so that "the features are running" means what actually happens rather than what is
    /// configured. A run that has been torn down is not running even if the setting still says on.
    /// </remarks>
    public bool FeaturesRunning { get; private set; }

    public LeaderboardGuard(HarmonyLib.Harmony harmony,
        MelonPreferences_Entry<bool> enabled,
        MelonPreferences_Entry<string> dirtyRunId,
        MelonPreferences_Entry<bool> streakDirty,
        MelonPreferences_Entry<bool> noticeShown)
    {
        _enabled = enabled;
        _dirtyRunId = dirtyRunId;
        _streakDirty = streakDirty;
        _noticeShown = noticeShown;
        _live = this;

        try
        {
            if (harmony == null)
                throw new InvalidOperationException("MelonLoader did not provide a Harmony instance");

            // Named directly rather than looked up by name, on purpose, and against this mod's own
            // habit everywhere else. A name lookup only searches assemblies that are already
            // loaded, and this runs early enough that the game's own assembly may not be among
            // them yet : that would report a missing type and switch the mod off on a healthy
            // install, which looks exactly like a game update having broken it. Naming the type
            // also means that if Leyline ever renames or removes it, the build fails here, in
            // front of me, instead of the mod quietly switching itself off in every player's game.
            Type serviceType = typeof(Il2CppEmber.Scopes.Application.GamePlatform.SteamPlatformService);

            VerifyNoUnknownSubmissionPath(serviceType);

            MethodInfo submitScore = FindScoreMethod(serviceType, "SubmitScore");
            MethodInfo submitChallengeScore = FindScoreMethod(serviceType, "SubmitChallengeScore");

            PatchAndVerify(harmony, submitScore, nameof(EndlessScorePrefix));
            PatchAndVerify(harmony, submitChallengeScore, nameof(ChallengeScorePrefix));
            Applied = true;
        }
        catch (Exception e)
        {
            Applied = false;
            UnavailableReason = e.Message;
            TryLogError("[TargetingMod] leaderboard suppression could not be applied: " + e.Message);
        }
    }

    // Every way the game has of writing a score has to be one this mod already closes, or the
    // guarantee is only as good as the day it was written.
    //
    // This is the one defence that survives Leyline adding a third board. Patching two methods
    // proves nothing about a third, and a new board would arrive silently : the mod would keep
    // reporting itself healthy while a preview-assisted score went straight out. So the shape is
    // checked rather than the count, and anything shaped like a submission that this mod does not
    // know about switches the whole mod off and says so.
    //
    // Deliberately narrow. It matches only a method whose name begins with Submit and which takes
    // exactly one int, which is the shape both real ones have. A wider test would eventually fire
    // on some generated member and brick the mod on a healthy install, which is a worse failure
    // than the one it is guarding against is likely.
    private static void VerifyNoUnknownSubmissionPath(Type serviceType)
    {
        var unknown = new List<string>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (MethodInfo method in serviceType.GetMethods(flags))
        {
            if (!method.Name.StartsWith("Submit", StringComparison.Ordinal)) continue;
            if (Array.IndexOf(KnownSubmissionMethods, method.Name) >= 0) continue;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 1 || parameters[0].ParameterType != typeof(int)) continue;
            unknown.Add(method.Name);
        }
        if (unknown.Count == 0) return;
        throw new InvalidOperationException(
            "the game can now submit a score by a route this mod does not close (" +
            string.Join(", ", unknown) + ")");
    }

    // Patch, then ask Harmony whether the patch is actually registered. A guard that fails closed
    // cannot settle for "no exception was thrown" : the entire point of it is that the mod refuses
    // to run unless submission is provably shut, so the proof has to be taken rather than assumed.
    private static void PatchAndVerify(HarmonyLib.Harmony harmony, MethodInfo target, string prefixName)
    {
        MethodInfo prefix = typeof(LeaderboardGuard).GetMethod(prefixName,
            BindingFlags.NonPublic | BindingFlags.Static);
        if (prefix == null)
            throw new MissingMethodException(nameof(LeaderboardGuard), prefixName);

        harmony.Patch(target, prefix: new HarmonyMethod(prefix));

        Patches info = HarmonyLib.Harmony.GetPatchInfo(target);
        bool registered = false;
        if (info != null && info.Prefixes != null)
        {
            foreach (Patch applied in info.Prefixes)
            {
                if (applied.PatchMethod == prefix)
                {
                    registered = true;
                    break;
                }
            }
        }
        if (!registered)
            throw new InvalidOperationException("Harmony did not register the prefix on " + target.Name);
    }

    private static MethodInfo FindScoreMethod(Type serviceType, string methodName)
    {
        MethodInfo method = serviceType.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(int) },
            modifiers: null);
        if (method == null)
            throw new MissingMethodException(serviceType.FullName, methodName + "(int)");
        return method;
    }

    // Returning true runs the game's own submission untouched. Returning false stops it dead, as
    // every version of this guard has done since it existed.
    //
    // Nothing in here may throw. A prefix that throws takes the game's end-of-battle sequence with
    // it, so the whole answer is wrapped and the exception path blocks, which is the safe verdict
    // in a method whose job is to withhold.
    private static bool EndlessScorePrefix()
    {
        try
        {
            LeaderboardGuard guard = _live;
            if (guard == null)
            {
                TryLogMessage("[TargetingMod] blocked an Endless score: the guard is patched but has no policy behind it");
                return false;
            }
            return guard.AllowEndless();
        }
        catch (Exception e)
        {
            TryLogError("[TargetingMod] blocked an Endless score because the guard itself failed: " + e);
            return false;
        }
    }

    // The parameter name has to match the game's own, because that is how Harmony binds it. Read
    // from the installed build's generated assembly : SubmitChallengeScore(int currentStreak).
    private static bool ChallengeScorePrefix(int currentStreak)
    {
        try
        {
            LeaderboardGuard guard = _live;
            if (guard == null)
            {
                TryLogMessage("[TargetingMod] blocked a Red Rift streak: the guard is patched but has no policy behind it");
                return false;
            }
            return guard.AllowChallenge(currentStreak);
        }
        catch (Exception e)
        {
            TryLogError("[TargetingMod] blocked a Red Rift streak because the guard itself failed: " + e);
            return false;
        }
    }

    private bool AllowEndless()
    {
        if (FeaturesRunning) return Block("Endless", "the mod is switched on");
        if (!CurrentRunIsClean(out string why)) return Block("Endless", why);
        TryLogMessage("[TargetingMod] Endless score submitted: this run was played without the mod");
        return true;
    }

    private bool AllowChallenge(int currentStreak)
    {
        string why = "the mod is switched on";
        bool runClean = !FeaturesRunning && CurrentRunIsClean(out why);

        // A streak of one contains only the run being submitted right now, so that run alone
        // decides whether the streak is clean. This is what lets a poisoned streak recover without
        // the mod having to watch for the reset : the game resets the streak itself when a Red
        // Rift run fails, and the next win arrives here as a one.
        if (currentStreak <= 1) SetStreakDirty(!runClean);

        if (!runClean) return Block("Red Rift", why);
        if (_streakDirty.Value)
            return Block("Red Rift", "this win streak also contains an earlier run played with the mod on");
        TryLogMessage("[TargetingMod] Red Rift streak submitted: no run in it was played with the mod");
        return true;
    }

    private static bool Block(string board, string reason)
    {
        TryLogMessage("[TargetingMod] blocked a " + board + " leaderboard submission: " + reason);
        return false;
    }

    // Whether the run happening right now is one the mod never touched.
    //
    // The comparison is made here, against the live run, rather than against anything cached. A
    // cached answer would have to be kept fresh by something running every frame, and the whole
    // point of the switch is that a mod which is off costs nothing per frame.
    //
    // The empty mark is answered without reading anything at all, and that is not an optimization.
    // A player who has never switched the mod on has no mark, so a failure to read the run can
    // never cost them a score : only somebody who has actually used the preview is ever judged by
    // the strict branch below.
    private bool CurrentRunIsClean(out string why)
    {
        why = null;
        string marked = _dirtyRunId.Value;
        if (string.IsNullOrEmpty(marked)) return true;
        if (string.Equals(marked, UnidentifiedRun, StringComparison.Ordinal))
        {
            why = "the mod ran inside a run it could not identify; start a new run";
            return false;
        }
        try
        {
            if (!DataReaders.TryGet<RunSessionDataReader>(out var run) || run == null)
            {
                why = "the run could not be read, so this run cannot be shown to be unmodded";
                return false;
            }
            string id = run.RunId.ToString();
            if (string.IsNullOrEmpty(id))
            {
                why = "the run has no id, so this run cannot be shown to be unmodded";
                return false;
            }
            if (string.Equals(id, marked, StringComparison.OrdinalIgnoreCase))
            {
                why = "this run was played with the mod on";
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            why = "reading the run failed (" + e.Message + ")";
            return false;
        }
    }

    /// <summary>
    /// Records that the mod has done work inside the run now in progress.
    /// </summary>
    /// <remarks>
    /// Called on entering a battle phase rather than every frame, because the mark only has to be
    /// written once per run and a preference write is a file write. Entering a battle is also the
    /// earliest point at which the mod has actually given the player anything, so continuing a run
    /// with the mod on and leaving again before a fight leaves the run clean.
    /// </remarks>
    public void NoteRunIsModded()
    {
        if (!Applied || !FeaturesRunning) return;
        try
        {
            string id = null;
            if (DataReaders.TryGet<RunSessionDataReader>(out var run) && run != null)
                id = run.RunId.ToString();
            if (string.IsNullOrEmpty(id))
            {
                MarkUnidentified();
                return;
            }

            bool changed = false;
            if (!string.Equals(id, _dirtyRunId.Value, StringComparison.OrdinalIgnoreCase))
            {
                _dirtyRunId.Value = id;
                changed = true;
                MelonLogger.Msg("[TargetingMod] this run is now marked as played with the mod on; its scores stay off the leaderboards");
            }
            if (!_streakDirty.Value && IsChallengeRun())
            {
                _streakDirty.Value = true;
                changed = true;
                MelonLogger.Msg("[TargetingMod] the Red Rift win streak now contains a run played with the mod on");
            }
            if (changed) MelonPreferences.Save();
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[TargetingMod] marking this run failed, so it is marked as unidentified: " + e.Message);
            MarkUnidentified();
        }
    }

    private void MarkUnidentified()
    {
        if (string.Equals(_dirtyRunId.Value, UnidentifiedRun, StringComparison.Ordinal)) return;
        _dirtyRunId.Value = UnidentifiedRun;
        MelonPreferences.Save();
        MelonLogger.Warning("[TargetingMod] the mod is running in a run it cannot identify; no score will be submitted until a new run starts");
    }

    private static bool IsChallengeRun()
    {
        try
        {
            return DataReaders.TryGet<ChallengeReader>(out var challenge) && challenge != null &&
                   challenge.IsChallengeRun;
        }
        catch
        {
            // Losing this read only costs the streak mark, and the run mark above already stands.
            // Reporting it as a challenge run when it is not would block a board the mod never
            // touched, so the quiet answer here is the honest one.
            return false;
        }
    }

    public void SetFeaturesRunning(bool running)
    {
        if (FeaturesRunning == running) return;
        FeaturesRunning = running;
    }

    private void SetStreakDirty(bool value)
    {
        if (_streakDirty.Value == value) return;
        _streakDirty.Value = value;
        MelonPreferences.Save();
        MelonLogger.Msg(value
            ? "[TargetingMod] the Red Rift win streak is now marked as containing a modded run"
            : "[TargetingMod] the Red Rift win streak restarted clean, so its mark is cleared");
    }

    public bool Available => Applied;
    public bool Enabled => _enabled != null && _enabled.Value;
    public bool SavedRunIsModded => _dirtyRunId != null && !string.IsNullOrEmpty(_dirtyRunId.Value);
    public bool ChallengeStreakIsModded => _streakDirty != null && _streakDirty.Value;
    public bool NoticeShown => _noticeShown != null && _noticeShown.Value;

    public void SetEnabled(bool value)
    {
        if (_enabled == null || _enabled.Value == value) return;
        _enabled.Value = value;
        MelonPreferences.Save();
        MelonLogger.Msg("[TargetingMod] switched " + (value ? "on" : "off") + " from the main menu");
    }

    public void MarkNoticeShown()
    {
        if (_noticeShown == null || _noticeShown.Value) return;
        _noticeShown.Value = true;
        MelonPreferences.Save();
    }

    /// <summary>
    /// Tells the guard what the main menu can see, so a mark that outlived its run is dropped.
    /// </summary>
    /// <remarks>
    /// The gate never depends on this. It exists so the menu can say "the run you have in progress
    /// was played with the mod on" without lying to a player who abandoned that run and started a
    /// clean one. The game keeps exactly one saved run, so no saved run means the marked run is
    /// gone. Reaching the menu is also the only way to abandon a run or to be returned here after
    /// a defeat, so the clear happens at the one moment it is provable.
    /// </remarks>
    public void NoteMainMenuState(bool hasSavedRun)
    {
        if (hasSavedRun || _dirtyRunId == null || string.IsNullOrEmpty(_dirtyRunId.Value)) return;
        _dirtyRunId.Value = string.Empty;
        MelonPreferences.Save();
        MelonLogger.Msg("[TargetingMod] no saved run is left, so the mark on the last modded run is cleared");
    }

    /// <summary>One line for the boot log: what the guard is, and what it will allow.</summary>
    public string Diagnostic => !Applied
        ? "leaderboards NOT guaranteed shut (" + UnavailableReason + ")"
        : "both boards patched; marked run=" +
          (string.IsNullOrEmpty(_dirtyRunId.Value) ? "none" : _dirtyRunId.Value) +
          ", Red Rift streak marked=" + _streakDirty.Value;

    // Logging must never reopen a submission path. If the loader is already shutting down, the
    // score still stays blocked even when its logger is no longer available.
    private static void TryLogMessage(string message)
    {
        try { MelonLogger.Msg(message); }
        catch { }
    }

    // Initialization is allowed to report a loader failure, but reporting it cannot be another
    // way for the failure to escape and leave the mod only partly initialized.
    private static void TryLogError(string message)
    {
        try { MelonLogger.Error(message); }
        catch { }
    }
}
