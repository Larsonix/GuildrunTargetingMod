using System;
using System.Collections.Generic;
using Il2CppCoffee.UIExtensions;
using Il2CppEmber.Scopes.Application.UI.Tooltips;
using Il2CppEmber.Scopes.Application.UI.Tooltips.Sources;
using Il2CppEmber.Scopes.Battle.Board.Controllers;
using Il2CppEmber.Scopes.Battle.UI;
using Il2CppEmber.Scopes.Battle.UI.BattleFlow;
using Il2CppEmber.Scopes.GameRun.UI.BattleSpeed;
using Il2CppEmber.Scopes.GameRun.Utilities.Tooltips;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

namespace GuildrunTargetingMod.Ui;

// The mod's own interface : three buttons, a status line, and the tooltips on the buttons.
//
// None of it is built from scratch. The buttons are clones of the game's speed toggles, so they
// carry its art, its press animation and its click sound, and the tooltips are the game's own
// tooltip, driven directly. The mod bundles no image and no font. That way it looks like part of
// the game, and it keeps looking like it when the game's art changes.
internal sealed class NativeUI
{
    private readonly Capabilities _capabilities;
    private readonly Action<bool> _previewChanged;
    private readonly MelonPreferences_Entry<bool> _arrowsFromGhosts;
    private readonly MelonPreferences_Entry<bool> _midlineHeads;
    private readonly MelonPreferences_Entry<bool> _transparentUnits;
    private readonly MelonPreferences_Entry<bool> _previewStartsOn;
    private readonly MelonPreferences_Entry<string> _previewKey;
    private readonly MelonPreferences_Entry<string> _originKey;
    private readonly MelonPreferences_Entry<string> _transparencyKey;
    private readonly HashSet<string> _loggedBadKeys = new(StringComparer.Ordinal);
    private ModToggle _previewToggle;
    private ModToggle _originToggle;
    private ModToggle _transparencyToggle;
    private readonly List<UnityEngine.Object> _ownedIconAssets = new();
    private GameObject _placementRoot;
    private TextMeshProUGUI _notice;
    private Image _noticePlate;
    // Clear of the hero panel and the item row underneath it. The status line used to sit at 30,
    // which put it across the item icons and made it genuinely unreadable in play.
    private const float NoticeHeight = 132f;
    private const float PlatePaddingX = 28f;
    private const float PlatePaddingY = 14f;
    private bool _tooltipUnavailable;
    private TMP_FontAsset _font;
    private Color _textColor;
    private bool _textColorResolved;
    private AppTooltipController _tooltipController;
    private TooltipRaycastTarget _tooltipTarget;
    private SimpleTooltipSource _tooltipSource;
    private TooltipView _tooltipView;          // read back, to check a tooltip really did appear.
    private RectTransform _tooltipAnchorPoint; // moved each frame ; the game follows it.
    private GameObject _tooltipAnchorRoot;
    private TooltipAnchor _tooltipAnchor;
    private TooltipPivotType _tooltipPivot = TooltipPivotType.BottomCenter;
    private RectTransform _tooltipAnchorTarget; // set : hang under this button instead of the cursor.
    private readonly Il2CppStructArray<Vector3> _worldCorners = new(4);
    private RectTransform _healthBarsParent;
    private bool _hudParentSearchFailed;
    private bool _resolved;
    private Sprite _previewIcon;
    private string _lastTooltipKey;
    private bool _loggedToggleTooltip;
    private bool _loggedUnverifiableTooltip;
    private bool _loggedTooltipDidNotRender;
    private bool _tooltipShownByUs;

    // Every word the player ever sees lives here, so nothing that computes a result also writes
    // prose.

    // Two sentences, and the mod is now silent in every case that is not one of them.
    //
    // The "self-check in progress" line is gone with the state it described : a fresh install and a
    // new game build no longer withhold anything, so there is nothing to announce and nothing to
    // wait for. Two playtesters read that line as the mod being broken, and they were reading a
    // mod that was working.
    //
    // The remaining one never blames a game update. It said "turned off after a game update, a mod
    // update will bring it back" on a machine where no game had updated and no mod update was
    // coming : it had disagreed with itself over a rounding difference and put the blame outside.
    // A notice that names the wrong cause sends the player looking in the wrong place, so this one
    // states only what is known, which is that the mod and the fight disagreed and it will look
    // again next battle.
    private static string NoticeText(ModNotice notice) => notice switch
    {
        ModNotice.Disagreement => "Preview turned off: the mod and the fight disagreed. It checks again next battle.",
        ModNotice.ReadFailure => "Preview turned off: the mod could not read the battle correctly.",
        _ => null
    };

    // Written for someone who has never used the mod : say what the button does, and what turning
    // it off gives you, so nobody has to click all three to find out what they are.
    private static string PreviewTooltipTitle => "Opening preview";
    private static string PreviewTooltipBody => "Shows the whole board at once: where every unit ends up, and who it is fighting there. Starts off again at the beginning of each battle.";

    private static string OriginTooltipTitle => "Arrows from final positions";
    private static string OriginTooltipBody => "Attack arrows start where units will be standing. Turn it off to make them start where units are right now.";

    private static string MidHeadTooltipTitle => "Direction marks";
    private static string MidHeadTooltipBody => "Adds a small arrow in the middle of every line, so you can tell which way it goes.";

    private static string TransparencyTooltipTitle => "See-through units";
    private static string TransparencyTooltipBody => "Fades the units so you can see the board through them, for the whole placement. Turn it off to keep them at full colour.";

    private static string StillMovingText => "Still moving";

    // A button's shortcut goes in brackets after its name, because that is how the game writes
    // its own : "Hero Panel (Tab)", "Open Shop (Space)", "Feedback (Enter)". Square brackets are
    // the game's other shape, kept for a key named in the middle of a sentence, as in "Hold
    // [Shift] for more detail". A button's own name takes the round ones.
    //
    // Read the label from the keyboard the player is actually using.
    private static string WithShortcut(string title, string keyLabel) =>
        string.IsNullOrEmpty(keyLabel) ? title : $"{title} ({keyLabel})";

