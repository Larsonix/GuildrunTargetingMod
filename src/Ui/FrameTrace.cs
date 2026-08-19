using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace GuildrunTargetingMod.Ui;

// The inner edge of a rarity frame's band, read off the very sprite the game is showing.
//
// The mark this feeds sits flush inside the frame's own band and follows the centre ornament's V
// where there is one. Nothing about that shape is written down here, which is the point : it
// survives an art change, and a rarity that does not exist yet gets a correct border with no code
// change at all. What IS written down is the rule, because four separate attempts at it were
// plausible-looking and wrong.
//
// A frame is a LAYERED stack, and that is what defeats every naive rule. Measured on the gold one :
//
//     y 14-21   black outer outline   luminance about 0
//     y 22-33   THE BAND              luminance 101 to 174 by rarity
//     y 34-41   black inner outline   luminance about 3
//     y 42-43   a second grey line    luminance 76, and only two pixels tall
//     y 44+     the slot interior     luminance about 30
//
// THE RULE : for each column, and then each row, scan inward from outside the sprite and collect
// every opaque run that is bright OR coloured. The band is the DEEPEST run that is thick enough to
// be a band and not shallower than the flank. The flank is the most common value over the plain
// stretches of that edge. The two ends of every edge are held at the flank, so the corners close at
// exact right angles.
//
// The four defects that rule exists to avoid, each of which rendered convincingly before it was
// caught. Do not re-derive them :
//
//   1. "the first bright run" catches the diamond stud on the red and dark blue frames. The stud
//      protrudes OUTWARD and carries a dark cross through it, so the run ends ABOVE the band and
//      drags the whole border up into the frame.
//   2. "the deepest run" catches the two pixel grey line under the inner outline, which puts the
//      border a bevel too deep. A minimum thickness is what separates them.
//   3. brightness alone cannot see the dark blue band at all : it is navy at luminance 24, DARKER
//      than the interior at 30. Accepting bright OR coloured fixes it, because the interior is
//      neutral grey and a band never is.
//   4. that same frame's bottom band is near black with zero colour, so there is genuinely nothing
//      for any test to find. It is mirrored from the top instead, because frames are symmetric. A
//      constant would have been wrong for the V.
//   5. a minimum thickness written as a share of the SPAN is a guess about the art. Measured across
//      both frame families, the second grey line is two pixels on an item frame and EIGHT on the
//      dark blue relic, which clears any fixed share that still admits a twelve pixel band. The
//      minimum is therefore derived from the band this frame actually has : every real competitor
//      measured at most 0.67 of the band's own thickness, so the bar sits at 0.75 of it.
//   6. two opposite edges that are BOTH readable can still disagree, and the old symmetry rule only
//      fired when one of them was unreadable. When they disagree the SHALLOWER one is right, always,
//      because every failure mode above picks something DEEPER than the band and none picks
//      something shallower once thin runs are excluded.
//
// A frame does not have to stand square. Relic frames are diamonds : the same square, turned. So the
// SAMPLING is rotated rather than the picture, which leaves the tracer, the stamping and every
// constant above exactly as they are, and the finished mark is turned back by the graphics hardware
// when it is drawn. An upright read is tried first and a turned one only if that is refused, so a
// frame shape nobody has seen yet is discovered rather than assumed.
//
// Every share below is a share of something, and getting the denominator wrong is not a small
// error. Three defects during the design came from exactly that, so the two axes are kept apart
// here rather than folded into one "slot" number : the sprite is 222 by 228 and is NOT square.
internal sealed class FrameContour
{
    // Where the border goes when the sprite could not be read at all. These are the plain frames'
    // measured flank positions, 30 of 222 across and 33 of 228 down, kept as two separate shares
    // for the reason in the comment above. The mark still works ; it just does not follow the V.
    private const float FallbackInsetX = 30f / 222f;
    private const float FallbackInsetY = 33f / 228f;

    public readonly int Width;
    public readonly int Height;
    // Top and Bottom are indexed by x and hold a y. Left and Right are indexed by y and hold an x.
    // All four run the FULL span with their ends held at the flank, which is what lets the corners
    // meet at a point instead of merely near one.
    public readonly int[] Top;
    public readonly int[] Bottom;
    public readonly int[] Left;
    public readonly int[] Right;
    // The corner box : the four flanks. The path is bounded by these.
    public readonly int X0, X1, Y0, Y1;
    /// <summary>False when the sprite could not be read and the flat fallback was used instead.</summary>
    public readonly bool Traced;
    /// <summary>The closed contour, clockwise from the top left corner.</summary>
    public readonly BorderStep[] Path;

    // WHERE this picture goes on the sprite it was traced from. All four are trivial for a frame
    // that stands square, and they are the whole of what a turned frame needs : the caller sizes an
    // image to Width by Height of the source's pixels, offsets it, and turns it back.
    /// <summary>The sampling rotation this was traced in, in degrees. Zero for a square frame.</summary>
    public float AngleDegrees;
    /// <summary>The picture's centre relative to the sprite's, in the sprite's pixels, y UP.</summary>
    public float OffsetX;
    public float OffsetY;
    /// <summary>The sprite's own size, which is what the offsets and extents are measured in.</summary>
    public int SourceWidth;
    public int SourceHeight;

