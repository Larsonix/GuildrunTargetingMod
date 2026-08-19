using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace GuildrunTargetingMod.Ui;

// Draws the mark's border, and the two lights that travel it, as a sprite of our own.
//
// WHY A TEXTURE AND NOT MOVING OBJECTS. The obvious build is a static border plus two little
// travelling images. It does not survive the shape : the border follows the frame's centre
// ornament, so on three of the five rarities the path bends, and a travelling quad is straight. It
// is also the expensive build, because moving anything in a canvas dirties that canvas, and the
// cost then scales with how many icons are marked at once.
//
// The lap is GLOBAL. Every mark on screen is at the same point of the same lap, by design, so two
// icons of the same rarity want a pixel for pixel identical picture. That turns the whole thing
// around : one animated texture per FRAME SPRITE, shared by every icon showing it. A full item row
// of blocked commons costs exactly what one costs, the path is followed exactly because the border
// and the light are stamped by the same walk, and nothing in the canvas moves at all.
//
// The cost that remains is the upload, once per rarity per step of the lap, and it is measured
// rather than assumed. Only the pixels the previous light touched are restored before the next one
// is stamped, so the work per step is the light's own footprint and not the whole frame.
/// <summary>Where a mark's picture goes on the frame it was traced from, in that sprite's pixels.</summary>
/// <remarks>
/// Every field is trivial for a frame that stands square : no turn, no offset, and extents equal to
/// the sprite. They carry weight only for a turned frame, where the picture is a square standing
/// upright inside a sprite that is a diamond, so it has to be sized, moved and turned back.
/// </remarks>
internal readonly struct MarkPlacement
{
    public readonly float AngleDegrees;
    public readonly float OffsetX, OffsetY;      // from the sprite's centre, in its pixels, y UP
    public readonly float Width, Height;         // in the sprite's pixels
    public readonly float SourceWidth, SourceHeight;

    public MarkPlacement(FrameContour contour)
    {
        AngleDegrees = contour.AngleDegrees;
        OffsetX = contour.OffsetX;
        OffsetY = contour.OffsetY;
        Width = contour.Width;
        Height = contour.Height;
        SourceWidth = contour.SourceWidth;
        SourceHeight = contour.SourceHeight;
    }

    public bool Valid => SourceWidth > 0f && SourceHeight > 0f;
    public bool Turned => AngleDegrees != 0f;
}

internal static class MarkArt
{
    // The mark's red. Read from the overlay rather than restated : two copies of a colour become
    // two colours the first time one of them is adjusted.
    private static Color32 Red => OverlayRenderer.GlowBlockedMarkColor;
    private const float BorderAlpha = 0.95f;
    // Share of the sprite, and deliberately applied PER AXIS. The frame is 222 by 228 and is not
    // square, so one "share of the slot" would draw the top and the side at different weights. This
    // is the denominator that moved three times during the design ; it is not a rounding detail.
    private const float BorderWeight = 0.040f;
    private const float GlintSpan = 0.025f;    // half length of the lens, as a share of the path
    private const float GlintBulge = 2.0f;     // border widths thick at the ball
    private const float GlintSolid = 0.55f;    // the SHAPE tapers, the brightness mostly does not
    private const float GlintShaped = 0.45f;
    // Steps in one lap. The reference render used sixty, and at the default lap that is a step
    // every sixty milliseconds, which reads as smooth travel rather than as a blink.
    private const int Steps = 60;
    // One readback is a stall on the graphics thread. Five rarities in one frame would be five of
    // them at once, on the frame a panel opens, which is the worst possible moment. One per frame
    // spreads it and nothing waits longer than five frames for a picture it did not have.
    private const int TracesPerFrame = 1;

    private static readonly Dictionary<int, Entry> _entries = new();
    private static readonly List<int> _touched = new(4096);
    private static float _phase;
    private static int _step = -1;
    private static int _tracesLeft;
    private static double _worstBuildMs;
    private static double _worstStepMs;
    private static int _flatFallbacks;
    private static int _unsupported;

