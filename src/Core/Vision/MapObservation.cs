using OpenCvSharp;

namespace GenshinMap.Core.Vision;

/// <summary>py: map_observation.raw_pair 결과. Points는 맞는 점(a, b 자리, float32)</summary>
public sealed class RawPair
{
    public bool Ok;
    public string Reason = "";
    public int? Good;
    public double? Scale, RotationDeg, MedianError;
    public int? Inliers;
    public float[]? SpanA, SpanB;
    public double[]? Shift;
    public (Point2f[] A, Point2f[] B)? Points;
}

/// <summary>py: map_observation.dense_motion·lk_motion 결과</summary>
public sealed class Motion
{
    public string Status = "";
    public double[]? Shift;
    public List<(double[] Shift, int Points, double P90Residual)> Clusters = new();
    public int? Points;
    public double? Share, MedianPx, P90;
}

/// <summary>py: map_observation.axis_slide 결과(d=null이면 못 찾음)</summary>
public readonly record struct SlideResult(double? D, double? Score, double? Second, double? AtZero);

/// <summary>py: map_observation.py — 자리 판단과 상관없는 지도 관측(보정 g, 밝기, 촘촘한 움직임, 끈 축 밀어 보기)</summary>
public static class MapObservation
{
    public const double D0 = 18.79;
    public const double ZOOM_DELTA = .00768950043235328;
    public const double LK_RADIUS_PX = .5;
    public const int LK_MIN_POINTS = 40;
    public const double LK_STILL_PX = .3;
    public const double SLIDE_SCALE = .25;
    public const int SLIDE_CROSS_PX = 8;
    public const double SLIDE_MIN_OVERLAP = .06;

    /// <summary>py: features(image) — 고정 금지 구역을 빼고 특징점 10000개</summary>
    public static ImageItem Features(Mat image)
    {
        int h = image.Rows, w = image.Cols;
        var exclude = new[] { (0, 0, (int)(w * GridParts.FIXED_UI.W), (int)(h * GridParts.FIXED_UI.H)) };
        return Engine.ExtractFeatures([("observation.png", image)], "genshin", exclude, 10000)[0];
    }

    static float Norm2F(float x, float y) => MathF.Sqrt(x * x + y * y);

