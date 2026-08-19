using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using GuildrunTargetingMod.Interop;
using Il2CppEmber.Balancing.SimulationBridge;
using Il2CppEmber.Scopes.Battle.Board.Data;
using Il2CppEmber.Scopes.GameRun.Effects.Data;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data.Characters;
using Il2CppEmber.Scopes.GameRun.Player.Data;
using Il2CppEmber.Scopes.GameRun.RunSession.Data;
using Il2CppEmber.Simulation.Core.Config;
using Il2CppEmber.Simulation.Core.Dto;
using Il2CppEmber.Simulation.Core.State.Effects;
using Il2CppEmber.Simulation.Core.Utilities;
using Il2CppEmber.Simulation.UnityRuntime.Serialization;
using Il2CppEmber.Utilities;
using Il2Cppgg.leyline.core.Mvcs.Model;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;
using FP = Il2CppPhoton.Deterministic.FP;
using FPVector3 = Il2CppPhoton.Deterministic.FPVector3;

namespace GuildrunTargetingMod.ShadowSim;

// Builds the battle configuration the game would build if the fight started right now : the same
// seed, the same board, the same heroes, enemies, reserve, relics and effects.
//
// The mod rebuilds it instead of calling the game's own builder, because that call also writes a
// row to the run's battle log. A preview must leave no trace, so the shape is mirrored here and
// the game's version is never touched. Everything this reads is read only ; the drag preview's
// hypothetical board edit is applied to the copy on the way out, never to the game's board.
internal sealed class ConfigMirror
{
    private readonly Capabilities _capabilities;
    private readonly ConfigSerializer _serializer = new();
    private readonly CellWorldTable _cells = new();
    private string _lastError;

    public ConfigMirror(Capabilities capabilities) => _capabilities = capabilities;
    public bool Faulted { get; private set; }

    /// <summary>Forgets the battle scene's cell position table. Called when placement ends.</summary>
    public void LeavePlacement() => _cells.Clear();

