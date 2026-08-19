using System;
using System.Collections.Generic;
using Il2CppEmber.Scopes.Battle.Board.Controllers;
using Il2CppEmber.Scopes.Battle.Characters;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace GuildrunTargetingMod.Ui;

// Everything the mod draws see-through, the ghost copies and the faded real units alike, shares
// one render queue and writes depth. So the order they are drawn in is the only thing deciding
// which one shows through which : whatever is drawn first blocks anything drawn behind it later.
// Each frame these are sorted from far to near and handed their place in that order.
internal interface IDepthRankedBody
{
    Vector3 BodyPosition { get; }
    float RankDistance { get; set; }
    void ApplyBodyOrder(int sortingOrder);
}

// Draws the board picture : ghost copies at the hexes units end up on, white movement lines,
// team-coloured attack arcs, and a tile under each end of them.
//
// Nothing here is a game object the game owns. Lines, arrowheads, tiles and ghosts are pooled and
// reused frame to frame, and everything not drawn this frame is switched off at the end of it.
internal sealed class OverlayRenderer
{
    private const float BoardLift = 0.16f;
    internal const float GhostAlpha = 0.45f; // ghosts and faded stationary units must match here.
    private const int CurveSamples = 18;     // enough that an arc reads smooth at this scale.
    private const float MoveWidth = 0.06f;
    private const float AttackArcWidth = 0.095f;
    private const float UnderlayWidthFactor = 1.55f;
    // The direction chevron at the middle of a line stays smaller than the head at its end, so
    // it never competes with it. Lines shorter than about one and a half hexes get none : over
    // one hex the direction is already obvious and the chevron would sit on top of the head.
    private const float MidHeadScale = 0.7f;
    private const float MidHeadMinLengthFactor = 5f;
    // Three head sizes, in order of how much they matter : an attack head is full size, a
    // mid-line chevron is smaller, and the head on a movement line is the smallest of all.
    private const float MoveHeadScale = 0.55f;
    // Dashes per world unit along an area outline. Close enough that a small footprint still reads
    // as a broken ring rather than as two marks, open enough that a large one does not fill in.
    private const float DashesPerUnit = 3.2f;
    // The strike across a blocked hex, against a movement line's width.
    private const float SlashWidthFactor = 1.45f;
    private static readonly Color UnderlayColor = new(0.05f, 0.05f, 0.07f, 0.42f);
    private static readonly Color HeroMoveColor = new(1f, 1f, 1f, 0.9f);
    private static readonly Color EnemyMoveColor = new(0.76f, 0.76f, 0.78f, 0.9f);
    // The jump a unit is thrown through before the fight starts, as against the ground it walks.
    // One grey, used by both the line and the tile it lands on. It is kept at full strength here
    // and faded at each use, because the two fade by different amounts : the line is deliberately
    // fainter than a walk, while the tile has to match the weight of the team-coloured tiles
    // beside it. Baking the line's fade into the colour made the landing hex the palest thing on
    // the board. The reasoning behind the jump itself is beside TryGetLeapLanding.
    private static readonly Color LeapColor = new(0.82f, 0.85f, 0.92f, 1f);
    private const float LeapLineAlpha = 0.45f;
    private const float LeapWidthFactor = 0.6f;
    // Two hexes, measured in hex spacing rather than in hexes, so it holds at any board scale.
    private const float LeapMinPitches = 1.9f;
    // Sky blue and vermillion, from the Okabe and Ito palette. They stay apart under every common
    // form of colour blindness, and both hold up against the arena's green. Red and green would
    // be the obvious choice and are the one pair to avoid.
    //
    // Colour is never the only thing carrying the meaning : movement is white or grey and always
    // a straight line with a ghost at its end, an attack is always a curved arc with a head.
    internal static readonly Color HeroColor = new Color32(86, 180, 233, 255);   // #56B4E9
    internal static readonly Color EnemyColor = new Color32(213, 94, 0, 255);    // #D55E00

    // A Hero standing where one of its parts wants it, and a Hero standing where one of them does
    // not. Both are the team colour rather than a new pair, because this is not a third kind of
    // thing on the board : it is the same Hero, doing well or not, on the hex the mod already marks.
    //
    // The lit one is the team blue lifted toward white. The blocked one is that blue drained of
    // nearly all of it.
    // Internal because the icon half of the same feature reads them from here. One home : two
    // copies of a colour become two colours the first time one of them is adjusted.
    internal static readonly Color GlowLiveColor = new Color32(158, 220, 255, 255);
    internal static readonly Color GlowBlockedColor = new Color32(150, 154, 162, 255);
    // The strike itself is red, and only the strike : the tile under a hero and the ring around an
    // icon both stay drained.
    //
    // The note here used to say never red, on the grounds that red is the enemy's colour on this
    // board. Seen in play that reasoning did not survive : the drained strike was simply hard to
    // find, and for a warning that is the only failure that counts. The two are further apart than
    // the note assumed anyway. The enemy is vermillion, orange with no blue in it at all, this is a
    // true red, and a strike only ever appears on one of the player's own things and never on an
    // enemy. The shape carries the meaning by itself regardless, which is what keeps it readable
    // for a player who cannot separate the two hues.
    internal static readonly Color GlowBlockedMarkColor = new Color32(229, 56, 59, 255);
    // Every tile is laid down at just over a third of the colour it is handed. A lit tile has to be
    // clearly stronger than the plain one beside it without becoming a solid block of colour, so it
    // asks for a little under twice the usual weight.
    private const float GlowCellAlpha = 1.7f;

    private readonly Capabilities _capabilities;
    private readonly List<WorldLine> _lines = new(64);
    private readonly List<MeshHead> _heads = new(32);
    private readonly List<CellHighlight> _cells = new(20);
    private readonly Dictionary<string, Ghost> _ghosts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hoverMovementDrawn = new(StringComparer.Ordinal);
    private readonly HashSet<Vector3Int> _cellStamps = new();
    private readonly UnitFader _fader = new();
    private readonly List<IDepthRankedBody> _rankedBodies = new(32);
    private static readonly Comparison<IDepthRankedBody> FarToNear =
        (a, b) => b.RankDistance.CompareTo(a.RankDistance);
    private bool _rankFaulted;
    private GameObject _root;
    // These assets do not belong to a battle scene. They are retained until mod deinitialization,
    // while the root and every object parented under it keep the battle lifetime below.
    private static Material _solidMaterial;
    private static Material _headMaterial;
    private static Material _ghostFallback;
    private static Material _dashMaterial;
    private static Texture2D _solidTexture;
    private static Texture2D _dashTexture;
    // One native buffer per outline length, reused. A closed shape is handed to the engine in one
    // call, and a fresh managed array per shape per frame would allocate and copy every time.
    private readonly Dictionary<int, Il2CppStructArray<Vector3>> _outlineBuffers = new();
    private static Mesh _headMesh;
    private float _targetEdgeInset;
    private float _headLength;
    private float _cellPitch;
    private Camera _camera;
    // Everything the last drawn picture was made of. See PictureUnchanged for why this list is a
    // proof obligation rather than a cache.
    private bool _pictureSampled;
    private PredictionResult _lastPrediction;
    private UnitViewRegistry _lastViews;
    private BoardController _lastBoard;
    private int _lastViewsVersion = -1;
    private string _lastHovered;
    private bool _lastHideWorld;
    private bool _lastDragPresent;
    private int _lastGlowVersion = -1;
    private int _lastAoeVersion = -1;
    private bool _lastPreviewEnabled;
    private bool _lastArrowsFromGhosts;
    private bool _lastMidlineHeads;
    private bool _lastTransparent;
    private bool _lastResolved;
    private int _lastCapabilityGeneration = -1;
    private Vector3 _lastCamPos;
    private Quaternion _lastCamRot;
    private Vector3 _camPos;
    private bool _pullActive;
    // Lines and heads are nudged toward the camera before being drawn. Ghosts write depth, so
    // that their own limbs do not show through their chests, and real units are solid, so a melee
    // arc running along the ground between two of them was being hidden by the bodies at both
    // ends. Six units clears anything up to the tallest boss, and never taking more than 40% of
    // the distance to the camera keeps it safe when the camera is close.
    private const float CameraPull = 6f;
    private static Sprite _previewIconSprite;
    private static bool _previewIconSearched;
    private Sprite _heroTileSprite;
    private Sprite _enemyTileSprite;
    private int _frameStamp;
    private int _lineCursor;
    private int _headCursor;
    private int _cellCursor;
    private bool _resolved;
    // Set while a hero is being dragged. The dragged model floats at the cursor and a teammate
    // about to be swapped has not moved at all, so for those two, every position and hex is read
    // from here rather than off the screen. Null the rest of the time.
    private DragSnapshot _drag;

    public string ColorDiagnostic { get; private set; }
    public string GhostShaderProperty { get; private set; } = "unresolved";
    public Sprite PreviewIconSprite => _previewIconSprite;
    public bool Resolved => _resolved;
    public bool PreviewEnabled { get; set; }
    // Where attack arcs start. On, the default, they start at the hexes units end up on, so an
    // arrow lands where the fight will really happen. Off, they start where the units are
    // standing right now, which is how the mod first shipped and can be quicker to attribute.
    public bool ArrowsFromGhosts { get; set; } = true;
    public bool MidlineHeads { get; set; }

