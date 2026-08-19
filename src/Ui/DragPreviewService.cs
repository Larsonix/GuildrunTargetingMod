using System;
using System.Collections.Generic;
using GuildrunTargetingMod.Interop;
using Il2CppEmber.Balancing.SimulationBridge;
using Il2CppEmber.Scopes.Battle.Board.Controllers;
using Il2CppEmber.Scopes.Battle.Board.Data;
using Il2CppEmber.Scopes.Battle.Characters;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data.Characters;
using Il2Cppgg.leyline.core.Mvcs.Model;
using UnityEngine;

namespace GuildrunTargetingMod.Ui;

// One frame of a drag in progress.
//
// Usable is false when a hero is in hand but no honest board can be predicted for it : held over
// the UI, where releasing takes it off the board altogether, or taken from the bench, where it
// has no board hex to move away from. The mod hides the board visuals in both cases rather than
// inventing an answer.
//
// CandidateValid is the game's own test applied to the hex under the hero. Valid means releasing
// puts the hero there, invalid means releasing does nothing and the board stays as it is. A hex
// holding one of your own heroes counts as valid, and releasing there swaps the two.
internal sealed class DragSnapshot
{
    public string EntityId;
    // The teammate standing on the candidate hex, if there is one. Releasing swaps the two, so
    // this hero really does end up on the hex the dragged hero came from.
    public string SwapEntityId;
    public Vector2Int StartCell;
    public Vector2Int CandidateCell;
    public bool CandidateValid;
    public bool Usable;

    // Where the dragged hero would stand if it were released right now.
    public Vector2Int AnchorCell => CandidateValid ? CandidateCell : StartCell;

    // Where a hero this release would move actually ends up. Every visual that would otherwise
    // read a position off the screen asks here first : the dragged model is floating at the
    // cursor, and a teammate about to be swapped has not moved at all yet, because the game does
    // not touch the board until the hero is dropped.
    public bool TryGetAnchorCell(string id, out Vector2Int cell)
    {
        if (EntityId != null && string.Equals(id, EntityId, StringComparison.Ordinal))
        {
            cell = AnchorCell;
            return true;
        }
        if (CandidateValid && SwapEntityId != null && string.Equals(id, SwapEntityId, StringComparison.Ordinal))
        {
            cell = StartCell;
            return true;
        }
        cell = default;
        return false;
    }
}

// Works out whether a hero is being dragged, and onto which hex, by looking at where the heroes
// are standing. It never watches the mouse.
//
// That is the whole point of this class. The first version listened for the frame the mouse
// button goes down, to learn which hero had been picked up. Anything that hid that single frame
// from the mod cost it the entire drag : the click that gives the window focus back after an alt
// tab, a press that starts over a piece of UI, a press swallowed on a stuttering frame. The
// preview then either froze on the picture from before the drag or showed nothing at all, and
// both were reported from play. An event you can miss is not something to build a feature on.
// Where a hero is standing cannot be missed, because it is true on every frame.
//
// What the game does, and what this reads:
//   a hero on the board is placed exactly on its tile centre
//   a hero in hand is moved to the cursor's ground point, lifted, every render frame
//   a hero held over the UI is parked far off the board, which is also where benched heroes sit
// So a hero away from its own tile is being dragged. Nothing here depends on how high the lift
// is, on the board's height in the world, or on any input event arriving.
//
// A fault here costs the live drag preview and nothing else : the mod goes back to simply hiding
// the visuals while a hero is in hand.
internal sealed class DragPreviewService
{
    // Heroes held over the UI are parked a thousand units out, and every board cell sits near the
    // origin, so this tells the two apart with orders of magnitude to spare.
    private const float ParkedX = 900f;
    // A hero on the board sits exactly on its tile centre, and the smallest displacement a drag
    // can produce is the vertical lift. This sits far above floating point noise and far below
    // any real lift, so it cannot mistake one for the other.
    private const float DisplacedDistance = 0.35f;

    private readonly Capabilities _capabilities;
    private readonly Dictionary<string, Vector2Int> _heroCells = new(StringComparer.Ordinal);
    private readonly Dictionary<Vector2Int, string> _heroKeysByCell = new();
    private readonly Dictionary<Vector2Int, EntityId> _lastHeroIdsByCell = new();
    private bool _planeResolved;
    private float _boardPlaneY;

    // True once this has read the board successfully during this placement. The mouse-based
    // backstop is only consulted while it is false. Otherwise a mouse button flag left stuck
    // down, which happens when the release lands while the window is not focused, would hide the
    // board visuals forever : the same "nothing happens while dragging" complaint, arrived at
    // from the other direction.
    public bool Healthy { get; private set; }

    /// <summary>
    /// A cheap stand-in for "who is standing where", changing whenever the board's occupancy does.
    /// </summary>
    /// <remarks>
    /// Free, and that is the point. This class already reads every tile on every frame to find the
    /// dragged hero, so noticing that the ANSWER changed costs an add per tile on a walk that was
    /// happening anyway.
    ///
    /// It exists because the prediction had no other way to hear about a board change that did not
    /// come from a live drag. Dragging a hero off the BENCH onto the board, or off the board onto
    /// the bench, produces no usable drag snapshot at all, so the board silently changed and the
    /// only thing that eventually noticed was a half second poll. Measured from play: drag response
    /// mean 36 ms, worst 498 ms, and that worst is the poll interval to the millisecond.
    ///
    /// Order independent, because the tiles are walked out of a dictionary and nothing promises the
    /// same order twice. Addition of a well mixed per tile value gives that, where a plain exclusive
    /// or would let two changes cancel each other out.
    /// </remarks>
    public ulong BoardSignature { get; private set; }

