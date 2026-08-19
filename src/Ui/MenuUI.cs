using System;
using Il2CppCoffee.UIExtensions;
using Il2CppEmber.Scopes.Application.Persistence.Data;
using Il2CppEmber.Scopes.MainMenu.UI;
using Il2CppEmber.System.UI;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using Il2Cppgg.leyline.core.Mvcs.Model;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Localization.Components;
using UnityEngine.UI;

namespace GuildrunTargetingMod.Ui;

// The mod's main-menu control is made from the game's own button, so it carries the game's art,
// press animation and click sound without shipping a second visual language beside it.
internal sealed class MenuUI
{
    private readonly Capabilities _capabilities;
    private readonly IModSwitch _modSwitch;
    // The single boolean Tick reads before anything else. Everything below exists to keep this
    // false for the whole of a battle, because the mod being switched off has to cost nothing and
    // a menu button that polls during a fight would be exactly the cost it promises not to have.
    private bool _tickNeeded;
    private int _attemptsLeft;
    private float _nextAttemptAt;
    private MainMenuUIController _controller;
    private GameObject _clone;
    private Button _button;
    private TextMeshProUGUI[] _labels;
    private UnityAction _clickAction;
    private Il2CppSystem.Action _confirmationAction;
    private DialogPanel _dialog;
    private PersistenceReader _persistence;
    private int _noticeAttemptsLeft;
    private float _noticeReadyAt;
    private float _nextRefreshAt;

    // Learned the first time the button is built, and then used to ignore every other scene the
    // game loads. Without it the mod pays a bounded but real burst of whole-scene object sweeps on
    // every battle load, which is precisely the cold-start cost three performance cycles were spent
    // removing. Session lifetime is enough : the main menu is always the first scope, so the name
    // is known long before any battle scene arrives.
    private string _menuSceneName;

    // Five tries, half a second apart. Each try walks every loaded object in the game and costs 8
    // to 20 ms, so the count is what bounds the damage when a scene is not the menu at all. One try
    // is nearly always enough, because the loader raises the scene event after the scene's objects
    // exist; the other four are for a machine slow enough to be assembling the menu around them.
    private const int ResolveAttempts = 5;
    private const float ResolveIntervalSeconds = 0.5f;
    private const float RefreshSeconds = 2f;
    // The notice waits, because the first main menu of a session is also when the game plays its
    // intro comic and may raise a survey or a privacy dialog of its own. A modal thrown on top of
    // any of those is worse than one that arrives a moment later.
    private const float NoticeDelaySeconds = 5f;
    private const int NoticeAttempts = 20;

    // Every word the player ever sees lives here, so nothing that computes a result also writes
    // prose.
    private static string LabelOn => "Targeting Mod: On";
    private static string LabelOff => "Targeting Mod: Off";
    private static string LabelUnavail => "Targeting Mod: unavailable";

    private static string TurnOnTitle => "Turn on the targeting mod?";
    private static string TurnOnBody => "The mod shows where every unit will move and who it will attack, before the fight starts. A run played with the mod on cannot submit a score and does not move the Red Rift win streak in either direction.";

    private static string TurnOffTitle => "Turn off the targeting mod?";
    private static string TurnOffBody => "The board goes back to normal and your scores are sent to the leaderboards again.";
    private static string TurnOffRunLine => "The run you have in progress was played with the mod on, so it still does not count. Start a new run for your scores to count again.";
    private static string NoticeTitle => "Targeting Mod";
    private static string NoticeBody => "The targeting mod is on. It shows where every unit will move and who it will attack, before the fight starts. A run played with the mod on cannot submit a score and does not move the Red Rift win streak in either direction. You can switch it off at any time from the main menu.";

    private static string UnavailableTitle => "Targeting Mod";
    private static string UnavailableBody => "The mod has switched itself off because the game has changed and it can no longer guarantee the leaderboard and win-streak rule. Check for a mod update.";

    public string Diagnostic { get; private set; } = "unresolved";

    public MenuUI(Capabilities capabilities, IModSwitch modSwitch)
    {
        _capabilities = capabilities;
        _modSwitch = modSwitch;
    }