    // There is deliberately no tooltip for hovering a unit. It named the unit's target in words,
    // next to arrows already saying the same thing, and read as leftover debugging. The arrows
    // are the feature ; a caption repeating them is noise.

    public string ToggleClonePath { get; private set; } = "unresolved";
    public string FontDiagnostic => _font != null ? _font.name : "unresolved";
    public GameObject PlacementRoot => _placementRoot;
    public bool PreviewEnabled => _previewToggle != null && _previewToggle.Toggle.isOn;

    public NativeUI(Capabilities capabilities, Action<bool> previewChanged,
        MelonPreferences_Entry<bool> arrowsFromGhosts, MelonPreferences_Entry<bool> midlineHeads,
        MelonPreferences_Entry<bool> transparentUnits, MelonPreferences_Entry<bool> previewStartsOn,
        MelonPreferences_Entry<string> previewKey, MelonPreferences_Entry<string> originKey,
        MelonPreferences_Entry<string> transparencyKey)
    {
        _capabilities = capabilities;
        _previewChanged = previewChanged;
        _arrowsFromGhosts = arrowsFromGhosts;
        _midlineHeads = midlineHeads;
        _transparentUnits = transparentUnits;
        _previewStartsOn = previewStartsOn;
        _previewKey = previewKey;
        _originKey = originKey;
        _transparencyKey = transparencyKey;
    }

    // P, F and D are not a matter of taste. Two things chose them.
    //
    // The game already uses Space, Enter, Tab, H, Shift and Escape. There is no keybinding screen
    // to read that from, so its own translated text is the only list of them there is.
    //
    // And a key is a position on the keyboard, not a letter. A, Q, Z, W and M move on common
    // non-QWERTY layouts, so any of them would land under a different finger there. P, F and D stay
    // in place. They also work as reminders: P for preview, F for from and D for direction.
    //
    // All three can be changed anyway. Any key name works, and a name that means nothing costs
    // that one shortcut and a line in the log, rather than a guess.
    private Key ParseKey(MelonPreferences_Entry<string> entry, Key fallback)
    {
        string raw = entry?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (Enum.TryParse(raw.Trim(), true, out Key parsed) && parsed != Key.None) return parsed;
        // Once per setting, not once overall. One shared flag would report the first mistyped key
        // and say nothing about the second, and someone rebinding all three at once is exactly
        // the person most likely to mistype two of them.
        if (_loggedBadKeys.Add(entry.Identifier))
            MelonLogger.Warning($"[TargetingMod] '{raw}' is not an InputSystem Key name; using {fallback} for {entry.Identifier}");
        return fallback;
    }

    // The label the player reads is taken from the keyboard they are actually using. It is the
    // character their own key produces, not a name inferred from its position. The plain name is
    // only the fallback for a key the layout cannot name.
    private static string KeyLabel(Key key)
    {
        try
        {
            Keyboard keyboard = Keyboard.current;
            string display = keyboard?[key]?.displayName;
            if (!string.IsNullOrWhiteSpace(display))
                return display.Length == 1 ? display.ToUpperInvariant() : display;
        }
        catch { /* no keyboard, or a key the layout cannot name ; fall through to the name. */ }
        return key.ToString();
    }

    public bool Resolve(Sprite previewIcon)
    {
        if (_resolved) return true;
        try
        {
            _previewIcon = previewIcon;
            BattleFlowUIStateController flow = RuntimeDiscovery.FindLive<BattleFlowUIStateController>();
            if (flow == null) return false;
            _placementRoot = RuntimeDiscovery.ReadField<GameObject>(flow, "_placementParent");
            if (_placementRoot == null) return false;
            ResolveFontAndColor();
            CreateNotice();
            CloneToggles();
            TryCreateNativeTooltip();
            _resolved = true;
            return true;
        }
        catch (Exception e)
        {
            _capabilities.DisableNativeUi("placement UI discovery failed: " + e.Message);
            Destroy();
            return false;
        }
    }

    public bool Resolved => _resolved;

    private void ResolveFontAndColor()
    {
        // Take the font and colour from the game's own tooltip, so the mod's text is styled by
        // the game rather than by a colour written down here that would go stale.
        var tooltipViews = RuntimeDiscovery.FindAll<TooltipView>();
        for (int i = 0; i < tooltipViews.Count; i++)
        {
            TextMeshProUGUI[] tooltipText = tooltipViews[i].GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int j = 0; j < tooltipText.Length; j++)
            {
                if (tooltipText[j].font == null) continue;
                _font = tooltipText[j].font;
                _textColor = tooltipText[j].color;
                _textColorResolved = true;
                if (_font.name.IndexOf("MendlSans", StringComparison.OrdinalIgnoreCase) >= 0) break;
            }
            if (_font != null && _font.name.IndexOf("MendlSans", StringComparison.OrdinalIgnoreCase) >= 0) break;
        }
        TextMeshProUGUI[] existing = _placementRoot.GetComponentsInChildren<TextMeshProUGUI>(true);
        for (int i = 0; i < existing.Length; i++)
        {
            TMP_FontAsset candidate = existing[i].font;
            if (candidate == null) continue;
            if (_font == null) { _font = candidate; _textColor = existing[i].color; _textColorResolved = true; }
            if (candidate.name.IndexOf("MendlSans", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (_font == null || _font.name.IndexOf("MendlSans", StringComparison.OrdinalIgnoreCase) < 0)
                { _font = candidate; _textColor = existing[i].color; _textColorResolved = true; }
                break;
            }
        }
        if (_font != null) return;
        var fonts = RuntimeDiscovery.FindAll<TMP_FontAsset>();
        for (int i = 0; i < fonts.Count; i++)
            if (fonts[i] != null && fonts[i].name.IndexOf("MendlSans", StringComparison.OrdinalIgnoreCase) >= 0)
            { _font = fonts[i]; break; }
        if (_font == null) throw new InvalidOperationException("MendlSans TMP font unavailable");
        if (!_textColorResolved)
        {
            Graphic graphic = _placementRoot.GetComponentInChildren<Graphic>(true);
            if (graphic == null) throw new InvalidOperationException("live placement UI color unavailable");
            _textColor = graphic.color;
            _textColorResolved = true;
        }
    }