    /// <summary>py: raw_pair(a, b) — 위치 엔진이 버릴 때도 닮음 정도를 그대로</summary>
    public static RawPair RawPairOf(ImageItem a0, ImageItem b0)
    {
        var (da, db) = GridParts.StillToDrop(a0, b0);
        ImageItem a = GridParts.WithoutPoints(a0, da), b = GridParts.WithoutPoints(b0, db);
        if (a.Descriptors is null || b.Descriptors is null) return new RawPair { Ok = false, Reason = "no_descriptors" };
        DMatch[][] knn;
        using (var bf = new BFMatcher(NormTypes.Hamming)) knn = bf.KnnMatch(a.Descriptors, b.Descriptors, 2);
        var good = knn.Where(p => p.Length == 2 && p[0].Distance < .75 * p[1].Distance).Select(p => p[0]).ToList();
        if (good.Count < 40) return new RawPair { Ok = false, Reason = "good<40", Good = good.Count };
        var pa = good.Select(m => a.KeyPoints[m.QueryIdx].Pt).ToArray();
        var pb = good.Select(m => b.KeyPoints[m.TrainIdx].Pt).ToArray();
        Cv2.SetTheRNG(1107);
        using var inl = new Mat();
        using Mat matrix = Cv2.EstimateAffinePartial2D(InputArray.Create(pa), InputArray.Create(pb), inl,
            RobustEstimationAlgorithms.RANSAC, 3, 5000, .999, 10);
        if (matrix is null || matrix.Empty() || inl.Empty()) return new RawPair { Ok = false, Reason = "no_affine", Good = good.Count };
        inl.GetArray(out byte[] inside);
        int n = inside.Count(v => v != 0);
        var aa = pa.Where((_, k) => inside[k] != 0).ToArray();
        var bb = pb.Where((_, k) => inside[k] != 0).ToArray();
        int h = a.Shape.H, w = a.Shape.W;
        float[] sa = n > 0 ? [aa.Max(p => p.X) - aa.Min(p => p.X), aa.Max(p => p.Y) - aa.Min(p => p.Y)] : [0f, 0f];
        float[] sb = n > 0 ? [bb.Max(p => p.X) - bb.Min(p => p.X), bb.Max(p => p.Y) - bb.Min(p => p.Y)] : [0f, 0f];
        double m00 = matrix.At<double>(0, 0), m10 = matrix.At<double>(1, 0);
        double scale = Py.Hypot(m00, m10);
        double rotation = Math.Atan2(m10, m00) * (180.0 / Math.PI);
        // py: np.all(sa >= np.array([w, h]) * .2) — float32와 float64 배열 비교는 float64로
        bool valid = n >= 40 && sa[0] >= w * .2 && sa[1] >= h * .2 && sb[0] >= w * .2 && sb[1] >= h * .2
                     && Math.Abs(rotation) <= .5 && double.IsFinite(scale) && scale > 0;
        // py: np.linalg.norm(cv2.transform(pa[None], matrix)[0] - pb, axis=1) — float32
        using var src = Mat.FromPixelData(pa.Length, 1, MatType.CV_32FC2, pa);
        using var dst = new Mat();
        Cv2.Transform(src, dst, matrix);
        dst.GetArray(out Point2f[] tr);
        var errIn = new List<float>();
        for (int k = 0; k < pa.Length; k++) if (inside[k] != 0) errIn.Add(Norm2F(tr[k].X - pb[k].X, tr[k].Y - pb[k].Y));
        double[] shift = n > 0
            ? [Py.Median(aa.Select((p, k) => bb[k].X - p.X)), Py.Median(aa.Select((p, k) => bb[k].Y - p.Y))]
            : [0.0, 0.0];
        return new RawPair
        {
            Ok = valid, Reason = valid ? "" : "inliers/span/rotation", Scale = scale, RotationDeg = rotation, Inliers = n, Good = good.Count,
            SpanA = sa, SpanB = sb, MedianError = n > 0 ? Py.Median(errIn) : null, Shift = shift, Points = (aa, bb),
        };
    }

    /// <summary>py: gain_sample 결과</summary>
    public sealed class GainSampleResult
    {
        public bool Accepted;
        public string Reason = "";
        public double MousePx;
        public double? G, AlongPx, CrossPx, ReleaseDriftPx;
    }

    /// <summary>py: gain_sample(fit, mouse_start, mouse_end, release_fit) — 끌기 한 번의 보정 g 표본</summary>
    public static GainSampleResult GainSample(RawPair fit, (double X, double Y) mouseStart, (double X, double Y) mouseEnd, RawPair releaseFit)
    {
        double mx = mouseEnd.X - mouseStart.X, my = mouseEnd.Y - mouseStart.Y;
        double length = Math.Sqrt(mx * mx + my * my); // py: np.linalg.norm(2-벡터) = sqrt(dot), 11/15 확인
        var r = new GainSampleResult { MousePx = length };
        if (!fit.Ok || !releaseFit.Ok || !double.IsFinite(length) || length <= D0) { r.Reason = "unmeasurable/short"; return r; }
        double sx = fit.Shift![0], sy = fit.Shift[1];
        double along = (sx * mx + sy * my) / length;
        double cross = Math.Abs(sx * my - sy * mx) / length;
        double gain = along / (length - D0);
        double rx = releaseFit.Shift![0], ry = releaseFit.Shift[1];
        double drift = Math.Sqrt(rx * rx + ry * ry);
        r.G = gain; r.AlongPx = along; r.CrossPx = cross; r.ReleaseDriftPx = drift;
        if (!double.IsFinite(gain) || gain <= 0 || cross > Py.Max(3, .01 * Math.Abs(along))) r.Reason = "direction/model";
        else if (Math.Abs(fit.Scale!.Value - 1) > ZOOM_DELTA || Math.Abs(releaseFit.Scale!.Value - 1) > ZOOM_DELTA) r.Reason = "zoom";
        else if (drift > 10) r.Reason = "release_return";
        else { r.Accepted = true; r.Reason = ""; }
        return r;
    }