    /// <summary>
    /// A scene just finished loading. Opens a bounded window in which the menu will be looked for.
    /// </summary>
    /// <param name="sceneName">
    /// The loader's own name for it, or null when the mod is asking at startup and does not know.
    /// Once the menu's scene name has been learned, every other name is ignored outright.
    /// </param>
    public void OnSceneInitialized(string sceneName)
    {
        if (_menuSceneName != null && sceneName != null &&
            !string.Equals(sceneName, _menuSceneName, StringComparison.Ordinal))
            return;
        // A scene that failed to give up its button gets a fresh chance from the next one, because
        // a half-built menu is a passing condition and treating one as permanent has already cost
        // this mod a session elsewhere.
        _capabilities.ResetForMainMenu();
        _attemptsLeft = ResolveAttempts;
        _nextAttemptAt = 0f;
        _tickNeeded = true;
    }

    public void Tick()
    {
        if (!_tickNeeded) return;
        if (!_capabilities.MenuUi)
        {
            _tickNeeded = false;
            return;
        }
        if (_attemptsLeft > 0)
        {
            ResolveMainMenu();
            return; // the button, if one was just built, is refreshed from the next frame.
        }
        TickButton();
    }

    private void ResolveMainMenu()
    {
        try
        {
            if (_button != null)
            {
                _attemptsLeft = 0; // already built and still alive; another scene loaded beside it.
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now < _nextAttemptAt) return;
            _nextAttemptAt = now + ResolveIntervalSeconds;
            _attemptsLeft--;
            MainMenuUIController controller = RuntimeDiscovery.FindLive<MainMenuUIController>();
            if (controller == null)
            {
                // Silent. Most scenes are not the main menu, so a warning here would fire on every
                // battle load and teach players to report a mod that is working perfectly.
                if (_attemptsLeft <= 0) _tickNeeded = false;
                return;
            }
            _attemptsLeft = 0;
            BuildButton(controller);
        }
        catch (Exception e)
        {
            DisableFeature("main menu UI discovery failed: " + e.Message);
        }
    }

    private void BuildButton(MainMenuUIController controller)
    {
        Button source = controller._settingsButton;
        if (source == null) source = controller._communityButton;
        if (source == null) source = controller._quitButton;
        if (source == null) source = controller._startButton;
        if (source == null)
        {
            DisableFeature("main menu has no button available to clone");
            return;
        }

        Transform parent = source.transform.parent;
        if (parent == null)
        {
            DisableFeature("main menu button parent unavailable");
            return;
        }

        LayoutGroup layout = parent.GetComponent<LayoutGroup>();
        Vector2 manualStep = Vector2.zero;
        if (layout == null) manualStep = MeasureManualStep(source, parent);

        _controller = controller;
        _clone = UnityEngine.Object.Instantiate(source.gameObject, parent);
        _clone.name = "GuildrunTargetingMod.MenuButton";
        _clone.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);

        string layoutPath;
        if (layout != null)
        {
            layoutPath = "layout group " + RuntimeDiscovery.NativeClassName(layout);
            MelonLogger.Msg("[TargetingMod] main menu button placement: " + layoutPath);
        }
        else
        {
            PlaceManually(source, _clone, manualStep);
            layoutPath = $"manual, step=({manualStep.x:F1},{manualStep.y:F1})";
            MelonLogger.Msg("[TargetingMod] main menu button placement: " + layoutPath);
        }

        _button = _clone.GetComponent<Button>();
        if (_button == null) throw new InvalidOperationException("cloned main menu object has no Button");
        _button.onClick.RemoveAllListeners();
        // Both of these are kept in fields for the same reason: nothing on the managed side would
        // otherwise hold them, and the garbage collector cannot see that the game is holding them.
        // Converted through DelegateSupport rather than constructed, because that is the one route
        // this codebase has already proven against this interop layer.
        _clickAction = DelegateSupport.ConvertDelegate<UnityAction>(OnClicked);
        _button.onClick.AddListener(_clickAction);
        _confirmationAction = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(OnConfirmed);

        int localizersDestroyed = DestroyLocalizers(_clone);
        _labels = _clone.GetComponentsInChildren<TextMeshProUGUI>(true);
        if (_labels == null || _labels.Length == 0)
            throw new InvalidOperationException("cloned main menu button has no TextMeshProUGUI label");
        NeutralizeClonedSwitchVfx(_clone);
        RefreshLabel();

