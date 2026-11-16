using System;

namespace BaroUI.Core
{
    /// <summary>What kind of gradient a layer is: a linear line (axis-aligned, an angle or a corner) or a radial one.</summary>
    internal enum GradientShape { Linear, Radial }

    /// <summary>The size of a radial gradient's ending shape: the keywords of CSS, or the two lengths written (Explicit).</summary>
    internal enum RadialSize { ClosestSide, FarthestSide, ClosestCorner, FarthestCorner, Explicit }

    /// <summary>The geometry a gradient layer was written with. Lengths and positions are kept as written (the box is known only at paint time).
    /// A linear line that is not on an axis (an angle other than 0/90/180/270deg, or a corner such as `to top right`) is <see cref="Oblique"/>: it is
    /// drawn as an area (<see cref="GradientArea.Bake"/>), not as a ramp. A radial layer is always drawn as an area.</summary>
    internal readonly struct GradientGeometry
    {
        public readonly GradientShape Shape;
        public readonly bool Oblique;
        /// <summary>The angle of an oblique line in degrees, CSS convention (0 up, 90 right, clockwise).</summary>
        public readonly float Angle;
        /// <summary>The corner form: -1, 0 or 1 towards left (-1) or right (1), and up (-1) or down (1). Both zero for an angle.</summary>
        public readonly int CornerX, CornerY;
        public readonly bool Ellipse;
        public readonly RadialSize Size;
        /// <summary>Explicit radii (X, and Y for an ellipse; for a circle both are the radius).</summary>
        public readonly Length SizeX, SizeY;
        /// <summary>The centre of a radial gradient (the position of `at`; 50% 50% by default).</summary>
        public readonly Length PosX, PosY;

        public GradientGeometry(GradientShape shape, bool oblique, float angle, int cornerX, int cornerY, bool ellipse, RadialSize size, Length sizeX, Length sizeY, Length posX, Length posY)
        {
            Shape = shape; Oblique = oblique; Angle = angle; CornerX = cornerX; CornerY = cornerY; Ellipse = ellipse; Size = size;
            SizeX = sizeX; SizeY = sizeY; PosX = posX; PosY = posY;
        }

        /// <summary>A plain linear line (the axis is kept by the gradient itself).</summary>
        public static GradientGeometry Axis => new(GradientShape.Linear, false, 0f, 0, 0, false, RadialSize.FarthestCorner, Length.Zero, Length.Zero, Length.Zero, Length.Zero);

        public static GradientGeometry Radial(bool ellipse, RadialSize size, Length sizeX, Length sizeY, Length posX, Length posY) =>
            new(GradientShape.Radial, true, 0f, 0, 0, ellipse, size, sizeX, sizeY, posX, posY);

        /// <summary>Everything the geometry says, as a hash (mixed into the gradient's hash, so two gradients that look different never share a texture).</summary>
        public ulong Mix(ulong h)
        {
            h = Step(h, (ulong)Shape);
            h = Step(h, Oblique ? 1UL : 0UL);
            h = Step(h, (ulong)(uint)BitConverter.SingleToInt32Bits(Angle));
            h = Step(h, (ulong)(uint)(CornerX + 2) * 4 + (ulong)(uint)(CornerY + 2));
            h = Step(h, Ellipse ? 1UL : 0UL);
            h = Step(h, (ulong)Size);
            h = Step(h, LenKey(SizeX));
            h = Step(h, LenKey(SizeY));
            h = Step(h, LenKey(PosX));
            h = Step(h, LenKey(PosY));
            return h;
        }

        private static ulong Step(ulong h, ulong v) { h ^= v; return h * 1099511628211UL; }

        private static ulong LenKey(Length l) => ((ulong)(uint)BitConverter.SingleToInt32Bits(l.Value) << 8) | (ulong)l.Unit;
    }

    /// <summary>The ellipse of a radial gradient for a box: the centre and the two radii, in layout px.</summary>
    internal readonly struct RadialFit
    {
        public readonly float Cx, Cy, Rx, Ry;
        public RadialFit(float cx, float cy, float rx, float ry) { Cx = cx; Cy = cy; Rx = rx; Ry = ry; }
    }