    public void SetPlacement(float angleDegrees, float offsetX, float offsetY, int sourceWidth, int sourceHeight)
    {
        AngleDegrees = angleDegrees;
        OffsetX = offsetX;
        OffsetY = offsetY;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    private FrameContour(int width, int height, int[] top, int[] bottom, int[] left, int[] right, bool traced)
    {
        Width = width;
        Height = height;
        Top = top;
        Bottom = bottom;
        Left = left;
        Right = right;
        Traced = traced;
        X0 = FrameTrace.Mode(left);
        X1 = FrameTrace.Mode(right);
        Y0 = FrameTrace.Mode(top);
        Y1 = FrameTrace.Mode(bottom);
        Path = BuildPath();
        // A frame that stands square is its own placement. SetPlacement overrides these for a
        // turned one, so the two paths differ in data rather than in code.
        SourceWidth = width;
        SourceHeight = height;
    }

    public static FrameContour Flat(int width, int height)
    {
        int insetX = Mathf.RoundToInt(width * FallbackInsetX);
        int insetY = Mathf.RoundToInt(height * FallbackInsetY);
        return new FrameContour(width, height,
            Filled(width, insetY), Filled(width, height - 1 - insetY),
            Filled(height, insetX), Filled(height, width - 1 - insetX), false);
    }

    public static FrameContour Traced4(int width, int height, int[] top, int[] bottom, int[] left, int[] right)
        => new FrameContour(width, height, top, bottom, left, right, true);

    private static int[] Filled(int length, int value)
    {
        var row = new int[length];
        for (int i = 0; i < length; i++) row[i] = value;
        return row;
    }

    // Clockwise from the top left. Each step carries which way is INWARD, so the border can be
    // thinned from the inside and its outer line never leaves the band whatever the weight is.
    private BorderStep[] BuildPath()
    {
        var path = new List<BorderStep>((X1 - X0 + Y1 - Y0 + 2) * 2);
        for (int x = X0; x <= X1; x++) path.Add(new BorderStep(x, Read(Top, x, Y0), +1, true));
        for (int y = Y0; y <= Y1; y++) path.Add(new BorderStep(Read(Right, y, X1), y, -1, false));
        for (int x = X1; x >= X0; x--) path.Add(new BorderStep(x, Read(Bottom, x, Y1), -1, true));
        for (int y = Y1; y >= Y0; y--) path.Add(new BorderStep(Read(Left, y, X0), y, +1, false));
        return path.ToArray();
    }

    private static int Read(int[] profile, int index, int fallback)
        => index >= 0 && index < profile.Length ? profile[index] : fallback;

    /// <summary>Where the traced band sits, as a share of the sprite, for the log.</summary>
    public string Describe()
    {
        int deepest = Y0;
        for (int x = 0; x < Top.Length; x++) if (Top[x] > deepest) deepest = Top[x];
        return $"{Width}x{Height} band at x {X0}..{X1}, y {Y0}..{Y1}, " +
               $"V depth {(deepest - Y0) / (float)Height:F4}" +
               (AngleDegrees != 0f ? $", read at {AngleDegrees:F0} degrees (a turned frame)" : "") +
               (Traced ? "" : " (FLAT FALLBACK)");
    }
}

/// <summary>How a frame's trace ended. The two failures are NOT interchangeable : see Trace.</summary>
internal enum FrameTraceResult
{
    /// <summary>Four straight edges were found and the contour follows them.</summary>
    Traced,
    /// <summary>No pixels were obtained. The flat rectangle is a reasonable guess for a square frame.</summary>
    ReadFailed,
    /// <summary>Pixels were obtained and are not four straight edges. Draw no border.</summary>
    ShapeNotSupported
}

/// <summary>One step of the contour : where it is, and which way the border thickens.</summary>
internal readonly struct BorderStep
{
    public readonly int X;
    public readonly int Y;
    /// <summary>+1 or -1, the direction the border is drawn in from the traced line.</summary>
    public readonly int Inward;
    /// <summary>True on the top and bottom edges, where the border's thickness runs along y.</summary>
    public readonly bool AcrossY;

    public BorderStep(int x, int y, int inward, bool acrossY)
    {
        X = x;
        Y = y;
        Inward = inward;
        AcrossY = acrossY;
    }
}

internal static class FrameTrace
{
    // The band is bright OR coloured. Either test alone misses a rarity : see defect 3 above.
    private const int BandMinLuminance = 60;   // the outlines sit near zero, the interior near thirty
    private const int BandMinChroma = 25;      // the interior is neutral grey and a band never is
    // At or below this the pixel is part of nothing. The comparison is "or below", so the boundary
    // value itself is transparent : an off by one here shifts which pixels the frame is made of.
    private const int OpaqueMaxTransparentAlpha = 120;
    private const float EdgeMargin = 0.16f;    // this much at each end of an edge is the corner
    private const float Clamp = 0.22f;         // further than this from the flank is corner noise
    private const float MinBandShare = 0.030f; // thinner than this is a hairline, not the band
    private const int Smooth = 2;              // radius of the running mean that takes off the stairs
    // A traced edge has to sit where a frame's band could be. This is the honest test behind the
    // flat fallback : it catches a readback that came back empty, upside down or in the wrong
    // colour space, none of which throw, and all of which would otherwise draw a confident border
    // across the middle of the icon.
    private const float PlausibleMin = 0.06f;
    private const float PlausibleMax = 0.34f;
    // Can this frame be described by four straight edges at all?
    //
    // The check above only asks whether each edge landed at a sane DEPTH, and a shape can pass that
    // while being nothing like a square. Relic frames are the live example : they are diamonds, so
    // every column crosses their band at a different depth, and tracing one as four edges produced
    // a red rectangle floating in the middle of the diamond, touching nothing, on four of the five
    // rarities. It looked deliberate. Nothing in the depth test could see it.
    //
    // So this measures what the border actually depends on : over the PLAIN stretches of an edge,
    // clear of the centre ornament, a straight edge stays at its flank. Measured over both frame
    // families, worst item 0.0135 and best relic 0.0820, which is a factor of six. The threshold
    // sits between them with room on both sides rather than next to either.
    private const float ShapeMaxDeparture = 0.030f;
    // A run has to be at least this share of the band's OWN measured thickness to be the band.
    // Measured over both frame families, every genuine competitor reaches at most 0.67 of the band
    // while the band is 1.00 of itself, so the bar sits between the two populations with room on
    // each side rather than beside either. See failure mode 5 at the top of this file : written as
    // a share of the span instead, this admitted an eight pixel hairline on the dark blue relic.
    private const float BandMinShareOfBand = 0.75f;
    // Opposite insets on a real frame differ by 0 to 2 pixels. Anything past this is a disagreement
    // rather than a rounding wobble, and failure mode 6 says which side of it to believe.
    private const float SymmetryTolerance = 0.02f;
    // How wide a departure from the flank has to be before it counts as the BAND BENDING rather
    // than as a decoration stuck on top of the band.
    //
    // This is the difference between an item frame's centre V, which the border must follow, and a
    // relic frame's corner studs, which it must ignore. Depth cannot tell them apart : measured,
    // the V ornaments are 0.0746 to 0.1009 deep and the studs 0.0896 to 0.1033, which overlap
    // almost exactly, and a rule built on depth would have looked entirely reasonable. WIDTH
    // separates them cleanly : V ornaments span 0.1261 to 0.1712 of their edge, studs 0.0469 to
    // 0.0991. This is the tightest of the three measured thresholds in this file, so if a future
    // frame lands near it, re-measure rather than nudge it.
    private const float OrnamentMinShare = 0.113f;
    // A departure of a pixel or two is the trace breathing, not an ornament of any kind.
    private const int FlankSlack = 3;

