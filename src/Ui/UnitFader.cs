using System;
using System.Collections.Generic;
using Il2CppEmber.Scopes.Battle.Board.Controllers;
using Il2CppEmber.Scopes.Battle.Characters;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace GuildrunTargetingMod.Ui;

// Fades the real units down while the opening preview is on, so the ghosts, lines and tiles the
// preview draws are readable through a board of full height bodies.
//
// The characters use the game's own shader, and it has no transparency to switch on. A first
// version that tried to switch it on anyway faded nothing at all and said nothing about it. What
// works is to give each renderer a copy of itself on a plain unlit shader instead, keeping its
// own texture, at reduced opacity.
//
// The colour says the same thing the ghosts say. A unit that is not going to move is already
// standing where it ends up, so it is tinted in its team's colour and becomes its own final
// position marker, matching the ghost copies that units who do move get elsewhere. Units that
// move stay plain white, so team colour always means "this is where it ends up". Which is which
// follows the current prediction, so the tints change as the player rearranges the board.
//
// Nothing here ever edits a material the game owns. Those are shared between units, so an edit
// would spread across the board and outlive the preview. Each renderer's own materials are put
// aside and given back when the preview goes off, when placement ends, when a unit disappears,
// and on every error path.
internal sealed class UnitFader
{
    private const float FadedAlpha = 0.30f;

    private sealed class FadedRenderer
    {
        public UnityEngine.Renderer Renderer;
        public Il2CppReferenceArray<Material> Originals;
        public Material[] Materials;
        public Texture[] Textures; // kept, so a unit that starts moving gets its texture straight back.
        public int OriginalOrder;  // restored after the depth ranking has reassigned it.
    }

    private readonly struct MaterialKey : IEquatable<MaterialKey>
    {
        private readonly IntPtr _texture;
        private readonly Color _color;

        internal MaterialKey(Texture texture, Color color)
        {
            _texture = texture != null ? texture.Pointer : IntPtr.Zero;
            _color = color;
        }

        public bool Equals(MaterialKey other) => _texture == other._texture && _color.Equals(other._color);
        public override bool Equals(object obj) => obj is MaterialKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(_texture, _color);
    }

    private sealed class FadedUnit : IDepthRankedBody
    {
        public CharacterViewController View;
        public bool Hero;
        public readonly List<FadedRenderer> Renderers = new();
        public bool? TintedStationary; // null until the first tint, so the first pass always runs.
        private int _lastOrder = int.MinValue;

        public Vector3 BodyPosition => View.transform.position;
        public float RankDistance { get; set; }

        public void ApplyBodyOrder(int sortingOrder)
        {
            if (sortingOrder == _lastOrder) return;
            _lastOrder = sortingOrder;
            for (int i = 0; i < Renderers.Count; i++)
                if (Renderers[i].Renderer != null) Renderers[i].Renderer.sortingOrder = sortingOrder;
        }
    }

    private readonly Dictionary<string, FadedUnit> _units = new(StringComparer.Ordinal);
    private readonly Dictionary<MaterialKey, Material> _materials = new();
    private Shader _unlitShader;
    private bool _faulted; // latched for the session : fading is decoration, and a real fault repeats.
    private bool _loggedApply;
    private bool _materialCacheMayBeReferenced;