    /// <summary>Moves the lap on. Call once a frame, before anything asks for a border.</summary>
    /// <remarks>
    /// Unscaled time on purpose. Placement is exactly when this feature matters and exactly when
    /// the game is most likely to have stopped the clock, and a mark that only animates while the
    /// world is running would sit frozen for the whole of the one screen it was built for.
    /// </remarks>
    public static void Advance(float lapSeconds)
    {
        _tracesLeft = TracesPerFrame;
        if (lapSeconds <= 0f)
        {
            // Motion off. The border stays, the light goes : the reference still is drawn this way
            // too, because a light frozen at one point of the path reads as a lump rather than as
            // the travelling highlight it is.
            if (_step == -1) return;
            _step = -1;
            foreach (Entry entry in _entries.Values) if (entry != null) entry.Dirty = true;
            return;
        }
        _phase += Time.unscaledDeltaTime / lapSeconds;
        _phase -= Mathf.Floor(_phase);
        int step = Mathf.Clamp(Mathf.FloorToInt(_phase * Steps), 0, Steps - 1);
        if (step == _step) return;
        _step = step;
        foreach (Entry entry in _entries.Values) if (entry != null) entry.Dirty = true;
    }

    /// <summary>
    /// The mark for one rarity frame, at the lap's current point. Null when there is nothing to
    /// draw yet, which the caller shows as no border rather than as a wrong one.
    /// </summary>
    public static Sprite BorderFor(Sprite frameSprite, out MarkPlacement placement)
    {
        placement = default;
        if (frameSprite == null) return null;
        int key = frameSprite.GetInstanceID();
        if (!_entries.TryGetValue(key, out Entry entry))
        {
            if (_tracesLeft <= 0) return null;
            _tracesLeft--;
            entry = Build(frameSprite);
            // A REFUSAL is stored exactly like an answer. Without that, a frame this border cannot
            // describe would pay for a fresh readback every frame it is on screen, and a readback
            // stalls the graphics thread : the cheapest possible outcome would become the most
            // expensive one the mod has.
            _entries[key] = entry;
        }
        if (entry == null) return null;
        if (entry.Dirty) Restamp(entry);
        placement = entry.Placement;
        return entry.Sprite;
    }

    /// <summary>Destroys every texture and sprite this made. Nothing the game owns is involved.</summary>
    public static void DropAll()
    {
        foreach (Entry entry in _entries.Values) entry?.Destroy();
        _entries.Clear();
        Clear();
    }

    /// <summary>Starts a fresh placement lap while retaining the session's shared pictures.</summary>
    public static void Clear()
    {
        _phase = 0f;
        _step = -1;
        _worstBuildMs = 0;
        _worstStepMs = 0;
        _flatFallbacks = 0;
        _unsupported = 0;
    }

    public static string Diagnostic =>
        $"{_entries.Count} frame sprite(s) seen, {_flatFallbacks} on a flat border, " +
        $"{_unsupported} not a shape the border follows (strike only); " +
        $"worst trace {_worstBuildMs:F1} ms, worst lap step {_worstStepMs:F2} ms";

    private static Entry Build(Sprite frameSprite)
    {
        using var measured = Perf.Measure(PerfSlot.MarkArtTrace);
        long start = Stopwatch.GetTimestamp();
        try
        {
            Rect area = frameSprite.textureRect;
            int width = Mathf.RoundToInt(area.width);
            int height = Mathf.RoundToInt(area.height);
            if (width < 16 || height < 16) return null;

            // Our picture is stretched evenly across the frame's rect. A nine sliced frame is not :
            // it holds its corners and stretches only its middle, so the game's pixels and ours
            // would land in different places and the border would sit off the band. No frame in
            // this game is sliced today, and one carrying a centre ornament could not be, so this
            // is not handled : it is WATCHED. If the art ever changes under us, this says so in the
            // log instead of the border quietly drifting and looking like a tracing defect.
            if (frameSprite.border != Vector4.zero)
                MelonLogger.Warning($"[TargetingMod] the frame sprite '{frameSprite.name}' is nine sliced " +
                                    "(border " + frameSprite.border + "), so the mark's border may not " +
                                    "line up with the band. Please report this with the log.");

            FrameTraceResult result = FrameTrace.Read(frameSprite, out FrameContour contour, out string readPath);
            if (result == FrameTraceResult.ShapeNotSupported)
            {
                // Not a defect and not a degradation : the border is a shape that follows a band of
                // four straight edges, and this frame is not one. It shows the strike alone, which
                // is the channel that carries the meaning without colour anyway.
                _unsupported++;
                MelonLogger.Msg($"[TargetingMod] '{frameSprite.name}' is not a shape the border can " +
                                $"follow, so it is marked with the strike alone; pixels via {readPath}");
                return null;
            }
            if (result == FrameTraceResult.ReadFailed)
            {
                contour = FrameContour.Flat(width, height);
                _flatFallbacks++;
            }
            var entry = new Entry(contour);
            MelonLogger.Msg($"[TargetingMod] traced the mark for '{frameSprite.name}': {contour.Describe()}, " +
                            $"pixels via {readPath}");
            return entry;
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[TargetingMod] could not build the mark for a frame sprite: " + e.Message);
            return null;
        }
        finally
        {
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (ms > _worstBuildMs) _worstBuildMs = ms;
        }
    }