    /// <summary>
    /// The sprite's own pixels, whether or not its texture was imported readable.
    /// </summary>
    /// <remarks>
    /// Game textures are normally not readable from script, so the pixels are fetched the long way
    /// round : blit into a temporary render target, then read that back. The blit is given a scale
    /// and an offset so only this sprite's own rectangle lands in the target, which matters because
    /// the source is usually one page of an atlas and allocating a full page sized target to read a
    /// two hundred pixel square out of it would be wasteful.
    ///
    /// The target is asked for in sRGB deliberately. If the project renders in linear space, a
    /// default target would hand back linearised values and every threshold above would be reading
    /// a different number than the one it was measured against. That failure is silent, which is
    /// exactly why the result is checked for plausibility rather than trusted.
    /// </remarks>
    public static FrameTraceResult Read(Sprite sprite, out FrameContour contour, out string readPath)
    {
        using var measured = Perf.Measure(PerfSlot.FrameTraceRead);
        contour = null;
        readPath = "unavailable";
        if (sprite == null) return FrameTraceResult.ReadFailed;
        Texture2D source = sprite.texture;
        if (source == null) return FrameTraceResult.ReadFailed;

        Rect area = sprite.textureRect;
        int width = Mathf.RoundToInt(area.width);
        int height = Mathf.RoundToInt(area.height);
        if (width < 16 || height < 16) return FrameTraceResult.ReadFailed;

        if (source.isReadable)
        {
            readPath = "readable texture";
            try
            {
                Color32[] pixels;
                using (Perf.Measure(PerfSlot.FramePixels))
                {
                    // The RECT, never the whole texture. A frame sprite lives on an atlas, and
                    // GetPixels32 with no arguments hands back every pixel of it : on a 4096 square
                    // sheet that is sixteen million pixels copied across the boundary to read the two
                    // hundred square that were wanted, which would make the fast path far slower than
                    // the readback it exists to avoid. This block overload reads the rect alone.
                    int left = Mathf.RoundToInt(area.x);
                    int bottom = Mathf.RoundToInt(area.y);
                    Il2CppStructArray<Color> block = source.GetPixels(left, bottom, width, height);
                    pixels = new Color32[width * height];
                    for (int i = 0; i < pixels.Length; i++) pixels[i] = block[i];
                }
                using (Perf.Measure(PerfSlot.FrameContourScan))
                {
                    FrameTraceResult upright = Trace(pixels, width, height, out contour);
                    if (upright == FrameTraceResult.Traced) return upright;
                    return TraceTurned(pixels, width, height, out contour);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[TargetingMod] could not read a frame sprite, the mark will use the " +
                                    "flat border instead of following the frame: " + e.Message);
                return FrameTraceResult.ReadFailed;
            }
        }

        RenderTexture target = null;
        RenderTexture previous = RenderTexture.active;
        Texture2D readable = null;
        readPath = "GPU readback";
        try
        {
            Color32[] pixels;
            using (Perf.Measure(PerfSlot.FramePixels))
            {
                target = RenderTexture.GetTemporary(width, height, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var scale = new Vector2(area.width / source.width, area.height / source.height);
                var offset = new Vector2(area.x / source.width, area.y / source.height);
                Graphics.Blit(source, target, scale, offset);

                RenderTexture.active = target;
                readable = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = "TargetingFrameRead" };
                readable.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                readable.Apply(false, false);

                // One interop call, not one per pixel, and then copied out ONCE into a plain array.
                // Copying INTO managed memory is safe and is the opposite direction from the trap :
                // what silently loses its contents here is handing a managed array back across the
                // boundary, which nothing below ever does.
                Il2CppStructArray<Color32> read = readable.GetPixels32();
                pixels = new Color32[width * height];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = read[i];
            }

            // Square first. A frame whose band really is four straight edges never needs turning,
            // and this keeps every item frame on exactly the path it was verified on.
            using (Perf.Measure(PerfSlot.FrameContourScan))
            {
                FrameTraceResult upright = Trace(pixels, width, height, out contour);
                if (upright == FrameTraceResult.Traced) return upright;
                return TraceTurned(pixels, width, height, out contour);
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[TargetingMod] could not read a frame sprite, the mark will use the " +
                                "flat border instead of following the frame: " + e.Message);
            return FrameTraceResult.ReadFailed;
        }
        finally
        {
            RenderTexture.active = previous;
            if (target != null) RenderTexture.ReleaseTemporary(target);
            if (readable != null) UnityEngine.Object.Destroy(readable);
        }
    }

