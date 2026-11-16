using System;

namespace BaroUI.Core
{
    /// <summary>Analytic antialiasing for the edges the engine draws as textures: circles, rings, quarter rings, rounded rectangles and their
    /// corners (game-free, so the tests run it). Every value is the coverage of one pixel: the distance from the pixel centre to the edge
    /// (signed, positive outside) goes through one linear ramp 1 px wide, centred on the edge (0.5 at a centre on the edge, 1 a pixel inside,
    /// 0 a pixel outside). That replaces the 4x4 point samples (17 levels, a hard step at the edge). A straight side on the grid gives exactly
    /// 0 or 1, so an arc and a side have the same edge width.
    /// Thin rings (1-2 px) get a mild lift of their partly covered pixels (<see cref="ThinLift"/>): a light line blended in sRGB carries less light
    /// at half coverage than in linear light.</summary>
    internal static class Coverage
    {
        /// <summary>The coverage of a pixel whose centre is sd px from an edge (negative inside). One linear 1 px ramp.</summary>
        public static float Edge(float sd) => Math.Clamp(0.5f - sd, 0f, 1f);

        /// <summary>Approximate signed distance from (dx, dy), measured from the centre of an axis-aligned ellipse of radii (rx, ry) in the quadrant
        /// dx, dy >= 0 (the caller clamps). Exact for a circle; the usual two-norm estimate (Inigo Quilez) for an ellipse.</summary>
        public static float EllipseDistance(float dx, float dy, float rx, float ry)
        {
            if (rx <= 0f || ry <= 0f) { return -MathF.Max(rx, ry); }
            float ax = dx / rx, ay = dy / ry;
            float k0 = MathF.Sqrt(ax * ax + ay * ay);
            float bx = dx / (rx * rx), by = dy / (ry * ry);
            float k1 = MathF.Sqrt(bx * bx + by * by);
            if (k1 <= 1e-9f) { return -MathF.Min(rx, ry); }
            return k0 * (k0 - 1f) / k1;
        }

        /// <summary>Coverage of an ellipse of radii (rx, ry) at a point (dx, dy) measured from its centre, for the corner of a box: the point is clamped
        /// to the quadrant, so a point beyond the centre line is judged on the centre line (full when it is inside the arc's bounding box).</summary>
        public static float Quadrant(float dx, float dy, float rx, float ry) =>
            Edge(EllipseDistance(MathF.Max(dx, 0f), MathF.Max(dy, 0f), rx, ry));

        /// <summary>Coverage of a disc of radius <paramref name="radius"/> (pixels) for a pixel whose centre is <paramref name="d"/> from its centre.</summary>
        public static float Disc(float d, float radius) => Edge(d - radius);

        /// <summary>A ring between two circles: the disc of radius <paramref name="outer"/> less the hole of radius <paramref name="inner"/> (no hole
        /// when inner is not positive). Outer minus inner, clamped: a ring 1 px wide has the coverage of a 1 px side.</summary>
        public static float Ring(float d, float outer, float inner)
        {
            float v = Disc(d, outer);
            if (inner > 0f) { v -= Disc(d, inner); }
            return Math.Clamp(v, 0f, 1f);
        }

        /// <summary>Coverage of a rounded square spanning [lo, hi] on both axes with corner radius r (a circle at each corner; r is clamped to half the
        /// side). Signed distance to the rounded box, then the 1 px ramp.</summary>
        public static float RoundedSquare(float x, float y, float lo, float hi, float r)
        {
            float half = (hi - lo) / 2f;
            if (half <= 0f) { return 0f; }
            r = Math.Clamp(r, 0f, half);
            float cx = lo + half, cy = lo + half;
            float qx = MathF.Abs(x - cx) - (half - r), qy = MathF.Abs(y - cy) - (half - r);
            float ox = MathF.Max(qx, 0f), oy = MathF.Max(qy, 0f);
            float sd = MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(qx, qy), 0f) - r;
            return Edge(sd);
        }

        /// <summary>The lift of a partly covered pixel for a thin ring: thickness 1 px uses an exponent of 0.8, 2 px 0.9, anything thicker none. The
        /// exponent is below 1, so a half-covered pixel gets more than half. Fully covered (1) and empty (0) pixels are unchanged.</summary>
        public static float ThinLift(float cov, int thickness)
        {
            if (thickness <= 0 || thickness > 2 || cov <= 0f || cov >= 1f) { return cov; }
            return MathF.Pow(cov, thickness == 1 ? 0.8f : 0.9f);
        }

        /// <summary>One alpha byte from a coverage, rounded.</summary>
        public static byte ToByte(float cov) => (byte)Math.Clamp((int)MathF.Round(cov * 255f), 0, 255);
    }
}
