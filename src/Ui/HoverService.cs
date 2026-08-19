using System;
using System.Collections.Generic;
using Il2CppEmber.Scopes.Battle.Board.Controllers;
using Il2CppEmber.Scopes.Battle.Characters;
using Il2CppEmber.Scopes.GameRun.UI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace GuildrunTargetingMod.Ui;

// Works out which unit the pointer is over, and whether the game's own UI is in the way.
//
// Two raycasts per frame, in the order the game does them : the interface first, because a unit
// standing behind a panel is not being pointed at, then the board. Both mirror what the game
// already does, so the mod agrees with it about what is under the cursor.
internal sealed class HoverService
{
    private const float RayDistance = 1000f;
    private const int DiagnosticFrameBudget = 600; // about ten seconds before the one-off report.
    private readonly Capabilities _capabilities;
    // This has to be a native array, not a plain managed one. Handing a managed array to the
    // raycast looks like it works and compiles, but the conversion copies it on the way in only :
    // the engine then fills a copy that is thrown away, and not one hit ever comes back. That
    // silent loss cost a whole round of work with hover simply dead.
    private readonly Il2CppStructArray<RaycastHit> _hits = new(16);
    private readonly Il2CppSystem.Collections.Generic.List<RaycastResult> _uiHits = new();
    // Null values remember colliders that were walked and proved not to belong to a unit.
    private readonly Dictionary<IntPtr, CharacterViewController> _characterViews = new();
    private readonly Il2CppSystem.Collections.Generic.List<CanvasGroup> _canvasGroups = new();
    private PointerEventData _pointerData;
    private Camera _camera;
    private bool _fallbackDrag;
    private bool _loggedFirstHover;
    private bool _uiViewProbeBroken;
    private string _hovered;
    private int _diagFrames;
    private int _diagUiBlocked;
    private int _diagPhysicsHits;
    private int _diagCandidateMiss;
    private bool _diagLogged;
    // The last pointer position that was actually raycast, and the unit map version it was cast
    // against. Together they are the whole of what the answer depends on.
    private Vector2 _lastPointer;
    private int _lastViewsVersion = -1;
    private bool _sampled;
    // Whether that cast found a unit. Held so a skipped frame can still tell the fallback drag
    // whether a press landed on a unit, which is the one thing it needs and cannot recompute.
    private bool _lastUnitUnderPointer;
    private float _lastFullCastAt;
    // How long the answer may be trusted without re-casting when nothing appears to have changed.
    private const float FullCastFloorSeconds = 0.25f;

    public string HoveredEntityId => _hovered;
    // A drag signal read from the mouse, kept only as a backstop. The drag tracker reads the
    // same thing from where the heroes are standing and has the final say, for the reason set
    // out in that file. Either one saying yes is enough to treat a hero as being held.
    public bool IsDragging { get; private set; }
    // Whichever of the mod's own buttons the pointer is over. The interface raycast already runs
    // here every frame to decide whether the board is blocked, so the button tooltips read its
    // result instead of casting a second ray for the same answer.
    public GameObject OwnUiUnderPointer { get; private set; }
    public Camera BattleCamera => _camera;
    public bool Resolved => _camera != null;
    public string SampleColliderType { get; private set; }

    /// <summary>
    /// How many frames in a row the pointer has been over a unit the view map does not recognise.
    /// </summary>
    /// <remarks>
    /// Reported from play : after clicking away to another window and back, hovering did nothing
    /// until a hero was picked up and put down. Picking one up is what makes the prediction rebuild,
    /// and the view map is rebuilt with it, which is the tell. The map is keyed on the models that
    /// were on the board when it was last built, and nothing forces it to be rebuilt while the
    /// board itself is unchanged.
    ///
    /// Rather than guess at what invalidates it, this counts the symptom : a model the pointer is
    /// really over, that the map cannot name. Pointing at bare board is not that, so this stays at
    /// zero during ordinary play and the mod can rebuild on it without rebuilding constantly.
    /// </remarks>
    public float UnknownUnitSeconds
    {
        get
        {
            if (_unknownUnitSince < 0f) return 0f;
            try { return Math.Max(0f, Time.realtimeSinceStartup - _unknownUnitSince); }
            catch { return 0f; }
        }
    }