        // Learned here and nowhere else, from the scene the controller is really in rather than
        // from the name the loader happened to report, so a menu reached by any route is matched.
        try { _menuSceneName = controller.gameObject.scene.name; } catch { _menuSceneName = null; }

        Diagnostic = $"scene={_menuSceneName ?? "unknown"}; " +
            $"controller={RuntimeDiscovery.HierarchyPath(controller.transform)}; " +
            $"source={RuntimeDiscovery.HierarchyPath(source.transform)}; " +
            $"clone={RuntimeDiscovery.HierarchyPath(_clone.transform)}; {layoutPath}; " +
            $"localizers={localizersDestroyed}; labels={_labels.Length}";
        MelonLogger.Msg("[TargetingMod] main menu button resolved: " + Diagnostic);

        TryNoteMainMenuState();
        // Only ever pending once. The delay and the attempt cap are what stop a notice that can
        // never be shown from sweeping the scene every two seconds for the rest of the session.
        float now = Time.realtimeSinceStartup;
        _noticeAttemptsLeft = _modSwitch.Available && _modSwitch.Enabled && !_modSwitch.NoticeShown
            ? NoticeAttempts : 0;
        _noticeReadyAt = now + NoticeDelaySeconds;
        _nextRefreshAt = now + RefreshSeconds;
        _tickNeeded = true;
    }

    // How far, and in which direction, one menu button sits from the next.
    //
    // Read from the menu rather than assumed, because which way the buttons run is the one thing
    // about this screen the mod cannot know in advance. Taking the step from a real pair of the
    // game's own buttons means a column and a row both come out right, and it also picks up the
    // designer's spacing instead of inventing one.
    //
    // Measured BEFORE the clone is parented, or the clone itself would be the next button found.
    private static Vector2 MeasureManualStep(Button source, Transform parent)
    {
        RectTransform sourceRect = source.GetComponent<RectTransform>();
        if (sourceRect == null) throw new InvalidOperationException("main menu source has no RectTransform");
        int sourceIndex = source.transform.GetSiblingIndex();
        for (int i = sourceIndex + 1; i < parent.childCount; i++)
        {
            Transform sibling = parent.GetChild(i);
            Button nextButton = sibling.GetComponent<Button>();
            if (nextButton == null) continue;
            RectTransform nextRect = nextButton.GetComponent<RectTransform>();
            if (nextRect == null) continue;
            Vector2 step = nextRect.anchoredPosition - sourceRect.anchoredPosition;
            // One axis only. A menu that is a hair off true would otherwise drag the new button
            // sideways a few pixels out of the column it belongs to.
            if (Mathf.Abs(step.x) > Mathf.Abs(step.y)) return new Vector2(step.x, 0f);
            if (Mathf.Abs(step.y) > 0f) return new Vector2(0f, step.y);
            break; // two buttons stacked exactly on each other says nothing ; fall through.
        }
        // Nothing to measure against, so assume the shape almost every main menu has and move one
        // whole button downward. Logged either way, so the first play session says which happened.
        return new Vector2(0f, -SourceExtent(sourceRect, vertical: true));
    }

    private static void PlaceManually(Button source, GameObject clone, Vector2 step)
    {
        RectTransform sourceRect = source.GetComponent<RectTransform>();
        RectTransform cloneRect = clone.GetComponent<RectTransform>();
        if (sourceRect == null || cloneRect == null)
            throw new InvalidOperationException("main menu button RectTransform unavailable");
        cloneRect.anchorMin = sourceRect.anchorMin;
        cloneRect.anchorMax = sourceRect.anchorMax;
        cloneRect.pivot = sourceRect.pivot;
        cloneRect.sizeDelta = sourceRect.sizeDelta;
        cloneRect.anchoredPosition = sourceRect.anchoredPosition + step;
    }

    private static float SourceExtent(RectTransform sourceRect, bool vertical)
    {
        Rect rect = sourceRect.rect;
        float size = vertical ? rect.height : rect.width;
        if (size > 0f) return size;
        Vector2 delta = sourceRect.sizeDelta;
        return Mathf.Abs(vertical ? delta.y : delta.x);
    }

    private static int DestroyLocalizers(GameObject clone)
    {
        LocalizeStringEvent[] localizers = clone.GetComponentsInChildren<LocalizeStringEvent>(true);
        int count = 0;
        for (int i = 0; i < localizers.Length; i++)
        {
            LocalizeStringEvent localizer = localizers[i];
            if (localizer == null) continue;
            localizer.enabled = false;
            UnityEngine.Object.Destroy(localizer);
            count++;
        }
        return count;
    }

    // A cloned particle effect keeps coordinates from the control it belonged to and can flash
    // across the menu when the copy wakes. The press animation and click sound are separate and
    // remain on the button.
    private static void NeutralizeClonedSwitchVfx(GameObject clone)
    {
        UIParticle[] uiParticles = clone.GetComponentsInChildren<UIParticle>(true);
        for (int i = 0; i < uiParticles.Length; i++)
        {
            UIParticle particle = uiParticles[i];
            if (particle == null) continue;
            particle.enabled = false;
            if (particle.gameObject.Pointer != clone.Pointer) particle.gameObject.SetActive(false);
        }
        ParticleSystem[] systems = clone.GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < systems.Length; i++)
        {
            ParticleSystem system = systems[i];
            if (system == null) continue;
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (system.gameObject.Pointer != clone.Pointer) system.gameObject.SetActive(false);
        }
    }

    private void TickButton()
    {
        try
        {
            // The managed wrapper survives scene teardown. Unity's null check is the first guard,
            // and the catch is still required because the scene can disappear between two calls.
            if (_button == null)
            {
                ClearSceneFields();
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now < _nextRefreshAt) return;
            RefreshLabel();
            TryNoteMainMenuState();
            TryShowNotice();
            _nextRefreshAt = now + RefreshSeconds;
        }
        catch (Exception e)
        {
            // Losing the object with its scene is ordinary teardown, not a capability verdict.
            bool buttonStillLive = false;
            try { buttonStillLive = _button != null; } catch { }
            if (buttonStillLive) DisableFeature("main menu button refresh failed: " + e.Message);
            else ClearSceneFields();
        }
    }

    private void RefreshLabel()
    {
        if (_labels == null) return;
        string text = !_modSwitch.Available ? LabelUnavail : _modSwitch.Enabled ? LabelOn : LabelOff;
        for (int i = 0; i < _labels.Length; i++)
            if (_labels[i] != null) _labels[i].text = text;
    }

    // Asked again on every refresh rather than answered once, because it changes underneath us:
    // abandoning a run is done from this very menu, and the answer decides whether the switch-off
    // dialog tells the player their run in progress no longer counts. Latching it meant the mod
    // would say that about a run the player had already thrown away.
    //
    // The reader is cached because finding it is the expensive half; asking it is a field read.
    private void TryNoteMainMenuState()
    {
        try
        {
            if (_persistence == null)
            {
                try { DataReaders.TryGet<PersistenceReader>(out _persistence); }
                catch { /* the registry is not up yet ; the controller's own copy is tried next. */ }
                if (_persistence == null)
                    _persistence = RuntimeDiscovery.ReadField<PersistenceReader>(_controller, "_persistenceReader");
                if (_persistence == null) return;
            }
            _modSwitch.NoteMainMenuState(_persistence.HasGameRunMetadata);
        }
        catch (Exception e)
        {
            // Nothing here can be allowed to matter. The gate compares real run ids at submission
            // time and never consults any of this ; all that is lost is one sentence of wording.
            _persistence = null;
            MelonLogger.Warning("[TargetingMod] main menu saved-run state was not readable: " + e.Message);
        }
    }

    private void OnClicked()
    {
        try
        {
            if (!_modSwitch.Available)
            {
                if (DialogIsClosed()) DialogPanel.ShowSimpleDialog(UnavailableTitle, UnavailableBody);
                return;
            }
            if (!DialogIsClosed()) return;
            if (!_modSwitch.Enabled)
            {
                DialogPanel.ShowConfirmationDialog(TurnOnTitle, TurnOnBody, _confirmationAction);
                return;
            }
            string body = TurnOffBody;
            if (_modSwitch.SavedRunIsModded) body += " " + TurnOffRunLine;
            DialogPanel.ShowConfirmationDialog(TurnOffTitle, body, _confirmationAction);
        }
        catch (Exception e)
        {
            DisableFeature("main menu dialog failed: " + e.Message);
        }
    }

    private void OnConfirmed()
    {
        try
        {
            _modSwitch.SetEnabled(!_modSwitch.Enabled);
            RefreshLabel();
            _nextRefreshAt = Time.realtimeSinceStartup + RefreshSeconds;
        }
        catch (Exception e)
        {
            DisableFeature("main menu toggle failed: " + e.Message);
        }
    }

    // Said once per install, and it is the layer that reaches the player who never presses the
    // button : the one who installs the mod, plays, loses an endless run and would otherwise find
    // out about the leaderboard rule by not getting a score.
    private void TryShowNotice()
    {
        if (_noticeAttemptsLeft <= 0) return;
        // Asked again here, not only when the notice was queued. A player who finds the button on
        // their own and switches the mod off inside the delay below already knows everything this
        // was going to tell them, and it would tell them the mod is on while the button beside it
        // says otherwise.
        if (!_modSwitch.Available || !_modSwitch.Enabled)
        {
            _noticeAttemptsLeft = 0;
            return;
        }
        if (Time.realtimeSinceStartup < _noticeReadyAt) return;
        _noticeAttemptsLeft--;
        if (!DialogIsClosed())
        {
            if (_noticeAttemptsLeft <= 0)
                MelonLogger.Warning("[TargetingMod] the leaderboard notice could not be shown; the readme and the menu button still say it");
            return;
        }
        DialogPanel.ShowSimpleDialog(NoticeTitle, NoticeBody);
        _noticeAttemptsLeft = 0;
        _modSwitch.MarkNoticeShown();
        // Said out loud, because otherwise the only trace this ever happened is a preference
        // flipping to true, and a preference cannot tell "it was shown" from "it was skipped for a
        // reason nobody recorded". This line is the difference between a log that answers a report
        // and one that leaves it open.
        MelonLogger.Msg("[TargetingMod] the leaderboard notice was shown, once, and will not be shown again");
    }

    // Finding the panel walks every loaded object, so it is found once and kept. Whether it is
    // OPEN is then a field read, which is what makes it affordable to ask on a timer.
    //
    // Asked at all because the game's own ValidateState logs an engine-level error when a dialog
    // is already up, and a mod that makes the game print errors is a mod that gets blamed for them.
    private bool DialogIsClosed()
    {
        try
        {
            if (_dialog == null) _dialog = RuntimeDiscovery.FindLive<DialogPanel>();
            if (_dialog == null)
            {
                MelonLogger.Warning("[TargetingMod] no live DialogPanel, so nothing is shown and nothing is changed");
                return false;
            }
            return !_dialog.gameObject.activeSelf;
        }
        catch (Exception e)
        {
            _dialog = null;
            MelonLogger.Warning("[TargetingMod] the game's dialog could not be reached: " + e.Message);
            return false;
        }
    }

    // Its own capability, not the placement one. Losing the menu button must not blank the three
    // buttons in battle, and a battle button that threw must not cost the player the only way to
    // switch the mod back on.
    private void DisableFeature(string reason)
    {
        _capabilities.DisableMenuUi(reason);
        Destroy();
    }

    // Scene teardown can invalidate each native call independently. No failed listener removal is
    // allowed to skip destroying the clone, and all managed state is cleared either way.
    public void Destroy()
    {
        _attemptsLeft = 0;
        try { if (_button != null) _button.onClick.RemoveAllListeners(); }
        catch { /* the scene already took the button and its listeners with it. */ }
        try { if (_clone != null) UnityEngine.Object.Destroy(_clone); }
        catch { /* already gone, which is the outcome this wanted anyway. */ }
        ClearSceneFields();
        _tickNeeded = false;
    }

    // Everything that belonged to a scene, dropped in one place so the two callers cannot disagree
    // about what "gone" means. The learned scene name deliberately survives : it describes the
    // game, not the objects, and re-learning it would put the sweep burst back on every load.
    private void ClearSceneFields()
    {
        _tickNeeded = _attemptsLeft > 0;
        _noticeAttemptsLeft = 0;
        try { if (_button != null) _button.onClick.RemoveAllListeners(); }
        catch { /* the scene already took the button and its listeners with it. */ }
        _controller = null;
        _clone = null;
        _button = null;
        _labels = null;
        _clickAction = null;
        _confirmationAction = null;
        _dialog = null;
        _persistence = null;
        _noticeReadyAt = 0f;
        _nextRefreshAt = 0f;
        Diagnostic = "unresolved";
    }

}
