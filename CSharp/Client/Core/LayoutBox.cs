using System;
using System.Collections.Generic;

namespace BaroUI.Core
{
    /// <summary>Text metrics the layout needs. In the game: the loaded ScalableFonts; in tests: a monospace stub
    /// with JetBrains Mono's metrics (advance 0.6 em, ascent 1.02 em, descent 0.3 em).</summary>
    public interface ITextMeasurer
    {
        float Width(string text, ComputedStyle style);
        float Ascent(ComputedStyle style);
        float Descent(ComputedStyle style);
        /// <summary>Used for line-height: normal.</summary>
        float NormalLineHeight(ComputedStyle style);
    }

    public struct Edges
    {
        public float Top, Right, Bottom, Left;
        public float Horizontal => Left + Right;
        public float Vertical => Top + Bottom;
        public override string ToString() => $"{Top} {Right} {Bottom} {Left}";
    }

    /// <summary>One placed piece of text: a line (or the part of a line) in one style.</summary>
    public sealed class TextFragment
    {
        public string Text;
        public float X, Y, Width, Height, Baseline;   // Y/Height: the line box; Baseline: absolute y of the baseline
        public ComputedStyle Style;
        public Element Owner;                           // the element whose text this is (span, b, the block itself)
        internal float RelX, RelY, RelBaseline;         // relative to the run box (incremental layout)
    }

    internal enum BoxKind { Flow, Flex, Grid, InlineRun }

    /// <summary>Layout result for one element (or an anonymous run of inline content). X/Y/W/H: border box. During
    /// layout they are relative to the parent's border box; after LayoutEngine.Run they are absolute.</summary>
    public sealed class LayoutBox
    {
        public Element Element;              // null for an anonymous inline run
        public ComputedStyle Style;
        public float X, Y, W, H;
        public Edges Margin, Border, Padding;
        public readonly List<LayoutBox> Children = new();
        public readonly List<TextFragment> Text = new();
        /// <summary>Extent of the content measured from the padding box origin: what a scroll area scrolls over.</summary>
        public float ScrollWidth, ScrollHeight;
        public bool ClipX, ClipY;
        public LayoutBox Parent { get; internal set; }

        internal BoxKind Kind;
        internal bool OutOfFlow, IsAtom, BfcRoot;
        /// <summary>The pixel size of the picture of an img (IImageSizes), 0 when there is none: the size of a side the CSS leaves to auto (LayoutEngine.SpecSize).</summary>
        internal float NaturalW, NaturalH;
        /// <summary>position: sticky: the paint and the hit test move it inside its scrolling ancestor (ScreenMap.StickyOffset); layout does not.</summary>
        internal bool Sticky;
        internal List<InlineItem> Inline;
        internal float Baseline = float.NaN;      // from the border-box top
        internal float MarginTopOut, MarginBottomOut;   // collapsed margins as seen by the parent
        internal float? MinContent, MaxContent;       // border-box intrinsic widths (cached)
        internal int GridColumnStart, GridColumnSpan = 1;
        /// <summary>Constraints of the last real layout of this subtree (what its children and text show).</summary>
        internal (float, float?, float?, float?, bool)? LastLayout;
        /// <summary>Constraints of the last Layout call; differs from LastLayout after a size-cache hit.</summary>
        internal (float, float?, float?, float?, bool)? LastCall;
        /// <summary>What the parent reads after laying this box out, per constraints seen: a flex or grid parent
        /// measures with one set and places with another, so one memo entry would always miss.</summary>
        /// <summary>Up to four entries: two inline (most boxes see one or two constraints), two more on demand.</summary>
        internal SizeEntry Size0, Size1;
        internal SizeEntry[] SizesMore;
        internal int SizeCount, SizeNext;

        internal ref SizeEntry SizeAt(int i)
        {
            if (i == 0) { return ref Size0; }
            if (i == 1) { return ref Size1; }
            return ref SizesMore[i - 2];
        }
        /// <summary>Position relative to the parent before LayoutEngine made it absolute (incremental layout).</summary>
        internal float RelX, RelY;
        /// <summary>Children in paint order (in-flow first, then positioned by z-index); set by the renderer.</summary>
        internal LayoutBox[] PaintOrder;
        /// <summary>Children as they were when PaintOrder was made: incremental layout replaces child boxes of a
        /// box it keeps, and the renderer must not paint the old ones (stage 8).</summary>
        internal LayoutBox[] PaintOrderOf;

        public float ContentX => X + Border.Left + Padding.Left;
        public float ContentY => Y + Border.Top + Padding.Top;
        public float ContentWidth => Math.Max(0, W - Border.Horizontal - Padding.Horizontal);
        public float ContentHeight => Math.Max(0, H - Border.Vertical - Padding.Vertical);

        /// <summary>Depth-first: this box, then its children.</summary>
        public IEnumerable<LayoutBox> All()
        {
            yield return this;
            foreach (var c in Children) { foreach (var d in c.All()) { yield return d; } }
        }

        public LayoutBox Find(Element e)
        {
            foreach (var b in All()) { if (b.Element == e) { return b; } }
            return null;
        }

        public override string ToString() => $"{Element?.ToString() ?? "(text)"} [{X:0.##},{Y:0.##} {W:0.##}x{H:0.##}]";
    }

    internal struct SizeEntry
    {
        public (float, float?, float?, float?, bool) Key;
        public float W, H, Baseline, MarginTopOut, MarginBottomOut, ScrollWidth, ScrollHeight;
        public Edges Margin, Border, Padding;
    }

    internal abstract class InlineItem
    {
        public bool SpaceBefore;
        /// <summary>Style of the text the collapsed space came from: "a <b>b</b>" sizes the space in a's font.</summary>
        public ComputedStyle SpaceStyle;
        public ComputedStyle Style;
    }

    internal sealed class WordItem : InlineItem
    {
        public string Text;
        public Element Owner;
    }

    internal sealed class AtomItem : InlineItem
    {
        public LayoutBox Box;
    }

    internal sealed class BreakItem : InlineItem { }
}