    /// <summary>py: calibration_gain(samples) — 표본 4개 이상이 다 받아들여지고 서로 3% 안이면 가운데 값</summary>
    public static double? CalibrationGain(IReadOnlyList<GainSampleResult> samples)
    {
        if (samples.Count < 4 || !samples.All(s => s.Accepted)) return null;
        var lengths = samples.Select(s => s.MousePx).ToList();
        if (lengths.Max() - lengths.Min() < 32) return null;
        double[] gains = samples.Select(s => s.G!.Value).ToArray();
        double median = Py.Median(gains);
        if (!double.IsFinite(median) || median <= 0) return null;
        double mx = 0; bool nan = false;
        foreach (double g in gains) { double v = Math.Abs(g / median - 1); if (double.IsNaN(v)) nan = true; else if (v > mx) mx = v; }
        if (!nan && mx > .03) return null; // py: np.max는 NaN이 있으면 NaN이고 NaN > .03은 거짓이라 통과
        return median;
    }

    /// <summary>py: moving_mask(fit, shape) — 움직인 맞는 점 둘레(0.25배)만 화면 요소가 아니라는 근거. 255=근거</summary>
    public static Mat MovingMask(RawPair fit, int h, int w)
    {
        var mask = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        if (fit.Ok && fit.Points is var (a, b))
        {
            for (int k = 0; k < a.Length; k++)
            {
                if (Norm2F(b[k].X - a[k].X, b[k].Y - a[k].Y) > 8)
                {
                    foreach (var p in new[] { a[k], b[k] })
                    {
                        float x = p.X * (float)GridParts.UI_SCALE, y = p.Y * (float)GridParts.UI_SCALE;
                        Cv2.Circle(mask, new Point((int)MathF.Round(x, MidpointRounding.ToEven), (int)MathF.Round(y, MidpointRounding.ToEven)), 2, Scalar.All(1), -1);
                    }
                }
            }
        }
        var o = new Mat();
        Cv2.Compare(mask, 0, o, CmpTypes.GT);
        mask.Dispose();
        return o;
    }