    // Whether the real units are faded down. This runs for the whole placement screen, not only
    // while the preview is on : the reason to want it is to read the board through the bodies
    // standing on it, and that is just as true while hovering. Off, they keep their full colour
    // and the board reads as itself with whatever the mod draws laid over it.
    //
    // Nothing else has to change when this is off, and that is worth stating because it looks like
    // it should. The fade also tints a unit that is not going to move in its team colour, so it
    // stands in for the ghost it never gets. But that tint is reinforcement, not the signal :
    // RenderPreview already puts a team-coloured tile under every unit's settled hex whether it
    // moved or not, so "standing on a lit tile" still means "this is where it ends up", and a
    // mover is still told apart by having a ghost and a movement line somewhere else.
    public bool Transparent { get; set; } = true;

    // Asks whether a Hero has a part that cares about where it is standing, and whether the board
    // as arranged is satisfying it. Set by the mod each frame ; null whenever the feature is off or
    // has switched itself off, in which case every tile is drawn the way it always was.
    public Func<string, PartState> HeroGlow { get; set; }

    // The footprint a unit's ability covers, or null when there is nothing honest to draw. Same
    // contract as the glow above : set by the mod, null whenever the feature is off.
    public Func<string, AoeOutline> UnitAoe { get; set; }

    public OverlayRenderer(Capabilities capabilities) => _capabilities = capabilities;

    public bool Resolve(BoardController board)
    {
        if (_resolved) return true;
        if (board == null || board.PlacementGrid == null) return false;
        try
        {
            // The sprite shader tints and blends without being configured. The unlit one ignores
            // a line's colours entirely and is solid by default, which drew every line as a black
            // and white striped texture the first time the mod ran. It is kept only as a fallback,
            // switched to transparency and tinted a different way.
            Shader lineShader = Shader.Find("Sprites/Default");
            Shader ghostShader = Shader.Find("Universal Render Pipeline/Unlit") ?? lineShader;
            if (lineShader == null) lineShader = ghostShader;
            if (lineShader == null) throw new InvalidOperationException("no live unlit shader found");
            // Smooth, mipmapped, soft edged. A tiny hard edged pattern seen at an angle is what
            // made the first lines look pixelated and broken up.
            if (_solidTexture == null) _solidTexture = MakeSoftLineTexture("TargetingSolidLine");
            if (_solidMaterial == null) _solidMaterial = NewLineMaterial(lineShader, _solidTexture, 2990);
            // Broken along its length, so an area outline never reads as one more solid line on a
            // board whose whole language is solid lines meaning movement and attacks.
            if (_dashTexture == null) _dashTexture = MakeDashTexture("TargetingDashLine");
            if (_dashMaterial == null) _dashMaterial = NewLineMaterial(lineShader, _dashTexture, 2989);
            if (_headMaterial == null) _headMaterial = NewLineMaterial(lineShader, null, 2991); // heads draw over the shafts.
            if (_ghostFallback == null)
                _ghostFallback = NewTransparentMaterial(ghostShader, "GuildrunTargetingMod.GhostFallback");
            if (_headMesh == null) _headMesh = BuildHeadMesh();

            ResolveSprites(board);
            // The same camera the hover raycast uses. Losing it is not fatal : the overlay draws
            // exactly as before, without the nudge toward the camera.
            _camera = RuntimeDiscovery.ReadField<Camera>(board, "_gameRenderCamera");
            if (_camera == null)
                MelonLoader.MelonLogger.Warning("[TargetingMod] overlay camera unresolved; depth pull disabled");
            // An attack head lands on the edge of the target's hex, not buried inside the unit
            // standing in the middle of it. Both that inset and the head's size are measured off
            // the distance between two hex centres, so they hold at any board scale.
            float cellPitch = Vector3.Distance(
                board.PlacementGrid.GetCellCenterWorld(Vector3Int.zero),
                board.PlacementGrid.GetCellCenterWorld(Vector3Int.right));
            if (cellPitch < 0.001f) cellPitch = 1f;
            _cellPitch = cellPitch;
            _targetEdgeInset = cellPitch * 0.45f;
            _headLength = cellPitch * 0.30f;

            ColorDiagnostic = "colorblind-safe palette: hero=#56B4E9 skyBlue, enemy=#D55E00 vermillion (Okabe-Ito via NCEAS guide)";
            _root = new GameObject("GuildrunTargetingMod.WorldOverlay");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _resolved = true;
            MelonLoader.MelonLogger.Msg($"[TargetingMod] visuals resolved: {ColorDiagnostic}, cellPitch={cellPitch:F3}");
            return true;
        }
        catch (Exception e)
        {
            _capabilities.DisableOverlay("runtime visual assets failed to resolve: " + e.Message);
            return false;
        }
    }

