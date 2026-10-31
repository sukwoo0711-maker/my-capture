using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Ocr;

namespace MyCapture.App.Pinning;

internal static partial class PinTranslationOverlay
{
    internal static double SourceLinePitch(IReadOnlyList<OcrLine> lines)
    {
        double[] tops = lines.Where(line => OcrGeometry.IsValid(line.Bounds))
            .Select(line => line.Bounds.Y).Distinct().Order().ToArray();
        if (tops.Length < 2) return 0;
        double minimum = Median(lines.Select(line => line.Bounds.Height)) * .4;
        double[] distances = tops.Zip(tops.Skip(1), (first, second) => second - first)
            .Where(distance => distance > minimum).ToArray();
        return distances.Length == 0 ? 0 : Median(distances);
    }

    internal static TextAlignment InferAlignment(IReadOnlyList<OcrLine> lines)
    {
        OcrRect[] rectangles = lines.Select(line => line.Bounds).Where(OcrGeometry.IsValid).ToArray();
        if (rectangles.Length < 2) return TextAlignment.Left;
        double tolerance = Math.Max(2, Median(rectangles.Select(rectangle => rectangle.Height)) * .15);
        static double Spread(IEnumerable<double> values)
        {
            double[] positions = values.ToArray();
            return positions.Max() - positions.Min();
        }
        if (Spread(rectangles.Select(rectangle => rectangle.X)) <= tolerance) return TextAlignment.Left;
        if (Spread(rectangles.Select(rectangle => rectangle.X + rectangle.Width / 2)) <= tolerance)
            return TextAlignment.Center;
        return Spread(rectangles.Select(rectangle => rectangle.Right)) <= tolerance
            ? TextAlignment.Right : TextAlignment.Left;
    }

    private static OcrRect EstablishedColumnLimits(OcrRect limits, IReadOnlyList<PinOverlayTranslation> translations,
        IReadOnlyList<double> fontSizes, int index, TextAlignment alignment)
    {
        PinOverlayBlock block = translations[index].Block;
        if (block.Lines.Count < 2 || alignment != TextAlignment.Left) return limits;
        double tolerance = Math.Max(2, Median(block.Lines.Select(line => line.Bounds.Height)) * .5);
        int peers = 0;
        double right = block.Bounds.Right;
        for (int otherIndex = 0; otherIndex < translations.Count; otherIndex++)
        {
            if (otherIndex == index) continue;
            PinOverlayBlock other = translations[otherIndex].Block;
            if (other.PreservePixels || other.Lines.Count < 2 || Math.Abs(other.Bounds.X - block.Bounds.X) > tolerance
                || Math.Max(fontSizes[index], fontSizes[otherIndex]) > Math.Min(fontSizes[index], fontSizes[otherIndex]) * 1.2
                || InferAlignment(other.Lines) != TextAlignment.Left) continue;
            peers++;
            right = Math.Max(right, other.Bounds.Right);
        }
        // Two independent paragraphs establish a column even beside a floating image. A
        // single neighbouring paragraph cannot establish a shared maximum text width.
        return peers < 2 ? limits : limits with { Width = Math.Max(0, Math.Min(limits.Right, right) - limits.X) };
    }

    /// <summary>
    /// Source ink belongs to this block; every other pixel occupied by its text or plate must
    /// match its ring. A graphics edge is a boundary even when OCR did not report any text there.
    /// </summary>
    private sealed class BackgroundSurface
    {
        private const int Tolerance = 18;
        private readonly byte[] pixels;
        private readonly int width, height, stride;
        private readonly OcrRect bounds;
        private readonly OcrRect[] sourceInk;
        private readonly OcrRect[] occupiedLines;
        private readonly Color background;
        private readonly int backgroundTolerance = Tolerance;
        private readonly bool noisySurface;
        private readonly bool texturedSurface;
        private readonly Color topLeft, topRight, bottomLeft, bottomRight;
        private readonly double sampleLeft, sampleRight, sampleTop, sampleBottom;
        private readonly bool gradient;
        private readonly Color[] upperSamples = [], lowerSamples = [];
        private readonly int continuousLeft, continuousRight;
        private readonly OcrRect[] extraInk;
        private readonly OcrRect[] preservedInk;

        internal IReadOnlyList<OcrRect> ExtraInk => extraInk;
        internal IReadOnlyList<OcrRect> TextExclusions => preservedInk;
        internal double LeadingInset { get; }
        internal bool Underline { get; }

        internal BackgroundSurface(byte[] pixels, int width, int height, int stride, PinOverlayBlock block, Color background)
            : this(pixels, width, height, stride, block, background, block.ProtectedRegions) { }