    // Put the light where the lap says it is. Only the pixels the last one covered are put back
    // first, which is what keeps this proportional to the light rather than to the frame.
    private static void Restamp(Entry entry)
    {
        using var measured = Perf.Measure(PerfSlot.MarkArtStamp);
        long start = Stopwatch.GetTimestamp();
        entry.Dirty = false;
        for (int i = 0; i < entry.Touched.Count; i++)
        {
            int index = entry.Touched[i];
            entry.Working[index] = entry.Border[index];
        }
        entry.Touched.Clear();

        if (_step >= 0) StampGlints(entry, _step / (float)Steps);

        entry.Texture.SetPixels32(entry.Working);
        // The mip chain has to be rebuilt with the picture, or the smaller levels keep the light
        // where it was and it smears across the border at any size below full.
        entry.Texture.Apply(true, false);
        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        if (ms > _worstStepMs) _worstStepMs = ms;
    }

    private static void StampGlints(Entry entry, float phase)
    {
        FrameContour contour = entry.Contour;
        BorderStep[] path = contour.Path;
        int total = path.Length;
        if (total == 0) return;

        float span = Mathf.Max(3f, GlintSpan * total);
        Color32 red = Red;

        for (int index = 0; index < total; index++)
        {
            // Two lights, half a lap apart, so at least two edges are moving at any moment.
            float best = 0f;
            for (int which = 0; which < 2; which++)
            {
                float centre = phase + which * 0.5f;
                centre -= Mathf.Floor(centre);
                float offset = Mathf.Abs(index - centre * total);
                offset = Mathf.Min(offset, total - offset);
                if (offset >= span) continue;
                // A lens : two droplets joined at the round ends. The square is the shape ; a square
                // root would give an ellipse whose blunt ends read as a blob.
                float shape = 1f - (offset / span) * (offset / span);
                if (shape > best) best = shape;
            }
            if (best <= 0.02f) continue;

            BorderStep step = path[index];
            int weight = entry.WeightFor(step);
            float peak = Mathf.Max(weight + 2f, weight * GlintBulge);
            int thickness = Mathf.Max(1, Mathf.RoundToInt(weight + (peak - weight) * best));
            // Floored, not truncated. The bulge is symmetric about the border's own centre line, so
            // it grows the same amount outward and inward and the outer line never wanders.
            int from = Mathf.FloorToInt(-(thickness - weight) / 2f);
            float alpha = Mathf.Min(1f, GlintSolid + GlintShaped * best);
            entry.Stamp(step, from + 1, from + thickness, red, alpha, _touchedInto: entry.Touched);
        }
    }

    // One rarity's picture : the shape, the border alone, the border with the light on it, and the
    // texture the game is actually shown.
    private sealed class Entry
    {
        public readonly FrameContour Contour;
        /// <summary>The border alone. Never handed across the boundary : it only refills Working.</summary>
        public readonly Color32[] Border;
        /// <summary>What the texture holds. An il2cpp array, so no managed array ever crosses.</summary>
        public readonly Il2CppStructArray<Color32> Working;
        public readonly List<int> Touched = new(4096);
        public readonly MarkPlacement Placement;
        public Texture2D Texture;
        public Sprite Sprite;
        public bool Dirty = true;

        private readonly int _weightX;
        private readonly int _weightY;