    // Safe to call every frame. Only units not seen before are walked, so a hero swapped in from
    // the bench fades as it arrives, and the steady state costs one lookup and one comparison
    // per unit.
    //
    // <paramref name="tintStationary"/> separates the two things this used to do at once. Fading
    // is a display choice and applies whenever the player asks for it. Tinting a unit that will
    // not move in its team colour is a claim about the fight, and it only makes sense while the
    // preview is drawing that claim everywhere else too : with the preview off, only the unit
    // being hovered has its story on screen, so colouring every other stationary unit would be
    // asserting something nothing on screen is showing.
    public void Apply(UnitViewRegistry views, PredictionResult prediction, BoardController board, DragSnapshot drag,
        bool tintStationary)
    {
        using var measured = Perf.Measure(PerfSlot.Fade);
        if (_faulted) return;
        try
        {
            if (_unlitShader == null)
            {
                // The same shader the ghost copies are drawn with, so a faded unit and a ghost
                // can be made to match exactly.
                _unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
                if (_unlitShader == null)
                {
                    _faulted = true;
                    MelonLoader.MelonLogger.Warning("[TargetingMod] unit fading disabled: URP Unlit shader unavailable");
                    return;
                }
            }
            foreach (KeyValuePair<string, CharacterViewController> pair in views.Views)
            {
                CharacterViewController view = pair.Value;
                if (view == null) continue;
                if (_units.TryGetValue(pair.Key, out FadedUnit existing))
                {
                    if (existing.View != null && existing.View.Pointer == view.Pointer) continue;
                    if (!RestoreUnit(existing)) _materialCacheMayBeReferenced = true;
                    // Same unit, new model: the old renderers are gone.
                    _units.Remove(pair.Key);
                }
                FadedUnit unit = FadeUnit(view);
                unit.Hero = views.IsHero(pair.Key);
                _units[pair.Key] = unit;
            }
            if (!_loggedApply && _units.Count > 0)
            {
                _loggedApply = true;
                MelonLoader.MelonLogger.Msg($"[TargetingMod] preview unit fade active (alpha {FadedAlpha}, {_units.Count} units)");
            }
            // No prediction this frame, because the board just changed and the new one is still
            // being computed. Keep the tints as they are rather than flickering ; the fade itself
            // follows the toggle alone and is never gated on the prediction.
            if (!tintStationary) ClearTints();
            else if (prediction != null && board != null) UpdateTints(prediction, board, drag);
        }
        catch (Exception e)
        {
            _faulted = true; // never take the preview down over the way it is shaded.
            Restore();
            MelonLoader.MelonLogger.Warning("[TargetingMod] unit fading disabled: " + e.Message);
        }
    }

    private FadedUnit FadeUnit(CharacterViewController view)
    {
        var unit = new FadedUnit { View = view };
        UnityEngine.Renderer[] renderers = view.GetComponentsInChildren<UnityEngine.Renderer>(false);
        for (int i = 0; i < renderers.Length; i++)
        {
            UnityEngine.Renderer renderer = renderers[i];
            if (renderer == null || !renderer.enabled) continue;
            // Ask the native object, never a C# type test. A wrapper reports the type it was
            // declared as, so a type test here always answers no : the same mistake in the ghost
            // builder produced ghosts made of nothing at all.
            if (renderer.TryCast<ParticleSystemRenderer>() != null || renderer.TryCast<TrailRenderer>() != null ||
                renderer.TryCast<LineRenderer>() != null) continue;
            Il2CppReferenceArray<Material> originals = renderer.sharedMaterials;
            if (originals == null || originals.Length == 0) continue;
            var faded = new Material[originals.Length];
            var textures = new Texture[originals.Length];
            for (int m = 0; m < originals.Length; m++)
            {
                textures[m] = MainTextureOf(originals[m]);
                faded[m] = MaterialFor(textures[m], new Color(1f, 1f, 1f, FadedAlpha));
            }
            int originalOrder = renderer.sortingOrder;
            renderer.sharedMaterials = faded;
            unit.Renderers.Add(new FadedRenderer
            {
                Renderer = renderer,
                Originals = originals,
                Materials = faded,
                Textures = textures,
                OriginalOrder = originalOrder
            });
        }
        return unit;
    }

    private void UpdateTints(PredictionResult prediction, BoardController board, DragSnapshot drag)
    {
        foreach (KeyValuePair<string, FadedUnit> pair in _units)
        {
            FadedUnit unit = pair.Value;
            if (unit.View == null) continue;
            bool stationary = false;
            if (prediction.Settled.TryGetValue(pair.Key, out SettledEntity settled))
            {
                // The same after-the-drop hexes the overlay uses, so a dragged or swapped hero is
                // tinted for where it will stand rather than where it currently appears.
                Vector3Int current = drag != null && drag.TryGetAnchorCell(pair.Key, out Vector2Int anchor)
                    ? new Vector3Int(anchor.x, anchor.y, 0)
                    : board.PlacementGrid.WorldToCell(unit.View.transform.position);
                stationary = settled.Cell.x == current.x && settled.Cell.y == current.y;
            }
            if (unit.TintedStationary == stationary) continue;
            unit.TintedStationary = stationary;
            ApplyTint(unit, stationary);
        }
    }