    // Everything below works top left down, the way the frame reads on screen, while the array that
    // arrives is bottom left up. One accessor turns one convention into the other, in one place,
    // rather than every edge being flipped in its own head.
    private static Color32 At(Color32[] pixels, int width, int height, int x, int y)
        => pixels[(height - 1 - y) * width + x];

    /// <summary>
    /// The same trace, on a frame standing on its corner, by turning the SAMPLING.
    /// </summary>
    /// <remarks>
    /// A relic frame is a square diamond : the identical shape, rotated. Turning the sampling
    /// rather than writing a second tracer means the algorithm, its six documented failure modes
    /// and every measured constant carry over untouched, and the finished picture is turned back by
    /// the graphics hardware when the image is drawn, which costs nothing and resamples nothing.
    ///
    /// The turned buffer is cropped to what is actually drawn in it. That is not tidiness : the
    /// margins and the plain-stretch windows are shares of the EDGE, so a square floating in a
    /// larger empty canvas would have every one of them land in the wrong place.
    /// </remarks>
    private static FrameTraceResult TraceTurned(Color32[] pixels, int width, int height,
        out FrameContour contour)
    {
        contour = null;
        const float degrees = 45f;
        int span = Mathf.CeilToInt(Mathf.Sqrt(width * width + height * height));
        Color32[] turned = SampleRotated(pixels, width, height, span, degrees);
        if (!OpaqueBox(turned, span, out int boxX, out int boxY, out int boxW, out int boxH))
            return FrameTraceResult.ShapeNotSupported;
        if (boxW < 16 || boxH < 16) return FrameTraceResult.ShapeNotSupported;

        Color32[] cropped = CropTo(turned, span, boxX, boxY, boxW, boxH);
        FrameTraceResult result = Trace(cropped, boxW, boxH, out contour);
        if (result != FrameTraceResult.Traced) return result;

        // Where the crop's centre lands back on the sprite. Same matrix as the sampler, so the two
        // cannot drift apart : turned space to source space, then flipped into the y-up world the
        // interface positions things in.
        float a = degrees * Mathf.Deg2Rad, ca = Mathf.Cos(a), sa = Mathf.Sin(a);
        float du = boxX + boxW / 2f - span / 2f;
        float dv = boxY + boxH / 2f - span / 2f;
        float sourceX = width / 2f + du * ca - dv * sa;
        float sourceY = height / 2f + du * sa + dv * ca;
        contour.SetPlacement(degrees, sourceX - width / 2f, -(sourceY - height / 2f), width, height);
        return result;
    }

