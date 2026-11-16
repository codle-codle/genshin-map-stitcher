using System.Reflection;
using OpenCvSharp;

namespace GenshinMap.Core.Vision;

/// <summary>사진 한 장의 특징점·가림. py: 엔진 ImageItem(dataclass). Image가 null이면 큰 그림을 버린 것(py: 0으로 채운 broadcast 배열).</summary>
public sealed class ImageItem
{
    public string Path = "";
    public string Name = "";
    public Mat? Image;
    /// <summary>Image를 버렸어도 남는 모양(높이, 너비, 채널)</summary>
    public (int H, int W, int C) Shape;
    public Mat? Gray;
    public Mat? MatchMask;
    public Mat? OutputMask;
    public KeyPoint[] KeyPoints = [];
    /// <summary>ORB 서술자(행 = 특징점, 32바이트). 특징점이 없으면 null(py: None)</summary>
    public Mat? Descriptors;
    /// <summary>py: item._still_geometry 캐시(grid_stitch._still_geometry)</summary>
    internal object? StillGeometry;

    /// <summary>py: dataclasses.replace(item, ...) — 바꾸지 않은 칸은 같은 객체를 같이 쓴다(파이썬과 같다)</summary>
    public ImageItem With(Mat? image = null, bool dropImage = false, Mat? gray = null, bool dropGray = false,
        Mat? matchMask = null, bool dropMatch = false, Mat? outputMask = null, bool dropOutput = false,
        KeyPoint[]? keyPoints = null, Mat? descriptors = null, bool setDescriptors = false) => new()
    {
        Path = Path, Name = Name, Shape = Shape,
        Image = dropImage ? null : image ?? Image,
        Gray = dropGray ? null : gray ?? Gray,
        MatchMask = dropMatch ? null : matchMask ?? MatchMask,
        OutputMask = dropOutput ? null : outputMask ?? OutputMask,
        KeyPoints = keyPoints ?? KeyPoints,
        Descriptors = setDescriptors ? descriptors : Descriptors,
    };
}

/// <summary>py: 엔진 PairMatch. point_j = point_i + (dx, dy)</summary>
public sealed record PairMatch(int I, int J, double Dx, double Dy, int Inliers, int GoodMatches, double Score, double Scale,
    double RotationDeg, int? InteriorInliers);

/// <summary>py: 엔진/genshin_map_stitcher.py의 가림·특징점·짝 맞추기(찍기와 붙이기가 같이 쓰는 부품)</summary>
public static class Engine
{
    static Mat? trialNotice, trialOutputMask;

    static Mat LoadAsset(string name)
    {
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("GenshinMap.Core.Assets." + name)
            ?? throw new InvalidOperationException($"자료 없음: {name}");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        Mat m = Cv2.ImDecode(ms.ToArray(), ImreadModes.Grayscale);
        if (m.Empty()) throw new InvalidOperationException($"자료를 못 읽음: {name}");
        return m;
    }

    public static readonly string[] IMAGE_EXTS = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff"];