    // When the pointer first settled on a model the map could not name, or negative for "it is not
    // doing that". A TIMESTAMP rather than a count of frames, for two reasons that arrived together.
    // The count meant a different dwell on every machine, which is the defect this cycle is about,
    // and the comment on the threshold that reads it always described a fifth of a SECOND. And the
    // raycast above no longer runs on a frame where the pointer has not moved, so a counter driven
    // by it would simply stop rising while the pointer rested, which is exactly the situation the
    // rebuild exists to escape.
    private float _unknownUnitSince = -1f;
    // Recognises the mod's own buttons. They are clones of the game's speed toggles, so they sit
    // among the game's panels and have to block unit hover the same way those do.
    public Func<GameObject, bool> OwnUiProbe { get; set; }

    public HoverService(Capabilities capabilities) => _capabilities = capabilities;

    public bool Resolve(BoardController board)
    {
        if (_camera != null) return true;
        try
        {
            // The board's own render camera, which is the same one the game hands to its input
            // handling. Anything else, the main camera included, can be a different view of the
            // scene and would put the ray somewhere the player is not pointing.
            _camera = RuntimeDiscovery.ReadField<Camera>(board, "_gameRenderCamera");
            if (_camera == null) return false;
            return true;
        }
        catch (Exception e)
        {
            _capabilities.DisableHover("registered battle camera discovery failed: " + e.Message);
            return false;
        }
    }

    public void Update(UnitViewRegistry views, bool devLog, bool forceRefresh)
    {
        if (!_capabilities.Hover || _camera == null)
        {
            Clear();
            return;
        }
        try
        {
            Mouse mouse = Mouse.current;
            // Device and focus changes can leave the input system without a current mouse for a
            // passing frame. Nothing is broken, so preserve the last state and try again next frame.
            if (mouse == null) return;
            Vector2 pointer = mouse.position.ReadValue();
            _diagFrames++;

            // What is under the cursor is a function of where the cursor is and what is on the
            // board. Neither having moved means both raycasts below would hand back exactly what
            // they handed back last frame, at the cost of a full interface raycast, a physics
            // raycast and a component walk per hit, every frame, forever. This is the single
            // cheapest thing in the mod to stop doing, and a still pointer is most of a placement.
            //
            // The mouse BUTTON is still read on the skipped frames, because pressing without moving
            // is an ordinary way to pick a hero up, and the fallback drag reads a press edge that
            // only exists on the frame it happens.
            int viewsVersion = views != null ? views.Version : 0;
            float now;
            try { now = Time.realtimeSinceStartup; }
            catch { now = 0f; }
            // The unconditional floor, and it is not belt and braces. The two tests above cover the
            // pointer moving and the board changing, which is nearly everything, but not a panel
            // that opens over a stationary cursor: that changes whether the board is blocked while
            // neither the pointer nor the unit map moves, and the arrows would go on being drawn
            // through it. Rather than hunt for a signal for every such case, anything the cheap
            // tests miss is corrected within a quarter of a second. Four casts a second instead of
            // sixty is still most of the saving.
            bool floorDue = now - _lastFullCastAt >= FullCastFloorSeconds;
            if (!forceRefresh && !floorDue && _sampled &&
                pointer == _lastPointer && viewsVersion == _lastViewsVersion)
            {
                UpdateFallbackDrag(_lastUnitUnderPointer);
                MaybeLogDiagnostic(devLog);
                return;
            }
            _lastPointer = pointer;
            _lastViewsVersion = viewsVersion;
            _lastFullCastAt = now;
            _sampled = true;
            OwnUiUnderPointer = null;

            if (PointerBlockedByInteractiveUi(pointer))
            {
                _diagUiBlocked++;
                _hovered = null;
                _lastUnitUnderPointer = false;
                _unknownUnitSince = -1f;
                UpdateFallbackDrag(false);
                MaybeLogDiagnostic(devLog);
                return;
            }
            Ray ray = _camera.ScreenPointToRay(pointer);
            int count = Physics.RaycastNonAlloc(ray, _hits, RayDistance);
            if (count > 0) _diagPhysicsHits++;
            // The hits come back in no particular order, so taking the first usable one can pick
            // a unit standing behind the one under the cursor wherever two of them overlap along
            // the ray. Take the nearest instead.
            CharacterViewController hovered = null;
            float nearest = float.MaxValue;
            bool sawTransform = false;
            // A model the pointer really is over, that the map does not know. That is a much
            // narrower thing than "found no unit" : pointing at bare board finds no unit every
            // frame and is entirely normal. This one means the map has gone stale underneath us.
            bool sawUnknownUnit = false;
            for (int i = 0; i < count; i++)
            {
                Transform transform = _hits[i].transform;
                if (transform == null || !transform.gameObject.activeSelf || _hits[i].distance >= nearest) continue;
                sawTransform = true;
                IntPtr transformPointer = transform.Pointer;
                if (!_characterViews.TryGetValue(transformPointer, out CharacterViewController candidate))
                {
                    candidate = transform.GetComponent<CharacterViewController>();
                    if (candidate == null) candidate = transform.GetComponentInParent<CharacterViewController>();
                    _characterViews[transformPointer] = candidate;
                }
                if (candidate == null) continue;
                if (!views.TryGetId(candidate, out _)) { sawUnknownUnit = true; continue; }
                hovered = candidate;
                nearest = _hits[i].distance;
                SampleColliderType ??= DescribeCollider(_hits[i].collider);
            }
            if (count > 0 && sawTransform && hovered == null) _diagCandidateMiss++;
            if (hovered == null && sawUnknownUnit)
            {
                if (_unknownUnitSince < 0f)
                {
                    try { _unknownUnitSince = Time.realtimeSinceStartup; }
                    catch { _unknownUnitSince = -1f; }
                }
            }
            else _unknownUnitSince = -1f;
            _lastUnitUnderPointer = hovered != null;
            _hovered = hovered != null && views.TryGetId(hovered, out string id) ? id : null;
            if (_hovered != null && !_loggedFirstHover)
            {
                _loggedFirstHover = true;
                MelonLoader.MelonLogger.Msg("[TargetingMod] hover active: first unit hovered (" + SampleColliderType + ")");
            }
            UpdateFallbackDrag(hovered != null);
            MaybeLogDiagnostic(devLog);
        }
        catch (Exception e)
        {
            Clear();
            _capabilities.DisableHover("LateUpdate raycast failed: " + e.Message);
        }
    }