    /// <summary>Gradients drawn as an area: a radial layer, or a linear one that is not on an axis (an angle or a corner). The geometry follows the CSS image
    /// rules (the gradient line of an angle, closest-side and farthest-corner sizes), the colour comes from the same stops as a ramp (Gradient.Eval), and the
    /// rounded corners of the box are written into the alpha, so a rounded box needs no mask here.</summary>
    internal static class GradientArea
    {
        /// <summary>The largest texture side of an area bake: a larger box is stretched from this (the colours are smooth, so the stretch does not show).</summary>
        public const int MaxSide = 512;

        /// <summary>The direction (unit vector, screen coordinates: y down) and the length of an oblique gradient line for a box w x h in layout px.
        /// An angle points (sin, -cos). A corner `to top right` points along (h, -w): the 50 % line runs through the other two corners, as in CSS.</summary>
        public static void Line(in GradientGeometry geo, float w, float h, out float dx, out float dy, out float len)
        {
            if (geo.CornerX != 0 && geo.CornerY != 0)
            {
                float ux = geo.CornerX * h, uy = geo.CornerY * w, n = MathF.Sqrt(ux * ux + uy * uy);
                if (n > 0) { dx = ux / n; dy = uy / n; }
                else { dx = 0; dy = -1; }
            }
            else
            {
                float a = geo.Angle * MathF.PI / 180f;
                dx = MathF.Sin(a); dy = -MathF.Cos(a);
            }
            len = MathF.Abs(w * dx) + MathF.Abs(h * dy);
        }

        /// <summary>The centre and radii of a radial gradient for a box w x h (layout px), by the CSS sizes: closest-side and farthest-side take the sides
        /// (min or max of the distances to them), closest-corner and farthest-corner the corners; a circle takes the distance to the corner (or the side),
        /// an ellipse keeps the aspect ratio of the sides and reaches the corner with sqrt(2) (CSS). Explicit lengths are used as written.</summary>
        public static RadialFit Fit(in GradientGeometry geo, float w, float h, float unitPx, float emPx)
        {
            float cx = Resolve(geo.PosX, w, unitPx, emPx), cy = Resolve(geo.PosY, h, unitPx, emPx);
            float dxMax = Math.Max(cx, w - cx), dyMax = Math.Max(cy, h - cy);
            float dxMin = Math.Min(cx, w - cx), dyMin = Math.Min(cy, h - cy);
            float rx, ry;
            if (geo.Size == RadialSize.Explicit)
            {
                rx = Resolve(geo.SizeX, w, unitPx, emPx);
                ry = geo.Ellipse ? Resolve(geo.SizeY, h, unitPx, emPx) : rx;
            }
            else if (!geo.Ellipse)
            {
                float r = geo.Size switch
                {
                    RadialSize.ClosestSide => Math.Min(dxMin, dyMin),
                    RadialSize.FarthestSide => Math.Max(dxMax, dyMax),
                    RadialSize.ClosestCorner => MathF.Sqrt(dxMin * dxMin + dyMin * dyMin),
                    _ => MathF.Sqrt(dxMax * dxMax + dyMax * dyMax),
                };
                rx = ry = r;
            }
            else
            {
                switch (geo.Size)
                {
                    case RadialSize.ClosestSide: rx = dxMin; ry = dyMin; break;
                    case RadialSize.FarthestSide: rx = dxMax; ry = dyMax; break;
                    case RadialSize.ClosestCorner: rx = dxMin * MathF.Sqrt(2f); ry = dyMin * MathF.Sqrt(2f); break;
                    default: rx = dxMax * MathF.Sqrt(2f); ry = dyMax * MathF.Sqrt(2f); break;
                }
            }
            return new RadialFit(cx, cy, Math.Max(rx, 1e-3f), Math.Max(ry, 1e-3f));
        }

        /// <summary>A length or position of a box side in layout px: percent of the side, u and em as the window and font size give, px as written.</summary>
        public static float Resolve(Length l, float side, float unitPx, float emPx) => l.Unit switch
        {
            Unit.Percent => l.Value / 100f * side,
            Unit.U => l.Value * unitPx,
            Unit.Em => l.Value * emPx,
            _ => l.Value,
        };