    // Turned space (u, v) reads from source (x, y), bilinearly. Anything falling outside the source
    // stays transparent, which is what makes the crop below find the shape.
    private static Color32[] SampleRotated(Color32[] source, int width, int height, int span, float degrees)
    {
        var turned = new Color32[span * span];
        float a = degrees * Mathf.Deg2Rad, ca = Mathf.Cos(a), sa = Mathf.Sin(a);
        float cx = width / 2f, cy = height / 2f, cd = span / 2f;
        for (int v = 0; v < span; v++)
        {
            float dv = v - cd;
            for (int u = 0; u < span; u++)
            {
                float du = u - cd;
                float x = cx + du * ca - dv * sa;
                float y = cy + du * sa + dv * ca;
                if (x < 0f || y < 0f || x > width - 1 || y > height - 1) continue;
                turned[(span - 1 - v) * span + u] = Bilinear(source, width, height, x, y);
            }
        }
        return turned;
    }

    private static Color32 Bilinear(Color32[] source, int width, int height, float x, float y)
    {
        int x0 = (int)x, y0 = (int)y;
        // System.Math, never UnityEngine.Mathf, and this is not a style preference.
        //
        // Mathf lives in the game's assembly, so from here every one of its calls crosses the
        // interop boundary. This method runs once per pixel of the turned buffer and calls Mix four
        // times, and Mix used two more, so one relic frame was spending about a million boundary
        // crossings on arithmetic. MEASURED: the trace was 87 ms of which the graphics readback was
        // 2 ms, and the rest was this. Math.Min and Math.Max are managed intrinsics and the results
        // are identical integers, so nothing about the traced shape changes.
        int x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
        float fx = x - x0, fy = y - y0;
        Color32 a = At(source, width, height, x0, y0), b = At(source, width, height, x1, y0);
        Color32 c = At(source, width, height, x0, y1), d = At(source, width, height, x1, y1);
        // Fields rather than the constructor, for the same reason : a generated struct's
        // constructor is a call into the game, and this one would run once per pixel.
        Color32 mixed = default;
        mixed.r = Mix(a.r, b.r, c.r, d.r, fx, fy);
        mixed.g = Mix(a.g, b.g, c.g, d.g, fx, fy);
        mixed.b = Mix(a.b, b.b, c.b, d.b, fx, fy);
        mixed.a = Mix(a.a, b.a, c.a, d.a, fx, fy);
        return mixed;
    }

    private static byte Mix(byte a, byte b, byte c, byte d, float fx, float fy)
    {
        float top = a + (b - a) * fx, bottom = c + (d - c) * fx;
        // Mathf.RoundToInt is (int)Math.Round(f) on the widened double, which is round half to
        // even ; Math.Round(double) is the same rule, so this rounds identically. The clamp is
        // written out because Mathf.Clamp is one more boundary crossing per colour channel per
        // pixel. See the note in Bilinear.
        int rounded = (int)Math.Round((double)(top + (bottom - top) * fy));
        if (rounded < 0) rounded = 0;
        else if (rounded > 255) rounded = 255;
        return (byte)rounded;
    }

    private static bool OpaqueBox(Color32[] pixels, int span, out int x, out int y, out int w, out int h)
    {
        int minX = span, minY = span, maxX = -1, maxY = -1;
        for (int v = 0; v < span; v++)
            for (int u = 0; u < span; u++)
            {
                if (At(pixels, span, span, u, v).a <= OpaqueMaxTransparentAlpha) continue;
                if (u < minX) minX = u;
                if (u > maxX) maxX = u;
                if (v < minY) minY = v;
                if (v > maxY) maxY = v;
            }
        x = minX; y = minY; w = maxX - minX + 1; h = maxY - minY + 1;
        return maxX >= 0;
    }

    private static Color32[] CropTo(Color32[] pixels, int span, int x, int y, int w, int h)
    {
        var cropped = new Color32[w * h];
        for (int v = 0; v < h; v++)
            for (int u = 0; u < w; u++)
                cropped[(h - 1 - v) * w + u] = At(pixels, span, span, x + u, y + v);
        return cropped;
    }