        public Entry(FrameContour contour)
        {
            Contour = contour;
            Placement = new MarkPlacement(contour);
            int width = contour.Width, height = contour.Height;
            // Per axis, both times as a share of the sprite's own extent in that direction. Drawn
            // this way the two come out the same thickness on screen whatever the frame's aspect is.
            _weightX = Mathf.Max(1, Mathf.RoundToInt(BorderWeight * width));
            _weightY = Mathf.Max(1, Mathf.RoundToInt(BorderWeight * height));

            Border = new Color32[width * height];
            Working = new Il2CppStructArray<Color32>(width * height);

            Color32 red = Red;
            var opaque = new Color32(red.r, red.g, red.b, (byte)Mathf.RoundToInt(255f * BorderAlpha));
            for (int i = 0; i < contour.Path.Length; i++)
            {
                BorderStep step = contour.Path[i];
                // Drawn INWARD from the traced line, starting one pixel in, so the border's outer
                // edge sits against the band and never on top of it.
                Write(Border, step, 1, WeightFor(step), opaque, null);
            }
            for (int i = 0; i < Border.Length; i++) Working[i] = Border[i];

            // Mipmapped, like the mod's other generated line textures, and for the same reason.
            // A slot is drawn far smaller than the two hundred odd pixels the frame is authored at,
            // so this picture is always being shrunk, and the border's one genuinely diagonal
            // stretch is the notch in the middle of the fancier frames. Shrunk with no mip chain,
            // a one pixel staircase on that slope samples unevenly and reads as a ragged edge. The
            // reference render solves the same problem by drawing four times too big and averaging
            // down ; here the hardware does the averaging, at whatever size the panel actually
            // uses, which is the better end of the same trade.
            Texture = new Texture2D(width, height, TextureFormat.RGBA32, true)
            {
                name = "TargetingMarkBorder",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            Texture.SetPixels32(Working);
            Texture.Apply(true, false);
            Sprite = UnityEngine.Sprite.Create(Texture, new Rect(0f, 0f, width, height),
                new Vector2(0.5f, 0.5f), 100f);
            Sprite.hideFlags = HideFlags.HideAndDontSave;
        }

        public int WeightFor(BorderStep step) => step.AcrossY ? _weightY : _weightX;

        /// <summary>Stamps into Working, over whatever the border already put there.</summary>
        public void Stamp(BorderStep step, int near, int far, Color32 colour, float alpha, List<int> _touchedInto)
        {
            int width = Contour.Width, height = Contour.Height;
            int a = step.AcrossY ? step.Y + step.Inward * near : step.X + step.Inward * near;
            int b = step.AcrossY ? step.Y + step.Inward * far : step.X + step.Inward * far;
            int lo = Mathf.Min(a, b), hi = Mathf.Max(a, b);
            for (int v = lo; v <= hi; v++)
            {
                int x = step.AcrossY ? step.X : v;
                int y = step.AcrossY ? v : step.Y;
                if (x < 0 || x >= width || y < 0 || y >= height) continue;
                int index = (height - 1 - y) * width + x;
                // Over the border, not instead of it. Both layers are the same red, so this is only
                // ever a lift in alpha, which is what the reference render does too.
                float under = Border[index].a / 255f;
                float result = alpha + under * (1f - alpha);
                Working[index] = new Color32(colour.r, colour.g, colour.b,
                    (byte)Mathf.Clamp(Mathf.RoundToInt(result * 255f), 0, 255));
                _touchedInto?.Add(index);
            }
        }

        private void Write(Color32[] buffer, BorderStep step, int near, int far, Color32 colour, List<int> touched)
        {
            int width = Contour.Width, height = Contour.Height;
            int a = step.AcrossY ? step.Y + step.Inward * near : step.X + step.Inward * near;
            int b = step.AcrossY ? step.Y + step.Inward * far : step.X + step.Inward * far;
            int lo = Mathf.Min(a, b), hi = Mathf.Max(a, b);
            for (int v = lo; v <= hi; v++)
            {
                int x = step.AcrossY ? step.X : v;
                int y = step.AcrossY ? v : step.Y;
                if (x < 0 || x >= width || y < 0 || y >= height) continue;
                int index = (height - 1 - y) * width + x;
                buffer[index] = colour;
                touched?.Add(index);
            }
        }

        public void Destroy()
        {
            try
            {
                if (Sprite != null) UnityEngine.Object.Destroy(Sprite);
                if (Texture != null) UnityEngine.Object.Destroy(Texture);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[TargetingMod] mark texture teardown skipped: " + e.Message);
            }
            Sprite = null;
            Texture = null;
        }
    }
}