    private void CreateNotice()
    {
        GameObject go = CreateRectGameObject("GuildrunTargetingMod.Notice");
        go.transform.SetParent(_placementRoot.transform, false);
        _notice = go.AddComponent<TextMeshProUGUI>();
        _notice.font = _font;
        _notice.color = _textColor;
        _notice.fontSize = 18f;
        _notice.alignment = TextAlignmentOptions.Center;
        _notice.raycastTarget = false;
        RectTransform rect = _notice.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0f);
        rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.anchoredPosition = new Vector2(0f, NoticeHeight);
        rect.sizeDelta = new Vector2(900f, 32f);
        _noticePlate = CreatePlate(go.transform, "GuildrunTargetingMod.NoticePlate");
        go.SetActive(false);
    }

    // Something to read the sentence against.
    //
    // Reported from play : the line was sitting across the item row and the icons behind it broke
    // the letters up badly enough that a player could not read it at all. Text over a board is text
    // over whatever the board happens to be showing, and no colour survives every background. A
    // dark plate behind it does, and it costs one object that the mod owns outright.
    //
    // Drawn first so it sits behind the text, and never in the way of a click.
    private static Image CreatePlate(Transform parent, string name)
    {
        var go = new GameObject(name);
        RectTransform rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.SetAsFirstSibling();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        Image image = go.AddComponent<Image>();
        image.sprite = UiSolid.Sprite;
        image.color = new Color(0.04f, 0.04f, 0.06f, 0.72f);
        image.raycastTarget = false;
        return image;
    }

    // Sized to the words rather than to the box, so a short sentence does not carry a banner the
    // width of the screen behind it.
    private static void FitPlate(Image plate, TextMeshProUGUI text)
    {
        if (plate == null || text == null) return;
        float width = Mathf.Min(text.preferredWidth, text.rectTransform.rect.width);
        if (width <= 0f) return;
        plate.rectTransform.sizeDelta = new Vector2(width + PlatePaddingX, text.fontSize + PlatePaddingY);
    }

    // There used to be a second line here, saying that winning this fight would not charge the
    // Rift Seal. It is gone, by owner ruling from play on 2026-08-18, and the reasoning is worth
    // keeping so nobody rebuilds it.
    //
    // The mod already had a way to say exactly that : the red mark on an icon, which everywhere
    // else means "this is not paying, and rearranging fixes it". A Seal on a Hero whose classes
    // are all charged is precisely that, so it was being said twice, in two languages, in two
    // places, and the icon the player was actually looking at was the one saying nothing. The mark
    // is now the only indicator, and AnchorWatch is what decides it.

    private void CloneToggles()
    {
        // Take the button to clone from the speed controller's own references, so it is always
        // the same one. Picking whichever a scene search happened to return first would make the
        // mod's buttons depend on an order nothing guarantees.
        SpeedToggleView source = null;
        SpeedToggleView autoView = null;
        BattleSpeedController controller = RuntimeDiscovery.FindLive<BattleSpeedController>();
        if (controller != null)
        {
            var speedViews = RuntimeDiscovery.ReadField<Il2CppReferenceArray<SpeedToggleView>>(controller, "_speedViews");
            if (speedViews != null && speedViews.Length > 0) source = speedViews[0];
            autoView = RuntimeDiscovery.ReadField<SpeedToggleView>(controller, "_autoView");
        }
        if (source == null)
        {
            var all = RuntimeDiscovery.FindAll<SpeedToggleView>();
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].gameObject.scene.IsValid()) { source = all[i]; break; }
        }
        if (source == null) throw new InvalidOperationException("live SpeedToggleView unavailable");

        // Left to right : arrow origin, opening preview, see-through units, then
        // the game's own auto and speed buttons. Building them in that order and slotting each one
        // in ahead of the auto button grows the row leftward, so the game's buttons never move.
        // Transparency sits immediately after the preview because it only means anything while the
        // preview is on : the two read as a control and its modifier.
        int fallbackIndex = 0;
        _originToggle = CloneToggle(source, autoView, ref fallbackIndex,
            "GuildrunTargetingMod.ArrowOriginToggle", MakeGhostIcon(), OnOriginChanged);
        _previewToggle = CloneToggle(source, autoView, ref fallbackIndex,
            "GuildrunTargetingMod.OpeningPreviewToggle", _previewIcon, OnPreviewChanged);
        _transparencyToggle = CloneToggle(source, autoView, ref fallbackIndex,
            "GuildrunTargetingMod.TransparencyToggle", MakeTransparencyIcon(), OnTransparencyChanged);
        ToggleClonePath = _previewToggle.ClonePath;
    }

    private ModToggle CloneToggle(SpeedToggleView source, SpeedToggleView autoView, ref int fallbackIndex,
        string name, Sprite icon, Action<bool> handler)
    {
        // Clone the whole control, so its art, its press animation and its click sound come along
        // and the mod's buttons feel like the game's. Only what happens on click is replaced.
        GameObject clone = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent);
        clone.name = name;
        SpeedToggleView view = clone.GetComponent<SpeedToggleView>();
        Toggle toggle = view?.Toggle;
        if (toggle == null) { UnityEngine.Object.Destroy(clone); throw new InvalidOperationException("cloned SpeedToggleView has no Toggle"); }
        toggle.onValueChanged.RemoveAllListeners();
        toggle.group = null;
        toggle.SetIsOnWithoutNotify(false);
        // Kept on the toggle below. Nothing on the managed side would otherwise hold this, and
        // the garbage collector cannot see that the game is holding it.
        UnityAction<bool> action = DelegateSupport.ConvertDelegate<UnityAction<bool>>(handler);
        toggle.onValueChanged.AddListener(action);
        view.SetActiveIndicator(false);
        if (autoView != null && autoView.transform.parent == clone.transform.parent)
            clone.transform.SetSiblingIndex(autoView.transform.GetSiblingIndex());
        else
            clone.transform.SetSiblingIndex(fallbackIndex++);
        HideClonedGlyphs(view, toggle, clone);
        NeutralizeClonedSwitchVfx(view, clone);
        if (icon != null)
        {
            GameObject iconGo = CreateRectGameObject(name + ".Icon");
            iconGo.transform.SetParent(clone.transform, false);
            Image image = iconGo.AddComponent<Image>();
            image.sprite = icon; // one of the game's own sprites, or one drawn in code. Nothing bundled.
            image.color = _textColor;
            image.raycastTarget = false;
            image.preserveAspect = true;
            RectTransform iconRect = image.rectTransform;
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.anchoredPosition = Vector2.zero;
            iconRect.sizeDelta = new Vector2(26f, 26f);
        }
        return new ModToggle
        {
            View = view,
            Toggle = toggle,
            Action = action,
            ClonePath = RuntimeDiscovery.HierarchyPath(clone.transform)
        };
    }

    // The cloned button brings the speed control's own chevron with it, and it showed through
    // behind the mod's icon. Switch off every image the button does not structurally need : keep
    // its background, its on indicator and the images its press animation drives. What was hidden
    // is logged by name, so the next change to the look does not have to guess.
    private static void HideClonedGlyphs(SpeedToggleView view, Toggle toggle, GameObject clone)
    {
        Image background = RuntimeDiscovery.ReadField<Image>(view, "_backgroundImage");
        GameObject onIndicator = RuntimeDiscovery.ReadField<GameObject>(view, "_onIndicator");
        Graphic targetGraphic = toggle.targetGraphic;
        Graphic toggleGraphic = toggle.graphic;
        Image[] images = clone.GetComponentsInChildren<Image>(true);
        string hidden = null;
        for (int i = 0; i < images.Length; i++)
        {
            Image image = images[i];
            if (image == null || image == background) continue;
            if (targetGraphic != null && image.Pointer == targetGraphic.Pointer) continue;
            if (toggleGraphic != null && image.Pointer == toggleGraphic.Pointer) continue;
            if (onIndicator != null && image.transform.IsChildOf(onIndicator.transform)) continue;
            image.enabled = false;
            hidden = hidden == null ? image.name : hidden + ", " + image.name;
        }
        if (hidden != null)
            MelonLogger.Msg("[TargetingMod] " + clone.name + ": hid cloned glyph image(s): " + hidden);
    }

    private void OnPreviewChanged(bool value)
    {
        ApplyToggleFeedback(_previewToggle, value);
        _previewChanged(value);
    }

    // The arrow origin and the direction marks are settings, not per-battle state. Unlike the
    // preview, which starts off in every battle, they are remembered, so a click writes straight
    // through to the settings file.
    private void OnOriginChanged(bool value)
    {
        ApplyToggleFeedback(_originToggle, value);
        _arrowsFromGhosts.Value = value;
        MelonPreferences.Save();
    }


    private void OnTransparencyChanged(bool value)
    {
        ApplyToggleFeedback(_transparencyToggle, value);
        _transparentUnits.Value = value;
        MelonPreferences.Save();
    }

    // The button's sparkle is deliberately not played. It is a particle effect that bakes itself
    // into the interface's coordinates, with state that does not survive being copied, so on the
    // mod's clones it came out as a flash across a quarter of the screen on every click. The
    // game's own speed buttons keep their sparkle. The clones keep the press animation and the
    // click sound, which are separate, and lose only the broken flash.
    private static void ApplyToggleFeedback(ModToggle toggle, bool value)
    {
        toggle.View.SetActiveIndicator(value);
    }

    // The same fix again, at the source. Not playing the effect is not enough on its own : it
    // could still play itself on waking, on being switched back on, or from some path in the
    // game that gets hold of the clone. The button's own list of effects is dealt with first,
    // then everything else on the clone, in case the list does not name them all.
    private static void NeutralizeClonedSwitchVfx(SpeedToggleView view, GameObject clone)
    {
        var vfx = RuntimeDiscovery.ReadField<Il2CppReferenceArray<UIParticle>>(view, "_switchVfx");
        if (vfx != null)
            for (int i = 0; i < vfx.Length; i++)
            {
                UIParticle particle = vfx[i];
                if (particle == null) continue;
                particle.enabled = false;
                if (particle.gameObject != clone) particle.gameObject.SetActive(false);
            }
        ParticleSystem[] systems = clone.GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < systems.Length; i++)
        {
            ParticleSystem system = systems[i];
            if (system == null) continue;
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (system.gameObject != clone) system.gameObject.SetActive(false);
        }
    }

    private static void SetToggleSilently(ModToggle toggle, bool value)
    {
        if (toggle == null) return;
        try
        {
            if (toggle.Toggle != null) toggle.Toggle.SetIsOnWithoutNotify(value);
            if (toggle.View != null) toggle.View.SetActiveIndicator(value);
        }
        catch { /* same reason as SetToggleVisible : the scene took the button with it. */ }
    }

    private void ShowToggles(bool visible)
    {
        SetToggleVisible(_previewToggle, visible);
        SetToggleVisible(_originToggle, visible);
        SetToggleVisible(_transparencyToggle, visible);
    }

    // A button's game object belongs to the battle scene, so by the time placement ends the game
    // can already have destroyed it. Checking the wrapper for null does not catch that : the
    // wrapper is still there, and it is reaching through it that throws.
    private static void SetToggleVisible(ModToggle toggle, bool visible)
    {
        if (toggle?.View == null) return;
        try { toggle.View.gameObject.SetActive(visible); }
        catch { /* the scene destroyed it first ; there is nothing left to show or hide. */ }
    }

    // Is this one of the mod's own buttons. Two things need the answer : unit hover has to stop
    // over them, and the button tooltips have to know which one. The pointer usually lands on
    // some piece inside a button, so the walk goes up until it finds one of ours.
    public bool OwnsGameObject(GameObject go)
    {
        if (go == null) return false;
        for (Transform t = go.transform; t != null; t = t.parent)
            if (IsOwnedRoot(t)) return true;
        return false;
    }

    private bool IsOwnedRoot(Transform t)
    {
        if (t == null) return false;
        IntPtr pointer = t.Pointer;
        return SameTransform(_previewToggle?.View?.transform, pointer)
            || SameTransform(_originToggle?.View?.transform, pointer)
            || SameTransform(_transparencyToggle?.View?.transform, pointer)
            ;
    }

    // Compare the native objects, never the wrappers around them. Two wrappers can stand for one
    // object, so comparing wrappers is not a reliable test of whether two things are the same
    // thing. Every identity check in the mod does it this way, for the same reason.
    private static bool SameTransform(Transform candidate, IntPtr pointer) =>
        candidate != null && candidate.Pointer == pointer;

    // Hides the game's overhead unit panels while the preview is on. The preview draws ghosts at
    // head height and the panels, with each unit's name, health, mana, items and status, sit
    // exactly there and bury the picture.
    //
    // Every panel in the battle, heroes and enemies alike, is created under one parent, and
    // nothing else is. So switching off that one parent clears the whole layer at once, and a
    // panel created later, when a hero is swapped in from the bench, arrives under it already
    // hidden. The game only ever switches the individual panels as the battle changes state, and
    // never the parent, so nothing fights over it.
    public void SetUnitHudHidden(bool hidden)
    {
        try
        {
            if (_healthBarsParent == null)
            {
                if (!hidden || _hudParentSearchFailed) return;
                BattleUIController battleUi = RuntimeDiscovery.FindLive<BattleUIController>();
                _healthBarsParent = RuntimeDiscovery.ReadField<RectTransform>(battleUi, "_healthBarsParentTransform");
                if (_healthBarsParent == null)
                {
                    _hudParentSearchFailed = true; // stop looking until the next battle sets up again.
                    MelonLogger.Warning("[TargetingMod] BattleUIController._healthBarsParentTransform unresolved; preview keeps unit HUD visible");
                    return;
                }
            }
            GameObject parent = _healthBarsParent.gameObject;
            if (parent.activeSelf == hidden) parent.SetActive(!hidden);
        }
        catch (Exception e)
        {
            // Never let this take anything else down with it. Stop touching the game's interface
            // instead. A restore that failed because the battle was already being taken apart
            // leaves nothing hidden anyway.
            _hudParentSearchFailed = true;
            _healthBarsParent = null;
            MelonLogger.Warning("[TargetingMod] unit HUD hide/restore failed: " + e.Message);
        }
    }

    // The battle's tooltip controller specifically, not just any of them. The run's controller is
    // a kind of application controller, so both are alive at once and a plain search returns
    // whichever one comes back first. Driving the wrong one fills in a tooltip that nothing in
    // this scene has a position for : it lands at the origin of the world, which is the middle of
    // the screen, and the freeze applied below then stops the game from ever clearing it. An
    // empty black panel parked mid screen is exactly what that looked like.
    private static AppTooltipController FindTooltipController()
    {
        GameRunTooltipController gameRun = RuntimeDiscovery.FindLive<GameRunTooltipController>();
        AppTooltipController resolved = gameRun != null
            ? gameRun.TryCast<AppTooltipController>()
            : RuntimeDiscovery.FindLive<AppTooltipController>();
        if (resolved != null)
            MelonLogger.Msg("[TargetingMod] tooltip controller: " + resolved.gameObject.name +
                            (gameRun != null ? " (GameRun scope)" : " (application scope fallback)"));
        return resolved;
    }

    private void TryCreateNativeTooltip()
    {
        try
        {
            _tooltipController = FindTooltipController();
            if (_tooltipController == null) throw new InvalidOperationException("AppTooltipController unavailable");
            // The controller's own tooltip. Needed twice over : to check afterwards that
            // something really appeared, and to put the anchor in the same coordinates the
            // controller positions the tooltip in.
            _tooltipView = RuntimeDiscovery.ReadField<TooltipView>(_tooltipController, "_tooltipView");
            if (_tooltipView == null) throw new MissingFieldException("AppTooltipController._tooltipView");

            // The anchor is not optional. For as long as anything is showing a tooltip, the
            // controller re-reads that thing's anchor every single frame, whatever else is
            // going on. Something with no usable anchor resolves to the origin of the world,
            // which is the middle of the screen, and it is put back there every frame while the
            // freeze below stops the game clearing it. So the mod brings an anchor of its own and
            // moves it to the cursor, which turns that same per-frame work into a tooltip that
            // follows the pointer exactly like the game's do.
            Transform tooltipParent = _tooltipView.transform.parent;
            if (tooltipParent == null) throw new InvalidOperationException("tooltip view has no parent to anchor within");

            GameObject anchorGo = CreateRectGameObject("GuildrunTargetingMod.TooltipAnchor");
            anchorGo.transform.SetParent(tooltipParent, false);
            _tooltipAnchorRoot = anchorGo;
            TooltipAnchor anchor = anchorGo.AddComponent<TooltipAnchor>();
            _tooltipAnchor = anchor;

            GameObject pointGo = CreateRectGameObject("GuildrunTargetingMod.TooltipAnchorPoint");
            pointGo.transform.SetParent(anchorGo.transform, false);
            // Ask the object for the component, never cast to it. A wrapper is typed as what it
            // was declared as, so the cast throws even though the object really is one. The same
            // mistake once built ghosts out of nothing ; here it disabled the whole tooltip.
            _tooltipAnchorPoint = pointGo.GetComponent<RectTransform>();
            if (_tooltipAnchorPoint == null) throw new InvalidOperationException("anchor point RectTransform unavailable");
            _tooltipAnchorPoint.sizeDelta = Vector2.zero;

            // The tooltip's bottom edge sits on the anchor point, so it opens upward and never
            // covers the thing being pointed at.
            if (!RuntimeDiscovery.WriteField(anchor, "_leftAnchorRect", _tooltipAnchorPoint) ||
                !RuntimeDiscovery.WriteField(anchor, "_leftPivotType", _tooltipPivot))
                throw new MissingFieldException("TooltipAnchor serialized fields unavailable");

            GameObject target = new GameObject("GuildrunTargetingMod.TooltipRaycastTarget");
            target.transform.SetParent(anchorGo.transform, false); // so the game finds our anchor.
            _tooltipTarget = target.AddComponent<TooltipRaycastTarget>();
            _tooltipSource = new SimpleTooltipSource();
            _tooltipTarget.SetSource(_tooltipSource.TryCast<ITooltipSource>());
        }
        catch (Exception e)
        {
            _tooltipUnavailable = true;
            MelonLogger.Warning("[TargetingMod] tooltip unavailable: " + e.Message);
        }
    }

    public void EnterPlacement()
    {
        if (!Resolve(_previewIcon)) return;
        // The preview starts off in every battle by default, because a poll of players preferred
        // meeting the ordinary board first. A playtester who lives in the preview asked to keep
        // it on instead, so the default stands and the setting decides. The display toggles are
        // settings either way and come back the way the player left them.
        bool preview = _previewStartsOn.Value;
        SetToggleSilently(_previewToggle, preview);
        SetToggleSilently(_originToggle, _arrowsFromGhosts.Value);
        SetToggleSilently(_transparencyToggle, _transparentUnits.Value);
        ShowToggles(true);
        _previewChanged(preview);
        _lastTooltipKey = null;
    }

    // This class no longer reads the prediction at all, which is why it is not passed one. A
    // parameter nothing reads misstates what a piece of code actually depends on.
    public void Update(string hovered, ModNotice notice, bool stillMoving, bool dragging,
        GameObject ownUiUnderPointer)
    {
        if (!_resolved || !_capabilities.NativeUi) return;
        // The buttons stay put while a hero is held. Hiding them made the whole row of speed
        // buttons shift sideways every time one was picked up. Only the status line and the
        // tooltip react to dragging.
        PollShortcuts();
        bool visible = !dragging;
        // "Still moving" says the picture is not final, and hover draws that same picture, so it
        // belongs to both. Showing it only with the preview on meant an unfinished board could be
        // read as settled by anyone hovering.
        if (visible) UpdateNotice(notice, stillMoving && (PreviewEnabled || !string.IsNullOrEmpty(hovered)));
        else _notice.gameObject.SetActive(false);

        // A button under the pointer wins. Unit hover is already suppressed there, and explaining
        // the button being pointed at is the whole reason these tooltips exist.
        string key = null, title = null, body = null;
        _tooltipAnchorTarget = null; // follows the cursor unless a button claims it below.
        if (visible && TryGetToggleTooltip(ownUiUnderPointer, out string toggleKey, out title, out body))
            key = toggleKey;
        // Hovering a unit deliberately shows nothing : the board already draws the answer.

        if (key == null)
        {
            HideTooltip();
            return;
        }
        // Filled in once per tooltip, not once per frame. Showing one forces the game to lay it
        // out again, and the pointer resting on a button would do that sixty times a second.
        if (!string.Equals(key, _lastTooltipKey, StringComparison.Ordinal))
        {
            _lastTooltipKey = key;
            ShowTooltip(title, body);
        }
        else UpdateTooltipAnchor(); // same tooltip, still following the cursor.
    }

    // A shortcut flips the button itself rather than doing the work directly, so a key press and
    // a click follow the same path through the animation, the saved setting and the renderer, and
    // the two can never drift apart. They only work during placement, which is the only time any
    // of this exists.
    //
    // This does watch for the frame a key goes down, which the drag tracking deliberately refuses
    // to do. The difference is real. A drag is a state, so missing one frame loses the whole
    // gesture, which is a defect that shipped once. A key press is not a state being tracked, it
    // is the event itself, and a dropped frame costs one press the player simply makes again. Do
    // not "fix" this into asking whether the key is down, which would flip the button on every
    // frame it is held.
    private void PollShortcuts()
    {
        if (!_resolved || !_capabilities.NativeUi) return;
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;
        try
        {
            FlipOnKeyPress(_previewToggle, keyboard, ParseKey(_previewKey, Key.P));
            FlipOnKeyPress(_originToggle, keyboard, ParseKey(_originKey, Key.F));
            // T for transparency, and it stays in place on the common layouts considered above.
            FlipOnKeyPress(_transparencyToggle, keyboard, ParseKey(_transparencyKey, Key.T));
        }
        catch (Exception e)
        {
            // Losing the shortcuts must never take the buttons or the renderer with it, so this
            // reports and stops rather than throwing on into the caller.
            _capabilities.DisableNativeUi("keyboard shortcut polling failed: " + e.Message);
        }
    }

    private static void FlipOnKeyPress(ModToggle toggle, Keyboard keyboard, Key key)
    {
        if (toggle?.Toggle == null || key == Key.None) return;
        KeyControl control = keyboard[key];
        if (control == null || !control.wasPressedThisFrame) return;
        toggle.Toggle.isOn = !toggle.Toggle.isOn;
    }

    // Which of the three buttons the pointer is on, and what its tooltip says. The pointer can
    // land on any piece inside a button, so the walk goes up until it finds one of ours.
    private bool TryGetToggleTooltip(GameObject go, out string key, out string title, out string body)
    {
        key = null;
        title = null;
        body = null;
        if (go == null) return false;
        ModToggle matched = null;
        for (Transform t = go.transform; t != null; t = t.parent)
        {
            IntPtr pointer = t.Pointer; // the native object ; see SameTransform.
            if (SameTransform(_previewToggle?.View?.transform, pointer))
            {
                key = "ui:preview"; matched = _previewToggle; body = PreviewTooltipBody;
                title = WithShortcut(PreviewTooltipTitle, KeyLabel(ParseKey(_previewKey, Key.P)));
                break;
            }
            if (SameTransform(_originToggle?.View?.transform, pointer))
            {
                key = "ui:origin"; matched = _originToggle; body = OriginTooltipBody;
                title = WithShortcut(OriginTooltipTitle, KeyLabel(ParseKey(_originKey, Key.F)));
                break;
            }
            if (SameTransform(_transparencyToggle?.View?.transform, pointer))
            {
                key = "ui:transparency"; matched = _transparencyToggle; body = TransparencyTooltipBody;
                title = WithShortcut(TransparencyTooltipTitle, KeyLabel(ParseKey(_transparencyKey, Key.T)));
                break;
            }
        }
        if (key == null) return false;
        // The button the tooltip hangs from, so it opens under this exact one.
        _tooltipAnchorTarget = matched?.View != null ? matched.View.transform.TryCast<RectTransform>() : null;
        if (!_loggedToggleTooltip)
        {
            // One line, once per session. It is proof in the log that the whole chain fired, so a
            // report that the tooltips do not appear can be answered without a play session.
            _loggedToggleTooltip = true;
            MelonLogger.Msg("[TargetingMod] toggle tooltip active (" + key + ")");
        }
        return true;
    }

    // The game's own tooltip and nothing else : the same object it uses for its own, so the
    // styling, the position and the lifetime are all the game's.
    private const float TooltipCursorGap = 24f;
    private const float TooltipButtonGap = 6f;

    // Moves the point the controller re-reads every frame.
    //   over a button : hangs from that button's bottom edge, so the tooltip sits just under it
    //                   instead of covering the thing it is explaining, and stays there.
    //   anywhere else : just above the cursor, following the pointer.
    private void UpdateTooltipAnchor()
    {
        if (_tooltipAnchorPoint == null) return;
        Transform parentTransform = _tooltipAnchorPoint.parent;
        RectTransform parent = parentTransform != null ? parentTransform.TryCast<RectTransform>() : null;
        if (parent == null) return; // ask, never cast ; see where the anchor point is created.
        Canvas canvas = _tooltipAnchorPoint.GetComponentInParent<Canvas>();
        Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;

        // The gaps are applied in screen pixels and converted back afterwards, so each one is a
        // fixed distance on screen rather than some number of world units that means nothing.
        Vector2 screenPoint;
        if (_tooltipAnchorTarget != null)
        {
            // This has to be a native array. A plain managed one is copied on the way in only, so
            // the engine fills a copy that is thrown away and every corner comes back zero. The
            // same silent loss once left hover dead for a whole round of work.
            _tooltipAnchorTarget.GetWorldCorners(_worldCorners);
            Vector3 bottomCenter = (_worldCorners[0] + _worldCorners[3]) * 0.5f;
            Canvas targetCanvas = _tooltipAnchorTarget.GetComponentInParent<Canvas>();
            Camera targetCamera = targetCanvas != null && targetCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? targetCanvas.worldCamera
                : null;
            screenPoint = RectTransformUtility.WorldToScreenPoint(targetCamera, bottomCenter) - new Vector2(0f, TooltipButtonGap);
            SetAnchorPivot(TooltipPivotType.TopCenter);
        }
        else
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) return;
            screenPoint = mouse.position.ReadValue() + new Vector2(0f, TooltipCursorGap);
            SetAnchorPivot(TooltipPivotType.BottomCenter);
        }

        if (RectTransformUtility.ScreenPointToWorldPointInRectangle(parent, screenPoint, camera, out Vector3 world))
            _tooltipAnchorPoint.position = world;
    }

    // Decides which edge of the tooltip lands on the anchor point. Written only when it really
    // changes : this reaches a field the game never means to expose, not a normal property.
    private void SetAnchorPivot(TooltipPivotType pivot)
    {
        if (_tooltipAnchor == null || _tooltipPivot == pivot) return;
        if (RuntimeDiscovery.WriteField(_tooltipAnchor, "_leftPivotType", pivot)) _tooltipPivot = pivot;
    }

    private void ShowTooltip(string title, string description)
    {
        if (_tooltipUnavailable) return;
        try
        {
            UpdateTooltipAnchor();
            _tooltipSource.SetData(title, string.Empty, description);
            // Stop the controller looking for its own tooltip while ours is up. It goes back to
            // normal the moment we hide, which is what HideTooltip guarantees.
            _tooltipController.FreezeRaycast = true;
            _tooltipController.ShowTooltip(_tooltipTarget);
            _tooltipShownByUs = true; // from here, and only from here, hiding it is ours to do.

            // Check, never assume. The call returning is not proof anything appeared : clearing
            // the tooltip raises a flag, and showing it returns immediately while that flag is
            // up, so the whole sequence can finish quietly having drawn nothing. Say so, rather
            // than leaving a report that nothing appears with no way to answer it.
            if (_tooltipView == null)
            {
                if (!_loggedUnverifiableTooltip)
                {
                    _loggedUnverifiableTooltip = true;
                    MelonLogger.Warning("[TargetingMod] tooltip view field unavailable; the tooltip cannot be verified");
                }
                return;
            }
            if (_tooltipView.gameObject.activeInHierarchy || _loggedTooltipDidNotRender) return;
            _loggedTooltipDidNotRender = true;
            MelonLogger.Warning("[TargetingMod] the game's tooltip accepted the call but did not render");
        }
        catch (Exception e)
        {
            _tooltipUnavailable = true;
            // The controller itself may be what just failed, so releasing it must not be able to
            // throw a second time out of here. Either way, hand the tooltip back to the game.
            _tooltipShownByUs = false;
            try { if (_tooltipController != null) _tooltipController.FreezeRaycast = false; } catch { }
            MelonLogger.Warning("[TargetingMod] tooltip failed at runtime, disabled for this session: " + e.Message);
        }
    }

    // Only ever touches a tooltip the mod itself put up. Doing this unconditionally made the
    // game's own tooltips flicker and disappear, because on every frame the mod had nothing to
    // show it was still hiding the shared tooltip out from under the game. When the mod has none
    // of its own on screen it has to be invisible to the game's.
    //
    // This can run at any time, including after the scene it belongs to is gone, so each step
    // stands on its own. The freeze is released first, so even a failed hide leaves the game back
    // in charge of its own tooltips.
    private void HideTooltip()
    {
        _lastTooltipKey = null;
        if (!_tooltipShownByUs) return;
        _tooltipShownByUs = false;
        try { if (_tooltipController != null) _tooltipController.FreezeRaycast = false; } catch { }
        try { if (_tooltipView != null) _tooltipView.Hide(); } catch { }
    }

    private void UpdateNotice(ModNotice notice, bool stillMoving)
    {
        string text = NoticeText(notice) ?? (stillMoving ? StillMovingText : null);
        _notice.text = text ?? string.Empty;
        bool showing = !string.IsNullOrEmpty(text);
        _notice.gameObject.SetActive(showing);
        if (showing) FitPlate(_noticePlate, _notice);
    }

    private static GameObject CreateRectGameObject(string name)
    {
        var types = new Il2CppReferenceArray<Il2CppSystem.Type>(1);
        types[0] = Il2CppType.Of<RectTransform>();
        return new GameObject(name, types);
    }

    // Two of the three button icons are drawn here, in code. The mod bundles no image, and the
    // game has no sprite that says "ghost" or "chevron in the middle of a line", so these are
    // white shapes tinted through the same colour the borrowed preview arrow uses. Each pixel is
    // sampled four times, which is what keeps the edges clean at the size they are drawn.

    private Sprite MakeGhostIcon()
    {
        return BakeIcon("GuildrunTargetingMod.GhostIcon", (x, y) =>
        {
            // Dome head + straight flanks, three cosine scallops on the hem, two punched eyes.
            float body = y >= 36f
                ? 20f - Mathf.Sqrt((x - 32f) * (x - 32f) + (y - 36f) * (y - 36f))
                : Mathf.Min(x - 12f, 52f - x);
            float hem = y - (10f + 6f * (0.5f + 0.5f * Mathf.Cos((x - 12f) / 40f * 6f * Mathf.PI)));
            float eyeL = Mathf.Sqrt((x - 25f) * (x - 25f) + (y - 40f) * (y - 40f)) - 3.5f;
            float eyeR = Mathf.Sqrt((x - 39f) * (x - 39f) + (y - 40f) * (y - 40f)) - 3.5f;
            return Mathf.Min(Mathf.Min(body, hem), Mathf.Min(eyeL, eyeR));
        });
    }

    private Sprite MakeTransparencyIcon()
    {
        return BakeIcon("GuildrunTargetingMod.TransparencyIcon", (x, y) =>
        {
            // A disc with one half filled : the way opacity is drawn everywhere, and legible at
            // 26 pixels where a checkerboard would turn to mush.
            float toCentre = Mathf.Sqrt((x - 32f) * (x - 32f) + (y - 32f) * (y - 32f));
            float ring = 2f - Mathf.Abs(toCentre - 20f);
            float filledHalf = Mathf.Min(20f - toCentre, 32f - x);
            return Mathf.Max(ring, filledHalf);
        });
    }


    private Sprite BakeIcon(string name, Func<float, float, float> distanceInside)
    {
        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            name = name,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 2
        };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float alpha = 0.25f * (Mathf.Clamp01(distanceInside(x + 0.25f, y + 0.25f))
                    + Mathf.Clamp01(distanceInside(x + 0.75f, y + 0.25f))
                    + Mathf.Clamp01(distanceInside(x + 0.25f, y + 0.75f))
                    + Mathf.Clamp01(distanceInside(x + 0.75f, y + 0.75f)));
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        texture.Apply(true, false);
        Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        sprite.name = name;
        _ownedIconAssets.Add(texture);
        _ownedIconAssets.Add(sprite);
        return sprite;
    }

    public void LeavePlacement()
    {
        HideTooltip();
        SetUnitHudHidden(false); // the game's own panels are back before the fight is on screen.
        SetToggleSilently(_previewToggle, false);
        ShowToggles(false);
        try { if (_notice != null) _notice.gameObject.SetActive(false); }
        catch { /* the notice went with the scene. */ }
        _previewChanged(false);
    }

    public void Destroy()
    {
        HideTooltip();
        SetUnitHudHidden(false);
        DestroyToggle(ref _previewToggle);
        DestroyToggle(ref _originToggle);
        DestroyToggle(ref _transparencyToggle);
        for (int i = 0; i < _ownedIconAssets.Count; i++)
            if (_ownedIconAssets[i] != null) UnityEngine.Object.Destroy(_ownedIconAssets[i]);
        _ownedIconAssets.Clear();
        DestroyObject(_notice);
        DestroyObject(_tooltipTarget);
        if (_tooltipAnchorRoot != null) UnityEngine.Object.Destroy(_tooltipAnchorRoot);
        _notice = null;
        _tooltipView = null;
        _tooltipAnchorPoint = null;
        _tooltipAnchorRoot = null;
        _tooltipAnchor = null;
        _tooltipAnchorTarget = null;
        _tooltipUnavailable = false;
        _tooltipShownByUs = false;
        _tooltipTarget = null;
        _tooltipController = null;
        _tooltipSource = null;
        _placementRoot = null;
        _font = null;
        _previewIcon = null;
        _healthBarsParent = null;
        _hudParentSearchFailed = false;
        _textColorResolved = false;
        _resolved = false;
    }

    private static void DestroyToggle(ref ModToggle toggle)
    {
        if (toggle == null) return;
        try { if (toggle.Toggle != null) toggle.Toggle.onValueChanged.RemoveAllListeners(); }
        catch { /* the scene already took the button ; its listeners went with it. */ }
        DestroyObject(toggle.View);
        toggle = null;
    }

    // Destroying something Unity has already destroyed is harmless. Reaching through a dead
    // component to find its game object first is not : that throws, exactly the way the buttons
    // did on the way out of placement. Every teardown that has to do it goes through here.
    private static void DestroyObject(Component component)
    {
        if (component == null) return;
        try { UnityEngine.Object.Destroy(component.gameObject); }
        catch { /* already gone, which is the outcome this wanted anyway. */ }
    }

    private sealed class ModToggle
    {
        public SpeedToggleView View;
        public Toggle Toggle;
        public UnityAction<bool> Action; // held, or the collector takes it while the game holds it.
        public string ClonePath;
    }
}