    private static FrameTraceResult Trace(Color32[] pixels, int width, int height,
        out FrameContour contour)
    {
        contour = null;
        int[] top = TraceEdge(pixels, width, height, width, height, horizontal: true, forward: true);
        int[] bottom = TraceEdge(pixels, width, height, width, height, horizontal: true, forward: false);
        int[] left = TraceEdge(pixels, width, height, height, width, horizontal: false, forward: true);
        int[] right = TraceEdge(pixels, width, height, height, width, horizontal: false, forward: false);

        RepairBySymmetry(ref top, ref bottom, height);
        RepairBySymmetry(ref left, ref right, width);

        // Anything reached from here has PIXELS, which is what separates the two failures and it is
        // a distinction worth being careful about.
        //
        // No pixels at all is absence of evidence : the caller is looking at a frame it could not
        // read, the frames this border was designed for are squares, and guessing a rectangle is
        // reasonable. Pixels that do not describe four straight edges are evidence AGAINST, and a
        // rectangle is then provably wrong rather than merely unverified. The relic diamonds are
        // the live case, and the plain one fails here rather than below : its band is too faint to
        // trace at all, so it arrives as an implausible depth rather than as a steep edge, and
        // treating that as "no data" would have drawn the same wrong rectangle by the other door.
        //
        // The cost of putting the line here is that a readback which succeeds but comes back in the
        // wrong colour space loses the border on items too. That is the right way round : the strike
        // still marks them, and a wrong border is worse than a missing one.
        if (!Plausible(top, height, true) || !Plausible(bottom, height, false) ||
            !Plausible(left, width, true) || !Plausible(right, width, false))
            return FrameTraceResult.ShapeNotSupported;

        if (WorstPlainDeparture(top, bottom, left, right, width, height) > ShapeMaxDeparture)
            return FrameTraceResult.ShapeNotSupported;

        // The gate has passed, so this IS a band of four straight edges and anything narrow still
        // sticking out of one is decoration rather than the band. Only now is it safe to hold those
        // at the flank, because doing it earlier is what lets a shape lie to the gate about itself.
        Settle(top);
        Settle(bottom);
        Settle(left);
        Settle(right);

        contour = FrameContour.Traced4(width, height, top, bottom, left, right);
        return FrameTraceResult.Traced;
    }

    private static float WorstPlainDeparture(int[] top, int[] bottom, int[] left, int[] right,
        int width, int height)
    {
        float worst = Departure(top, height);
        worst = Mathf.Max(worst, Departure(bottom, height));
        worst = Mathf.Max(worst, Departure(left, width));
        return Mathf.Max(worst, Departure(right, width));
    }

    // How far one edge wanders from its flank over the stretches that should be plain band. The
    // centre ornament lives between them and is deliberately not looked at : that part is SUPPOSED
    // to move, and it is the whole reason the border is traced rather than written down.
    private static float Departure(int[] profile, int along)
    {
        int flank = Mode(profile);
        float worst = 0f;
        for (int i = 0; i < profile.Length; i++)
        {
            float share = i / (float)profile.Length;
            if (!((share > 0.22f && share < 0.40f) || (share > 0.60f && share < 0.78f))) continue;
            float departure = Mathf.Abs(profile[i] - flank) / (float)along;
            if (departure > worst) worst = departure;
        }
        return worst;
    }

    // A frame is symmetric, so an edge that cannot be read is its opposite reflected.
    private static void RepairBySymmetry(ref int[] near, ref int[] far, int along)
    {
        bool okNear = Plausible(near, along, true);
        bool okFar = Plausible(far, along, false);
        if (okNear != okFar)
        {
            if (okNear) far = Mirror(near, along);
            else near = Mirror(far, along);
            return;
        }
        if (!okNear) return;

        // Both readable, and they still disagree. The old rule stopped here, and that is how the
        // dark blue relic came back with one edge at an inset of 28 and its opposite at 45 : both
        // are "plausible" on their own and only their disagreement says one is wrong.
        //
        // The SHALLOWER one is right. That is not a preference, it is the direction of every
        // failure this tracer has : each one picks something DEEPER than the band, and once runs
        // too thin to be a band are excluded nothing can pick something shallower. The flank is
        // measured on plain stretches, so a legitimate centre ornament never enters this comparison.
        int nearInset = Mode(near);
        int farInset = along - 1 - Mode(far);
        if (Mathf.Abs(nearInset - farInset) <= SymmetryTolerance * along) return;
        if (nearInset < farInset) far = Mirror(near, along);
        else near = Mirror(far, along);
    }

    private static int[] Mirror(int[] profile, int along)
    {
        var mirrored = new int[profile.Length];
        for (int i = 0; i < profile.Length; i++) mirrored[i] = along - 1 - profile[i];
        return mirrored;
    }

    private static bool Plausible(int[] profile, int along, bool forward)
    {
        if (profile == null || profile.Length == 0) return false;
        int flank = Mode(profile);
        int inset = forward ? flank : along - 1 - flank;
        return inset > PlausibleMin * along && inset < PlausibleMax * along;
    }