    /// <summary>py: vignette_field(shape, strength, power=2, start=0, min_v=.35) — 화면 어두워짐 V(x,y), float32로 계산(11/15 확인 2457/2457)</summary>
    public static float[] VignetteField(int h, int w, double strength, double power = 2.0, double start = 0.0, double minV = 0.35)
    {
        var v = new float[h * w];
        if (strength <= 0) { Array.Fill(v, 1f); return v; }
        if (power != 2.0 || start != 0.0) throw new NotSupportedException("앱은 power=2, start=0만 쓴다");
        float cx = (float)((w - 1) / 2.0), cy = (float)((h - 1) / 2.0);
        float dx = (float)Math.Max((w - 1) / 2.0, 1.0), dy = (float)Math.Max((h - 1) / 2.0, 1.0);
        float s = (float)strength, mn = (float)minV;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = ((float)x - cx) / dx, t = ((float)y - cy) / dy;
                float r = MathF.Sqrt(u * u + t * t);
                if (r > 1f) r = 1f;
                float val = 1f - s * (r * r);
                v[y * w + x] = val < mn ? mn : val > 1f ? 1f : val;
            }
        return v;
    }

    /// <summary>py: remove_vignette(img, strength) — V로 나눠 밝힌다(float32, 0~255로 자르고 uint8로 버림)</summary>
    public static Mat RemoveVignette(Mat img, double strength)
    {
        if (strength <= 0) return img;
        int h = img.Rows, w = img.Cols, ch = img.Channels();
        float[] v = VignetteField(h, w, strength);
        byte[] src = ImageIO.RawBytes(img);
        var dst = new byte[src.Length];
        for (int p = 0; p < h * w; p++)
            for (int c = 0; c < ch; c++)
            {
                float f = src[p * ch + c] / v[p];
                dst[p * ch + c] = f <= 0f ? (byte)0 : f >= 255f ? (byte)255 : (byte)(int)f; // np.clip → astype(uint8)은 0 쪽으로 버림
            }
        var o = new Mat(h, w, img.Type());
        ImageIO.SetRaw(o, dst);
        return o;
    }

    /// <summary>py: parse_rect("x1,y1,x2,y2")</summary>
    public static (int, int, int, int) ParseRect(string s)
    {
        int[] p = s.Split(',').Select(x => int.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (p.Length != 4) throw new ArgumentException("rectangle must be x1,y1,x2,y2");
        return (p[0], p[1], p[2], p[3]);
    }

    /// <summary>py: load_images(input_dir) — 이름 순(윈도우 파이썬 경로 비교 = 소문자로 바꾼 글자 순)</summary>
    public static List<(string Path, Mat Img)> LoadImages(string inputDir)
    {
        var paths = Directory.GetFiles(inputDir).Where(p => IMAGE_EXTS.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant()))
            .OrderBy(p => p.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        if (paths.Count == 0) throw new FileNotFoundException($"No image files found in: {inputDir}");
        var loaded = new List<(string, Mat)>();
        (int, int)? first = null;
        foreach (var p in paths)
        {
            Mat? img = ImageIO.ImreadUnicode(p);
            if (img is null) { Console.WriteLine($"[WARN] Cannot read image: {p}"); continue; }
            if (first is null) first = (img.Rows, img.Cols);
            else if ((img.Rows, img.Cols) != first.Value)
                throw new ArgumentException($"All screenshots must have the same size for this MVP. {System.IO.Path.GetFileName(p)}: ({img.Rows}, {img.Cols}) != {first}");
            loaded.Add((p, img));
        }
        if (loaded.Count < 2) throw new ArgumentException("Need at least 2 readable images.");
        return loaded;
    }

    /// <summary>py: trial_notice_present(img) — 한국어 시련 공지가 떠 있나</summary>
    public static bool TrialNoticePresent(Mat img)
    {
        int h = img.Rows, w = img.Cols;
        if (w < 320 || h < 180) return false;
        trialNotice ??= LoadAsset("trial_notice.png");
        int y0 = Py.RoundEvenInt(980.0 * h / 1152), y1 = Py.RoundEvenInt(1060.0 * h / 1152);
        int x0 = Py.RoundEvenInt(160.0 * w / 2048), x1 = Py.RoundEvenInt(350.0 * w / 2048);
        var (r0, r1) = Py.Slice(y0, y1, h);
        var (c0, c1) = Py.Slice(x0, x1, w);
        using Mat crop = new Mat(img, new Rect(c0, r0, c1 - c0, r1 - r0));
        using Mat resized = new Mat();
        Cv2.Resize(crop, resized, new Size(190, 80));
        using Mat gray = new Mat();
        if (resized.Channels() == 3) Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
        else resized.CopyTo(gray);
        using Mat res = new Mat();
        Cv2.MatchTemplate(gray, trialNotice, res, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(res, out _, out double mx);
        return (double)(float)mx >= .8; // py: float(res.max())는 float32 값
    }

    /// <summary>py: mask_trial_notice(mask, for_output)</summary>
    public static void MaskTrialNotice(Mat mask, bool forOutput = false)
    {
        int h = mask.Rows, w = mask.Cols;
        foreach (var (x, y) in new[] { (320, 900), (500, 1000), (700, 1080) })
        {
            var (r0, r1) = Py.Slice(Py.RoundEvenInt((double)y * h / 1152), h, h);
            var (c0, c1) = Py.Slice(0, Py.RoundEvenInt((double)x * w / 2048), w);
            if (r1 > r0 && c1 > c0) mask[new Rect(c0, r0, c1 - c0, r1 - r0)].SetTo(0);
        }
        if (forOutput)
        {
            trialOutputMask ??= LoadAsset("trial_output_mask.png");
            using Mat big = new Mat();
            Cv2.Resize(trialOutputMask, big, new Size(w, h), 0, 0, InterpolationFlags.Nearest);
            using Mat on = new Mat();
            Cv2.Compare(big, 127, on, CmpTypes.GT);
            mask.SetTo(0, on);
        }
    }

    /// <summary>py: _apply_rect(mask, rect, pad) — 사각형 자리를 0으로</summary>
    public static void ApplyRect(Mat mask, (int X1, int Y1, int X2, int Y2) rect, int pad = 0)
    {
        int h = mask.Rows, w = mask.Cols;
        int x1 = rect.X1 - pad, y1 = rect.Y1 - pad, x2 = rect.X2 + pad, y2 = rect.Y2 + pad;
        var (r0, r1) = Py.Slice(Math.Max(0, y1), Math.Min(h, y2), h);
        var (c0, c1) = Py.Slice(Math.Max(0, x1), Math.Min(w, x2), w);
        if (r1 > r0 && c1 > c0) mask[new Rect(c0, r0, c1 - c0, r1 - r0)].SetTo(0);
    }

    /// <summary>py: make_mask(img, preset, extra_excludes, dark_filter, purpose, ui_pad) — 쓸 수 있는 곳 255, 뺄 곳 0</summary>
    public static Mat MakeMask(Mat img, string preset, IReadOnlyList<(int, int, int, int)> extraExcludes, bool darkFilter = false,
        string purpose = "match", int uiPad = 0)
    {
        int h = img.Rows, w = img.Cols;
        var mask = new Mat(h, w, MatType.CV_8UC1, Scalar.All(255));
        if (preset == "genshin")
        {
            double sx = w / 2048.0, sy = h / 1152.0;
            (int, int, int, int)[] rects;
            int pad;
            if (purpose == "output")
            {
                rects = [
                    (0, 0, w, (int)(170 * sy)),
                    (0, 0, (int)(145 * sx), h),
                    (0, (int)(970 * sy), (int)(190 * sx), h),
                    ((int)(1630 * sx), (int)(890 * sy), w, h),
                    ((int)(1900 * sx), 0, w, (int)(220 * sy)),
                    ((int)(1840 * sx), (int)(390 * sy), w, (int)(720 * sy)),
                ];
                pad = Math.Max(0, uiPad);
            }
            else
            {
                rects = [
                    (0, 0, w, (int)(145 * sy)),
                    (0, 0, (int)(115 * sx), h),
                    (0, (int)(990 * sy), (int)(150 * sx), h),
                    ((int)(1750 * sx), (int)(935 * sy), w, h),
                    ((int)(1860 * sx), (int)(370 * sy), w, (int)(750 * sy)),
                ];
                pad = 0;
            }
            foreach (var r in rects) ApplyRect(mask, r, pad);
            if (purpose == "output" && TrialNoticePresent(img)) MaskTrialNotice(mask, forOutput: true);
        }
        else if (preset != "none") throw new ArgumentException($"Unknown mask preset: {preset}");

        foreach (var r in extraExcludes) ApplyRect(mask, r, 0);

        if (darkFilter)
        {
            using Mat hsv = new Mat(), gray = new Mat(), lap = new Mat(), tex = new Mat();
            Cv2.CvtColor(img, hsv, ColorConversionCodes.BGR2HSV);
            using Mat v = new Mat();
            Cv2.ExtractChannel(hsv, v, 2);
            Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.Laplacian(gray, lap, MatType.CV_16S, 3);
            Cv2.ConvertScaleAbs(lap, tex);
            using Mat a = new Mat(), b = new Mat(), stable = new Mat();
            Cv2.Compare(v, 35, a, CmpTypes.GT);
            Cv2.Compare(tex, 18, b, CmpTypes.GT);
            Cv2.BitwiseOr(a, b, stable); // 0 또는 255(py: astype(uint8)*255와 같은 값)
            Cv2.BitwiseAnd(mask, stable, mask);
        }

        using Mat kernel = Mat.Ones(5, 5, MatType.CV_8UC1);
        var outMask = new Mat();
        Cv2.Erode(mask, outMask, kernel, null, 1, BorderTypes.Constant, Cv2.MorphologyDefaultBorderValue());
        mask.Dispose();
        return outMask;
    }

    /// <summary>py: extract_features(items, preset, extra_excludes, nfeatures, output_excludes, output_ui_pad)</summary>
    public static List<ImageItem> ExtractFeatures(IReadOnlyList<(string Path, Mat Img)> items, string preset,
        IReadOnlyList<(int, int, int, int)> extraExcludes, int nfeatures,
        IReadOnlyList<(int, int, int, int)>? outputExcludes = null, int outputUiPad = 25)
    {
        using var orb = ORB.Create(nfeatures, 1.2f, 8, 15, 0, 2, ORBScoreType.Harris, 31, 7);
        var result = new List<ImageItem>();
        outputExcludes ??= [];
        foreach (var (p, img) in items)
        {
            var gray = new Mat();
            Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
            Mat matchMask = MakeMask(img, preset, extraExcludes, darkFilter: true, purpose: "match", uiPad: 0);
            Mat outputMask = MakeMask(img, preset, [.. extraExcludes, .. outputExcludes], darkFilter: false, purpose: "output", uiPad: outputUiPad);
            var desc = new Mat();
            orb.DetectAndCompute(gray, matchMask, out KeyPoint[] kps, desc);
            string name = System.IO.Path.GetFileName(p);
            result.Add(new ImageItem
            {
                Path = p, Name = name, Image = img, Shape = (img.Rows, img.Cols, img.Channels()), Gray = gray,
                MatchMask = matchMask, OutputMask = outputMask, KeyPoints = kps, Descriptors = desc.Empty() ? null : desc,
            });
        }
        return result;
    }

    /// <summary>py: estimate_pair(a, b, i, j, ratio, min_good, min_inliers, support_rect) — 평행 이동만 있는 지도 짝 맞추기</summary>
    public static PairMatch? EstimatePair(ImageItem a, ImageItem b, int i, int j, double ratio, int minGood, int minInliers,
        (double X1, double Y1, double X2, double Y2)? supportRect = null)
    {
        if (a.Descriptors is null || b.Descriptors is null) return null;
        DMatch[][] knn;
        try
        {
            using var matcher = new BFMatcher(NormTypes.Hamming);
            knn = matcher.KnnMatch(a.Descriptors, b.Descriptors, 2);
        }
        catch (OpenCVException)
        {
            return null;
        }
        var good = new List<DMatch>();
        foreach (var pair in knn)
        {
            if (pair.Length < 2) continue;
            // py: m.distance < ratio * n.distance (distance는 float32, ratio는 파이썬 실수 → 실수 곱은 float64)
            if (pair[0].Distance < ratio * pair[1].Distance) good.Add(pair[0]);
        }
        if (good.Count < minGood) return null;

        var ptsA = good.Select(m => a.KeyPoints[m.QueryIdx].Pt).ToArray();
        var ptsB = good.Select(m => b.KeyPoints[m.TrainIdx].Pt).ToArray();
        using var inl = new Mat();
        using Mat M = Cv2.EstimateAffinePartial2D(InputArray.Create(ptsA), InputArray.Create(ptsB), inl,
            RobustEstimationAlgorithms.RANSAC, 3.0, 5000, 0.999, 10);
        if (M is null || M.Empty() || inl.Empty()) return null;
        inl.GetArray(out byte[] mask);
        int inliers = mask.Count(v => v != 0);
        if (inliers < minInliers) return null;

        double a00 = M.At<double>(0, 0), a01 = M.At<double>(0, 1);
        double a10 = M.At<double>(1, 0);
        double scale = Math.Sqrt(a00 * a00 + a01 * a01);
        double rotationDeg = Math.Atan2(a10, a00) * (180.0 / Math.PI);

        var dxs = new List<float>(); var dys = new List<float>();
        var pin = new List<Point2f>();
        for (int k = 0; k < mask.Length; k++)
        {
            if (mask[k] == 0) continue;
            dxs.Add(ptsB[k].X - ptsA[k].X);
            dys.Add(ptsB[k].Y - ptsA[k].Y);
            pin.Add(ptsA[k]);
        }
        float dx = Py.Median(dxs), dy = Py.Median(dys); // py: np.median(float32, axis=0) → float32

        if (Math.Abs(scale - 1.0) > 0.015 || Math.Abs(rotationDeg) > 0.5) return null;

        var err = new float[dxs.Count];
        for (int k = 0; k < err.Length; k++)
        {
            float rx = dxs[k] - dx, ry = dys[k] - dy;
            err[k] = MathF.Sqrt(rx * rx + ry * ry);
        }
        double medianError = Py.Median(err);
        double score = inliers / (1.0 + medianError);
        int? interior = null;
        if (supportRect is var (x1, y1, x2, y2))
        {
            // py(NumPy 2, NEP 50): float32 배열과 파이썬 실수 비교는 실수를 float32로 바꿔 float32로 비교한다
            float fx1 = (float)x1, fy1 = (float)y1, fx2 = (float)x2, fy2 = (float)y2;
            interior = pin.Count(p => p.X >= fx1 && p.X <= fx2 && p.Y >= fy1 && p.Y <= fy2);
        }
        return new PairMatch(i, j, dx, dy, inliers, good.Count, score, scale, rotationDeg, interior);
    }
}
