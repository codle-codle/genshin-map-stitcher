using OpenCvSharp;

namespace GenshinMap.Core.Vision;

/// <summary>py: fast_compare.compare 결과. Answer: "맞음"·"다름"·"모름"</summary>
public sealed record CompareResult(string Answer, double?[] Scores, int[]? Pixels, int? Best, double? Evidence, bool? Tied);

/// <summary>py: fast_compare.py — 끈 축 8가설의 고주파 영평균 정규화 상관. 학습 문턱은 MARGIN 하나</summary>
public static class FastCompare
{
    public static readonly int[] OFFSETS = [0, -60, -120, -180, -240, -300, -360, 60];
    public const double MARGIN = 0.011858;

    /// <summary>py: compare(before, after, predicted, ui_mask). uiMask: 원래 크기, 0이 아니면 화면 요소</summary>
    public static CompareResult Compare(Mat before, Mat after, (double X, double Y) predicted, Mat uiMask)
    {
        if (before.Size() != after.Size() || before.Channels() != 3 || after.Channels() != 3 || !double.IsFinite(predicted.X) || !double.IsFinite(predicted.Y))
            throw new ArgumentException("same-size BGR frames and finite xy prediction required");
        if (uiMask.Rows != before.Rows || uiMask.Cols != before.Cols) throw new ArgumentException("full-size UI exclusion mask required");
        double[] vector = [predicted.X, predicted.Y];
        int axis = Math.Abs(vector[1]) > Math.Abs(vector[0]) ? 1 : 0;
        if (vector[axis] == 0.0) return new CompareResult("모름", new double?[8], null, null, null, null);
        int direction = vector[axis] > 0 ? 1 : -1;
        var shape = new Size(Py.RoundEvenInt(before.Cols * .25), Py.RoundEvenInt(before.Rows * .25));
        float[] Prep(Mat im)
        {
            using Mat r = new Mat(), g = new Mat(), f = new Mat(), bl = new Mat(), o = new Mat();
            Cv2.Resize(im, r, shape, 0, 0, InterpolationFlags.Area);
            Cv2.CvtColor(r, g, ColorConversionCodes.BGR2GRAY);
            g.ConvertTo(f, MatType.CV_32F);
            Cv2.GaussianBlur(f, bl, new Size(0, 0), 2);
            Cv2.Subtract(f, bl, o);
            o.GetArray(out float[] v);
            return v;
        }
        float[] a = Prep(before), b = Prep(after);
        bool[] mask;
        using (Mat m01 = new Mat(), small = new Mat())
        {
            // py: ui_mask.astype(np.uint8)는 0/1 — 넓이 평균(INTER_AREA) 뒤 반올림이 0/255일 때와 다르므로 0/1로 맞춘다
            Cv2.Threshold(uiMask, m01, 0, 1, ThresholdTypes.Binary);
            Cv2.Resize(m01, small, shape, 0, 0, InterpolationFlags.Area);
            small.GetArray(out byte[] mv);
            mask = mv.Select(v => v > 0).ToArray();
        }
        int h = shape.Height, w = shape.Width;
        var scores = new double?[8];
        var counts = new int[8];
        for (int t = 0; t < OFFSETS.Length; t++)
        {
            double[] hyp = [vector[0], vector[1]];
            hyp[axis] += direction * OFFSETS[t];
            int dx = (int)Math.Round(hyp[0] * .25, MidpointRounding.ToEven), dy = (int)Math.Round(hyp[1] * .25, MidpointRounding.ToEven);
            if (Math.Abs(dx) >= w || Math.Abs(dy) >= h) { scores[t] = null; counts[t] = 0; continue; }
            int ay0 = Math.Max(0, dy), ay1 = h + Math.Min(0, dy), ax0 = Math.Max(0, dx), ax1 = w + Math.Min(0, dx);
            int by0 = Math.Max(0, -dy), bx0 = Math.Max(0, -dx);
            var av = new List<float>(); var bv = new List<float>();
            for (int y = 0; y < ay1 - ay0; y++)
                for (int x = 0; x < ax1 - ax0; x++)
                {
                    int pa = (ay0 + y) * w + ax0 + x, pb = (by0 + y) * w + bx0 + x;
                    if (mask[pa] || mask[pb]) continue;
                    av.Add(a[pa]); bv.Add(b[pb]);
                }
            if (av.Count < 3) { scores[t] = null; counts[t] = 0; continue; }
            float[] av2 = av.Select(v => v * v).ToArray(), bv2 = bv.Select(v => v * v).ToArray();
            float ma = Py.MeanF32(av2), mb = Py.MeanF32(bv2);
            var ta = new List<float>(); var tb = new List<float>();
            for (int k = 0; k < av.Count; k++)
                if (av2[k] > ma || bv2[k] > mb) { ta.Add(av[k]); tb.Add(bv[k]); }
            counts[t] = ta.Count;
            if (ta.Count < 3) { scores[t] = null; continue; }
            float mta = Py.MeanF32(ta.ToArray()), mtb = Py.MeanF32(tb.ToArray());
            float[] ca = ta.Select(v => v - mta).ToArray(), cb = tb.Select(v => v - mtb).ToArray();
            double norm = Math.Sqrt(Py.SumF32AsF64(ca.Select(v => v * v).ToArray()) * Py.SumF32AsF64(cb.Select(v => v * v).ToArray()));
            scores[t] = norm <= 2.220446049250313e-16 ? null : Py.SumF32AsF64(ca.Select((v, k) => v * cb[k]).ToArray()) / norm;
        }
        if (scores.Any(s => s is null)) return new CompareResult("모름", scores, counts, null, null, null);
        int best = 0;
        for (int t = 1; t < 8; t++) if (scores[t]!.Value > scores[best]!.Value) best = t;
        bool tied = scores.Count(s => s!.Value == scores[best]!.Value) > 1;
        double evidence = best == 0 ? scores[0]!.Value - scores.Skip(1).Take(6).Max(s => s!.Value) : -(scores[best]!.Value - scores[0]!.Value);
        string answer = "모름";
        if (!tied)
        {
            if (best == 0 && evidence >= MARGIN) answer = "맞음";
            else if (best != 0 && -evidence >= MARGIN) answer = "다름";
        }
        return new CompareResult(answer, scores, counts, OFFSETS[best], evidence, tied);
    }
}