        internal BackgroundSurface(byte[] pixels, int width, int height, int stride, PinOverlayBlock block, Color background,
            IReadOnlyList<OcrRect> protectedRectangles)
        {
            this.pixels = pixels;
            this.width = width;
            this.height = height;
            this.stride = stride;
            this.background = background;
            bounds = Clamp(block.Bounds, width, height);
            static OcrRect PixelBounds(OcrRect ink) => new(Math.Floor(ink.X), Math.Floor(ink.Y),
                Math.Ceiling(ink.Right) - Math.Floor(ink.X), Math.Ceiling(ink.Bottom) - Math.Floor(ink.Y));
            occupiedLines = block.Lines.Select(line => PixelBounds(Clamp(line.Bounds, width, height)))
                .OrderBy(rect => rect.X).ToArray();
            // A line's rectangle includes spaces and can include a word OCR missed. Only
            // recognized word rectangles grant erase ownership; line rectangles grant layout
            // space, with any unread ink clipped out of the drawing below.
            sourceInk = block.Lines.SelectMany(line =>
            {
                OcrRect[] words = line.Words.Where(word => OcrGeometry.IsValid(word.Bounds))
                    .Select(word => PixelBounds(Clamp(word.Bounds, width, height))).ToArray();
                return words.Length > 0 ? words : [PixelBounds(Clamp(line.Bounds, width, height))];
            }).OrderBy(rect => rect.X).ToArray();
            // Leave room for a subtitle's soft glyph shadow when validating the side ring.
            // Candidate occupancy still checks every intervening pixel against the model.
            sampleLeft = Math.Max(0, Math.Floor(bounds.X) - 5);
            sampleRight = Math.Min(width - 1, Math.Ceiling(bounds.Right) + 5);
            sampleTop = Math.Max(0, Math.Floor(bounds.Y) - 3);
            sampleBottom = Math.Min(height - 1, Math.Ceiling(bounds.Bottom) + 2);
            topLeft = Pixel(pixels, stride, (int)sampleLeft, (int)sampleTop);
            topRight = Pixel(pixels, stride, (int)sampleRight, (int)sampleTop);
            bottomLeft = Pixel(pixels, stride, (int)sampleLeft, (int)sampleBottom);
            bottomRight = Pixel(pixels, stride, (int)sampleRight, (int)sampleBottom);
            // Preserve nonlinear horizontal gradients by sampling each column's upper/lower
            // ring. Require a smooth ring and stop at its first colour step before extrapolating
            // vertically; another panel or an image edge cannot become a new background sample.
            if (sampleRight > sampleLeft && sampleBottom > sampleTop
                && new[] { topRight, bottomLeft, bottomRight }.Any(color => ColorDistance(color, topLeft) > 6))
            {
                upperSamples = new Color[width]; lowerSamples = new Color[width];
                for (int x = 0; x < width; x++)
                {
                    upperSamples[x] = Pixel(pixels, stride, x, (int)sampleTop);
                    lowerSamples[x] = Pixel(pixels, stride, x, (int)sampleBottom);
                }
                continuousLeft = (int)sampleLeft; continuousRight = (int)sampleRight;
                while (continuousLeft > 0 && ColumnStep(continuousLeft - 1, continuousLeft) <= 8) continuousLeft--;
                while (continuousRight < width - 1 && ColumnStep(continuousRight, continuousRight + 1) <= 8) continuousRight++;
                gradient = RingMatchesGradient();
            }
            if (!gradient) backgroundTolerance = EstimateNoiseTolerance(out noisySurface, out texturedSurface);
            OcrRect[] protection = protectedRectangles.Concat(block.ProtectedRegions).Distinct().ToArray();
            OcrRect[] decorations = FindUnderlines(block.Lines, protection);
            Underline = block.Lines.Count == 1 && decorations.Any(rect => rect.Width >= block.Bounds.Width * .9);
            decorations = decorations.Concat(FindWordSurfaces(block.Lines, protection)).ToArray();
            if (decorations.Length > 0)
            {
                sourceInk = sourceInk.Concat(decorations).OrderBy(rect => rect.X).ToArray();
                occupiedLines = occupiedLines.Concat(decorations).OrderBy(rect => rect.X).ToArray();
            }
            OcrRect[] preservedLeading = [];
            extraInk = FindUnrecognizedInk(block.Lines, protection, out preservedLeading)
                .Concat(decorations).ToArray();
            if (extraInk.Length > 0)
            {
                sourceInk = sourceInk.Concat(extraInk).OrderBy(rect => rect.X).ToArray();
                occupiedLines = occupiedLines.Concat(extraInk).OrderBy(rect => rect.X).ToArray();
            }
            if (preservedLeading.Length > 0)
                occupiedLines = occupiedLines.Concat(preservedLeading).OrderBy(rect => rect.X).ToArray();
            preservedInk = preservedLeading.Concat(block.Lines.Where(line => line.Words.Count > 0)
                .Select(line => new OcrRect(line.Bounds.X, line.Bounds.Y,
                    Math.Max(0, line.Words.Min(word => word.Bounds.X) - line.Bounds.X), line.Bounds.Height))
                .Where(rect => rect.Width > 2)).Concat(block.ProtectedRegions).Distinct().ToArray();
            if (block.ProtectedRegions.Count > 0)
                occupiedLines = occupiedLines.Concat(block.ProtectedRegions).OrderBy(rect => rect.X).ToArray();
            LeadingInset = preservedInk.Where(rect => rect.Y < bounds.Y + 2)
                .Select(rect => Math.Max(0, rect.Right - bounds.X + 2)).DefaultIfEmpty(0).Max();
        }

        internal OcrRect HorizontalLimits(TextAlignment alignment)
        {
            double left = 0, right = width;
            if (alignment != TextAlignment.Right)
            {
                for (int x = (int)Math.Ceiling(bounds.Right); x < width; x++)
                    if (!MatchingColumn(x, (int)bounds.Y, (int)Math.Ceiling(bounds.Bottom)))
                    { right = Math.Max(bounds.Right, x); break; }
            }
            if (alignment != TextAlignment.Left)
            {
                for (int x = (int)bounds.X - 1; x >= 0; x--)
                    if (!MatchingColumn(x, (int)bounds.Y, (int)Math.Ceiling(bounds.Bottom)))
                    { left = Math.Min(bounds.X, x + 1); break; }
            }
            return new(left, bounds.Y, Math.Max(0, right - left), bounds.Height);
        }

        private bool MatchingColumn(int x, int top, int bottom)
        {
            for (int y = top; y < bottom; y++)
                if (!Matches(y * stride + x * 4) && !Contains(sourceInk, x, y)) return false;
            return true;
        }