    public bool TryBuild(string buildGuid, BoardOverride? drag, out BattleConfig config, out string configHash, out string cacheKey)
    {
        config = null;
        configHash = null;
        cacheKey = null;
        try
        {
            if (!TryReaders(out var board, out var registry, out var effects, out var player, out var run))
                return false;
            // Where the game says each hex is. Read once per placement, for this board's hexes
            // only ; see CellWorldTable for why the hex formula is not the same answer.
            _cells.Resolve(board.BoardWidth, board.BoardHeight);

            int shards = player.CurrentShards.CurrentValue;
            // Endless counts its own floor, and the seed is folded from that one while endless is
            // running. Always taking the normal floor index would mispredict every endless fight.
            int floor = run.IsEndlessMode
                ? run.CurrentEndlessFloor.CurrentValue.GetDecrypted()
                : run.CurrentFloorIndex.CurrentValue;
            int runSeed = run.RunSeed.CurrentValue;
            int seed = PredictSeed(shards, (int)BattleFlowState.Resolution, floor, runSeed);

            var heroes = new List<CharacterDto>();
            var enemies = new List<CharacterDto>();

            // Match BoardData.GetAllTiles exactly. Its generated coordinate order becomes DTO
            // order, which breaks pathfinding ties by deciding which unit reserves a hex first.
            for (int x = 0; x < board.BoardWidth; x++)
            {
                for (int y = 0; y < board.BoardHeight; y++)
                {
                    var cell = new Vector2Int(x, y);
                    if (!board.Data.Board.TryGetValue(cell, out TileInfo tile) || tile == null) continue;
                    // The drag preview's board edit : the two hexes exchange occupants, which is what
                    // the release itself does. Enemies are never moved, since an enemy hex can be
                    // neither the hero's origin nor a legal destination.
                    Vector2Int placed = cell;
                    if (drag.HasValue)
                    {
                        if (cell == drag.Value.FromCell) placed = drag.Value.ToCell;
                        else if (cell == drag.Value.ToCell) placed = drag.Value.FromCell;
                    }
                    // Read from the game's own table rather than computed from the hex formula. The
                    // two are not the same number, and comparing the computed one against the real
                    // battle is what used to switch the preview off.
                    FPVector3 world = _cells.For(placed);
                    if (NullableRaw.TryReadTileHero(tile, out HeroId heroId))
                    {
                        if (!registry.Data.Heroes.TryGetValue(heroId, out HeroData hero) || hero == null)
                            throw new DecodeFaultException("decoded TileInfo.HeroId does not resolve in GameRegistry");
                        heroes.Add(hero.ToDto(world, false));
                    }
                    else if (NullableRaw.TryReadTileEnemy(tile, out EnemyId enemyId))
                    {
                        if (!registry.Data.Enemies.TryGetValue(enemyId, out EnemyData enemy) || enemy == null)
                            throw new DecodeFaultException("decoded TileInfo.EnemyId does not resolve in GameRegistry");
                        enemies.Add(enemy.ToDto(world, true));
                    }
                }
            }

            // Benched heroes go in at the origin, exactly as the game sends them. They never take
            // the field, but relics and effects that read the bench need them present.
            var reserve = new List<CharacterDto>();
            foreach (HeroData hero in registry.Data.Heroes.Values)
                if (hero != null && registry.IsHeroInReserve(hero.HeroId))
                    reserve.Add(hero.ToDto(FPVector3.Zero, false));

            var effectDtos = BuildEffects(effects, registry, run);
            var relicDtos = new List<RelicDto>();
            foreach (var relic in player.Data.ActiveRelics.Values)
                if (relic != null) relicDtos.Add(relic.ToDto());

            // This generous duration only feeds EndConditionSystem.IsTimedLimitReached. The
            // playout stops at arrival and never reaches it, so it need not match the serialized
            // duration configured by the installed game. The empty thresholds list does match.
            config = new BattleConfig
            {
                Seed = seed,
                BoardWidth = board.BoardWidth,
                BoardHeight = board.BoardHeight,
                CellSize = EmberConstants.HexCellSize,
                BattleDuration = FP._10 * 60,
                HeroDtos = heroes.ToArray(),
                EnemyDtos = enemies.ToArray(),
                ReserveHeroDtos = reserve.ToArray(),
                EffectDtos = effectDtos.ToArray(),
                ActiveRelics = relicDtos.ToArray(),
                CurrentPlayerShards = shards,
                GlobalPermanentCustomData = registry.GlobalPermanentCustomData,
                CurrentChunkIndex = run.CurrentChunkIndex,
                IsBossFloor = run.IsBossFloor,
                FightMode = run.CurrentFightMode,
                FightModeThresholds = Array.Empty<int>()
            };

            // The cache key has to change whenever the board does, or a stale playout is served
            // for a board the player has since edited. Serializing the config gets most of the
            // way there, but the serializer deliberately skips the enemy and effect lists, so a
            // change with no stat difference behind it, such as moving an item between heroes,
            // would serialize identically. Fold the effect ids and enemy entity ids in by hand.
            // Enemy ids are stable within placement but are minted again for the next battle.
            var effectKey = new StringBuilder();
            foreach (EffectDto dto in effectDtos) effectKey.Append(dto.Id.ToString()).Append(';');
            var enemyKey = new StringBuilder();
            foreach (CharacterDto dto in enemies) enemyKey.Append(dto.EntityId.ToString()).Append(';');
            configHash = Sha256(_serializer.Serialize(config) + "|fx|" + effectKey + "|en|" + enemyKey);
            cacheKey = Sha256(buildGuid + "|" + configHash + "|" + shards + "|" + floor + "|" + runSeed);
            return true;
        }
        catch (DecodeFaultException e)
        {
            // A decoded id that resolves to nothing means the raw field offsets are wrong, which
            // is permanent for this session and not something a retry can fix. Latch it and stop
            // reading the game at all rather than predicting from bad data.
            Faulted = true;
            _capabilities.DisableCoreRead(e.Message);
            _capabilities.DisablePrediction(e.Message);
            return false;
        }
        catch (Exception e)
        {
            // Not latched, unlike the fault above. A reader caught mid setup, or a scope changing
            // underneath us, is a passing condition, and treating it as permanent once cost a
            // whole session of silently unavailable battles. Log each distinct message once and
            // try again on the next rebuild.
            if (_lastError != e.Message)
            {
                _lastError = e.Message;
                MelonLogger.Error("[TargetingMod] ConfigMirror failed (will retry): " + e);
            }
            return false;
        }
    }