    private void ResolveSprites(BoardController board)
    {
        Tile ally = RuntimeDiscovery.ReadField<Tile>(board, "_allyTile");
        Tile enemy = RuntimeDiscovery.ReadField<Tile>(board, "_enemySideTile");
        Tile open = RuntimeDiscovery.ReadField<Tile>(board, "_openTile");
        _heroTileSprite = ally != null && ally.sprite != null ? ally.sprite : open?.sprite;
        _enemyTileSprite = enemy != null && enemy.sprite != null ? enemy.sprite : open?.sprite;
        // Only the preview button's icon borrows one of the game's own sprites. The arrowheads
        // are drawn from a mesh instead : the borrowed arrow sprite was small, hollow and never
        // pointed quite the right way.
        if (!_previewIconSearched)
        {
            List<Sprite> sprites = RuntimeDiscovery.FindAll<Sprite>();
            for (int i = 0; i < sprites.Count; i++)
            {
                string name = sprites[i]?.name;
                if (string.IsNullOrEmpty(name)) continue;
                if (name.IndexOf("arrow", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _previewIconSprite = sprites[i];
                    if (name.IndexOf("rank", StringComparison.OrdinalIgnoreCase) >= 0) break;
                }
            }
            _previewIconSearched = true;
        }
        if (_heroTileSprite == null || _enemyTileSprite == null)
            MelonLoader.MelonLogger.Warning("[TargetingMod] BoardController open-tile sprite unresolved; destination highlights disabled");
    }

    /// <summary>
    /// Everything the drawn picture is derived from, so a frame that would draw exactly the same
    /// thing can be skipped instead of redrawn.
    /// </summary>
    /// <remarks>
    /// This list is the whole safety of the skip and it is meant to be read as a proof obligation
    /// rather than as an optimisation. If the renderer ever reads something that is not represented
    /// here, the overlay will hold a stale picture, and a speed change that alters what the mod
    /// draws is a defect rather than a bonus.
    ///
    /// The two version numbers are the subtle ones. The tiles under units are coloured from the
    /// placement marks and the footprints come from the area shapes, and BOTH of those are computed
    /// by other objects on their own schedules. Neither is visible in the arguments to this method,
    /// so without them a redraw would be skipped on the exact frame those answers changed. They are
    /// bumped by their owners whenever the answer could differ, deliberately over-eagerly.
    ///
    /// A drag redraws unconditionally, on the frame it starts and every frame it lasts, because the
    /// snapshot is rebuilt each frame and everything about it moves.
    /// </remarks>
    private bool PictureUnchanged(PredictionResult prediction, UnitViewRegistry views, BoardController board,
        string hovered, bool hideWorld, DragSnapshot drag, int glowVersion, int aoeVersion)
    {
        if (!_pictureSampled) return false;
        if (!ReferenceEquals(prediction, _lastPrediction)) return false;
        if (!ReferenceEquals(views, _lastViews)) return false;
        if (!ReferenceEquals(board, _lastBoard)) return false;
        if ((views != null ? views.Version : 0) != _lastViewsVersion) return false;
        if (!string.Equals(hovered, _lastHovered, StringComparison.Ordinal)) return false;
        if (hideWorld != _lastHideWorld) return false;
        if (drag != null || _lastDragPresent) return false;
        if (glowVersion != _lastGlowVersion || aoeVersion != _lastAoeVersion) return false;
        if (PreviewEnabled != _lastPreviewEnabled) return false;
        if (ArrowsFromGhosts != _lastArrowsFromGhosts) return false;
        if (MidlineHeads != _lastMidlineHeads) return false;
        if (Transparent != _lastTransparent) return false;
        if (_resolved != _lastResolved) return false;
        // A feature going down changes what is drawn and appears in none of the arguments. Without
        // this, a rendering fault that switched the preview off would leave the preview on screen:
        // the fault itself is the only thing that changed, so every other test would say "same
        // picture" and the frame that was supposed to clear it would be skipped.
        if (_capabilities.Generation != _lastCapabilityGeneration) return false;
        // Every line is billboarded to the camera and every translucent body is depth sorted
        // against it, so a camera that has moved is a different picture even when nothing on the
        // board has changed at all.
        try
        {
            if (_camera == null) return false;
            Transform cam = _camera.transform;
            if (cam.position != _lastCamPos || cam.rotation != _lastCamRot) return false;
        }
        catch { return false; }
        return true;
    }

    private void RememberPicture(PredictionResult prediction, UnitViewRegistry views, BoardController board,
        string hovered, bool hideWorld, DragSnapshot drag, int glowVersion, int aoeVersion)
    {
        _lastPrediction = prediction;
        _lastViews = views;
        _lastBoard = board;
        _lastViewsVersion = views != null ? views.Version : 0;
        _lastHovered = hovered;
        _lastHideWorld = hideWorld;
        _lastDragPresent = drag != null;
        _lastGlowVersion = glowVersion;
        _lastAoeVersion = aoeVersion;
        _lastPreviewEnabled = PreviewEnabled;
        _lastArrowsFromGhosts = ArrowsFromGhosts;
        _lastMidlineHeads = MidlineHeads;
        _lastTransparent = Transparent;
        _lastResolved = _resolved;
        try
        {
            if (_camera != null)
            {
                Transform cam = _camera.transform;
                _lastCamPos = cam.position;
                _lastCamRot = cam.rotation;
            }
        }
        catch { /* the camera died ; the next frame will fail the sample test and redraw. */ }
        _pictureSampled = true;
    }

    /// <summary>Forgets the last drawn picture, so the next Render draws unconditionally.</summary>
    /// <remarks>
    /// The held references are dropped as well as the flag. Nothing ever dereferences them, they
    /// are only compared by identity, so this is not a correctness fix : it is so that a placement
    /// that has ended does not leave the mod holding a board and a unit map the game has destroyed.
    /// </remarks>
    internal void InvalidatePicture()
    {
        _pictureSampled = false;
        _lastPrediction = null;
        _lastViews = null;
        _lastBoard = null;
        _lastHovered = null;
        _lastViewsVersion = -1;
        _lastGlowVersion = -1;
        _lastAoeVersion = -1;
    }

    public void Render(PredictionResult prediction, UnitViewRegistry views, BoardController board, string hovered,
        bool hideWorld, DragSnapshot drag, int glowVersion, int aoeVersion)
    {
        // Nothing this picture is made of has moved, so redrawing it would rewrite about a hundred
        // renderer transforms, colours and materials with the values they already hold, and then
        // walk every pooled object to switch off the ones it did not touch. Skipping leaves all of
        // that exactly as it is, which is what "unchanged" means. The frame stamp deliberately does
        // NOT advance: it is what the sweep compares against, and advancing it without drawing
        // would make the sweep hide everything on screen.
        if (PictureUnchanged(prediction, views, board, hovered, hideWorld, drag, glowVersion, aoeVersion)) return;
        // Taken BEFORE drawing, not after, and the difference is a defect that was nearly shipped.
        // Drawing can fault, and a fault disables a feature, which moves the generation. Recording
        // it afterwards would record the state the fault produced, the next frame would compare
        // equal and skip, and the half drawn overlay the fault left behind would stay on screen
        // with nothing ever clearing it. Taken first, a fault always leaves the two disagreeing, so
        // the next frame is forced to redraw.
        _lastCapabilityGeneration = _capabilities.Generation;
        _frameStamp++;
        _lineCursor = 0;
        _headCursor = 0;
        _cellCursor = 0;
        _cellStamps.Clear();
        _pullActive = false;
        try
        {
            if (_camera != null) { _camPos = _camera.transform.position; _pullActive = true; }
        }
        catch { /* the camera died during teardown ; draw without the nudge this frame. */ }
        try
        {
            _drag = drag;
            if (_capabilities.Overlay && _capabilities.Prediction && _resolved && prediction != null && !hideWorld)
            {
                if (PreviewEnabled && _capabilities.Preview)
                    RenderPreview(prediction, views, board);
                else if (!string.IsNullOrEmpty(hovered))
                    RenderHover(prediction, views, board, hovered);
            }
        }
        catch (Exception e)
        {
            if (PreviewEnabled) _capabilities.DisablePreview("preview rendering failed: " + e.Message);
            else _capabilities.DisableOverlay("hover-arrow rendering failed: " + e.Message);
        }
        // The fade follows the toggle and nothing else. Tying it to the prediction would flash
        // every unit back to solid each time the board changed and a new one was being computed.
        // Like the hidden unit panels, it stays on through a drag.
        // See-through is a mode for the whole placement screen, not a detail of the preview. It
        // holds while hovering and while nothing at all is pointed at, because the reason to want
        // it is to read the board through the bodies standing on it, and that reason does not
        // wait for a toggle. It is still gated on the mod having something to say : with the
        // prediction switched off the mod draws nothing, and fading the units then would only
        // make a working game look broken.
        if (Transparent && _capabilities.Prediction && _capabilities.Overlay && _resolved && views != null)
            _fader.Apply(views, prediction, board, _drag, PreviewEnabled && _capabilities.Preview);
        else
            _fader.Restore(); // also the path that puts every material back when the player turns it off.
        // Anything not drawn this frame is switched off now. Cheaper than hiding everything up
        // front and turning back on what is needed, which touched every object twice a frame.
        Sweep();
        SortTranslucentBodies();
        // Recorded last, so what is remembered is a picture that was actually drawn. Everything
        // above this line can throw into its own handler and leave the overlay half written, and
        // remembering before the work would have told the next frame that half written state was
        // final.
        RememberPicture(prediction, views, board, hovered, hideWorld, drag, glowVersion, aoeVersion);
    }

    // Puts every see-through body in the right draw order, far ones first. They all write depth,
    // so whichever is drawn first hides whatever comes after it : with a fixed order, a ghost
    // standing behind a faded unit simply disappeared. Sorting by distance each frame makes the
    // nearer one blend over the farther one in every combination, while each body still hides its
    // own far side. These orders stay below the ones the tiles, lines and heads use, so those
    // always draw last and over the top.
    private void SortTranslucentBodies()
    {
        if (_rankFaulted || !_pullActive) return;
        try
        {
            _rankedBodies.Clear();
            foreach (Ghost ghost in _ghosts.Values)
                if (ghost.Stamp == _frameStamp && ghost.Root.activeSelf) _rankedBodies.Add(ghost);
            _fader.CollectBodies(_rankedBodies);
            if (_rankedBodies.Count == 0) return;
            for (int i = 0; i < _rankedBodies.Count; i++)
            {
                IDepthRankedBody body = _rankedBodies[i];
                body.RankDistance = (body.BodyPosition - _camPos).sqrMagnitude;
            }
            _rankedBodies.Sort(FarToNear);
            for (int i = 0; i < _rankedBodies.Count; i++)
                _rankedBodies[i].ApplyBodyOrder(Mathf.Min(10 + i, 44));
        }
        catch (Exception e)
        {
            _rankFaulted = true; // ordering is polish ; the fixed orders are the safe old behaviour.
            MelonLoader.MelonLogger.Warning("[TargetingMod] translucent-body ranking disabled: " + e.Message);
        }
    }

    // An arc is always coloured for the unit doing the attacking, never for the direction it
    // runs in. Colouring incoming arcs one way and outgoing the other made three arcs pointing
    // at a hovered enemy all read as that enemy's colour, which looks like one enemy attacking
    // three heroes. A unit only ever has one target. The head at the far end says which way it
    // goes.
    private void RenderHover(PredictionResult result, UnitViewRegistry views, BoardController board, string hovered)
    {
        if (!views.TryGetView(hovered, out CharacterViewController from)) return;
        bool hero = views.IsHero(hovered);
        // Marks the unit being pointed at. The game gives no feedback of its own for hovering a
        // unit, and its placement tile is the closest thing it has to saying "this one".
        Vector3Int hoveredCell = UnitCell(board, hovered, from);
        DrawUnitCell(board.PlacementGrid.GetCellCenterWorld(hoveredCell), hovered, hero);
        DrawHoveredAoe(result, board, hovered, hero, hoveredCell);

        if (ArrowsFromGhosts) RenderHoverGhostFrame(result, views, board, hovered);
        else RenderHoverStartFrame(result, views, board, hovered, from, hero, hoveredCell);
    }

    // The hovered unit's ability footprint, on the same origin its arrows use. Pulled out of the
    // two hover paths because it is the same picture for both : only where it is anchored differs,
    // and that is the one toggle it follows.
    private void DrawHoveredAoe(PredictionResult result, BoardController board, string hovered, bool hero, Vector3Int hoveredCell)
    {
        Vector3 anchor = board.PlacementGrid.GetCellCenterWorld(hoveredCell);
        if (ArrowsFromGhosts && result.Settled.TryGetValue(hovered, out SettledEntity settled))
            anchor = CenterOfCell(board, settled.Cell);

        string target = SettledTargetOf(result, hovered);
        Vector3 facing = anchor;
        bool hasFacing = false;
        if (!string.IsNullOrEmpty(target) && result.Settled.TryGetValue(target, out SettledEntity targetState))
        {
            facing = CenterOfCell(board, targetState.Cell);
            hasFacing = true;
        }
        DrawAoe(anchor, hovered, hero, facing, hasFacing);
    }

    private static Vector3 CenterOfCell(BoardController board, Vector2Int cell) =>
        board.PlacementGrid.GetCellCenterWorld(new Vector3Int(cell.x, cell.y, 0));

    // Every arrow in this file comes from here, and here is the settled pairings. Hover and the
    // preview toggle are the same picture at two sizes, and both describe one moment : where each
    // unit stops, and who it is fighting once it gets there.
    //
    // Hover used to take its arrows from the opening picks while drawing the ghosts where units
    // end up, which composed a board the fight never passes through : a unit standing at the hex
    // it finishes on, aiming at the enemy it only chose on the first frame. A hero picks again
    // every time it crosses a hex, so it routinely abandons that first choice before arriving.
    // Across 280 boards, a third of heroes ended up fighting someone other than their first pick,
    // and a seventh of them did so with nothing ambiguous about it at all.
    //
    // Never take an arrow and a position from two different moments.
    private static string SettledTargetOf(PredictionResult result, string id) =>
        result.Settled.TryGetValue(id, out SettledEntity settled) && settled.Alive
            ? settled.TargetPairing
            : null;

    private static bool SettledAlive(PredictionResult result, string id) =>
        !result.Settled.TryGetValue(id, out SettledEntity settled) || settled.Alive;

    // Hover as it ships : the preview picture, narrowed to the units this hover is about. The
    // unit being pointed at, the one it fights once everything settles, and everyone fighting it
    // there. Each gets its ghost and its movement line, and the arcs run between the ghosts, so
    // an arrow lands where the unit will really be standing.
    private void RenderHoverGhostFrame(PredictionResult result, UnitViewRegistry views, BoardController board, string hovered)
    {
        _hoverMovementDrawn.Clear(); // two units fighting each other would draw one line twice.
        DrawInvolvedMovement(result, views, board, hovered);
        string target = SettledTargetOf(result, hovered);
        if (!string.IsNullOrEmpty(target) && SettledAlive(result, target) && views.TryGetView(target, out _))
        {
            DrawInvolvedMovement(result, views, board, target);
            DrawCurved(AnchorCenter(result, views, board, hovered), AnchorCenter(result, views, board, target),
                views.IsHero(hovered) ? HeroColor : EnemyColor, 1f);
        }
        // Nothing points at a unit that is already dead in this picture either.
        if (!SettledAlive(result, hovered)) return;
        foreach (KeyValuePair<string, SettledEntity> pair in result.Settled)
        {
            if (!pair.Value.Alive) continue;
            if (!string.Equals(pair.Value.TargetPairing, hovered, StringComparison.Ordinal) || pair.Key == hovered) continue;
            if (!views.TryGetView(pair.Key, out _)) continue;
            DrawInvolvedMovement(result, views, board, pair.Key);
            DrawCurved(AnchorCenter(result, views, board, pair.Key), AnchorCenter(result, views, board, hovered),
                views.IsHero(pair.Key) ? HeroColor : EnemyColor, 1f);
        }
    }

    // Hover with the arrows starting where the units are standing now. This is how the mod first
    // shipped, kept behind the origin toggle because tracing an arrow back to a unit you can
    // actually see can be quicker to read than tracing it back to a ghost.
    private void RenderHoverStartFrame(PredictionResult result, UnitViewRegistry views, BoardController board,
        string hovered, CharacterViewController from, bool hero, Vector3Int hoveredCell)
    {
        // Where the hovered unit walks to. Movement has to read as movement and not as targeting,
        // so it is a plain white line with a ghost at the end of it.
        if (result.Settled.TryGetValue(hovered, out SettledEntity settledSelf) && settledSelf.Alive)
        {
            Vector3 landing = board.PlacementGrid.GetCellCenterWorld(new Vector3Int(settledSelf.Cell.x, settledSelf.Cell.y, 0));
            DrawMovementFor(result, hovered, from, hero, settledSelf.Cell, landing, board);
        }

        // A tile under each end of the arc. An arc leaving or landing on an unmarked hex reads as
        // floating, and worst of all on long-range attackers. The pairing is still the settled
        // one : this toggle only moves where an arc starts, never who it says is fighting whom.
        string target = SettledTargetOf(result, hovered);
        if (!string.IsNullOrEmpty(target) && SettledAlive(result, target) &&
            views.TryGetView(target, out CharacterViewController to))
        {
            DrawUnitCell(UnitCellCenter(board, target, to), target, views.IsHero(target));
            DrawCurved(UnitBodyPosition(board, hovered, from), UnitCellCenter(board, target, to), hero ? HeroColor : EnemyColor, 1f);
        }

        if (!SettledAlive(result, hovered)) return;
        foreach (KeyValuePair<string, SettledEntity> pair in result.Settled)
        {
            if (!pair.Value.Alive) continue;
            if (!string.Equals(pair.Value.TargetPairing, hovered, StringComparison.Ordinal) || pair.Key == hovered) continue;
            if (views.TryGetView(pair.Key, out CharacterViewController incoming))
            {
                bool attackerHero = views.IsHero(pair.Key);
                DrawUnitCell(UnitCellCenter(board, pair.Key, incoming), pair.Key, attackerHero);
                DrawCurved(UnitBodyPosition(board, pair.Key, incoming), board.PlacementGrid.GetCellCenterWorld(hoveredCell),
                    attackerHero ? HeroColor : EnemyColor, 1f);
            }
        }
    }

    private void DrawInvolvedMovement(PredictionResult result, UnitViewRegistry views, BoardController board, string id)
    {
        if (!_hoverMovementDrawn.Add(id)) return;
        if (!views.TryGetView(id, out CharacterViewController view)) return;
        if (!result.Settled.TryGetValue(id, out SettledEntity settled) || !settled.Alive) return;
        bool hero = views.IsHero(id);
        Vector3 center = board.PlacementGrid.GetCellCenterWorld(new Vector3Int(settled.Cell.x, settled.Cell.y, 0));
        DrawMovementFor(result, id, view, hero, settled.Cell, center, board);
        // A tile even when the unit does not move at all : a long-range arc leaving an unmarked
        // hex read as coming from somewhere else. One tile per hex, so this costs nothing when
        // the unit moves and its landing tile is already there.
        DrawUnitCell(center, id, hero);
    }

    // One unit's walk to where it ends up : a straight white line from where it is standing, with
    // a ghost and a tile on the hex it stops at. Draws nothing if it does not move.
    //
    // A unit thrown across the board before the fight starts gets the jump first, as a fainter and
    // thinner line, then a grey tile on the hex it lands on, then the ordinary line onward from
    // there. The ghost still belongs to the settled hex alone : the grey tile says the unit passed
    // through, the team-coloured one says it stopped.
    private void DrawMovementFor(PredictionResult result, string id, CharacterViewController view, bool hero,
        Vector2Int settledCell, Vector3 settledCenter, BoardController board)
    {
        Vector3Int currentCell = UnitCell(board, id, view);
        bool leaps = TryGetLeapLanding(result, board, currentCell, id, out Vector3 landingCenter);
        if (!leaps && new Vector3Int(settledCell.x, settledCell.y, currentCell.z) == currentCell) return;
        GetGhost(id, view, hero).Show(settledCenter, GhostAlpha);
        Vector3 from = UnitBodyPosition(board, id, view);
        from.y = settledCenter.y;
        Color moveColor = hero ? HeroMoveColor : EnemyMoveColor;
        if (leaps)
        {
            landingCenter.y = settledCenter.y;
            DrawStraight(from, landingCenter, LeapColor, LeapLineAlpha, LeapWidthFactor);
            from = landingCenter;
        }
        if ((settledCenter - from).sqrMagnitude > 0.0001f)
        {
            // A grey tile on the landing hex, in the same language as the team-coloured ones : an
            // arc that ends on an unmarked hex reads as unanchored. Only when the unit walks on
            // from there, because a unit that lands where it stops gets the team-coloured tile
            // below instead, and one hex only ever takes one tile per frame.
            if (leaps) DrawCell(from, hero, LeapColor, 1f);
            DrawStraight(from, settledCenter, moveColor, 1f);
        }
        DrawUnitCell(settledCenter, id, hero);
    }

    // Was this unit teleported before the fight even began, and if so, where did it land.
    //
    // One enemy does this. The Lizard's combat-start passive throws it across to the player's back
    // line, and it walks on from there. Drawn as one line from its placement hex, that reads as a
    // unit that walked the whole way, straight through everything in between.
    //
    // This cannot be caught by watching the playout tick. The game raises its combat-start
    // triggers inside the battle's own constructor, while the simulation is still being built, so
    // the jump has already happened before the mod ticks anything at all : a check asking "did its
    // hex change this tick" would compile, ship, and never once fire. What can be compared is
    // where the unit stands on the real board, which the player has not started yet, against where
    // it stands in the very first frame of the playout.
    //
    // Comparing distance rather than equality is what makes that safe. The mod reads a unit's hex
    // off the board and the playout reads it out of the simulation, and those two only have to
    // agree to within a hex for this to hold. Nothing that walks can be two hexes from where it
    // was placed before the fight has started, so anything further is a teleport.
    private bool TryGetLeapLanding(PredictionResult result, BoardController board, Vector3Int currentCell,
        string id, out Vector3 landingCenter)
    {
        landingCenter = default;
        if (result.OpeningCells == null || !result.OpeningCells.TryGetValue(id, out Vector2Int opening)) return false;
        if (opening.x == currentCell.x && opening.y == currentCell.y) return false;
        Vector3 current = board.PlacementGrid.GetCellCenterWorld(currentCell);
        Vector3 candidate = board.PlacementGrid.GetCellCenterWorld(new Vector3Int(opening.x, opening.y, 0));
        float threshold = _cellPitch * LeapMinPitches;
        if ((candidate - current).sqrMagnitude <= threshold * threshold) return false;
        landingCenter = candidate;
        return true;
    }

    // Where an arc starts or ends : the hex the unit settles on when the prediction knows one,
    // otherwise the hex it is standing on. Either way it is a hex centre on the ground, which is
    // what the arc drawing needs to work out where the board's edge is.
    private Vector3 AnchorCenter(PredictionResult result, UnitViewRegistry views, BoardController board, string id)
    {
        if (result.Settled.TryGetValue(id, out SettledEntity settled))
            return board.PlacementGrid.GetCellCenterWorld(new Vector3Int(settled.Cell.x, settled.Cell.y, 0));
        views.TryGetView(id, out CharacterViewController view);
        return view != null ? UnitCellCenter(board, id, view) : Vector3.zero;
    }

    // For the two heroes a release would move, the one in hand and any teammate it swaps with,
    // every position and hex is read from the drag rather than off the screen, which still shows
    // the board as it was before the drop.
    private Vector3Int UnitCell(BoardController board, string id, CharacterViewController view) =>
        _drag != null && _drag.TryGetAnchorCell(id, out Vector2Int cell)
            ? new Vector3Int(cell.x, cell.y, 0)
            : board.PlacementGrid.WorldToCell(view.transform.position);

    private Vector3 UnitCellCenter(BoardController board, string id, CharacterViewController view) =>
        _drag != null && _drag.TryGetAnchorCell(id, out Vector2Int cell)
            ? board.PlacementGrid.GetCellCenterWorld(new Vector3Int(cell.x, cell.y, 0))
            : CellCenterOf(board, view);

    private Vector3 UnitBodyPosition(BoardController board, string id, CharacterViewController view) =>
        _drag != null && _drag.TryGetAnchorCell(id, out Vector2Int cell)
            ? board.PlacementGrid.GetCellCenterWorld(new Vector3Int(cell.x, cell.y, 0))
            : view.transform.position;

    // With the toggle on, hovering changes nothing. An earlier version dimmed everything the
    // cursor was not over, which made the whole board pulse as the pointer crossed units. The
    // picture stays still ; reading one unit at a time is what hover mode is for.
    private void RenderPreview(PredictionResult result, UnitViewRegistry views, BoardController board)
    {
        foreach (KeyValuePair<string, SettledEntity> pair in result.Settled)
        {
            string id = pair.Key;
            // A long playout can outlive a unit, and a corpse gets no ghost, no line and no arc.
            if (!pair.Value.Alive) continue;
            if (!views.TryGetView(id, out CharacterViewController view)) continue;
            bool hero = views.IsHero(id);
            Vector3 settled = board.PlacementGrid.GetCellCenterWorld(new Vector3Int(pair.Value.Cell.x, pair.Value.Cell.y, 0));
            DrawMovementFor(result, id, view, hero, pair.Value.Cell, settled, board);
            // A tile under the hex every arc leaves from, whether the unit moved or not, for the
            // same reason as in hover mode. It shares the tile with a mover's landing hex.
            Vector3 anchor = ArrowsFromGhosts ? settled : UnitCellCenter(board, id, view);
            DrawUnitCell(anchor, id, hero);

            // The same arcs hover mode draws. One language everywhere : a straight white line is
            // movement, a curved coloured arc is an attack. The origin toggle only chooses which
            // hexes the arcs run between, and the anchor above is where they start either way.
            string target = pair.Value.TargetPairing;
            bool hasTarget = !string.IsNullOrEmpty(target) && SettledAlive(result, target);
            Vector3 targetAnchor = anchor;
            if (hasTarget && ArrowsFromGhosts)
            {
                if (result.Settled.TryGetValue(target, out SettledEntity targetState)) targetAnchor = CenterOfCell(board, targetState.Cell);
                else hasTarget = false;
            }
            else if (hasTarget)
            {
                if (views.TryGetView(target, out CharacterViewController targetView)) targetAnchor = UnitCellCenter(board, target, targetView);
                else hasTarget = false;
            }
            // The footprint first, so an arc always crosses over its own area rather than being
            // swallowed by it. A unit with nobody to fight still gets its footprint : it is the
            // ground the ability covers, and only the handful of shapes that are not circles care
            // which way their owner is turned.
            DrawAoe(anchor, id, hero, targetAnchor, hasTarget);
            if (hasTarget) DrawCurved(anchor, targetAnchor, hero ? HeroColor : EnemyColor, 1f);
        }
    }

    private static Vector3 CellCenterOf(BoardController board, CharacterViewController view) =>
        board.PlacementGrid.GetCellCenterWorld(board.PlacementGrid.WorldToCell(view.transform.position));

    // An attack arc. It comes back down to the ground at the edge of the target's hex and the
    // head lies flat there. An endpoint left in the air looks like it lands somewhere else
    // entirely under an angled camera, which is why the white movement lines, drawn on the
    // ground from the start, never had the problem. Pass the target's hex centre : the point on
    // its edge is worked out from there.
    private void DrawCurved(Vector3 start, Vector3 targetCellCenter, Color color, float alpha)
    {
        Vector3 flat = targetCellCenter - start;
        flat.y = 0f;
        float dist = flat.magnitude;
        if (dist < 0.001f) return;
        Vector3 dir = flat / dist;
        // Every arc keeps to its own side of the line, the way traffic does. Two units fighting
        // each other, which is most of melee, would otherwise draw two arcs on top of one another
        // with a head at each end, and short arcs are nearly straight, so the pair merged into one
        // thick line pointing both ways. Opposite directions take opposite sides and separate into
        // two clean parallel arrows.
        Vector3 lane = Vector3.Cross(Vector3.up, dir) * (_cellPitch * 0.07f);
        float inset = Mathf.Min(_targetEdgeInset, dist * 0.45f); // over one hex, keep some shaft.
        Vector3 tip = targetCellCenter - dir * inset + lane;
        tip.y = targetCellCenter.y + 0.038f;
        Vector3 shaftEnd = tip - dir * (_headLength * 0.8f); // shaft tucks under the head, no gap.
        Vector3 lifted = start + lane;
        lifted.y = start.y + BoardLift * 0.75f;
        // The curve comes in with distance. Over a single hex a curve is pure decoration, so
        // melee arcs run nearly straight and nearly flat, and both the sideways bow and the hop
        // over the board grow from there. Long shots are still capped, so an arc across the whole
        // board looks the same as it always did.
        float bend = Mathf.SmoothStep(0f, 1f, (dist / _cellPitch - 1f) / 3.5f);
        Vector3 side = Vector3.Cross(Vector3.up, dir) * Mathf.Min(0.9f, dist * (0.03f + 0.17f * bend));
        Vector3 control = (lifted + shaftEnd) * 0.5f + side + Vector3.up * (0.06f + 0.16f * bend);

        float widthScale = PulledScale((lifted + shaftEnd) * 0.5f);
        WorldLine underlay = GetLine(_solidMaterial, AttackArcWidth * UnderlayWidthFactor * widthScale, 49);
        WorldLine line = GetLine(_solidMaterial, AttackArcWidth * widthScale, 50);
        underlay.SetColor(WithAlpha(UnderlayColor, alpha));
        line.SetColor(WithAlpha(color, alpha));
        for (int i = 0; i < CurveSamples; i++)
        {
            float t = i / (CurveSamples - 1f);
            float u = 1f - t;
            Vector3 point = Pulled(u * u * lifted + 2f * u * t * control + t * t * shaftEnd);
            line.CurveBuffer[i] = point;
            underlay.CurveBuffer[i] = point;
        }
        // One upload per line for the whole curve. The buffers are native and kept, so the points
        // are written straight into memory the engine already owns, with nothing copied across.
        underlay.Renderer.positionCount = CurveSamples;
        underlay.Renderer.SetPositions(underlay.CurveBuffer);
        line.Renderer.positionCount = CurveSamples;
        line.Renderer.SetPositions(line.CurveBuffer);
        DrawHead(tip, dir, color, alpha);
        if (MidlineHeads && dist > _headLength * MidHeadMinLengthFactor)
        {
            // The middle of a quadratic curve has a closed form, and its direction there is
            // simply the direction from one end to the other, so neither needs to be searched
            // for by sampling the curve.
            Vector3 mid = 0.25f * lifted + 0.5f * control + 0.25f * shaftEnd;
            Vector3 tangent = shaftEnd - lifted;
            if (tangent.sqrMagnitude > 0.0001f)
                DrawHead(mid + Vector3.up * 0.012f, tangent.normalized, color, alpha, MidHeadScale);
        }
    }

    // A movement line : straight, solid, with a small head landing on the edge of the
    // destination hex the way the attack arcs do. It is the smallest of the three heads, because
    // movement is the least important arrow on the board, and the ghost already marks where the
    // unit stops. Pass the destination's hex centre.
    //
    // The width factor thins the whole shaft without touching the head, which is what separates a
    // jump from a walk while keeping both in the same visual family.
    private void DrawStraight(Vector3 start, Vector3 end, Color color, float alpha, float widthFactor = 1f)
    {
        start.y += BoardLift * 0.6f;
        end.y += BoardLift * 0.6f;
        Vector3 flat = end - start;
        flat.y = 0f;
        float dist = flat.magnitude;
        if (dist < 0.001f) return;
        Vector3 dir = flat / dist;
        float inset = Mathf.Min(_targetEdgeInset, dist * 0.45f); // same short-distance cap as the arcs.
        Vector3 tip = end - dir * inset;
        Vector3 shaftEnd = tip - dir * (_headLength * MoveHeadScale * 0.8f); // tucks under the head.
        float widthScale = PulledScale((start + shaftEnd) * 0.5f);
        Vector3 pulledStart = Pulled(start);
        Vector3 pulledEnd = Pulled(shaftEnd);
        WorldLine underlay = GetLine(_solidMaterial, MoveWidth * UnderlayWidthFactor * widthScale * widthFactor, 49);
        underlay.SetColor(WithAlpha(UnderlayColor, alpha));
        FillStraight(underlay, pulledStart, pulledEnd);
        WorldLine line = GetLine(_solidMaterial, MoveWidth * widthScale * widthFactor, 50);
        line.SetColor(WithAlpha(color, alpha));
        FillStraight(line, pulledStart, pulledEnd);
        DrawHead(tip, dir, color, alpha, MoveHeadScale);
        if (MidlineHeads)
        {
            Vector3 span = shaftEnd - start;
            if (span.magnitude > _headLength * MidHeadMinLengthFactor)
                DrawHead((start + shaftEnd) * 0.5f + Vector3.up * 0.012f, span.normalized, color, alpha, MidHeadScale);
        }
    }

    private static void FillStraight(WorldLine line, Vector3 start, Vector3 end)
    {
        line.StraightBuffer[0] = start;
        line.StraightBuffer[1] = end;
        line.Renderer.positionCount = 2;
        // The texture already repeats along the line's real length, so one repeat per world unit
        // is the right scale. Scaling it by the distance as well counted the length twice and
        // ground the texture down into noise.
        line.Renderer.textureScale = Vector2.one;
        line.Renderer.SetPositions(line.StraightBuffer);
    }

    // A filled arrowhead, lying flat on the ground, its tip on the hex edge and pointing the way
    // the line came in. It replaces a borrowed sprite that was small, hollow and never quite
    // aimed right. A scale below one draws the smaller mid-line chevrons in the same shape.
    private void DrawHead(Vector3 tip, Vector3 direction, Color color, float alpha, float scale = 1f)
    {
        Quaternion rotation = Quaternion.LookRotation(direction, Vector3.up);
        float pulledScale = scale * PulledScale(tip);
        Vector3 pulledTip = Pulled(tip);
        MeshHead underlay = GetHead();
        underlay.Transform.SetPositionAndRotation(pulledTip + Vector3.up * -0.006f, rotation);
        underlay.Transform.localScale = Vector3.one * (_headLength * 1.3f * pulledScale);
        underlay.SetColor(WithAlpha(UnderlayColor, alpha), 51);
        MeshHead head = GetHead();
        head.Transform.SetPositionAndRotation(pulledTip, rotation);
        head.Transform.localScale = Vector3.one * (_headLength * pulledScale);
        head.SetColor(WithAlpha(color, alpha), 52);
    }

    // Moves a point toward the camera along the line the camera already sees it on. Where it
    // appears on screen does not change at all, only how far away it counts as, so a line drawn
    // this way comes out in front of the ghosts, the real bodies and the tall bosses that were
    // swallowing arcs running along the ground. The scale below undoes the size a perspective
    // camera would add, so nothing gets fatter for being pulled forward. Tiles are left alone :
    // the floor should stay the floor.
    private Vector3 Pulled(Vector3 point)
    {
        if (!_pullActive) return point;
        Vector3 toCam = _camPos - point;
        float ray = toCam.magnitude;
        if (ray < 0.001f) return point;
        return point + toCam * (Mathf.Min(CameraPull, ray * 0.4f) / ray);
    }

    private float PulledScale(Vector3 point)
    {
        if (!_pullActive) return 1f;
        float ray = (_camPos - point).magnitude;
        if (ray < 0.001f) return 1f;
        return 1f - Mathf.Min(CameraPull, ray * 0.4f) / ray;
    }

    // The tile under a unit, carrying that unit's placement-dependent parts when it has any.
    //
    // This re-tints the tile the mod already draws rather than adding a second mark beside it. The
    // tile is how the board already says "this is where it ends up", so "and where it ends up is
    // paying off, or is not" belongs on the same object. It also has to be the same object : only
    // one tile is drawn per hex per frame, so a second one would simply be dropped, and which of
    // the two survived would depend on the order the callers happened to run in.
    //
    // Enemies are never asked about. Items, relics, rank modifiers and specialization passives are
    // all the player's, and the feature is about a board the player can rearrange.
    private void DrawUnitCell(Vector3 position, string id, bool hero)
    {
        PartState state = hero && HeroGlow != null && id != null ? HeroGlow(id) : PartState.NotPositional;
        switch (state)
        {
            case PartState.Live:
                DrawCell(position, true, GlowLiveColor, GlowCellAlpha);
                return;
            case PartState.Blocked:
                DrawCell(position, true, GlowBlockedColor, GlowCellAlpha);
                DrawSlash(position, GlowBlockedMarkColor);
                return;
            default:
                DrawCell(position, hero, hero ? HeroColor : EnemyColor, 1f);
                return;
        }
    }

    // The ground an ability's area will cover, drawn where the unit will be standing.
    //
    // Anchored on the unit and following the same origin toggle the arrows do, which is the whole
    // scheme : with arrows starting from the settled positions the footprint sits where the unit
    // ends up, and with them starting from the current ones it sits where the unit is now. It never
    // tries to work out where the ability will CHOOSE to put its area, because that decision lives
    // in each hero's own compiled logic and reimplementing fifteen of those is exactly the kind of
    // dependency this mod exists without.
    //
    // Honest for the settled opening and no further, which is the contract the rest of the picture
    // already makes. Nothing here mixes two instants : the anchor, the facing and the arrows all
    // describe the same moment.
    private void DrawAoe(Vector3 anchor, string id, bool hero, Vector3 facingTowards, bool hasFacing)
    {
        if (UnitAoe == null || id == null) return;
        AoeOutline outline = UnitAoe(id);
        if (outline == null) return;
        // Coloured for the unit that places it, never for who it lands on. That is already the rule
        // every arc on this board follows, and a footprint is the same statement about the same
        // unit : colouring it by who it would catch made three areas around one hero read as three
        // different units acting.
        Color color = hero ? HeroColor : EnemyColor;
        float facing = 0f;
        if (outline.RotatesWithFacing && hasFacing)
        {
            Vector3 flat = facingTowards - anchor;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.0001f) facing = Mathf.Atan2(flat.x, flat.z);
        }
        for (int i = 0; i < outline.Loops.Length; i++) DrawOutlineLoop(outline.Loops[i], anchor, facing, color);
    }

