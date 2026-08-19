using System;
using System.Collections.Generic;
using GuildrunTargetingMod.Ui;
using Il2CppEmber.Scopes.Battle;
using Il2CppEmber.Scopes.Battle.BattleSimulation.Data;
using Il2CppEmber.Simulation.Core.Utilities;
using Il2CppEmber.Utilities;
using MelonLoader;
using UnityEngine;
using FPVector3 = Il2CppPhoton.Deterministic.FPVector3;

namespace GuildrunTargetingMod.ShadowSim;

// Where a hex is, in the simulation's own coordinates, taken from the table the game itself reads.
//
// The obvious way to answer this is HexGridUtils.GetCellCenterWorldPosition, the game's own hex
// formula, and that is what the mod used to do. It is wrong, and it is worth writing down why,
// because nothing about it looks wrong and it cost the whole preview.
//
// The game does not use that formula when it builds a battle. CreateBattleConfig reads
// SimulationDebugConfig.GetCellToWorldLookup(), a table computed once in the EDITOR from Unity's
// floating point Grid and stored through FP.FromFloat_UNSAFE. A hex's width carries a square root
// of three ; float and fixed point round it differently. So the two answers differ by a handful of
// sixty-five-thousandths of a world unit, on the X axis only, because the Z axis is a whole
// multiple of the cell size and lands exactly either way.
//
// That difference is about one fifteen-thousandth of a hex. It has never changed a target, a path
// or a settled position. What it did do was make every unit's opening position disagree with the
// real battle by a few raw units, which the self-check compared for exact equality, called a
// disagreement, and answered by switching the preview off and blaming a game update that had not
// happened. Measured 2026-08-18 : three battles in a row, every one of them behaviourally perfect.
//
// Reading the table also means the mod follows Leyline if they ever move the grid, which is the
// same reason every other answer in this mod is read rather than reimplemented.
//
// The formula stays as the fallback. It is what shipped, it predicts correctly, and the parity gate
// now treats a sub-hex position difference as a note rather than a verdict, so falling back costs
// nothing but exactness.
internal sealed class CellWorldTable
{
    // Finding the scope walks every loaded object, so a scene that never produces one must not be
    // searched again on every rebuild for the rest of the placement. A placement that has not found
    // it within this many rebuilds is a placement without one, and the formula answers instead.
    private const int MaxAttempts = 20;

    private readonly Dictionary<Vector2Int, FPVector3> _cells = new();
    private int _attempts;
    private bool _logged;

    /// <summary>True once the game's own table has been read. False means the formula is answering.</summary>
    public bool HasGameTable { get; private set; }

    /// <summary>
    /// Copies this board's hexes out of the game's table. Cheap to call repeatedly : it searches at
    /// most once per rebuild and not at all once it has an answer or has given up for this
    /// placement.
    /// </summary>
    /// <remarks>
    /// The board's own hexes and no others, so this is a few dozen numbers rather than the ten
    /// thousand the game bakes. Copying them out and letting the game's dictionary go also means no
    /// il2cpp object is held across frames, which is a lifetime question worth not having.
    ///
    /// Deliberately never disables anything. A scene still assembling itself has no BattleScope
    /// yet, which is an ordinary condition and not a fault, and every miss simply leaves the
    /// formula answering.
    /// </remarks>
    public void Resolve(int boardWidth, int boardHeight)
    {
        if (HasGameTable || _attempts >= MaxAttempts) return;
        if (boardWidth <= 0 || boardHeight <= 0) return;
        _attempts++;
        try
        {
            BattleScope scope = RuntimeDiscovery.FindLive<BattleScope>();
            if (scope == null) return;

            SimulationDebugConfig config = scope._debugConfig;
            if (config == null) return;
            // Asked of the array before the method that walks it. GetCellToWorldLookup runs a LINQ
            // ToDictionary straight over this field, so an unbaked config would throw inside the
            // game's own code rather than answer nothing.
            var baked = config._cellToWorldPositions;
            if (baked == null || baked.Length == 0) return;

            var lookup = config.GetCellToWorldLookup();
            if (lookup == null || lookup.Count == 0) return;

            int copied = 0;
            for (int x = 0; x < boardWidth; x++)
            {
                for (int y = 0; y < boardHeight; y++)
                {
                    var cell = new Vector2Int(x, y);
                    if (!lookup.TryGetValue(cell, out FPVector3 world)) continue;
                    _cells[cell] = world;
                    copied++;
                }
            }
            // An empty copy is not an answer, so it is not recorded as one and the next rebuild
            // tries again.
            if (copied == 0) return;
            HasGameTable = true;
            if (_logged) return;
            _logged = true;
            MelonLogger.Msg($"[TargetingMod] cell positions read from the game's own baked table: " +
                            $"{copied} of the board's {boardWidth}x{boardHeight} hexes");
        }
        catch (Exception e)
        {
            if (_logged) return;
            _logged = true;
            MelonLogger.Warning("[TargetingMod] the game's cell position table could not be read; " +
                                "falling back to the hex formula, which predicts correctly and " +
                                "differs only in the last few fixed-point digits. Full fault follows:\n" + e);
        }
    }

    /// <summary>The position the game itself would place a unit at on this hex.</summary>
    public FPVector3 For(Vector2Int cell) =>
        _cells.TryGetValue(cell, out FPVector3 world)
            ? world
            : HexGridUtils.GetCellCenterWorldPosition(cell, EmberConstants.HexCellSize);

    /// <summary>Forgets the table, so the next battle scene reads its own.</summary>
    public void Clear()
    {
        HasGameTable = false;
        _attempts = 0;
        _cells.Clear();
    }
}