    /// <summary>One edge over its full span : the band's inner edge at every position along it.</summary>
    /// <remarks>
    /// The middle is traced and the two ends are held at the flank. That is not laziness, it is the
    /// corner fix. The corner decoration reads as garbage to any of these tests, and an edge that
    /// simply stopped short of it would leave the border open at the corner, showing the slot
    /// background through the angle. Held at the flank instead, the four edges meet exactly.
    /// </remarks>
    private static int[] TraceEdge(Color32[] pixels, int width, int height,
        int across, int along, bool horizontal, bool forward)
    {
        int lo = Mathf.RoundToInt(across * EdgeMargin);
        int hi = Mathf.RoundToInt(across * (1f - EdgeMargin));
        int step = forward ? 1 : -1;
        // The SEED minimum, used only to find the band so its real thickness can be measured. It is
        // a share of the span and is deliberately loose : it has to admit the band on every frame,
        // and it is allowed to admit competitors too because nothing is chosen with it.
        int minThickness = Mathf.Max(3, Mathf.RoundToInt(along * MinBandShare));

        var runs = new List<RunEnd>[across];
        for (int i = lo; i < hi; i++)
            runs[i] = BrightRuns(pixels, width, height, along, i, horizontal, forward);

        // The flank is measured on the two stretches of PLAIN band : clear of the corner decoration
        // at the ends and clear of the centre ornament in the middle. Taking it from positions
        // merely "far from the centre" put it in the corners and measured the wrong feature.
        var outer = new List<int>(across);
        var thicknesses = new List<int>(across);
        for (int i = lo; i < hi; i++)
        {
            float share = i / (float)across;
            bool plain = (share > 0.22f && share < 0.40f) || (share > 0.60f && share < 0.78f);
            if (!plain || runs[i] == null || runs[i].Count == 0) continue;
            for (int r = 0; r < runs[i].Count; r++)
                if (runs[i][r].Thickness >= minThickness)
                {
                    outer.Add(runs[i][r].End);
                    thicknesses.Add(runs[i][r].Thickness);
                    break;
                }
        }
        if (outer.Count == 0) return null;
        int flank = Mode(outer);
        // Now the real bar, measured from the band this frame actually has rather than guessed from
        // the size of the sprite. This is the whole of failure mode 5.
        int bandThickness = Mode(thicknesses);
        minThickness = Mathf.Max(3, Mathf.RoundToInt(BandMinShareOfBand * bandThickness));

        float limit = along * Clamp;
        var traced = new int[across];
        for (int i = 0; i < across; i++)
        {
            if (i < lo || i >= hi) { traced[i] = flank; continue; }
            int edge = BandEdge(runs[i], flank, step, limit, minThickness);
            traced[i] = edge == int.MinValue ? flank : edge;
        }
        // RAW, deliberately. Holding ornaments at the flank belongs AFTER the shape gate has had
        // its look, and putting it here broke exactly that : flattened first, a relic diamond read
        // upright came back looking like four straight edges, passed the gate, and would have gone
        // back to drawing a rectangle inside a diamond. A gate must judge what came off the sprite,
        // never a version of it that has already been pushed toward the answer.
        return RunningMean(traced);
    }

    /// <summary>
    /// Every opaque run scanning inward, as its inner end and its thickness.
    /// </summary>
    /// <remarks>
    /// A transparent pixel neither extends a run nor ends one : it is skipped entirely. Frames have
    /// transparent notches cut through them, and treating one as a break would split the band in
    /// two and hand back two runs half as thick as the real one, which the thickness test would
    /// then throw away.
    /// </remarks>
    private static List<RunEnd> BrightRuns(Color32[] pixels, int width, int height,
        int along, int fixedAt, bool horizontal, bool forward)
    {
        var runs = new List<RunEnd>(6);
        int start = int.MinValue, last = int.MinValue;
        for (int n = 0; n < along; n++)
        {
            int i = forward ? n : along - 1 - n;
            Color32 q = horizontal ? At(pixels, width, height, fixedAt, i) : At(pixels, width, height, i, fixedAt);
            if (q.a <= OpaqueMaxTransparentAlpha) continue;
            if (IsBand(q))
            {
                if (start == int.MinValue) start = i;
                last = i;
            }
            else if (start != int.MinValue)
            {
                runs.Add(new RunEnd(last, Mathf.Abs(last - start) + 1));
                start = int.MinValue;
                last = int.MinValue;
            }
        }
        if (start != int.MinValue) runs.Add(new RunEnd(last, Mathf.Abs(last - start) + 1));
        return runs;
    }

    private static bool IsBand(Color32 q)
    {
        // Floating point, not a scaled integer. An integer version of this truncates, so a pixel
        // whose luminance is a fraction over the threshold falls the wrong side of it, and the band
        // that is closest to the threshold is the one the whole feature was hardest to trace.
        float luminance = 0.2126f * q.r + 0.7152f * q.g + 0.0722f * q.b;
        // System.Math for the same measured reason as Bilinear : this runs once per scanned pixel,
        // four edges per trace and twice over for a turned frame, so it was the single largest
        // block of interop crossings in the mod. Integer max and min are identical either way.
        int chroma = Math.Max(q.r, Math.Max(q.g, q.b)) - Math.Min(q.r, Math.Min(q.g, q.b));
        return luminance > BandMinLuminance || chroma > BandMinChroma;
    }