        private static bool Contains(IReadOnlyList<OcrRect> rectangles, int x, int y)
        {
            for (int index = 0; index < rectangles.Count; index++)
            {
                OcrRect rect = rectangles[index];
                if (x >= rect.X && x < rect.Right && y >= rect.Y && y < rect.Bottom) return true;
            }
            return false;
        }

        private OcrRect[] FindUnderlines(IReadOnlyList<OcrLine> lines, IReadOnlyList<OcrRect> protectedRectangles)
        {
            if (texturedSurface) return [];
            var found = new HashSet<OcrRect>();
            bool Ink(int x, int y) => x >= 0 && x < width && y >= 0 && y < height
                && PixelDistance(x, y, gradient ? GradientColor(x, y) : background) >= 48;
            foreach (OcrLine line in lines)
            {
                OcrRect area = Clamp(line.Bounds, width, height);
                double edgeAllowance = Math.Max(1, Math.Ceiling(area.Height * .12));
                int left = Math.Max(0, (int)Math.Floor(area.X - edgeAllowance)), right = Math.Min(width, (int)Math.Ceiling(area.Right + edgeAllowance));
                int bottom = Math.Min(height, (int)Math.Ceiling(area.Bottom + area.Height * .4));
                for (int y = Math.Max(0, (int)(area.Y + area.Height * .6)); y < bottom; y++)
                    for (int x = left; x < right; x++)
                    {
                        if (!Ink(x, y)) continue;
                        int start = x;
                        while (x + 1 < width && Ink(x + 1, y)) x++;
                        int end = x + 1;
                        if (Ink(start - 1, y) || start < area.X - edgeAllowance || end > area.Right + edgeAllowance
                            || end - start < Math.Max(6, area.Height * .5)) continue;
                        int upper = y, lower = y, thickness = int.MaxValue;
                        // Descenders may touch the underline at its midpoint. Its thin strip
                        // is established across several columns, including the spaces.
                        for (int probe = start; probe < end; probe += Math.Max(1, (end - start) / 12))
                        {
                            int above = y, below = y;
                            while (above > 0 && Ink(probe, above - 1)) above--;
                            while (below + 1 < height && Ink(probe, below + 1)) below++;
                            if (below - above + 1 >= thickness) continue;
                            upper = above; lower = below; thickness = below - above + 1;
                        }
                        if (lower - upper + 1 > Math.Max(2, area.Height * .1)) continue;
                        double ownerLeft = double.PositiveInfinity, ownerRight = double.NegativeInfinity;
                        foreach (OcrWord word in line.Words)
                            if (word.Bounds.Right > start && word.Bounds.X < end
                                && word.Bounds.Bottom <= upper + Math.Max(1, area.Height * .25)
                                && upper - word.Bounds.Bottom <= area.Height * .4)
                            {
                                ownerLeft = Math.Min(ownerLeft, word.Bounds.X);
                                ownerRight = Math.Max(ownerRight, word.Bounds.Right);
                            }
                        if (start < ownerLeft - edgeAllowance || end > ownerRight + edgeAllowance || end - start < 12) continue;
                        OcrRect decoration = new(start, upper, end - start, lower - upper + 1);
                        bool protectedInk = false;
                        foreach (OcrRect rect in protectedRectangles)
                            if (Intersects(rect, decoration)) { protectedInk = true; break; }
                        if (!protectedInk) found.Add(decoration);
                    }
            }
            return found.ToArray();
        }

        private OcrRect[] FindWordSurfaces(IReadOnlyList<OcrLine> lines, IReadOnlyList<OcrRect> protectedRectangles)
        {
            if (gradient || noisySurface || texturedSurface) return [];
            var found = new List<OcrRect>();
            foreach (OcrLine line in lines)
            foreach (OcrWord word in line.Words)
            {
                OcrRect ink = Clamp(word.Bounds, width, height);
                double glyphHeight = Math.Max(ink.Height, line.Bounds.Height * .75);
                if (word.Text.Any(char.IsWhiteSpace) || ink.Width > glyphHeight * 12) continue;
                int margin = Math.Max(3, (int)Math.Ceiling(glyphHeight * .8));
                double punctuationOverhang = Math.Ceiling(glyphHeight * .3);
                int left = Math.Max(0, (int)ink.X - margin), right = Math.Min(width, (int)Math.Ceiling(ink.Right) + margin);
                int top = Math.Max(0, (int)ink.Y - margin), bottom = Math.Min(height, (int)Math.Ceiling(ink.Bottom) + margin);
                var tested = new HashSet<int>();
                bool accepted = false;
                for (int distance = 1; distance <= margin && !accepted; distance++)
                {
                    (int X, int Y)[] seeds =
                    [
                        ((int)(ink.X + ink.Width / 2), (int)ink.Y - distance),
                        ((int)(ink.X + ink.Width / 2), (int)Math.Ceiling(ink.Bottom) + distance - 1),
                        ((int)ink.X - distance, (int)(ink.Y + ink.Height / 2)),
                        ((int)Math.Ceiling(ink.Right) + distance - 1, (int)(ink.Y + ink.Height / 2)),
                    ];
                    foreach (var seed in seeds)
                    {
                        if (seed.X < left || seed.X >= right || seed.Y < top || seed.Y >= bottom) continue;
                        int seedKey = seed.Y * width + seed.X;
                        if (!tested.Add(seedKey)) continue;
                        Color color = Pixel(pixels, stride, seed.X, seed.Y);
                        int contrast = ColorDistance(color, background);
                        if (contrast <= Tolerance || contrast > 96) continue;
                        bool Same(int x, int y) => PixelDistance(x, y, color) <= 8;
                        var queue = new List<int> { seedKey };
                        var visited = new HashSet<int> { seedKey };
                        int minX = seed.X, maxX = seed.X, minY = seed.Y, maxY = seed.Y;
                        bool clipped = false;
                        for (int cursor = 0; cursor < queue.Count && !clipped; cursor++)
                        {
                            int x = queue[cursor] % width, y = queue[cursor] / width;
                            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                            if (x == left || x == right - 1 || y == top || y == bottom - 1) { clipped = true; break; }
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int xx = x + dx, yy = y + dy, key = yy * width + xx;
                                    if (visited.Add(key) && Same(xx, yy)) { queue.Add(key); tested.Add(key); }
                                }
                        }
                        if (clipped || minX > ink.X + punctuationOverhang || maxX + 1 < ink.Right - punctuationOverhang
                            || minY > ink.Y || maxY + 1 < ink.Bottom
                            || !((minX <= ink.X - 2 && maxX + 1 >= ink.Right + 2)
                                || (minY <= ink.Y - 2 && maxY + 1 >= ink.Bottom + 2))) continue;
                        OcrRect area = new(minX, minY, maxX - minX + 1, maxY - minY + 1);
                        if (protectedRectangles.Any(rect => Intersects(rect, area))
                            || lines.SelectMany(sourceLine => sourceLine.Words).Any(other => !ReferenceEquals(other, word)
                                && Intersects(other.Bounds, area))) continue;
                        int upper = 0, lower = 0, first = 0, last = 0;
                        for (int x = minX; x <= maxX; x++) { if (Same(x, minY)) upper++; if (Same(x, maxY)) lower++; }
                        for (int y = minY; y <= maxY; y++) { if (Same(minX, y)) first++; if (Same(maxX, y)) last++; }
                        // Four bounded edges distinguish a word's capsule from a grid, panel,
                        // neighbouring graphic, or a few disconnected antialiased glyph pixels.
                        if (Math.Min(upper, lower) < area.Width * .4 || Math.Min(first, last) < area.Height * .4) continue;
                        found.Add(area); accepted = true; break;
                    }
                }
            }
            return found.ToArray();
        }