    // Puts every unit back to the plain faded look, for when the fade is on but the preview is
    // not. A unit that was never tinted is already plain, because that is how its materials were
    // built, so only the ones actually carrying a team colour are touched.
    private void ClearTints()
    {
        foreach (KeyValuePair<string, FadedUnit> pair in _units)
        {
            FadedUnit unit = pair.Value;
            if (unit.View == null || unit.TintedStationary != true) continue;
            unit.TintedStationary = false;
            ApplyTint(unit, false);
        }
    }

    private void ApplyTint(FadedUnit unit, bool stationary)
    {
        // A unit that stays put is drawn exactly like a ghost, only still animated : flat team
        // colour, no texture. Tinting the texture instead can only darken it, so it never reaches
        // the ghosts' flat colour. On a red enemy it came out looking like no colour at all.
        // Units that move get their texture back.
        Color tint = stationary
            ? (unit.Hero ? OverlayRenderer.HeroColor : OverlayRenderer.EnemyColor)
            : Color.white;
        // The same opacity the ghosts use, not the fade's. These two have to be the same colour on
        // screen, and at the fade's opacity they read dimmer than the ghosts do.
        tint.a = stationary ? OverlayRenderer.GhostAlpha : FadedAlpha;
        for (int i = 0; i < unit.Renderers.Count; i++)
        {
            FadedRenderer entry = unit.Renderers[i];
            if (entry.Renderer == null) continue;
            for (int m = 0; m < entry.Materials.Length; m++)
                entry.Materials[m] = MaterialFor(stationary ? null : entry.Textures[m], tint);
            entry.Renderer.sharedMaterials = entry.Materials;
        }
    }

    private Material MaterialFor(Texture texture, Color color)
    {
        var key = new MaterialKey(texture, color);
        if (_materials.TryGetValue(key, out Material material)) return material;
        material = new Material(_unlitShader) { name = "TargetingFade.Shared", renderQueue = 2985 };
        // Writes depth, like the ghosts do, so a see-through body hides its own far side instead
        // of showing an arm through a chest. The overlay pulls its lines toward the camera, so
        // they still draw over the top. Sharing the material adds no property block, which keeps
        // these renderers eligible for the SRP Batcher exactly as they were before.
        OverlayRenderer.ConfigureTransparent(material, 1f);
        if (texture != null) material.SetTexture("_BaseMap", texture);
        material.SetColor("_BaseColor", color);
        _materials[key] = material;
        return material;
    }

    // The character shader can name its texture either of two ways, depending on which Unity
    // convention it was written to, so try both before the general accessor. Nothing found just
    // means this slot becomes an untextured silhouette.
    private static Texture MainTextureOf(Material source)
    {
        if (source == null) return null;
        if (source.HasProperty("_BaseMap"))
        {
            Texture texture = source.GetTexture("_BaseMap");
            if (texture != null) return texture;
        }
        if (source.HasProperty("_MainTex"))
        {
            Texture texture = source.GetTexture("_MainTex");
            if (texture != null) return texture;
        }
        try { return source.mainTexture; } catch { return null; }
    }

    // The overlay sorts everything see-through from far to near each frame and hands back a draw
    // order through here. Only units that are still alive on screen take part.
    public void CollectBodies(List<IDepthRankedBody> into)
    {
        foreach (FadedUnit unit in _units.Values)
            if (unit.View != null) into.Add(unit);
    }

    public void Restore()
    {
        foreach (FadedUnit unit in _units.Values)
            if (!RestoreUnit(unit)) _materialCacheMayBeReferenced = true;
        _units.Clear();
        if (_materialCacheMayBeReferenced) return;
        foreach (Material material in _materials.Values)
            if (material != null) UnityEngine.Object.Destroy(material);
        _materials.Clear();
    }

    private bool RestoreUnit(FadedUnit unit)
    {
        bool restored = true;
        for (int i = 0; i < unit.Renderers.Count; i++)
        {
            FadedRenderer entry = unit.Renderers[i];
            try
            {
                if (entry.Renderer != null)
                {
                    entry.Renderer.sharedMaterials = entry.Originals;
                    entry.Renderer.sortingOrder = entry.OriginalOrder;
                }
            }
            catch
            {
                // Usually the unit disappeared mid preview. Retaining the shared cache is safer
                // than destroying a material if an unusual live renderer rejected its restore.
                restored = false;
            }
        }
        unit.Renderers.Clear();
        return restored;
    }
}