    /// <summary>py: brightness_samples(a, b, fit) — (a 반지름², b 반지름², log 밝기비) float32 또는 null</summary>
    public static (float[] Ra, float[] Rb, float[] Z)? BrightnessSamples(Mat a, Mat b, RawPair fit)
    {
        if (!fit.Ok || fit.Points is null) return null;
        var (pa, pb) = fit.Points.Value;
        int h = a.Rows, w = a.Cols;
        float[] Blur(Mat img)
        {
            using Mat g = new Mat(), f = new Mat(), bl = new Mat();
            Cv2.CvtColor(img, g, ColorConversionCodes.BGR2GRAY);
            g.ConvertTo(f, MatType.CV_32F);
            Cv2.GaussianBlur(f, bl, new Size(0, 0), 3);
            bl.GetArray(out float[] v);
            return v;
        }
        float[] ga = Blur(a), gb = Blur(b);
        var ra = new List<float>(); var rb = new List<float>(); var z = new List<float>();
        float Rad(Point2f p)
        {
            // py: (w-1)/2는 파이썬 실수지만 float32 배열과 계산하면 float32(NumPy 2, NEP 50)
            float u = (p.X - (float)((w - 1) / 2.0)) / (float)((w - 1) / 2.0), v = (p.Y - (float)((h - 1) / 2.0)) / (float)((h - 1) / 2.0);
            float s = u * u + v * v;
            return s < 1f ? s : 1f; // py: np.minimum(1, s)
        }
        var use = new bool[pa.Length];
        int cnt = 0;
        var vaL = new float[pa.Length]; var vbL = new float[pa.Length];
        for (int k = 0; k < pa.Length; k++)
        {
            int ax = (int)MathF.Round(pa[k].X, MidpointRounding.ToEven), ay = (int)MathF.Round(pa[k].Y, MidpointRounding.ToEven);
            int bx = (int)MathF.Round(pb[k].X, MidpointRounding.ToEven), by = (int)MathF.Round(pb[k].Y, MidpointRounding.ToEven);
            float va = ga[ay * w + ax], vb = gb[by * w + bx];
            vaL[k] = va; vbL[k] = vb;
            use[k] = va > 20 && vb > 20 && va < 235 && vb < 235 && Norm2F(pb[k].X - pa[k].X, pb[k].Y - pa[k].Y) > 8;
            if (use[k]) cnt++;
        }
        if (cnt < 40) return null;
        for (int k = 0; k < pa.Length; k++)
        {
            if (!use[k]) continue;
            ra.Add(Rad(pa[k])); rb.Add(Rad(pb[k]));
            z.Add(MathF.Log(vbL[k] / vaL[k]));
        }
        return (ra.ToArray(), rb.ToArray(), z.ToArray());
    }

    /// <summary>py: brightness_strength 결과</summary>
    public readonly record struct StrengthResult(double Strength, string Reason, int? Samples, double? RawLogError, double? CorrectedLogError);

    /// <summary>py: brightness_strength(samples) — 화면 가장자리 어두워짐 세기(0~0.6, 0.02 간격)</summary>
    public static StrengthResult BrightnessStrength(IEnumerable<(float[] Ra, float[] Rb, float[] Z)?> samples0)
    {
        var samples = samples0.Where(s => s is not null).Select(s => s!.Value).ToList();
        if (samples.Count == 0) return new(0.0, "no_photometric_samples", null, null, null);
        float[] a = samples.SelectMany(s => s.Ra).ToArray(), b = samples.SelectMany(s => s.Rb).ToArray(), z = samples.SelectMany(s => s.Z).ToArray();
        if (z.Length < 100 || Py.PercentileF32(a.Select((v, k) => MathF.Abs(v - b[k])), 75) < (float).05) // float32와 파이썬 실수 비교는 float32로
            return new(0.0, "insufficient_screen_displacement", null, null, null);
        (double Score, double Strength)? best = null;
        var res = new double[z.Length];
        for (int i = 0; i < 31; i++)
        {
            double strength = i * 0.02; // py: np.arange(0, .601, .02)
            for (int k = 0; k < z.Length; k++)
            {
                double num = Py.Max(.35, 1 - strength * b[k]), den = Py.Max(.35, 1 - strength * a[k]);
                res[k] = Math.Abs(z[k] - Math.Log(num / den));
            }
            var cand = (Py.Median(res), strength);
            if (best is null || cand.Item1 < best.Value.Score || (cand.Item1 == best.Value.Score && cand.strength < best.Value.Strength)) best = cand;
        }
        double raw = Py.Median(z.Select(MathF.Abs));
        return new(best!.Value.Score < raw * .9 ? best.Value.Strength : 0.0, "", z.Length, raw, best.Value.Score);
    }

