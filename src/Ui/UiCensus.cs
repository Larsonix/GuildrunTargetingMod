using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2CppEmber.Scopes.Battle.Characters;
using Il2CppTMPro;
using UnityEngine;

namespace GuildrunTargetingMod.Ui;

// Writes down everything the mod resolved out of the game, once, when verbose logging is on.
//
// This exists for the report that says "nothing appeared". Rather than asking the player to try
// things, the file names the canvas the buttons went under, the font and colours that were
// picked up, the camera the hover raycast uses and what a unit's model is made of. After a game
// update it is also the fastest way to see what moved.
internal sealed class UiCensus
{
    private readonly string _path;
    private bool _written;

    public UiCensus(string userDataRoot) =>
        _path = Path.Combine(userDataRoot, "GuildrunTargetingMod", "ui_census.json");

    public void TryWrite(bool devLog, Bindings bindings, NativeUI ui, OverlayRenderer overlay,
        HoverService hover, UnitViewRegistry views)
    {
        if (_written || !devLog || views.Views.Count == 0) return;
        try
        {
            var canvases = new List<object>();
            var roots = new List<Canvas>();
            if (ui?.PlacementRoot != null)
            {
                Canvas[] parents = ui.PlacementRoot.GetComponentsInParent<Canvas>(true);
                for (int i = 0; i < parents.Length; i++) roots.Add(parents[i]);
            }
            else
            {
                List<Canvas> discovered = RuntimeDiscovery.FindAll<Canvas>();
                for (int i = 0; i < discovered.Count; i++)
                    if (discovered[i] != null && discovered[i].gameObject.scene.IsValid()) roots.Add(discovered[i]);
            }
            for (int i = 0; i < roots.Count; i++)
                canvases.Add(new
                {
                    name = roots[i].name,
                    path = RuntimeDiscovery.HierarchyPath(roots[i].transform),
                    sortingOrder = roots[i].sortingOrder,
                    renderMode = roots[i].renderMode.ToString(),
                    worldCamera = roots[i].worldCamera != null ? roots[i].worldCamera.name : null
                });

            var fonts = new List<string>();
            List<TMP_FontAsset> allFonts = RuntimeDiscovery.FindAll<TMP_FontAsset>();
            for (int i = 0; i < allFonts.Count; i++)
            {
                string name = allFonts[i]?.name;
                if (!string.IsNullOrEmpty(name) && name.IndexOf("Mendl", StringComparison.OrdinalIgnoreCase) >= 0 && !fonts.Contains(name))
                    fonts.Add(name);
            }

            CharacterViewController sample = null;
            foreach (CharacterViewController view in views.Views.Values) { sample = view; break; }
            Collider collider = sample != null ? sample.GetComponentInChildren<Collider>(true) : null;
            string ghostProperty = ProbeGhostProperty(sample);
            var rendererTypes = new List<string>();
            if (sample != null)
            {
                UnityEngine.Renderer[] renderers = sample.GetComponentsInChildren<UnityEngine.Renderer>(true);
                for (int i = 0; i < renderers.Length && rendererTypes.Count < 12; i++)
                {
                    string typeName = Il2CppTypeName(renderers[i]);
                    if (typeName != null && !rendererTypes.Contains(typeName)) rendererTypes.Add(typeName);
                }
            }
            var cameras = new List<object>();
            if (hover?.BattleCamera != null)
            {
                cameras.Add(new
                {
                    role = "BattleInputHandlerContext/GameRenderCamera",
                    name = hover.BattleCamera.name,
                    path = RuntimeDiscovery.HierarchyPath(hover.BattleCamera.transform),
                    depth = hover.BattleCamera.depth,
                    cullingMask = hover.BattleCamera.cullingMask
                });
            }
            for (int i = 0; i < roots.Count; i++)
            {
                Camera uiCamera = roots[i].worldCamera;
                if (uiCamera == null || uiCamera == hover?.BattleCamera) continue;
                cameras.Add(new
                {
                    role = "UI canvas camera",
                    name = uiCamera.name,
                    path = RuntimeDiscovery.HierarchyPath(uiCamera.transform),
                    depth = uiCamera.depth,
                    cullingMask = uiCamera.cullingMask
                });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new
            {
                buildGuid = bindings.BuildGuid,
                stage = StageIdentity.Read(),
                resolvedAtUtc = DateTime.UtcNow,
                roots = new[] { ui?.PlacementRoot != null ? RuntimeDiscovery.HierarchyPath(ui.PlacementRoot.transform) : "placement root unresolved" },
                canvases,
                fontsFound = fonts,
                selectedFont = ui.FontDiagnostic,
                toggleClonePath = ui.ToggleClonePath,
                cameras,
                sampleUnit = sample == null ? null : new
                {
                    name = sample.gameObject.name,
                    path = RuntimeDiscovery.HierarchyPath(sample.transform),
                    // Ask the object what it is. The managed wrapper only ever reports the type
                    // it was declared as, which for a collider is always just "Collider".
                    colliderType = collider != null ? Il2CppTypeName(collider) ?? collider.GetType().Name : "unavailable",
                    rendererTypes
                },
                ghostShaderProperty = ghostProperty,
                runtimeTeamColors = overlay.ColorDiagnostic,
                arrowSpriteAndTileAssets = "resolved from live Resources / BoardController; see TargetingMod boot log"
            }, new JsonSerializerOptions { WriteIndented = true }));
            _written = true;
            MelonLoader.MelonLogger.Msg("[TargetingMod] UI census wrote " + _path);
        }
        catch (Exception e)
        {
            _written = true; // once per session, a failure included, so it cannot repeat per frame.
            MelonLoader.MelonLogger.Error("[TargetingMod] UI census failed: " + e);
        }
    }

    private static string Il2CppTypeName(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase obj)
    {
        if (obj == null) return null;
        try { return obj.TryCast<Il2CppSystem.Object>()?.GetIl2CppType()?.Name; }
        catch { return null; }
    }

    // Which colour property a unit's material answers to. The two names below are the same idea
    // from two Unity eras, and a character shader can expose either, so the ghost copies have to
    // know which one to write before they can be tinted at all.
    private static string ProbeGhostProperty(CharacterViewController sample)
    {
        if (sample == null) return "unavailable";
        UnityEngine.Renderer[] renderers = sample.GetComponentsInChildren<UnityEngine.Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Material[] materials = renderers[i].sharedMaterials;
            for (int j = 0; j < materials.Length; j++)
                if (materials[j] != null && materials[j].HasProperty("_BaseColor")) return "_BaseColor";
        }
        for (int i = 0; i < renderers.Length; i++)
        {
            Material[] materials = renderers[i].sharedMaterials;
            for (int j = 0; j < materials.Length; j++)
                if (materials[j] != null && materials[j].HasProperty("_Color")) return "_Color";
        }
        return "solid-unlit-fallback";
    }
}
