using System;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace GI_Subtitles.Services.Assist
{
    /// <summary>
    /// A small picture that is on one screen and nowhere else, ready to be compared: three colour planes, each with
    /// its mean taken off, and their length. At most <see cref="MaxSide"/> pixels a side, drawn as the canvas shows it.
    /// </summary>
    public sealed class AssistTemplate
    {
        public const int MaxSide = 64;

        /// <summary>LogicalName prefix of the embedded templates: Kaption.Assist.&lt;game&gt;.&lt;template&gt;.png.</summary>
        public const string ResourcePrefix = "Kaption.Assist.";

        /// <summary>A template flatter than this (0-255, standard deviation) matches everything and nothing.</summary>
        private const double MinDeviation = 4;

        public string Name { get; }
        public int Width { get; }
        public int Height { get; }
        internal float[] Planes { get; }
        internal double Norm { get; }

        private AssistTemplate(string name, int width, int height, float[] planes, double norm)
        {
            Name = name;
            Width = width;
            Height = height;
            Planes = planes;
            Norm = norm;
        }

        /// <summary>From tight BGR rows; null when the picture is too large, empty or flat.</summary>
        public static AssistTemplate FromBgr(string name, ReadOnlySpan<byte> bgr, int width, int height)
        {
            if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || bgr.Length < width * height * 3) return null;
            int n = width * height;
            var planes = new float[n * 3];
            double squares = 0;
            for (int c = 0; c < 3; c++)
            {
                double sum = 0;
                for (int i = 0; i < n; i++) sum += bgr[i * 3 + c];
                double mean = sum / n;
                for (int i = 0; i < n; i++)
                {
                    double v = bgr[i * 3 + c] - mean;
                    planes[c * n + i] = (float)v;
                    squares += v * v;
                }
            }
            if (squares < MinDeviation * MinDeviation * n * 3) return null;
            return new AssistTemplate(name, width, height, planes, Math.Sqrt(squares));
        }

        /// <summary>The embedded template of a game (wire id), or null when the build carries none by that name.</summary>
        public static AssistTemplate Load(string gameId, string name)
        {
            if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(name)) return null;
            using Stream stream = typeof(AssistTemplate).Assembly.GetManifestResourceStream(ResourcePrefix + gameId + "." + name + ".png");
            if (stream == null) return null;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            using Mat picture = Cv2.ImDecode(bytes.ToArray(), ImreadModes.Color);
            if (picture.Empty() || !picture.IsContinuous()) return null;
            var bgr = new byte[picture.Width * picture.Height * 3];
            System.Runtime.InteropServices.Marshal.Copy(picture.Data, bgr, 0, bgr.Length);
            return FromBgr(name, bgr, picture.Width, picture.Height);
        }
    }

    /// <summary>
    /// "Is this picture at its declared place?" The caller copies the place plus <see cref="Slack"/> canvas pixels
    /// on every side; the probe brings that patch to canvas size and takes the best normalised correlation over the
    /// few shifts the slack allows. It never searches the frame: a place is declared, or there is no answer. No
    /// allocation after the first call of a size; not thread-safe (one look at a time uses it).
    /// </summary>
    public sealed class ScreenProbe
    {
        /// <summary>Canvas pixels around the declared place: rounding of the scale, a HUD that sits a pixel off.</summary>
        public const int Slack = 2;

        /// <summary>A patch flatter than this (0-255, standard deviation) is a blank screen, not a weak match.</summary>
        private const double MinDeviation = 2;

        private float[] _planes = Array.Empty<float>();
        // Per plane, the running sums of the patch and of its squares, so a window's sum is four look-ups.
        private double[] _sums = Array.Empty<double>(), _squares = Array.Empty<double>();

        /// <summary>
        /// The best match of <paramref name="template"/> in a patch of tight BGRA rows, 0 to 1 (0 for a flat or
        /// too-small patch).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public unsafe double Match(AssistTemplate template, ReadOnlySpan<byte> bgra, int width, int height)
        {
            if (template == null || width <= 0 || height <= 0 || bgra.Length < width * height * 4) return 0;
            int gw = template.Width + Slack * 2, gh = template.Height + Slack * 2;
            // A patch much smaller than the picture it should hold cannot show it.
            if (width * 2 < gw || height * 2 < gh) return 0;
            int cells = gw * gh, iw = gw + 1, table = iw * (gh + 1);
            if (_planes.Length < cells * 3) _planes = new float[cells * 3];
            if (_sums.Length < table * 3)
            {
                _sums = new double[table * 3];
                _squares = new double[table * 3];
            }
            Resample(bgra, width, height, _planes, gw, gh);

            int tw = template.Width, th = template.Height, n = tw * th;
            double best = 0;
            fixed (float* p = _planes, t = template.Planes)
            fixed (double* sums = _sums, squares = _squares)
            {
                for (int c = 0; c < 3; c++)
                {
                    float* plane = p + c * cells;
                    double* s = sums + c * table, q = squares + c * table;
                    for (int x = 0; x < iw; x++) s[x] = q[x] = 0;
                    for (int y = 0; y < gh; y++)
                    {
                        double rowSum = 0, rowSquares = 0;
                        double* above = s + y * iw, here = above + iw, aboveQ = q + y * iw, hereQ = aboveQ + iw;
                        here[0] = hereQ[0] = 0;
                        for (int x = 0; x < gw; x++)
                        {
                            double v = plane[y * gw + x];
                            rowSum += v;
                            rowSquares += v * v;
                            here[x + 1] = above[x + 1] + rowSum;
                            hereQ[x + 1] = aboveQ[x + 1] + rowSquares;
                        }
                    }
                }
                for (int dy = 0; dy <= Slack * 2; dy++)
                {
                    for (int dx = 0; dx <= Slack * 2; dx++)
                    {
                        // The patch's own spread first: a flat window (a fade, a loading screen) is no match at all.
                        double spread = 0;
                        for (int c = 0; c < 3; c++)
                        {
                            double sum = Window(sums + c * table, iw, dx, dy, tw, th);
                            spread += Window(squares + c * table, iw, dx, dy, tw, th) - sum * sum / n;
                        }
                        if (spread < MinDeviation * MinDeviation * n * 3) continue;
                        double dot = 0;
                        for (int c = 0; c < 3; c++)
                        {
                            float* row = p + c * cells + dy * gw + dx, other = t + c * n;
                            for (int y = 0; y < th; y++, row += gw, other += tw) dot += Dot(row, other, tw);
                        }
                        double score = dot / (Math.Sqrt(spread) * template.Norm);
                        if (score > best) best = score;
                    }
                }
            }
            return Math.Min(1, best);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe double Window(double* table, int stride, int x, int y, int w, int h) =>
            table[(y + h) * stride + x + w] - table[y * stride + x + w] - table[(y + h) * stride + x] + table[y * stride + x];

        /// <summary>One row of the patch times one row of the template.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
        private static unsafe float Dot(float* row, float* other, int count)
        {
            int i = 0;
            float dot = 0;
            if (Vector.IsHardwareAccelerated && count >= Vector<float>.Count)
            {
                Vector<float> sum = Vector<float>.Zero;
                int step = Vector<float>.Count;
                for (; i <= count - step; i += step)
                    sum += Unsafe.ReadUnaligned<Vector<float>>(row + i) * Unsafe.ReadUnaligned<Vector<float>>(other + i);
                dot = Vector.Sum(sum);
            }
            for (; i < count; i++) dot += row[i] * other[i];
            return dot;
        }

        /// <summary>
        /// BGRA rows to three planes of <paramref name="gw"/> x <paramref name="gh"/>: each cell is the average of
        /// the source pixels it covers (weighted at the edges), which is also right when nothing is scaled.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Resample(ReadOnlySpan<byte> bgra, int width, int height, float[] planes, int gw, int gh)
        {
            int cells = gw * gh;
            if (width == gw && height == gh)
            {
                for (int i = 0; i < cells; i++)
                {
                    planes[i] = bgra[i * 4];
                    planes[cells + i] = bgra[i * 4 + 1];
                    planes[cells * 2 + i] = bgra[i * 4 + 2];
                }
                return;
            }
            double sx = width / (double)gw, sy = height / (double)gh;
            for (int y = 0; y < gh; y++)
            {
                double top = y * sy, bottom = top + sy;
                int y0 = (int)top, y1 = Math.Min(height - 1, (int)Math.Ceiling(bottom) - 1);
                for (int x = 0; x < gw; x++)
                {
                    double left = x * sx, right = left + sx;
                    int x0 = (int)left, x1 = Math.Min(width - 1, (int)Math.Ceiling(right) - 1);
                    double b = 0, g = 0, r = 0, weight = 0;
                    for (int v = y0; v <= y1; v++)
                    {
                        double wy = Math.Min(bottom, v + 1) - Math.Max(top, v);
                        int at = (v * width + x0) * 4;
                        for (int u = x0; u <= x1; u++, at += 4)
                        {
                            double w = wy * (Math.Min(right, u + 1) - Math.Max(left, u));
                            b += bgra[at] * w;
                            g += bgra[at + 1] * w;
                            r += bgra[at + 2] * w;
                            weight += w;
                        }
                    }
                    int cell = y * gw + x;
                    if (weight <= 0) weight = 1;
                    planes[cell] = (float)(b / weight);
                    planes[cells + cell] = (float)(g / weight);
                    planes[cells * 2 + cell] = (float)(r / weight);
                }
            }
        }
    }

    /// <summary>
    /// Which screens are showing. One probe is a glimpse; a screen counts, or stops counting, after
    /// <see cref="ProbesToAgree"/> probes in a row say the same (a fade, a frame between two menus).
    /// </summary>
    public sealed class ScreenPresence
    {
        public const int ProbesToAgree = 2;

        private readonly bool[] _present, _last;
        private readonly int[] _run;

        public ScreenPresence(int screens)
        {
            _present = new bool[screens];
            _last = new bool[screens];
            _run = new int[screens];
        }

        public int Count => _present.Length;

        /// <summary>Whether any probe was counted yet: without one there is nothing to repeat.</summary>
        public bool HasObservation { get; private set; }

        public bool IsPresent(int screen) => (uint)screen < (uint)_present.Length && Volatile(screen);

        private bool Volatile(int screen) => System.Threading.Volatile.Read(ref _present[screen]);

        /// <summary>Counts one probe of one screen; returns whether the screen counts as showing after it.</summary>
        public bool Observe(int screen, bool seen)
        {
            HasObservation = true;
            _run[screen] = seen == _last[screen] ? Math.Min(ProbesToAgree, _run[screen] + 1) : 1;
            _last[screen] = seen;
            if (_run[screen] >= ProbesToAgree) System.Threading.Volatile.Write(ref _present[screen], seen);
            return _present[screen];
        }

        /// <summary>A frame that did not change shows what the last probe of this screen saw: counts that probe again, without pixels.</summary>
        public void Repeat(int screen)
        {
            if ((uint)screen < (uint)_present.Length && _run[screen] > 0) Observe(screen, _last[screen]);
        }
    }
}