    /// <summary>py: dense_motion(delta) — 움직임 무리 둘이면 모호</summary>
    public static Motion DenseMotion(IReadOnlyList<(float X, float Y)> delta)
    {
        int n = delta.Count;
        var remaining = Enumerable.Repeat(true, n).ToArray();
        var clusters = new List<(double[] Shift, int Points, double P90Residual)>();
        while (remaining.Count(r => r) >= LK_MIN_POINTS && clusters.Count < 2)
        {
            var idx = Enumerable.Range(0, n).Where(k => remaining[k]).ToArray();
            int bestK = -1, bestC = -1;
            foreach (int i in idx)
            {
                int c = 0;
                foreach (int j in idx) if (Norm2F(delta[i].X - delta[j].X, delta[i].Y - delta[j].Y) < LK_RADIUS_PX) c++;
                if (c > bestC) { bestC = c; bestK = i; }
            }
            (float X, float Y) center = delta[bestK];
            bool[] Inside((float X, float Y) c0) => Enumerable.Range(0, n).Select(k => remaining[k] && Norm2F(delta[k].X - c0.X, delta[k].Y - c0.Y) < LK_RADIUS_PX).ToArray();
            var inside = Inside(center);
            var sel = Enumerable.Range(0, n).Where(k => inside[k]).Select(k => delta[k]).ToList();
            center = (Py.Median(sel.Select(p => p.X)), Py.Median(sel.Select(p => p.Y)));
            inside = Inside(center);
            int cnt = inside.Count(v => v);
            if (cnt < LK_MIN_POINTS) break;
            var shifts = Enumerable.Range(0, n).Where(k => inside[k]).Select(k => delta[k]).ToList();
            (float X, float Y) cen = (Py.Median(shifts.Select(p => p.X)), Py.Median(shifts.Select(p => p.Y)));
            double p90 = Py.PercentileF32(shifts.Select(p => Norm2F(p.X - cen.X, p.Y - cen.Y)), 90);
            clusters.Add(([cen.X, cen.Y], cnt, p90));
            for (int k = 0; k < n; k++) if (inside[k]) remaining[k] = false;
        }
        return new Motion
        {
            Status = clusters.Count > 1 ? "ambiguous" : clusters.Count == 1 ? "valid" : "few_points",
            Shift = clusters.Count == 1 ? clusters[0].Shift : null, Clusters = clusters,
        };
    }