    private static List<EffectDto> BuildEffects(
        EffectsReader effects,
        GameRegistryDataReader registry,
        RunSessionDataReader run)
    {
        var result = new List<EffectDto>();
        // Called for its shape only. The boot self-check needs to know this member still exists,
        // but the sequence it returns is an interface iterator and must not be walked.
        _ = effects.GetActiveEffects();
        foreach (EffectData effect in effects.Data.Effects.Values)
        {
            if (effect == null) continue;
            var instance = effect.EffectInstance as Il2CppObjectBase;
            if (instance == null) continue;

            bool ownerItem = NullableRaw.HasNullableAt<ItemId>(instance, "<OwnerItemId>k__BackingField");
            bool ownerEntity = NullableRaw.TryReadEntityIdAt(instance, "<OwnerEntityId>k__BackingField", out EntityId entityId);
            // The same filter the game applies, in the same order. An item's effect counts only
            // while the item is equipped on someone, so an item sitting in the inventory with no
            // owner is skipped.
            if (ownerItem && !ownerEntity) continue;
            if (effects.IsQuestEffectCompleted(effect.Id)) continue;
            if (!ownerEntity)
            {
                if (!ownerItem) result.Add(effect.ToDto());
                continue;
            }

            HeroId heroId = HeroId.FromEntityId(entityId);
            // A bench-only effect applies while its hero is benched, and every other owned effect
            // applies while its hero is not.
            if (effect.IsReserveOnlyEffect)
            {
                if (registry.IsHeroInReserve(heroId)) result.Add(effect.ToDto());
                continue;
            }
            // A duel is fought by one hero, so only that hero's effects come along.
            if (run.CurrentFightMode == Il2CppEmber.Balancing.Sheets.Encounters.FightModes.FightModeType.OneOnOne &&
                registry.OneOnOneHero != null)
            {
                if (registry.OneOnOneHero.HeroId.ToEntityId() == entityId) result.Add(effect.ToDto());
                continue;
            }
            // The game asks the bench list directly here. This asks the reader's own predicate for
            // the same answer, which avoids reaching into a list of nullable ids through interop.
            if (!registry.IsHeroInReserve(heroId)) result.Add(effect.ToDto());
        }
        return result;
    }

    private static bool TryReaders(
        out BoardDataReader board,
        out GameRegistryDataReader registry,
        out EffectsReader effects,
        out PlayerReader player,
        out RunSessionDataReader run)
    {
        board = null;
        registry = null;
        effects = null;
        player = null;
        run = null;
        return DataReaders.TryGet(out board) && board != null &&
               DataReaders.TryGet(out registry) && registry != null &&
               DataReaders.TryGet(out effects) && effects != null &&
               DataReaders.TryGet(out player) && player != null &&
               DataReaders.TryGet(out run) && run != null;
    }

    // The game's own seed derivation, reproduced. The four inputs are the run's seed, the floor,
    // the flow state the fight starts in, and the shard count, so a shard spent during placement
    // really does change the fight. Getting this wrong is not a subtle error : the parity gate
    // compares the predicted seed against the real one and shuts the preview off on a mismatch.
    internal static int PredictSeed(int shards, int state, int floor, int runSeed)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + shards;
            h = h * 31 + state;
            h = h * 31 + floor;
            h = h * 31 + runSeed;
            return h;
        }
    }

    private static string Sha256(string value)
    {
        using SHA256 hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    // Raised when a raw field read produces an id that the game does not recognise. That means
    // the memory layout the mod resolved at startup is wrong, not that the board is unusual.
    private sealed class DecodeFaultException : Exception
    {
        public DecodeFaultException(string message) : base(message) { }
    }
}
