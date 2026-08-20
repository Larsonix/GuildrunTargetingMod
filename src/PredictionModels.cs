using System.Collections.Generic;
using UnityEngine;
using FPVector3 = Il2CppPhoton.Deterministic.FPVector3;

namespace GuildrunTargetingMod;

// One finished playout of one board. Everything the mod draws is read from here, and the parity
// gate compares it against the real fight afterwards.
internal sealed class PredictionResult
{
    public string ConfigHash { get; init; }
    // Identifies the board this was played out for. A layout already seen during a drag is
    // served from the cache under this key instead of being replayed.
    public string CacheKey { get; init; }
    public int Seed { get; init; }
    // Where every unit stands in the very first frame, before a single tick has run. That is not
    // the same as where the player put them : the game fires its combat-start abilities while it
    // is still building the battle, so a unit can already have been thrown across the board by
    // the time this is read. Comparing the two is the only way to see that jump.
    public Dictionary<string, Vector2Int> OpeningCells { get; init; }
    // The same opening frame in raw simulation coordinates. The parity gate compares every
    // fixed-point component exactly, proving the mirrored cell-to-world conversion continuously.
    public Dictionary<string, FPVector3> OpeningPositions { get; init; }
    // Who each unit picks the instant the fight starts. Evidence for the parity gate and nothing
    // else : a hero picks again on every hex it crosses, so this is not what the player is shown.
    public Dictionary<string, string> Tick0Targets { get; init; }
    public Dictionary<string, SettledEntity> Settled { get; init; }
    // The board this was played out on, in hexes. Carried because the only honest way to ask "could
    // the player have stood somewhere this does not reach" is to measure the ground they may stand
    // on, and that is the board itself rather than wherever the units happen to have ended up. A
    // clustered board is not a smaller board.
    public int BoardWidth { get; init; }
    public int BoardHeight { get; init; }
    // The playout ran out of ticks while units were still moving, so the picture is not final.
    public bool StillMovingAtCap { get; init; }
    public int Ticks { get; init; }
    public int PreventedDeaths { get; init; }
    public bool ImmunityWriteFailed { get; init; }
    public bool ReplayDiverged { get; init; }
    // The board kept killing somebody however it was replayed, so it was played out with deaths
    // allowed instead of producing nothing. The opening is unaffected and still what the arrows
    // claim ; the settled half simply cannot be checked against a real fight.
    public bool DeathsUnprevented { get; init; }
    // Which parts care where units stand, answered against THIS board rather than the real one.
    // That is the whole reason the placement marks can follow a hero being dragged : this playout
    // was built for the hex under the cursor, so the answer is about the board the player is
    // considering. Null when the feature is off or could not read the frame.
    public PlacementSnapshot Placement { get; init; }
}

// What the mod has to tell the player, as a state rather than a sentence. The words live in one
// place, in NativeUI, so the two languages are written together and nothing that computes a
// result also decides how it reads.
// There is no longer a notice for a fresh install or a new game build. Neither of those withholds
// anything, so neither has anything to announce : the mod runs, checks itself against the fight
// afterwards, and says nothing unless something is actually wrong.
internal enum ModNotice
{
    None,
    Disagreement,   // the prediction disagreed with two real fights running, so it is off for now.
    ReadFailure     // the mod could not read the battle reliably.
}

// Where a unit stops, who it is fighting there, and whether it is still standing. This is the one
// moment every visual describes : hover and the preview toggle read this and nothing else.
internal sealed class SettledEntity
{
    public Vector2Int Cell { get; init; }
    public string TargetPairing { get; init; }
    // A playout that runs to the tick cap can outlive a unit, so a corpse gets no ghost, no
    // movement line and no arrow.
    public bool Alive { get; init; }

    // Everyone else this unit hits, beyond the one it is paired with. Empty for almost every unit
    // in the game, because almost every attack in the game hits exactly one thing.
    //
    // This exists because the picture was previously incapable of saying "and these too". A hero
    // whose whole point is hitting the room read as identical to a hero hitting one enemy, and the
    // board could not tell a player that standing two hexes further left would have kept a second
    // hero out of it. The rules behind it are read from the simulation's own geometry, never
    // guessed : see Runner.CaptureExtraTargets.
    public IReadOnlyList<string> ExtraTargets { get; init; }

    // This unit's ordinary attacks also strike everything standing next to whatever it is aimed at.
    // Authored as a condition on a passive, so it is invisible in the unit's active ability and was
    // invisible to this mod until now. It is the single most placement-sensitive fact the bosses
    // have : the primary target is not a choice, but who is standing beside them is.
    public bool SplashesAroundTarget { get; init; }
}

// A hypothetical board edit : the contents of two hexes are exchanged. That is exactly what
// releasing a dragged hero does, so the live drag preview uses it to play out the board as if the
// drag had been released on the candidate hex. The game's own board data is left alone until the
// real drop happens.
// A hex holding a teammate is a legal drop target, and the release swaps the two heroes, so the
// second hex is often occupied and its hero really does move to the first. Modelling this as a
// one-way move puts two heroes on one hex.
internal readonly struct BoardOverride
{
    public readonly Vector2Int FromCell;
    public readonly Vector2Int ToCell;

    public BoardOverride(Vector2Int fromCell, Vector2Int toCell)
    {
        FromCell = fromCell;
        ToCell = toCell;
    }
}