    // One closed ring, handed to the engine in a single call. Turned only when the shape is one of
    // the handful that is not a circle : a circle looks the same whichever way its owner faces, and
    // the game's own code for placing one ignores the rotation it is given for exactly that reason.
    private void DrawOutlineLoop(Vector2[] local, Vector3 anchor, float facing, Color color)
    {
        if (local == null || local.Length < 3) return;
        Il2CppStructArray<Vector3> buffer = OutlineBuffer(local.Length);
        float cos = Mathf.Cos(facing), sin = Mathf.Sin(facing);
        bool turn = facing != 0f;
        float lift = anchor.y + BoardLift * 0.5f;
        // One nudge toward the camera for the whole ring, taken at its centre, rather than one per
        // point. The nudge is a fixed distance along each point's own ray, so applying it per point
        // lifts the near side of a ring further than the far side and draws a circle as an egg.
        // Everything else here is a line between two points, where that has nothing to distort.
        Vector3 pull = Pulled(anchor) - anchor;
        for (int i = 0; i < local.Length; i++)
        {
            Vector2 point = local[i];
            float x = turn ? point.x * cos + point.y * sin : point.x;
            float z = turn ? -point.x * sin + point.y * cos : point.y;
            buffer[i] = new Vector3(anchor.x + x + pull.x, lift + pull.y, anchor.z + z + pull.z);
        }
        // Under the movement lines and the arcs. A footprint is context for them, not a competitor.
        WorldLine line = GetLine(_dashMaterial, MoveWidth * 0.8f * PulledScale(anchor), 47);
        line.Renderer.loop = true;
        line.Renderer.endWidth = line.Renderer.startWidth; // a ring has no far end to taper towards.
        line.Renderer.positionCount = local.Length;
        // Repeats per world unit, which is what this already means for a tiled line. Scaling it by
        // the ring's length as well would count the length twice and grind the dashes into noise.
        line.Renderer.textureScale = new Vector2(DashesPerUnit, 1f);
        line.SetColor(color);
        line.Renderer.SetPositions(buffer);
    }