    private bool PointerBlockedByInteractiveUi(Vector2 pointer)
    {
        EventSystem es = EventSystem.current;
        if (es == null) return false;
        _pointerData ??= new PointerEventData(es);
        _pointerData.position = pointer;
        _uiHits.Clear();
        // The same call the game's own input service makes, into a concrete native list rather
        // than anything reached through an interface.
        es.RaycastAll(_pointerData, _uiHits);
        int count = _uiHits.Count;

        // First pass, looking for the mod's own buttons, and it deliberately reads the whole
        // list. The blocking pass below stops at the first hit it recognises as a control, and
        // the mod's buttons are clones of one, sitting among the game's panels, so folding this
        // into that loop meant it often returned before ever reaching them. That is exactly why
        // the button tooltips did not appear. A loop that stops early cannot also be a search.
        if (OwnUiProbe != null)
            for (int i = 0; i < count; i++)
            {
                GameObject candidate = _uiHits[i].gameObject;
                if (candidate == null || !OwnUiProbe(candidate)) continue;
                OwnUiUnderPointer = candidate;
                break;
            }

        // Second pass : the game's own rule for whether the board is blocked. The first real
        // control in the list decides, and it blocks only while it can be interacted with. A
        // full-screen catcher or a decorative panel is not a control and never blocks, which is
        // where an earlier guess, anything with a selectable parent, got it wrong.
        for (int i = 0; i < count; i++)
        {
            GameObject go = _uiHits[i].gameObject;
            if (go == null) continue;
            if (OwnUiProbe != null && OwnUiProbe(go)) return true;
            if (_uiViewProbeBroken) return go.GetComponentInParent<Selectable>() != null;
            try
            {
                Component view = go.GetComponent(Il2CppType.Of<IUiView>());
                if (view == null) continue;
                return IsInteractableMirror(view);
            }
            catch (Exception e)
            {
                // Fall back to the rough rule rather than losing hover altogether.
                _uiViewProbeBroken = true;
                MelonLoader.MelonLogger.Warning("[TargetingMod] IUiView probe unavailable, falling back to Selectable heuristic: " + e.Message);
                return go.GetComponentInParent<Selectable>() != null;
            }
        }
        return false;
    }