    public DragPreviewService(Capabilities capabilities) => _capabilities = capabilities;

    public DragSnapshot Update(UnitViewRegistry views, BoardController board)
    {
        if (!_capabilities.DragPreview || views == null || board == null) return null;
        try
        {
            if (!DataReaders.TryGet(out BoardDataReader reader) || reader == null) return null;
            Healthy = true;

            // The game does not touch the board until a hero is dropped, so this holds who was
            // standing where before the drag started, for the whole frame. It is a few dozen
            // tiles read straight out of memory, rebuilt every frame, which costs less than the
            // bookkeeping it would take to know when to rebuild it.
            _heroCells.Clear();
            ulong signature = 0UL;
            foreach (Vector2Int cell in reader.Data.Board.Keys)
            {
                if (!reader.Data.Board.TryGetValue(cell, out TileInfo tile) || tile == null) continue;
                if (!NullableRaw.TryReadTileHero(tile, out HeroId heroId)) continue;
                EntityId entityId = heroId.ToEntityId();
                if (!_lastHeroIdsByCell.TryGetValue(cell, out EntityId lastHeroId) ||
                    !NullableRaw.SameRawId(in lastHeroId, in entityId))
                {
                    _lastHeroIdsByCell[cell] = entityId;
                    _heroKeysByCell[cell] = entityId.ToString();
                }
                string heroKey = _heroKeysByCell[cell];
                _heroCells[heroKey] = cell;
                unchecked
                {
                    ulong mixed = (ulong)heroKey.GetHashCode() * 0x9E3779B97F4A7C15UL;
                    mixed ^= (ulong)((cell.x * 73856093) ^ (cell.y * 19349663));
                    signature += mixed;
                }
            }
            BoardSignature = signature;

            string draggedId = null;
            Vector2Int startCell = default;
            Vector3 draggedPosition = default;
            bool benchDrag = false;
            float worst = DisplacedDistance * DisplacedDistance;

            foreach (KeyValuePair<string, CharacterViewController> pair in views.Views)
            {
                CharacterViewController view = pair.Value;
                if (view == null || !views.IsHero(pair.Key)) continue;
                Vector3 position = view.transform.position;
                bool parked = position.x > ParkedX;
                if (_heroCells.TryGetValue(pair.Key, out Vector2Int cell))
                {
                    // Still owns a tile, but parked : the hero is being held over the UI, where
                    // letting go takes it off the board, to the bench or to be sold. No board
                    // prediction would be honest, and seeing this settles it on the spot.
                    if (parked) return new DragSnapshot { EntityId = pair.Key, StartCell = cell, Usable = false };
                    Vector3 center = board.PlacementGrid.GetCellCenterWorld(new Vector3Int(cell.x, cell.y, 0));
                    float distance = (position - center).sqrMagnitude;
                    if (distance <= worst) continue;
                    worst = distance;
                    draggedId = pair.Key;
                    startCell = cell;
                    draggedPosition = position;
                }
                else if (!parked)
                {
                    // A benched hero away from the park is being dragged off the bench. It owns no
                    // hex to move away from, so there is no board edit to predict.
                    benchDrag = true;
                }
            }

            if (draggedId == null) return benchDrag ? new DragSnapshot { Usable = false } : null;

            // Flatten onto the board's own height before asking which hex this is. The game
            // resolves the drop from the point on the ground, so removing the lift the drag adds
            // gives the same answer the game will give, whatever the grid's orientation.
            if (!_planeResolved)
            {
                _boardPlaneY = board.PlacementGrid.GetCellCenterWorld(Vector3Int.zero).y;
                _planeResolved = true;
            }
            draggedPosition.y = _boardPlaneY;
            Vector3Int candidate3 = board.PlacementGrid.WorldToCell(draggedPosition);
            var candidate = new Vector2Int(candidate3.x, candidate3.y);
            // The game's own two tests, asked in the same way. Free here means free of an enemy,
            // so a hex holding one of your own heroes passes, and releasing swaps the two.
            bool valid = reader.IsInPlayableRange(candidate) && reader.IsPositionFree(candidate);
            string swapId = null;
            if (valid)
            {
                foreach (KeyValuePair<string, Vector2Int> occupant in _heroCells)
                {
                    if (occupant.Value != candidate) continue;
                    // Skip the dragged hero itself : passing back over its own hex is not a swap.
                    if (string.Equals(occupant.Key, draggedId, StringComparison.Ordinal)) continue;
                    swapId = occupant.Key;
                    break;
                }
            }
            return new DragSnapshot
            {
                EntityId = draggedId,
                SwapEntityId = swapId,
                StartCell = startCell,
                CandidateCell = candidate,
                CandidateValid = valid,
                Usable = true
            };
        }
        catch (Exception e)
        {
            Reset();
            _capabilities.DisableDragPreview("drag tracking failed: " + e.Message);
            return null;
        }
    }

    public void Reset()
    {
        _heroCells.Clear();
        _heroKeysByCell.Clear();
        _lastHeroIdsByCell.Clear();
        BoardSignature = 0UL;
        _planeResolved = false;
        Healthy = false;
    }
}