    private Il2CppStructArray<Vector3> OutlineBuffer(int length)
    {
        if (_outlineBuffers.TryGetValue(length, out Il2CppStructArray<Vector3> buffer)) return buffer;
        buffer = new Il2CppStructArray<Vector3>(length);
        _outlineBuffers[length] = buffer;
        return buffer;
    }

    // A line struck across a hex, for a part that is switched off by where its unit is standing.
    //
    // Deliberately not an arrow, and deliberately not a colour on its own. Every arrow on this
    // board means motion or an attack and nothing is moving here, and a state told by tint alone
    // would be the one thing on the board that a player who cannot separate two blues has no way
    // to read. The strike says it without the colour.
    private void DrawSlash(Vector3 center, Color color)
    {
        if (_cellPitch <= 0f) return;
        float reach = _cellPitch * 0.32f;
        var offset = new Vector3(reach, 0f, reach);
        Vector3 lift = Vector3.up * (BoardLift * 0.6f);
        Vector3 start = Pulled(center - offset + lift);
        Vector3 end = Pulled(center + offset + lift);
        float widthScale = PulledScale(center);
        // Above the movement lines rather than among them : this annotates the tile, it is not
        // another thing happening on the board.
        // Thicker than a movement line, because it is the one mark here that has to be found
        // rather than followed. A player looking at a board is not looking for it.
        float width = MoveWidth * SlashWidthFactor * widthScale;
        WorldLine underlay = GetLine(_solidMaterial, width * UnderlayWidthFactor, 51);
        underlay.SetColor(UnderlayColor);
        FillStraight(underlay, start, end);
        WorldLine line = GetLine(_solidMaterial, width, 52);
        line.SetColor(color);
        FillStraight(line, start, end);
    }