    /// <summary>py: lk_motion(a, b, ui, center) — 화면 요소 밖 원래 크기 촘촘한 평행 이동. ui는 0.25배 화면 요소(0이 아니면 요소)</summary>
    public static Motion? LkMotion(Mat a, Mat b, Mat? ui = null, (double Top, double Bottom, double Side)? center = null)
    {
        int h = a.Rows, w = a.Cols;
        using Mat mask = Engine.MakeMask(a, "genshin", [], darkFilter: false);
        if (Engine.TrialNoticePresent(a) || Engine.TrialNoticePresent(b)) Engine.MaskTrialNotice(mask);
        void Zero(int r0, int r1, int c0, int c1)
        {
            var (y0, y1) = Py.Slice(r0, r1, h);
            var (x0, x1) = Py.Slice(c0, c1, w);
            if (y1 > y0 && x1 > x0) mask[new Rect(x0, y0, x1 - x0, y1 - y0)].SetTo(0);
        }
        Zero(0, Py.RoundEvenInt(h * GridParts.FIXED_UI.H), 0, Py.RoundEvenInt(w * GridParts.FIXED_UI.W));
        if (ui is not null) using (Mat full = GridParts.UiFull(ui, w, h)) mask.SetTo(0, full);
        if (center is var (top, bottom, side))
        {
            Zero(0, Py.RoundEvenInt(h * top), 0, w);
            Zero(h - Py.RoundEvenInt(h * bottom), h, 0, w);
            Zero(0, h, 0, Py.RoundEvenInt(w * side));
            Zero(0, h, w - Py.RoundEvenInt(w * side), w);
        }
        using Mat g1 = new Mat(), g2 = new Mat(), t = new Mat(), m = new Mat();
        Cv2.CvtColor(a, t, ColorConversionCodes.BGR2GRAY); Cv2.Resize(t, g1, new Size(0, 0), .5, .5);
        Cv2.CvtColor(b, t, ColorConversionCodes.BGR2GRAY); Cv2.Resize(t, g2, new Size(0, 0), .5, .5);
        Cv2.Resize(mask, m, new Size(g1.Cols, g1.Rows), 0, 0, InterpolationFlags.Nearest);
        Point2f[] pts = Cv2.GoodFeaturesToTrack(g1, 600, .01, 6, m, 3, false, .04);
        if (pts is null || pts.Length < LK_MIN_POINTS) return null;
        Point2f[] nxt = [];
        Cv2.CalcOpticalFlowPyrLK(g1, g2, pts, ref nxt, out byte[] status, out float[] error, new Size(21, 21), 3);
        if (nxt is null || status is null || error is null) return null;
        var delta = new List<(float X, float Y)>();
        for (int k = 0; k < pts.Length; k++)
        {
            if (status[k] == 0 || !float.IsFinite(nxt[k].X) || !float.IsFinite(nxt[k].Y) || !(error[k] < 20)) continue;
            delta.Add(((nxt[k].X - pts[k].X) * 2, (nxt[k].Y - pts[k].Y) * 2));
        }
        if (delta.Count < LK_MIN_POINTS) return null;
        var result = DenseMotion(delta);
        if (result.Status == "few_points") return null;
        if (result.Status == "valid")
        {
            var c = result.Clusters[0];
            double sx = result.Shift![0], sy = result.Shift[1];
            result.Points = c.Points;
            result.Share = (double)c.Points / delta.Count;
            result.MedianPx = Math.Sqrt(sx * sx + sy * sy);
            // py: delta - result["shift"](파이썬 실수 목록 → float64 배열)이라 이 거리만 float64로 잰다
            var near = delta.Where(d => { double ex = d.X - sx, ey = d.Y - sy; return Math.Sqrt(ex * ex + ey * ey) < LK_RADIUS_PX; });
            result.P90 = Py.PercentileF32(near.Select(d => Norm2F(d.X, d.Y)), 90);
        }
        return result;
    }

    /// <summary>py: _slide_prep(image) — 0.25배 회색 float32에서 흐린 것을 뺀 무늬</summary>
    public static float[] SlidePrep(Mat image, out int hh, out int ww)
    {
        int h = image.Rows, w = image.Cols;
        using Mat r = new Mat(), g = new Mat(), f = new Mat(), bl = new Mat(), o = new Mat();
        Cv2.Resize(image, r, new Size(Py.RoundEvenInt(w * SLIDE_SCALE), Py.RoundEvenInt(h * SLIDE_SCALE)), 0, 0, InterpolationFlags.Area);
        Cv2.CvtColor(r, g, ColorConversionCodes.BGR2GRAY);
        g.ConvertTo(f, MatType.CV_32F);
        Cv2.GaussianBlur(f, bl, new Size(0, 0), 2);
        Cv2.Subtract(f, bl, o);
        hh = o.Rows; ww = o.Cols;
        o.GetArray(out float[] v);
        return v;
    }

    /// <summary>순환 상호상관 c[k] = Σ f[x+k]·g[x] (py: irfft2(rfft2(f, shape) * conj(rfft2(g, shape)), shape)). shape = (2hh, 2ww), f·g는 왼쪽 위에 놓고 0으로 채움</summary>
    static double[] CrossCorr(float[] f, float[] g, int hh, int ww)
    {
        int H = 2 * hh, W = 2 * ww;
        using Mat F = Pad(f), G = Pad(g), Ff = new Mat(), Gf = new Mat(), P = new Mat(), R = new Mat();
        Cv2.Dft(F, Ff, DftFlags.ComplexOutput);
        Cv2.Dft(G, Gf, DftFlags.ComplexOutput);
        Cv2.MulSpectrums(Ff, Gf, P, DftFlags.None, conjB: true);
        Cv2.Dft(P, R, DftFlags.Inverse | DftFlags.Scale | DftFlags.RealOutput);
        R.GetArray(out double[] v);
        return v;

        Mat Pad(float[] src)
        {
            var m = new Mat(H, W, MatType.CV_64FC1, Scalar.All(0));
            var buf = new double[H * W];
            for (int y = 0; y < hh; y++) for (int x = 0; x < ww; x++) buf[y * W + x] = src[y * ww + x];
            m.SetArray(buf);
            return m;
        }
    }