        private OcrRect[] FindUnrecognizedInk(IReadOnlyList<OcrLine> lines, IReadOnlyList<OcrRect> protectedRectangles,
            out OcrRect[] preservedLeading)
        {
            preservedLeading = [];
            int left = (int)bounds.X, top = (int)bounds.Y;
            int right = (int)Math.Ceiling(bounds.Right), bottom = (int)Math.Ceiling(bounds.Bottom);
            int columns = right - left;
            if (columns <= 0 || bottom <= top) return [];
            if (lines.Count == 1 && lines[0].Words.Count <= 1
                && sourceInk.Any(rect => rect.X <= left && rect.Right >= right && rect.Y <= top && rect.Bottom >= bottom)) return [];
            bool[] visited = new bool[checked(columns * (bottom - top))];
            var component = new List<int>();
            var found = new List<OcrRect>();
            var punctuation = new List<OcrRect>();
            var candidates = new List<(OcrRect Bounds, int Line, bool Raised, bool Leading, bool Clipped)>();
            var incompleteLeadingLines = new HashSet<int>();
            double inkHeight = Median(lines.Select(line => line.Bounds.Height));
            OcrRect[] nearbyProtected = protectedRectangles.Where(rect => Intersects(rect, bounds)).ToArray();
            bool InLineBand(int y)
            {
                for (int index = 0; index < lines.Count; index++)
                    if (y >= lines[index].Bounds.Y - inkHeight * .65 && y < lines[index].Bounds.Bottom + inkHeight * .2) return true;
                return false;
            }
            bool Background(int x, int y) => Matches(y * stride + x * 4);
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                {
                    int offset = (y - top) * columns + x - left;
                    if (visited[offset] || Contains(sourceInk, x, y)
                        || Contains(nearbyProtected, x, y) || Background(x, y) || !InLineBand(y)) continue;
                    component.Clear(); component.Add(y * width + x); visited[offset] = true;
                    int minX = x, maxX = x, minY = y, maxY = y;
                    int maximumContrast = 0;
                    bool touchesProtected = false, touchesSource = false, clippedComponent = false;
                    for (int cursor = 0; cursor < component.Count; cursor++)
                    {
                        int current = component[cursor], currentX = current % width, currentY = current / width;
                        minX = Math.Min(minX, currentX); maxX = Math.Max(maxX, currentX);
                        minY = Math.Min(minY, currentY); maxY = Math.Max(maxY, currentY);
                        clippedComponent |= currentX == 0 || currentX == width - 1 || currentY == 0 || currentY == height - 1;
                        maximumContrast = Math.Max(maximumContrast, ColorDistance(Pixel(pixels, stride, currentX, currentY),
                            gradient ? GradientColor(currentX, currentY) : background));
                        touchesSource |= Contains(sourceInk, currentX, currentY);
                        // A large graphic is a barrier. Stop before its flood can allocate an
                        // image-sized list; abandoning recovery preserves every original pixel.
                        if (maxX - minX + 1 > inkHeight * 8 || maxY - minY + 1 > inkHeight * 1.8
                            || component.Count > inkHeight * inkHeight * 8) return [];
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nextX = currentX + dx, nextY = currentY + dy;
                                if (nextX < left || nextX >= right || nextY < top || nextY >= bottom)
                                {
                                    if (nextX >= 0 && nextX < width && nextY >= 0 && nextY < height
                                        && !Background(nextX, nextY)) clippedComponent = true;
                                    continue;
                                }
                                int next = (nextY - top) * columns + nextX - left;
                                if (visited[next] || Background(nextX, nextY)) continue;
                                visited[next] = true;
                                if (Contains(nearbyProtected, nextX, nextY)) { touchesProtected = true; continue; }
                                component.Add(nextY * width + nextX);
                            }
                    }
                    int componentWidth = maxX - minX + 1, componentHeight = maxY - minY + 1;
                    if (touchesProtected || componentWidth > inkHeight * (touchesSource ? 8 : 3) || componentHeight > inkHeight * 1.8
                        || component.Count > inkHeight * inkHeight * (touchesSource ? 8 : 2.5)
                        || (touchesSource && componentWidth > inkHeight * 3 && componentHeight < inkHeight * .4)
                        || (componentWidth >= inkHeight * .5 && componentHeight >= inkHeight * .5
                            && component.Count > componentWidth * componentHeight * .85)) continue;
                    OcrRect recovered = new(minX, minY, componentWidth, componentHeight);
                    // Connected glyph fringes have a known owner. Disconnected ink needs a
                    // multi-glyph run beside an OCR line; a lone icon in a short line's trailing
                    // whitespace must never become source text merely because it is small.
                    if (touchesSource) { found.Add(recovered); continue; }
                    // A narrow detached raster fragment just beyond an OCR edge still has a
                    // word owner. Whole glyphs and separated words do not receive that grant.
                    if ((componentWidth <= inkHeight * .25 || componentHeight <= inkHeight * .25)
                        && sourceInk.Any(ink => recovered.X >= ink.X - 2 && recovered.Right <= ink.Right + 2
                            && recovered.Y >= ink.Y - 2 && recovered.Bottom <= ink.Bottom + 2))
                    { found.Add(recovered); continue; }
                    for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                    {
                        OcrRect line = lines[lineIndex].Bounds;
                        bool leading = recovered.Right <= line.X && line.X - recovered.X <= inkHeight * 3
                            && recovered.Y >= line.Y - 1 && recovered.Bottom <= line.Bottom + 1;
                        bool raised = recovered.X >= line.Right - inkHeight * .25 && recovered.X - line.Right <= inkHeight
                            && recovered.Y < line.Y - 1 && recovered.Bottom <= line.Y + inkHeight * .65
                            && recovered.Y >= line.Y - inkHeight * .65 && recovered.Height <= inkHeight * .95;
                        bool inline = recovered.X >= line.X && recovered.X <= line.Right + inkHeight * 2
                            && recovered.Y >= line.Y - 1 && recovered.Bottom <= line.Bottom + 1;
                        if (clippedComponent && recovered.Right >= width && recovered.X >= line.Right
                            && recovered.Y >= line.Y - 1 && recovered.Bottom <= line.Bottom + 1)
                        {
                            punctuation.Add(new(recovered.X, line.Y, width - recovered.X, line.Height));
                            break;
                        }
                        bool trailingDash = !texturedSurface && maximumContrast >= 100
                            && recovered.X >= line.Right && recovered.X - line.Right <= inkHeight
                            && recovered.Width >= inkHeight * .5 && recovered.Width <= inkHeight * 1.5
                            && recovered.Height <= Math.Max(2, inkHeight * .2)
                            && recovered.Y >= line.Y + line.Height * .25 && recovered.Bottom <= line.Y + line.Height * .75;
                        if (trailingDash) { punctuation.Add(Inflate(recovered, 1, width, height)); break; }
                        if (inline && !raised && (maximumContrast < 100 || recovered.Height < inkHeight * .45)) inline = false;
                        if (!leading && !raised && !inline) continue;
                        if (leading && clippedComponent) incompleteLeadingLines.Add(lineIndex);
                        // Tiny references can join through pale antialiasing. Requiring three
                        // distinct columns of dark ink recovers [2] without owning a hollow icon.
                        if (raised && HasSeparateGlyphColumns(recovered)) { found.Add(recovered); break; }
                        candidates.Add((recovered, lineIndex, raised, leading, clippedComponent));
                        break;
                    }
                }
            var kept = new List<(OcrRect Bounds, int Line)>();
            foreach (var candidate in candidates)
            {
                bool hasNeighbor = candidates.Any(other => other != candidate && other.Line == candidate.Line && other.Raised == candidate.Raised
                    && Math.Max(candidate.Bounds.X, other.Bounds.X) - Math.Min(candidate.Bounds.Right, other.Bounds.Right) <= inkHeight * .6
                    && Math.Abs(candidate.Bounds.Bottom - other.Bounds.Bottom) <= inkHeight * .3);
                bool internalGlyph = !candidate.Raised && !candidate.Leading && candidate.Bounds.Width >= inkHeight * .45
                    && lines[candidate.Line].Words.Any(word => word.Bounds.Right <= candidate.Bounds.X)
                    && lines[candidate.Line].Words.Any(word => word.Bounds.X >= candidate.Bounds.Right);
                if (hasNeighbor && (candidate.Raised || (candidate.Leading && !incompleteLeadingLines.Contains(candidate.Line)))) found.Add(candidate.Bounds);
                else if (hasNeighbor || internalGlyph || (candidate.Clipped && !candidate.Leading && !candidate.Raised
                    && (candidate.Bounds.Width >= inkHeight * .6 || candidate.Bounds.Right >= width))) kept.Add((candidate.Bounds, candidate.Line));
            }
            // A crop through the first glyph gives only a fragment of an unread word. Do not
            // erase the other glyphs of that run and leave the fragment behind. Keep the whole
            // run in the source, while permitting the paragraph's recognized text to be laid out.
            var preserved = incompleteLeadingLines.Where(line => candidates.Count(candidate => candidate.Line == line && candidate.Leading) >= 2)
                .Select(line => new OcrRect(left, lines[line].Bounds.Y,
                    Math.Max(0, lines[line].Bounds.X - left), lines[line].Bounds.Height)).Concat(punctuation).ToList();
            foreach (var group in kept.GroupBy(item => item.Line))
            {
                OcrRect run = default;
                foreach (OcrRect componentBounds in group.Select(item => item.Bounds).OrderBy(rect => rect.X))
                {
                    if (OcrGeometry.IsValid(run) && componentBounds.X - run.Right > inkHeight * .6)
                    { preserved.Add(Inflate(run, 1, width, height)); run = default; }
                    run = OcrRect.Union(run, componentBounds);
                }
                if (OcrGeometry.IsValid(run)) preserved.Add(Inflate(run, 1, width, height));
            }
            // A Hangul syllable can consist of several separate short components. The whole
            // internal word gap provides a bounded footprint when those components together
            // have glyph-scale extent. This also keeps new letters out of their blank counters.
            foreach (OcrLine line in lines)
            {
                OcrRect[] words = line.Words.Select(word => word.Bounds).OrderBy(rect => rect.X).ToArray();
                for (int word = 1; word < words.Length; word++)
                {
                    int gapLeft = (int)Math.Ceiling(words[word - 1].Right), gapRight = (int)Math.Floor(words[word].X);
                    if (gapRight - gapLeft < line.Bounds.Height * .5) continue;
                    int minX = gapRight, maxX = gapLeft, minY = (int)Math.Ceiling(line.Bounds.Bottom), maxY = (int)line.Bounds.Y;
                    for (int yy = (int)line.Bounds.Y; yy < Math.Ceiling(line.Bounds.Bottom); yy++)
                        for (int xx = gapLeft; xx < gapRight; xx++)
                        {
                            if (Matches(yy * stride + xx * 4) || ColorDistance(Pixel(pixels, stride, xx, yy),
                                gradient ? GradientColor(xx, yy) : background) < 100) continue;
                            minX = Math.Min(minX, xx); maxX = Math.Max(maxX, xx);
                            minY = Math.Min(minY, yy); maxY = Math.Max(maxY, yy);
                        }
                    if (maxX - minX + 1 >= line.Bounds.Height * .4 && maxY - minY + 1 >= line.Bounds.Height * .45)
                        preserved.Add(new(gapLeft, line.Bounds.Y, gapRight - gapLeft, line.Bounds.Height));
                }
            }
            preservedLeading = preserved.ToArray();
            return found.ToArray();
        }

        private bool HasSeparateGlyphColumns(OcrRect area)
        {
            int maximum = 0;
            for (int y = (int)area.Y; y < area.Bottom; y++)
                for (int x = (int)area.X; x < area.Right; x++)
                    maximum = Math.Max(maximum, ColorDistance(Pixel(pixels, stride, x, y), gradient ? GradientColor(x, y) : background));
            int runs = 0;
            bool previous = false;
            for (int x = (int)area.X; x < area.Right; x++)
            {
                bool dark = false;
                for (int y = (int)area.Y; y < area.Bottom && !dark; y++)
                    dark = ColorDistance(Pixel(pixels, stride, x, y), gradient ? GradientColor(x, y) : background) >= maximum * .55;
                if (dark && !previous) runs++;
                previous = dark;
            }
            return runs >= 3;
        }

        internal bool CanOccupy(OcrRect candidate) => AreaMatches(candidate, occupiedLines);

        private bool AreaMatches(OcrRect candidate, IReadOnlyList<OcrRect> ownedAreas)
        {
            candidate = Clamp(candidate, width, height);
            int right = (int)Math.Ceiling(candidate.Right), bottom = (int)Math.Ceiling(candidate.Bottom);
            for (int y = (int)candidate.Y; y < bottom; y++)
            {
                int x = (int)candidate.X;
                foreach (OcrRect owned in ownedAreas)
                {
                    if (y < owned.Y || y >= owned.Bottom || owned.Right <= x || owned.X >= right) continue;
                    int end = Math.Min(right, (int)owned.X);
                    for (; x < end; x++)
                        if (!Matches(y * stride + x * 4)) return false;
                    x = Math.Max(x, Math.Min(right, (int)owned.Right));
                }
                for (; x < right; x++)
                    if (!Matches(y * stride + x * 4)) return false;
            }
            return true;
        }

        internal Geometry? DrawingClip(OcrRect candidate)
        {
            // The source's two-pixel erase margin is not source ink. Crop only its nonmatching
            // pixels instead of allowing a plate to overwrite an adjacent border or picture.
            if (AreaMatches(candidate, sourceInk) && !preservedInk.Any(rect => Intersects(rect, candidate))) return null;
            candidate = Clamp(candidate, width, height);
            int left = (int)candidate.X, top = (int)candidate.Y;
            int right = (int)Math.Ceiling(candidate.Right), bottom = (int)Math.Ceiling(candidate.Bottom);
            var clip = new StreamGeometry();
            using (StreamGeometryContext geometry = clip.Open())
            {
                for (int y = top; y < bottom; y++)
                {
                    int start = -1;
                    for (int x = left; x <= right; x++)
                    {
                        bool allowed = x < right && !Contains(preservedInk, x, y)
                            && (Contains(sourceInk, x, y)
                            || Matches(y * stride + x * 4));
                        if (allowed && start < 0) start = x;
                        if (allowed || start < 0) continue;
                        geometry.BeginFigure(new Point(start, y), true, true);
                        geometry.LineTo(new Point(x, y), true, false);
                        geometry.LineTo(new Point(x, y + 1), true, false);
                        geometry.LineTo(new Point(start, y + 1), true, false);
                        start = -1;
                    }
                }
            }
            clip.Freeze();
            return clip;
        }

        private bool RingMatchesGradient()
        {
            for (int x = (int)sampleLeft + 1; x <= sampleRight; x++)
                if (ColumnStep(x - 1, x) > 8) return false;
            for (int y = (int)sampleTop; y <= sampleBottom; y += 4)
                if (!MatchesGradient((int)sampleLeft, y, 8) || !MatchesGradient((int)sampleRight, y, 8)) return false;
            return true;
        }

        private int ColumnStep(int first, int second) => Math.Max(ColorDistance(upperSamples[first], upperSamples[second]),
            ColorDistance(lowerSamples[first], lowerSamples[second]));

        private int EstimateNoiseTolerance(out bool noisy, out bool textured)
        {
            // JPEG ringing has many low-amplitude levels around a flat surface. A solid
            // neighbouring panel or rule has a colour step, not that distribution. Learn a
            // bounded noise floor from the outer ring, excluding strong foreground samples.
            Span<int> histogram = stackalloc int[65];
            histogram.Clear();
            int count = 0, lowNoise = 0, total = 0, strong = 0;
            int left = Math.Max(0, (int)bounds.X - 7), right = Math.Min(width, (int)Math.Ceiling(bounds.Right) + 7);
            int top = Math.Max(0, (int)bounds.Y - 7), bottom = Math.Min(height, (int)Math.Ceiling(bounds.Bottom) + 7);
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                {
                    if (x >= bounds.X - 3 && x < bounds.Right + 3 && y >= bounds.Y - 3 && y < bounds.Bottom + 3) continue;
                    int delta = PixelDistance(x, y, background);
                    total++;
                    if (delta > 64) { strong++; continue; }
                    histogram[delta]++; count++;
                    if (delta is > 0 and <= 18) lowNoise++;
                }
            int levels = 0;
            for (int value = 1; value <= 18; value++) if (histogram[value] > 0) levels++;
            noisy = count >= 64 && levels >= 8 && lowNoise >= Math.Max(8, count * .015);
            textured = total > 0 && strong > total * .2;
            if (!noisy || lowNoise < count * .1) return Tolerance;
            int cumulative = 0;
            for (int value = 0; value < histogram.Length; value++)
            {
                cumulative += histogram[value];
                if (cumulative >= count * .995) return Math.Clamp(value + 4, Tolerance, 48);
            }
            return Tolerance;
        }

        private bool Matches(int index)
        {
            int x = index % stride / 4, y = index / stride;
            bool matches = gradient ? MatchesGradient(x, y, Tolerance)
                : Math.Abs(pixels[index] - background.B) <= backgroundTolerance
                    && Math.Abs(pixels[index + 1] - background.G) <= backgroundTolerance
                    && Math.Abs(pixels[index + 2] - background.R) <= backgroundTolerance;
            return matches || IsCompressionResidual(x, y, index) || IsSourceFringe(x, y, index);
        }

        private int PixelDistance(int x, int y, Color expected)
        {
            int index = y * stride + x * 4;
            return Math.Max(Math.Abs(pixels[index] - expected.B),
                Math.Max(Math.Abs(pixels[index + 1] - expected.G), Math.Abs(pixels[index + 2] - expected.R)));
        }

        private bool IsCompressionResidual(int x, int y, int index)
        {
            if (!noisySurface || gradient || Math.Min(background.R, Math.Min(background.G, background.B)) < 230) return false;
            int contrast = Math.Max(Math.Abs(pixels[index] - background.B),
                Math.Max(Math.Abs(pixels[index + 1] - background.G), Math.Abs(pixels[index + 2] - background.R)));
            if (contrast > 64) return false;
            foreach (OcrRect ink in sourceInk)
            {
                if (x >= ink.X - 8 && x < ink.Right + 8 && y >= ink.Y - 8 && y < ink.Bottom + 8
                    && !ContinuesAsRule(x, y, ink, background))
                {
                    if (contrast <= 48) return true;
                    // Stronger ringing needs a nearby, substantially darker recognized stroke.
                    // The noisy white surface and one JPEG block radius are both prerequisites.
                    for (int yy = Math.Max((int)ink.Y, y - 8); yy < Math.Min((int)Math.Ceiling(ink.Bottom), y + 9); yy++)
                        for (int xx = Math.Max((int)ink.X, x - 8); xx < Math.Min((int)Math.Ceiling(ink.Right), x + 9); xx++)
                            if (ColorDistance(Pixel(pixels, stride, xx, yy), background) >= contrast * 2) return true;
                }
            }
            return false;
        }

        private bool IsSourceFringe(int x, int y, int index)
        {
            if (texturedSurface) return false;
            // OCR can clip a strong edge, not just its antialiasing. A short connected path
            // back into a recognized word attributes that edge; proximity alone does not.
            Color expected = gradient ? GradientColor(x, y) : background;
            int outside = Math.Max(Math.Abs(pixels[index] - expected.B),
                Math.Max(Math.Abs(pixels[index + 1] - expected.G), Math.Abs(pixels[index + 2] - expected.R)));
            Span<int> queue = stackalloc int[49];
            foreach (OcrRect ink in sourceInk)
            {
                if (x < ink.X - 3 || x >= ink.Right + 3 || y < ink.Y - 3 || y >= ink.Bottom + 3) continue;
                if (x >= ink.X && x < ink.Right && y >= ink.Y && y < ink.Bottom) continue;
                if (ContinuesAsRule(x, y, ink, expected)) continue;
                int count = 1;
                queue[0] = 24;
                ulong visited = 1UL << 24;
                for (int cursor = 0; cursor < count; cursor++)
                {
                    int position = queue[cursor], currentX = x + position % 7 - 3, currentY = y + position / 7 - 3;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                    {
                        int cellX = position % 7 + dx, cellY = position / 7 + dy;
                        if (cellX < 0 || cellX >= 7 || cellY < 0 || cellY >= 7) continue;
                        int next = cellY * 7 + cellX;
                        if ((visited & (1UL << next)) != 0) continue;
                        visited |= 1UL << next;
                        int xx = currentX + dx, yy = currentY + dy;
                        if (xx < 0 || xx >= width || yy < 0 || yy >= height) continue;
                        int neighbor = yy * stride + xx * 4;
                        int inside = Math.Max(Math.Abs(pixels[neighbor] - expected.B),
                            Math.Max(Math.Abs(pixels[neighbor + 1] - expected.G), Math.Abs(pixels[neighbor + 2] - expected.R)));
                        if (inside <= Tolerance) continue;
                        if ((pixels[index] - expected.B) * (pixels[neighbor] - expected.B) < 0
                            || (pixels[index + 1] - expected.G) * (pixels[neighbor + 1] - expected.G) < 0
                            || (pixels[index + 2] - expected.R) * (pixels[neighbor + 2] - expected.R) < 0) continue;
                        if (xx >= ink.X && xx < ink.Right && yy >= ink.Y && yy < ink.Bottom && inside >= outside * .8) return true;
                        queue[count++] = next;
                    }
                }
            }
            return false;
        }

        private bool ContinuesAsRule(int x, int y, OcrRect ink, Color expected)
        {
            bool vertical = x < ink.X || x >= ink.Right;
            double size = vertical ? ink.Height : ink.Width;
            int start = (int)Math.Floor((vertical ? ink.Y : ink.X) - Math.Max(2, size * .25));
            int end = (int)Math.Ceiling((vertical ? ink.Bottom : ink.Right) + Math.Max(2, size * .25));
            if (start < 0 || end >= (vertical ? height : width)) return false;
            Color center = Pixel(pixels, stride, x, y);
            for (int offset = start; offset <= end; offset++)
            {
                Color color = Pixel(pixels, stride, vertical ? x : offset, vertical ? offset : y);
                if (ColorDistance(color, expected) <= Tolerance || ColorDistance(color, center) > 16) return false;
            }
            return true;
        }

        private bool MatchesGradient(int x, int y, int tolerance)
        {
            if (x < continuousLeft || x > continuousRight) return false;
            Color expected = GradientColor(x, y);
            int index = y * stride + x * 4;
            return Math.Abs(pixels[index] - expected.B) <= tolerance
                && Math.Abs(pixels[index + 1] - expected.G) <= tolerance
                && Math.Abs(pixels[index + 2] - expected.R) <= tolerance;
        }

        private Color GradientColor(double x, double y)
        {
            if (upperSamples.Length > 0)
            {
                int first = Math.Clamp((int)Math.Floor(x), 0, width - 1), second = Math.Min(width - 1, first + 1);
                double columnFraction = Math.Clamp(x - first, 0, 1), rowFraction = (y - sampleTop) / (sampleBottom - sampleTop);
                Color a = upperSamples[first], b = upperSamples[second], c = lowerSamples[first], d = lowerSamples[second];
                byte Sample(byte aa, byte bb, byte cc, byte dd) => (byte)Math.Round(Math.Clamp(
                    (aa + (bb - aa) * columnFraction) * (1 - rowFraction)
                        + (cc + (dd - cc) * columnFraction) * rowFraction, 0, 255));
                return Color.FromRgb(Sample(a.R, b.R, c.R, d.R), Sample(a.G, b.G, c.G, d.G), Sample(a.B, b.B, c.B, d.B));
            }
            double horizontal = (x - sampleLeft) / (sampleRight - sampleLeft);
            double vertical = (y - sampleTop) / (sampleBottom - sampleTop);
            byte Channel(byte a, byte b, byte c, byte d) => (byte)Math.Round(Math.Clamp(
                (a + (b - a) * horizontal) * (1 - vertical) + (c + (d - c) * horizontal) * vertical, 0, 255));
            return Color.FromRgb(Channel(topLeft.R, topRight.R, bottomLeft.R, bottomRight.R),
                Channel(topLeft.G, topRight.G, bottomLeft.G, bottomRight.G),
                Channel(topLeft.B, topRight.B, bottomLeft.B, bottomRight.B));
        }

        internal Brush Brush(OcrRect area, Color fallback, double opacity = 1)
        {
            if (!gradient) return new SolidColorBrush(fallback) { Opacity = opacity };
            // Native-pixel samples avoid the flat outer quarters introduced by stretching a
            // two-texel texture. Flat surfaces take the allocation-free solid-brush path above.
            int columns = Math.Max(1, (int)Math.Ceiling(area.Width)), rows = Math.Max(1, (int)Math.Ceiling(area.Height));
            byte[] colors = new byte[checked(columns * rows * 4)];
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < columns; x++)
                {
                    Color color = GradientColor(area.X + (x + .5) * area.Width / columns,
                        area.Y + (y + .5) * area.Height / rows);
                    int index = (y * columns + x) * 4;
                    colors[index] = color.B; colors[index + 1] = color.G;
                    colors[index + 2] = color.R; colors[index + 3] = 255;
                }
            BitmapSource image = BitmapSource.Create(columns, rows, 96, 96, PixelFormats.Bgra32, null, colors, columns * 4);
            image.Freeze();
            var brush = new ImageBrush(image) { Opacity = opacity, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            brush.Freeze();
            return brush;
        }

        private static int ColorDistance(Color a, Color b) =>
            Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
    }
}
