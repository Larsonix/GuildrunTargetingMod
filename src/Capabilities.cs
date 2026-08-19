using System.Collections.Generic;
using MelonLoader;

namespace GuildrunTargetingMod;

// One switch per thing the mod can lose. Each feature is its own failure domain : a tooltip that
// throws must never take the renderer, the hover raycast or the prediction with it, so every
// failure path turns off exactly one of these and says why in the log.
internal sealed class Capabilities
{
    public bool CoreRead { get; private set; } = true;
    public bool Prediction { get; private set; } = true;
    public bool Hover { get; private set; } = true;
    public bool Overlay { get; private set; } = true;
    public bool Preview { get; private set; } = true;
    public bool NativeUi { get; private set; } = true;
    // The main menu switch, kept apart from NativeUi even though both are buttons cloned from the
    // game. They live in different scenes with different lifetimes, and a failure in one has no
    // bearing on the other : losing the menu button must not blank the three placement toggles,
    // and a placement toggle that threw must not cost the player the only way back on.
    public bool MenuUi { get; private set; } = true;
    public bool DragPreview { get; private set; } = true;
    // The Rift Seal mark. Named for what it is now rather than what it was : it used to be a
    // sentence on screen and is now the same red mark every other part of the mod uses.
    public bool SealMark { get; private set; } = true;
    public bool PositionalGlow { get; private set; } = true;
    // Split from PositionalGlow on purpose. The data layer reads the game's own effect evaluation,
    // which is stable ; marking the item row and the relic bar reaches into the game's UI, which is
    // the most update-fragile surface here. When that moves, the icons go quiet and the board mark,
    // the arrows and the prediction carry on.
    public bool GlowIcons { get; private set; } = true;
    public bool AoeOutline { get; private set; } = true;

    /// <summary>
    /// Bumped every time any switch above moves, in either direction.
    /// </summary>
    /// <remarks>
    /// The world overlay skips redrawing a frame whose picture is provably identical to the last
    /// one. What it draws depends on these switches, and a feature going down is not visible in any
    /// argument passed to it, so without this a fault that turned the preview off mid placement
    /// would leave the preview on screen until something else happened to force a redraw. Cheap to
    /// read, impossible to forget: every setter goes through the two helpers below.
    /// </remarks>
    public int Generation { get; private set; }

    private string _lastCoreLog;
    private string _lastPredictionLog;
    private readonly HashSet<string> _uiLogs = new();

    public void DisableCoreRead(string reason)
    {
        // Log a new reason even when the flag is already down. Staying quiet the second time
        // once hid a real config-building exception behind the boot-time safe-mode message for
        // a whole session.
        if (CoreRead) Generation++;
        CoreRead = false;
        if (_lastCoreLog == reason) return;
        _lastCoreLog = reason;
        MelonLogger.Error("[TargetingMod] CORE READ DISABLED: " + reason);
    }

    public void DisablePrediction(string reason)
    {
        if (Prediction) Generation++;
        Prediction = false;
        if (_lastPredictionLog == reason) return;
        _lastPredictionLog = reason;
        MelonLogger.Error("[TargetingMod] PREDICTION DISABLED: " + reason);
    }

    public void EnablePredictionAfterParity()
    {
        if (!Prediction) Generation++;
        Prediction = true;
    }

    // Presentation gets another chance when the next placement builds a fresh set of scene views.
    // A view caught halfway through being rebuilt is a passing condition, and treating one as
    // permanent has already cost this mod a session. Core reading and prediction are correctness
    // verdicts with separate lifecycles, so this deliberately cannot change either one.
    public void ResetForPlacement()
    {
        Generation++;
        DragPreview = true;
        Hover = true;
        Overlay = true;
        Preview = true;
        NativeUi = true;
        SealMark = true;
        PositionalGlow = true;
        GlowIcons = true;
        // The area outlines belong here too, for the same reason as every line above it. They were
        // missing from the first list by oversight rather than by argument, and a presentation
        // switch left out of a presentation reset is a feature that stays off for the session on
        // the machine least able to afford losing it.
        AoeOutline = true;
    }

    // Deliberately NOT in ResetForPlacement, which is about the battle scene. The menu button gets
    // its own second chance when the menu scene is built again, which is the moment that matters
    // for it and the only moment there is anything to rebuild.
    public void ResetForMainMenu()
    {
        if (MenuUi) return;
        Generation++;
        MenuUi = true;
    }

    public void DisableMenuUi(string reason) { MenuUi = false; DisableUi("MENU UI", reason); }
    public void DisableDragPreview(string reason) { DragPreview = false; DisableUi("DRAG PREVIEW", reason); }
    public void DisableHover(string reason) { Hover = false; DisableUi("HOVER", reason); }
    public void DisableOverlay(string reason) { Overlay = false; DisableUi("OVERLAY", reason); }
    public void DisablePreview(string reason) { Preview = false; DisableUi("PREVIEW", reason); }
    public void DisableNativeUi(string reason) { NativeUi = false; DisableUi("NATIVE UI", reason); }
    public void DisableSealMark(string reason) { SealMark = false; DisableUi("RIFT SEAL MARK", reason); }
    public void DisablePositionalGlow(string reason) { PositionalGlow = false; DisableUi("POSITIONAL GLOW", reason); }
    public void DisableGlowIcons(string reason) { GlowIcons = false; DisableUi("GLOW ICONS", reason); }
    public void DisableAoeOutline(string reason) { AoeOutline = false; DisableUi("AREA OUTLINE", reason); }

    // Once per distinct feature and reason, so a fault that repeats every frame reports once.
    //
    // The generation moves on EVERY call rather than only on a newly logged one. The log is
    // deduplicated because a player does not need the same line sixty times a second; the renderer
    // is not, because it needs to know the switch is down whether or not this is the first time
    // anyone said so.
    private void DisableUi(string feature, string reason)
    {
        Generation++;
        if (!_uiLogs.Add(feature + ":" + reason)) return;
        MelonLogger.Error($"[TargetingMod] {feature} DISABLED: {reason}");
    }
}