    private void DrawCell(Vector3 position, bool hero, Color color, float alpha)
    {
        // One tile per hex per frame. A unit's own tile and the tile marking where it walks to
        // can be the same hex, and drawing it twice would tint it twice as strongly. These are
        // exact hex centres a full hex apart, so rounding them is a safe way to compare them.
        if (!_cellStamps.Add(Vector3Int.RoundToInt(position * 2f))) return;
        Sprite sprite = hero ? _heroTileSprite : _enemyTileSprite;
        if (sprite == null) return;
        CellHighlight cell = GetCell();
        cell.Transform.position = position + Vector3.up * 0.025f;
        cell.Transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        cell.Renderer.sprite = sprite;
        cell.Renderer.color = WithAlpha(color, 0.35f * alpha);
    }

    private WorldLine GetLine(Material material, float width, int sortingOrder)
    {
        // Requests are one ordered pass per frame stamp. The cursor therefore returns the same
        // lowest free entry as the old scan without walking all entries already used this frame.
        if (_lineCursor < _lines.Count)
        {
            WorldLine existing = _lines[_lineCursor++];
            existing.Activate(material, width, sortingOrder, _frameStamp);
            return existing;
        }
        var item = new WorldLine(_root.transform);
        _lines.Add(item);
        _lineCursor++;
        item.Activate(material, width, sortingOrder, _frameStamp);
        return item;
    }