    /// <summary>Which of the runs at one position is the band. Returns int.MinValue for none.</summary>
    private static int BandEdge(List<RunEnd> runs, int flank, int step, float limit, int minThickness)
    {
        if (runs == null) return int.MinValue;
        int best = int.MinValue, bestDepth = int.MinValue;
        for (int i = 0; i < runs.Count; i++)
        {
            RunEnd run = runs[i];
            if (run.Thickness < minThickness) continue;
            int depth = (run.End - flank) * step;
            // Never shallower than the flank by more than a rounding wobble, and never further from
            // it than a corner's worth. Between those two the DEEPEST wins, which keeps the gold
            // frame's chevron, where the band genuinely does run deeper, and discards both the
            // protruding stud and the hairline under the inner outline.
            if (depth < -3 || Mathf.Abs(run.End - flank) > limit) continue;
            if (depth > bestDepth) { bestDepth = depth; best = run.End; }
        }
        return best;
    }

    /// <summary>Holds narrow departures at the flank : those are decoration, not the band bending.</summary>
    /// <remarks>
    /// The band's own shape is what the border exists to follow, and a frame that carries a centre
    /// ornament genuinely bends there. A stud sitting ON the band does not, and following one drew
    /// a small red curl into each of the four studs on two relic frames. Width is the discriminator
    /// and depth is not : see OrnamentMinShare for both measured populations.
    /// </remarks>
    private static void Settle(int[] profile)
    {
        if (profile == null || profile.Length == 0) return;
        int flank = Mode(profile);
        // Nothing to settle means nothing changes. The re-smoothing below is only there to blend a
        // stretch that was just flattened, and running it anyway rounded a pixel off the tip of the
        // centre V on two item frames : a cosmetic pass that alters output when it had no work to
        // do is a defect, not a rounding detail.
        if (!FlattenNarrowExcursions(profile, flank, profile.Length)) return;
        int[] smoothed = RunningMean(profile);
        for (int i = 0; i < profile.Length; i++) profile[i] = smoothed[i];
    }

    /// <summary>Returns true when something was actually held at the flank.</summary>
    private static bool FlattenNarrowExcursions(int[] traced, int flank, int across)
    {
        int minWidth = Mathf.RoundToInt(across * OrnamentMinShare);
        bool changed = false;
        int i = 0;
        while (i < across)
        {
            if (Mathf.Abs(traced[i] - flank) <= FlankSlack) { i++; continue; }
            int start = i;
            while (i < across && Mathf.Abs(traced[i] - flank) > FlankSlack) i++;
            if (i - start >= minWidth) continue;          // wide enough : this is the band bending
            for (int j = start; j < i; j++) traced[j] = flank;
            changed = true;
        }
        return changed;
    }

    // A short running mean, so the trace reads as a drawn line rather than as a staircase.
    private static int[] RunningMean(int[] values)
    {
        var smoothed = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            int from = Mathf.Max(0, i - Smooth), to = Mathf.Min(values.Length - 1, i + Smooth);
            int sum = 0;
            for (int j = from; j <= to; j++) sum += values[j];
            smoothed[i] = Mathf.RoundToInt(sum / (float)(to - from + 1));
        }
        return smoothed;
    }

    /// <summary>
    /// The most common value. On a tie the one that OCCURS first wins, not the one that reaches the
    /// count first.
    /// </summary>
    /// <remarks>
    /// Two passes, and the second one is the point. Picking a winner while counting gives the value
    /// that reached the top count first, which is a different answer whenever two values tie : for
    /// 1,2,2,1 that rule says 2 and this one says 1. A dead tie between two flank positions is a
    /// band whose inner edge is genuinely ambiguous by a pixel, so either answer draws a defensible
    /// border, but only one of them draws the same border as the render this was measured against.
    /// </remarks>
    public static int Mode(int[] values)
    {
        if (values == null || values.Length == 0) return 0;
        var counts = new Dictionary<int, int>(values.Length);
        int most = 0;
        for (int i = 0; i < values.Length; i++)
        {
            counts.TryGetValue(values[i], out int seen);
            counts[values[i]] = ++seen;
            if (seen > most) most = seen;
        }
        for (int i = 0; i < values.Length; i++)
            if (counts[values[i]] == most) return values[i];
        return values[0];
    }

    private static int Mode(List<int> values)
    {
        if (values == null || values.Count == 0) return 0;
        var counts = new Dictionary<int, int>(values.Count);
        int most = 0;
        for (int i = 0; i < values.Count; i++)
        {
            counts.TryGetValue(values[i], out int seen);
            counts[values[i]] = ++seen;
            if (seen > most) most = seen;
        }
        for (int i = 0; i < values.Count; i++)
            if (counts[values[i]] == most) return values[i];
        return values[0];
    }

    private readonly struct RunEnd
    {
        public readonly int End;
        public readonly int Thickness;

        public RunEnd(int end, int thickness)
        {
            End = end;
            Thickness = thickness;
        }
    }
}
