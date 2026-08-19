using System;
using System.Collections.Generic;
using Il2CppEmber.Scopes.Battle.Board.Controllers;
using Il2CppEmber.Scopes.Battle.Characters;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data;
using Il2Cppgg.leyline.core.Mvcs.Model;

namespace GuildrunTargetingMod.Ui;

// Ties the units in the prediction to the models standing on the board.
//
// The prediction talks in unit ids and the screen has models, so everything the mod draws needs
// this map in both directions : an id to find the model to copy or fade, and a model to name the
// unit the pointer is over. Rebuilt whenever the board changes.
internal sealed class UnitViewRegistry
{
    private readonly Dictionary<string, CharacterViewController> _views = new(StringComparer.Ordinal);
    private readonly HashSet<string> _heroes = new(StringComparer.Ordinal);
    private readonly Dictionary<IntPtr, string> _idsByView = new();
    private readonly HashSet<IntPtr> _heroViewPointers = new();

    public Dictionary<string, CharacterViewController> Views => _views;
    public string CacheKey { get; private set; }

    /// <summary>
    /// Bumped every time the map is rebuilt or cleared, so anything drawn FROM this map can tell
    /// whether it is looking at the same set of units it drew last time. Cheaper and more reliable
    /// than comparing the map itself, and it cannot report equal across a rebuild that happened to
    /// produce the same count.
    /// </summary>
    public int Version { get; private set; }

    public bool TryGetView(string entityId, out CharacterViewController view) => _views.TryGetValue(entityId, out view);
    public bool IsHero(string entityId) => _heroes.Contains(entityId);

    public bool MatchesBoard(BoardController board)
    {
        if (board == null || CacheKey == null || board.CharacterViewControllers == null) return false;
        if (board.CharacterViewControllers.Count != _heroViewPointers.Count) return false;
        foreach (CharacterViewController view in board.CharacterViewControllers.Values)
        {
            if (!IsLive(view) || !_heroViewPointers.Contains(view.Pointer)) return false;
        }
        foreach (CharacterViewController view in _views.Values)
        {
            if (!IsLive(view)) return false;
        }
        return true;
    }

    private static bool IsLive(CharacterViewController view)
    {
        try { return view != null && view.gameObject != null && view.gameObject.scene.IsValid(); }
        catch { return false; }
    }

    // Keyed on the native object rather than the wrapper : two wrappers can stand for the same
    // object, so comparing wrappers is not a reliable test of identity.
    public bool TryGetId(CharacterViewController view, out string entityId)
    {
        entityId = null;
        return view != null && _idsByView.TryGetValue(view.Pointer, out entityId);
    }

    public bool Rebuild(BoardController board, string cacheKey)
    {
        _views.Clear();
        _heroes.Clear();
        _idsByView.Clear();
        _heroViewPointers.Clear();
        CacheKey = cacheKey;
        Version++;
        if (board == null || !DataReaders.TryGet<GameRegistryDataReader>(out var registry) || registry == null) return false;

        // Heroes come from the board's own list. Enumerate the concrete collection : walking one
        // of these through an interface can bring the process down.
        foreach (CharacterViewController view in board.CharacterViewControllers.Values)
        {
            if (view == null) continue;
            string id = view.EntityId.ToString();
            if (string.IsNullOrEmpty(id)) continue;
            _views[id] = view;
            _idsByView[view.Pointer] = id;
            _heroViewPointers.Add(view.Pointer);
            _heroes.Add(id);
        }

        // Enemies have no equivalent list, so the scene is swept instead. The registry lookup is
        // the point of the loop, not a detail : it is what proves a model really is an enemy in
        // this battle, rather than trusting whatever order the sweep happened to return.
        List<CharacterViewController> all = RuntimeDiscovery.FindAll<CharacterViewController>();
        for (int i = 0; i < all.Count; i++)
        {
            CharacterViewController view = all[i];
            if (view == null || !view.gameObject.scene.IsValid()) continue;
            string id = view.EntityId.ToString();
            if (string.IsNullOrEmpty(id) || _views.ContainsKey(id)) continue;
            try
            {
                if (!registry.TryGetEnemyData(view.EntityId, out var enemy) || enemy == null) continue;
                _views[id] = view;
                _idsByView[view.Pointer] = id;
            }
            catch { /* the registry is being torn down ; this model is simply not usable. */ }
        }
        return true;
    }

    public void Clear()
    {
        _views.Clear();
        _heroes.Clear();
        _idsByView.Clear();
        _heroViewPointers.Clear();
        CacheKey = null;
        Version++;
    }
}