    private MeshHead GetHead()
    {
        if (_headCursor < _heads.Count)
        {
            MeshHead existing = _heads[_headCursor++];
            existing.Activate(_frameStamp);
            return existing;
        }
        var item = new MeshHead(_root.transform, _headMesh, _headMaterial);
        _heads.Add(item);
        _headCursor++;
        item.Activate(_frameStamp);
        return item;
    }

    private CellHighlight GetCell()
    {
        if (_cellCursor < _cells.Count)
        {
            CellHighlight existing = _cells[_cellCursor++];
            existing.Activate(_frameStamp);
            return existing;
        }
        var item = new CellHighlight(_root.transform);
        _cells.Add(item);
        _cellCursor++;
        item.Activate(_frameStamp);
        return item;
    }

    private Ghost GetGhost(string id, CharacterViewController source, bool hero)
    {
        if (!_ghosts.TryGetValue(id, out Ghost ghost))
        {
            ghost = new Ghost(_root.transform, source, hero ? HeroColor : EnemyColor, _ghostFallback, this);
            _ghosts[id] = ghost;
        }
        ghost.Stamp = _frameStamp;
        return ghost;
    }

    private void Sweep()
    {
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].Stamp != _frameStamp && IsShowing(_lines[i].GameObject)) _lines[i].GameObject.SetActive(false);
        for (int i = 0; i < _heads.Count; i++)
            if (_heads[i].Stamp != _frameStamp && IsShowing(_heads[i].GameObject)) _heads[i].GameObject.SetActive(false);
        for (int i = 0; i < _cells.Count; i++)
            if (_cells[i].Stamp != _frameStamp && IsShowing(_cells[i].GameObject)) _cells[i].GameObject.SetActive(false);
        foreach (Ghost ghost in _ghosts.Values)
            if (ghost.Stamp != _frameStamp && IsShowing(ghost.Root)) ghost.Root.SetActive(false);
    }

    // Is this object still there and still on. Asking a destroyed object whether it is active
    // throws through interop, and the sweep runs one last time on the way out, including at
    // process shutdown where Unity has already destroyed even the objects the mod deliberately
    // keeps across scenes. Answering false for something that no longer exists is both true and
    // what every caller wants, since there is nothing left to hide.
    private static bool IsShowing(GameObject go)
    {
        try { return go != null && go.activeSelf; }
        catch { return false; }
    }

    public void ClearPlacement()
    {
        InvalidatePicture();
        _drag = null;
        _frameStamp++;
        _lineCursor = 0;
        _headCursor = 0;
        _cellCursor = 0;
        Sweep();
        _fader.Restore(); // faded units are back to normal before the fight is on screen.
        PreviewEnabled = false;
        foreach (Ghost ghost in _ghosts.Values) ghost.Destroy();
        _ghosts.Clear();
    }

    internal void SetWorldDrawing(bool on)
    {
        if (_root != null) _root.SetActive(on);
        // The draw-cost probe switches the whole overlay off and on to measure what drawing it
        // costs. Nothing about the picture changes, so the skip would be correct, and it is
        // invalidated anyway: an instrument that quietly changes how often the thing it measures
        // runs is measuring itself.
        InvalidatePicture();
    }

    public void Destroy()
    {
        ClearPlacement();
        if (_root != null) UnityEngine.Object.Destroy(_root);
        _outlineBuffers.Clear();
        _lines.Clear();
        _heads.Clear();
        _cells.Clear();
        _root = null;
        _targetEdgeInset = 0f;
        _headLength = 0f;
        _cellPitch = 0f;
        _camera = null;
        _pullActive = false;
        _heroTileSprite = null;
        _enemyTileSprite = null;
        _resolved = false;
    }

    public static void DropSessionAssets()
    {
        if (_solidMaterial != null) UnityEngine.Object.Destroy(_solidMaterial);
        if (_headMaterial != null) UnityEngine.Object.Destroy(_headMaterial);
        if (_ghostFallback != null) UnityEngine.Object.Destroy(_ghostFallback);
        if (_dashMaterial != null) UnityEngine.Object.Destroy(_dashMaterial);
        if (_solidTexture != null) UnityEngine.Object.Destroy(_solidTexture);
        if (_dashTexture != null) UnityEngine.Object.Destroy(_dashTexture);
        if (_headMesh != null) UnityEngine.Object.Destroy(_headMesh);
        _solidMaterial = null;
        _headMaterial = null;
        _ghostFallback = null;
        _dashMaterial = null;
        _solidTexture = null;
        _dashTexture = null;
        _headMesh = null;
        _previewIconSprite = null;
        _previewIconSearched = false;
    }

    internal void NoteGhostProperty(string property)
    {
        if (GhostShaderProperty == "unresolved") GhostShaderProperty = property;
    }

    private static Color WithAlpha(Color color, float alpha) { color.a *= alpha; return color; }

    private static Material NewLineMaterial(Shader shader, Texture2D texture, int queue)
    {
        var material = new Material(shader)
        {
            name = "GuildrunTargetingMod.Line",
            renderQueue = queue,
            hideFlags = HideFlags.HideAndDontSave
        };
        // Only the fallback shader needs to be told to blend. On the sprite shader, which already
        // does, every one of these writes finds no such property and does nothing.
        ConfigureTransparent(material, 0f);
        if (texture != null)
        {
            material.mainTexture = texture;
            material.mainTextureScale = Vector2.one;
        }
        return material;
    }

    private static Material NewTransparentMaterial(Shader shader, string name)
    {
        var material = new Material(shader)
        {
            name = name,
            renderQueue = 2985,
            hideFlags = HideFlags.HideAndDontSave
        };
        // Ghosts write depth, so a see-through body hides its own far side instead of showing an
        // arm through a chest. Lines are drawn later and are unaffected.
        ConfigureTransparent(material, 1f);
        return material;
    }

    internal static void ConfigureTransparent(Material material, float zWrite)
    {
        if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
        if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", 5f);
        if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", 10f);
        if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", zWrite);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
    }

    // On for half its length and off for the other half, so a line drawn with it comes out broken.
    // Soft across the width for the same reason as the solid one, and soft at each end of a dash so
    // the pattern does not shimmer as the camera moves. Repeats along the line, clamps across it.
    private static Texture2D MakeDashTexture(string name)
    {
        const int width = 16, height = 8;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, true)
        {
            name = name,
            wrapModeU = TextureWrapMode.Repeat,
            wrapModeV = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 4,
            hideFlags = HideFlags.HideAndDontSave
        };
        for (int y = 0; y < height; y++)
        {
            float v = (y + 0.5f) / height;
            float acrossEdge = Mathf.Min(v, 1f - v) * 2f;
            float across = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(acrossEdge / 0.5f));
            for (int x = 0; x < width; x++)
            {
                float u = (x + 0.5f) / width;
                // The dash occupies the first 55%, with a short ramp at each end of it.
                float along = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(u / 0.12f)) *
                              Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((0.55f - u) / 0.12f));
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, across * along));
            }
        }
        texture.Apply(true, false);
        return texture;
    }

    // Even along the line and fading out towards its edges. Smoothing, mipmaps and clamping
    // across the width are what keep a thin line seen at an angle smooth instead of speckled.
    private static Texture2D MakeSoftLineTexture(string name)
    {
        const int width = 4, height = 16;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, true)
        {
            name = name,
            wrapModeU = TextureWrapMode.Repeat,
            wrapModeV = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 4,
            hideFlags = HideFlags.HideAndDontSave
        };
        for (int y = 0; y < height; y++)
        {
            float v = (y + 0.5f) / height;
            float distFromEdge = Mathf.Min(v, 1f - v) * 2f;
            float alpha = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(distFromEdge / 0.5f));
            for (int x = 0; x < width; x++) texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
        }
        texture.Apply(true, false);
        return texture;
    }

    // An arrowhead one unit long, lying flat, tip at the origin, with a notch cut into its tail.
    // It is scaled to the board's hex size when drawn, and the shader renders both faces, so it
    // is visible whichever way the camera comes round.
    private static Mesh BuildHeadMesh()
    {
        var mesh = new Mesh
        {
            name = "GuildrunTargetingMod.ArrowHead",
            hideFlags = HideFlags.HideAndDontSave
        };
        mesh.vertices = new[]
        {
            new Vector3(0f, 0f, 0f),        // tip
            new Vector3(-0.42f, 0f, -1f),   // left barb
            new Vector3(0f, 0f, -0.72f),    // tail notch
            new Vector3(0.42f, 0f, -1f)     // right barb
        };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        return mesh;
    }

    private sealed class WorldLine
    {
        public readonly GameObject GameObject;
        public readonly LineRenderer Renderer;
        // Kept native and reused. Handing the engine a plain managed array would allocate and
        // copy one on every single call ; writing into these lands straight in its own memory.
        public readonly Il2CppStructArray<Vector3> CurveBuffer = new(CurveSamples);
        public readonly Il2CppStructArray<Vector3> StraightBuffer = new(2);
        private readonly MaterialPropertyBlock _block = new();
        public int Stamp;

        public WorldLine(Transform parent)
        {
            GameObject = new GameObject("TargetingLine");
            GameObject.transform.SetParent(parent, false);
            Renderer = GameObject.AddComponent<LineRenderer>();
            Renderer.useWorldSpace = true;
            Renderer.alignment = LineAlignment.View;
            Renderer.textureMode = LineTextureMode.Tile;
            Renderer.numCapVertices = 2;
            Renderer.numCornerVertices = 2;
            // The line's own colours stay white. The sprite shader would multiply them by the
            // tint applied below, darkening it twice over, and the fallback shader ignores them
            // entirely. That tint is the one channel both shaders agree on.
            Renderer.startColor = Color.white;
            Renderer.endColor = Color.white;
            GameObject.SetActive(false);
        }

        public void Activate(Material material, float width, int sortingOrder, int stamp)
        {
            Stamp = stamp;
            if (!GameObject.activeSelf) GameObject.SetActive(true);
            Renderer.sharedMaterial = material;
            Renderer.sortingOrder = sortingOrder;
            Renderer.startWidth = width;
            Renderer.endWidth = width * 0.72f;
            // Reset on every hand-out, because these are pooled. An area outline closes its ring
            // and a straight line does not, and a line handed back out still closed would join its
            // two ends across the board.
            Renderer.loop = false;
        }

        public void SetColor(Color color)
        {
            _block.Clear();
            _block.SetColor("_Color", color);      // Sprites/Default
            _block.SetColor("_BaseColor", color);  // URP Unlit fallback
            Renderer.SetPropertyBlock(_block);
        }
    }

    private sealed class MeshHead
    {
        public readonly GameObject GameObject;
        public readonly Transform Transform;
        private readonly MeshRenderer _renderer;
        private readonly MaterialPropertyBlock _block = new();
        public int Stamp;

        public MeshHead(Transform parent, Mesh mesh, Material material)
        {
            GameObject = new GameObject("TargetingArrowHead");
            Transform = GameObject.transform;
            Transform.SetParent(parent, false);
            GameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            _renderer = GameObject.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            GameObject.SetActive(false);
        }

        public void Activate(int stamp)
        {
            Stamp = stamp;
            if (!GameObject.activeSelf) GameObject.SetActive(true);
        }

        public void SetColor(Color color, int sortingOrder)
        {
            _renderer.sortingOrder = sortingOrder;
            _block.Clear();
            _block.SetColor("_Color", color);
            _block.SetColor("_BaseColor", color);
            _renderer.SetPropertyBlock(_block);
        }
    }

    private sealed class CellHighlight
    {
        public readonly GameObject GameObject;
        public readonly Transform Transform;
        public readonly SpriteRenderer Renderer;
        public int Stamp;

        public CellHighlight(Transform parent)
        {
            GameObject = new GameObject("TargetingDestinationCell");
            Transform = GameObject.transform;
            Transform.SetParent(parent, false);
            Renderer = GameObject.AddComponent<SpriteRenderer>();
            Renderer.sortingOrder = 45;
            GameObject.SetActive(false);
        }

        public void Activate(int stamp)
        {
            Stamp = stamp;
            if (!GameObject.activeSelf) GameObject.SetActive(true);
        }
    }

    // A see-through copy of a unit, standing on the hex that unit ends up on. Built once per unit
    // and reused, since a character's shape does not change during placement.
    private sealed class Ghost : IDepthRankedBody
    {
        private readonly List<MeshRenderer> _renderers = new();
        private readonly List<Mesh> _bakedMeshes = new();
        private readonly MaterialPropertyBlock _block = new();
        private readonly Color _fallbackTint;
        private readonly Quaternion _rotation;
        private int _lastOrder = -1;
        public readonly GameObject Root;
        public int Stamp;

        public Ghost(Transform parent, CharacterViewController source, Color teamColor,
            Material fallback, OverlayRenderer owner)
        {
            _fallbackTint = teamColor;
            _rotation = source.transform.rotation;
            Root = new GameObject("TargetingGhost." + source.gameObject.name);
            Root.transform.SetParent(parent, false);
            // Never copy a live unit wholesale. Only the shapes it is drawn from are taken, so
            // no script, collider, animator, effect, sound or subscription of the game's can end
            // up in here and start doing something on its own.
            UnityEngine.Renderer[] sources = source.GetComponentsInChildren<UnityEngine.Renderer>(true);
            Quaternion invSourceRotation = Quaternion.Inverse(source.transform.rotation);
            for (int i = 0; i < sources.Length; i++)
            {
                UnityEngine.Renderer src = sources[i];
                // Ask the native object what it is, never a C# type test. A wrapper reports the
                // type it was declared as, so the type test always answers no : the version that
                // used one built ghosts out of nothing and every one of them was invisible.
                if (src == null || !src.enabled || !src.gameObject.activeInHierarchy) continue;
                if (src.TryCast<ParticleSystemRenderer>() != null || src.TryCast<TrailRenderer>() != null ||
                    src.TryCast<LineRenderer>() != null) continue;
                Mesh mesh = null;
                bool bakedWithScale = false;
                SkinnedMeshRenderer skinned = src.TryCast<SkinnedMeshRenderer>();
                if (skinned != null)
                {
                    mesh = new Mesh { name = "TargetingGhost.BakedMesh" };
                    // Baking with the unit's scale folds it into the shape itself, so the copy
                    // is left unscaled and nothing gets scaled a second time.
                    skinned.BakeMesh(mesh, true);
                    bakedWithScale = true;
                    _bakedMeshes.Add(mesh);
                }
                else if (src.TryCast<MeshRenderer>() != null)
                {
                    MeshFilter sourceFilter = src.GetComponent<MeshFilter>();
                    if (sourceFilter != null) mesh = sourceFilter.sharedMesh;
                }
                if (mesh == null) continue;
                var part = new GameObject("Renderer");
                part.transform.SetParent(Root.transform, false);
                // Each part keeps its place relative to the unit's own centre, so the ghost turns
                // as one piece and holds the pose it was copied in.
                part.transform.localPosition = invSourceRotation * (src.transform.position - source.transform.position);
                part.transform.localRotation = invSourceRotation * src.transform.rotation;
                part.transform.localScale = bakedWithScale ? Vector3.one : src.transform.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer renderer = part.AddComponent<MeshRenderer>();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.sortingOrder = 40; // temporary ; the far-to-near sort sets the real one.
                // Always a flat team-coloured silhouette. An earlier version kept the unit's own
                // textures whenever its materials happened to allow it, which meant an enemy
                // built from mixed materials came out white next to orange ones. The rule is
                // settled : flat team colour means final position, everywhere.
                renderer.sharedMaterials = Uniform(fallback, src.sharedMaterials.Length);
                _renderers.Add(renderer);
            }
            owner.NoteGhostProperty("flat-team-silhouette");
            Root.SetActive(false);
        }

        private static Material[] Uniform(Material material, int count)
        {
            var result = new Material[Math.Max(1, count)];
            for (int i = 0; i < result.Length; i++) result[i] = material;
            return result;
        }

        public void Show(Vector3 position, float alpha)
        {
            if (!Root.activeSelf) Root.SetActive(true);
            Root.transform.position = position;
            Root.transform.rotation = _rotation;
            Color tint = _fallbackTint;
            tint.a = alpha;
            _block.Clear();
            _block.SetColor("_BaseColor", tint);
            _block.SetColor("_Color", tint);
            for (int i = 0; i < _renderers.Count; i++) _renderers[i].SetPropertyBlock(_block);
        }

        public Vector3 BodyPosition => Root.transform.position;
        public float RankDistance { get; set; }

        public void ApplyBodyOrder(int sortingOrder)
        {
            if (sortingOrder == _lastOrder) return;
            _lastOrder = sortingOrder;
            for (int i = 0; i < _renderers.Count; i++) _renderers[i].sortingOrder = sortingOrder;
        }

        public void Destroy()
        {
            for (int i = 0; i < _bakedMeshes.Count; i++) if (_bakedMeshes[i] != null) UnityEngine.Object.Destroy(_bakedMeshes[i]);
            if (Root != null) UnityEngine.Object.Destroy(Root);
        }
    }
}