    // The game's own test for whether a control can be interacted with, reproduced. A control
    // that is switched off cannot ; otherwise the canvas groups above it decide, with the first
    // one that ignores its parents having the last word. Anything else counts as interactive.
    private bool IsInteractableMirror(Component view)
    {
        Selectable selectable = view.TryCast<Selectable>();
        if (selectable != null && !selectable.interactable) return false;
        bool? flag = null;
        for (Transform t = view.transform; t != null; t = t.parent)
        {
            _canvasGroups.Clear();
            t.GetComponents(_canvasGroups);
            for (int i = 0; i < _canvasGroups.Count; i++)
            {
                flag = (flag ?? true) && _canvasGroups[i].interactable;
                if (_canvasGroups[i].ignoreParentGroups) return flag.Value;
            }
        }
        return flag ?? true;
    }

    private static string DescribeCollider(Collider collider)
    {
        if (collider == null) return "null";
        // Ask the object what it is. The managed wrapper only reports the type it was declared
        // as, which here is always just "Collider".
        try { return collider.GetIl2CppType().Name; }
        catch { return collider.GetType().Name; }
    }

    // If ten seconds of placement go by without a single unit being hovered, say so once, with
    // the counts that tell the three causes apart : the interface swallowing the pointer, the ray
    // hitting nothing, or the ray hitting things that are not units.
    private void MaybeLogDiagnostic(bool devLog)
    {
        if (_diagLogged || _loggedFirstHover || !devLog || _diagFrames < DiagnosticFrameBudget) return;
        _diagLogged = true;
        MelonLoader.MelonLogger.Warning(
            $"[TargetingMod] hover diagnostic: no unit hovered in {_diagFrames} placement frames; " +
            $"uiBlocked={_diagUiBlocked}, framesWithPhysicsHits={_diagPhysicsHits}, hitsWithoutUnit={_diagCandidateMiss}");
    }

    private void UpdateFallbackDrag(bool boardUnitUnderPointer)
    {
        // The game's drag state is not something the mod can find in the scene, so this is the
        // rough version : count it a drag only while the left button is still down after a press
        // that began on a unit. It is the backstop for the tracker that reads the board, which is
        // more reliable and normally answers first.
        Mouse mouse = Mouse.current;
        // Left exactly as it was. This code was already safe with no mouse, and it clears the
        // fallback drag rather than holding it, which is the failing-open direction: a drag that
        // is wrongly still believed to be in progress keeps the board visuals hidden, while one
        // wrongly cleared is corrected by the next frame that has a mouse. Only the throw further
        // up needed removing, and changing this too would have traded a session-long disable for
        // a stuck drag.
        if (mouse != null && mouse.leftButton.wasPressedThisFrame && boardUnitUnderPointer) _fallbackDrag = true;
        if (mouse == null || !mouse.leftButton.isPressed) _fallbackDrag = false;
        IsDragging = _fallbackDrag;
        if (IsDragging) _hovered = null;
    }

    public void Clear()
    {
        _characterViews.Clear();
        _hovered = null;
        _fallbackDrag = false;
        IsDragging = false;
        OwnUiUnderPointer = null;
        // The next Update must cast rather than trust a position sampled before this reset, and
        // _sampled is what says so. Leaving it raised would answer the first frame of the next
        // placement from a board that no longer exists.
        _sampled = false;
        _lastViewsVersion = -1;
        _lastUnitUnderPointer = false;
        _lastFullCastAt = 0f;
        _unknownUnitSince = -1f;
    }
}