    /// <summary>py: axis_slide(a, b, axis, lo, hi, exclude) — b(x) ≈ a(x+s)인 이동 s를 끈 축으로만 lo~hi(원본 px)에서 찾는다.
    /// NumPy 2는 float32 FFT, C#은 double FFT라 점수 끝자리가 다르다(봉우리 자리 같음, 점수 1e-4 안).</summary>
    public static SlideResult AxisSlide(Mat a, Mat b, int axis, double lo, double hi, Mat? exclude = null)
    {
        float[] A = SlidePrep(a, out int hh, out int ww), B = SlidePrep(b, out _, out _);
        var keep = Enumerable.Repeat(1f, hh * ww).ToArray();
        if (exclude is not null)
        {
            using Mat r = new Mat();
            Cv2.Resize(exclude, r, new Size(ww, hh), 0, 0, InterpolationFlags.Nearest);
            r.GetArray(out byte[] ev);
            for (int p = 0; p < keep.Length; p++) keep[p] = ev[p] > 0 ? 0f : 1f;
        }
        int H = 2 * hh, W = 2 * ww;
        var Am = new float[hh * ww]; var Bm = new float[hh * ww]; var Am2 = new float[hh * ww]; var Bm2 = new float[hh * ww];
        for (int p = 0; p < Am.Length; p++) { Am[p] = A[p] * keep[p]; Bm[p] = B[p] * keep[p]; Am2[p] = Am[p] * Am[p]; Bm2[p] = Bm[p] * Bm[p]; }
        double[] num = CrossCorr(Am, Bm, hh, ww), d1 = CrossCorr(Am2, keep, hh, ww), d2 = CrossCorr(keep, Bm2, hh, ww), kk = CrossCorr(keep, keep, hh, ww);
        var score = new double[H * W];
        double minOverlap = SLIDE_MIN_OVERLAP * hh * ww;
        for (int p = 0; p < score.Length; p++)
        {
            double den = Math.Sqrt(Py.Max(d1[p] * d2[p], 1e-6));
            score[p] = kk[p] < minOverlap ? -1 : num[p] / den;
        }
        int c = Math.Max(0, Py.RoundEvenInt(SLIDE_CROSS_PX * SLIDE_SCALE));
        int n = axis == 0 ? W : H;
        var line = new double[n];
        for (int k = -c; k <= c; k++)
            for (int i = 0; i < n; i++)
            {
                double v = axis == 0 ? score[Py.Mod(k, H) * W + i] : score[i * W + Py.Mod(k, W)];
                line[i] = k == -c ? v : Math.Max(line[i], v); // py: np.max(np.stack(rows), axis=0)
            }
        double S(int i) { int q = i < n / 2 ? i : i - n; return q / SLIDE_SCALE; }
        var idx = Enumerable.Range(0, n).Where(i => S(i) >= lo && S(i) <= hi && line[i] > -1).ToArray();
        if (idx.Length == 0) return new SlideResult(null, null, null, null);
        int best = idx[0];
        foreach (int i in idx) if (line[i] > line[best]) best = i;
        var far = idx.Where(i => Math.Abs(S(i) - S(best)) > 40).ToArray();
        double second = far.Length > 0 ? far.Max(i => line[i]) : 0.0;
        double? atZero = lo <= 0 && 0 <= hi ? line[0] : null;
        return new SlideResult(S(best), line[best], second, atZero);
    }
}