        /// <summary>The stops of an area gradient in fractions of its length: for a linear line the line length, for a radial one the radius on the x axis.</summary>
        public static float[] Stops(Gradient g, in GradientGeometry geo, float w, float h, float unitPx, float emPx, out float dx, out float dy, out float len, out RadialFit fit)
        {
            dx = 0; dy = 0; len = 1; fit = default;
            if (geo.Shape == GradientShape.Linear)
            {
                Line(geo, w, h, out dx, out dy, out len);
                return g.ResolveStops(len, unitPx, emPx);
            }
            fit = Fit(geo, w, h, unitPx, emPx);
            return g.ResolveStops(fit.Rx, unitPx, emPx);
        }

        /// <summary>The area of a gradient layer as RGBA (straight alpha), tw x th texels, for a box of tw/scale x th/scale layout px: texel (x, y) is the centre
        /// of its square. The corners are the texel radii <paramref name="rt"/> (CornerPx of the texture, in texels): outside a corner the alpha falls to 0 over
        /// one texel, so the rounded box is drawn by one quad. Pure: the same numbers give the same bytes.</summary>
        public static byte[] Bake(Gradient g, int tw, int th, float scale, in CornerPx rt, float unitPx, float emPx)
        {
            var px = new byte[Math.Max(0, tw * th * 4)];
            if (tw <= 0 || th <= 0 || !(scale > 0)) { return px; }
            float w = tw / scale, h = th / scale;
            var geo = g.Geometry;
            float[] p = Stops(g, geo, w, h, unitPx, emPx, out float dx, out float dy, out float len, out RadialFit fit);
            bool rounded = rt.TLx > 0 || rt.TLy > 0 || rt.TRx > 0 || rt.TRy > 0 || rt.BRx > 0 || rt.BRy > 0 || rt.BLx > 0 || rt.BLy > 0;
            bool radial = geo.Shape == GradientShape.Radial;
            for (int y = 0; y < th; y++)
            {
                float Y = (y + 0.5f) / scale;
                for (int x = 0; x < tw; x++)
                {
                    float X = (x + 0.5f) / scale;
                    float t;
                    if (radial)
                    {
                        float ex = (X - fit.Cx) / fit.Rx, ey = (Y - fit.Cy) / fit.Ry;
                        t = MathF.Sqrt(ex * ex + ey * ey);
                    }
                    else { t = ((X - w * 0.5f) * dx + (Y - h * 0.5f) * dy) / len + 0.5f; }
                    g.Eval(p, t, out float r, out float gg, out float b, out float a);
                    float cov = rounded ? Coverage(x + 0.5f, y + 0.5f, tw, th, rt) : 1f;
                    int o = (y * tw + x) * 4;
                    px[o] = B(r); px[o + 1] = B(gg); px[o + 2] = B(b); px[o + 3] = B(a * cov);
                }
            }
            return px;
        }

        private static byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

        /// <summary>The coverage of a texel centre (texel units) by the rounded box of tw x th with the corner radii rt: 1 inside, 0 outside, one texel
        /// of soft edge. A corner is an ellipse with its centre at the radii (the same shape as the painter's corner masks).</summary>
        public static float Coverage(float x, float y, float tw, float th, in CornerPx rt)
        {
            if (x < rt.TLx && y < rt.TLy) { return Edge(x, y, rt.TLx, rt.TLy, rt.TLx, rt.TLy); }
            if (x > tw - rt.TRx && y < rt.TRy) { return Edge(x, y, tw - rt.TRx, rt.TRy, rt.TRx, rt.TRy); }
            if (x > tw - rt.BRx && y > th - rt.BRy) { return Edge(x, y, tw - rt.BRx, th - rt.BRy, rt.BRx, rt.BRy); }
            if (x < rt.BLx && y > th - rt.BLy) { return Edge(x, y, rt.BLx, th - rt.BLy, rt.BLx, rt.BLy); }
            return 1f;
        }

        private static float Edge(float x, float y, float cx, float cy, float rx, float ry)
        {
            if (rx <= 0 || ry <= 0) { return 1f; }
            float nx = (x - cx) / rx, ny = (y - cy) / ry;
            float f = MathF.Sqrt(nx * nx + ny * ny);
            return Math.Clamp(0.5f + (1f - f) * Math.Min(rx, ry), 0f, 1f);
        }
    }
}
