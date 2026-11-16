using System.Globalization;
using OpenCvSharp;

namespace GenshinMap.Core.Trim;

/// <summary>
/// 붙인 지도의 가장자리 다듬기(게임 지도 경계선을 따라 바깥을 잘라 냄). py: 엔진/map_edge_trimmer.py v1.5 — 인자 이름·기본값·출력 줄 그대로.
/// 앱은 같은 exe를 'trim' 인자로 자식 프로세스로 띄운다. 바깥 색(--bg-color)은 앱이 안 넘겨 늘 검정이라 옮기지 않았다(안_옮기는_목록).
/// </summary>
public static class EdgeTrimmer
{
    public sealed class Options
    {
        public string Input = "", Output = "output/trimmed.png";
        public string TargetLine = "cyan";
        public int Margin = 70;
        public double ProcessScale = 0.35;
        public bool Crop = true;
        public int CropPad = 0;
        public string? Debug;
        public int UpscaleSmooth = 7, MaskSmooth = 9, MaskThreshold = 128, FinalClose = 5, FinalCloseAfterSmooth = 0;
        public bool NoEdgeGate = false;
        public int CannyLow = 20, CannyHigh = 80, EdgeDilate = 1;
        public int GrayHMin = 80, GrayHMax = 130, GraySMin = 5, GraySMax = 70, GrayVMin = 70, GrayVMax = 180;
        public int CyanHMin = 80, CyanHMax = 100, CyanSMin = 80, CyanVMin = 70, CyanVMax = 220;
        public int MinComponent = 20, MinComponentLength = 20, PreDilate = 3, Close = 35, WallThickness = 7;
        public bool CleanBoundaryCandidates = true;
        public int MinBoundaryCandidateArea = 160, MinBoundaryCandidateSpan = 70;
        public bool CleanWallComponents = true;
        public int MinWallArea = 2000, MinWallSpan = 180, KeepLargestWallComponents = 0;
        public int FillSmallHoles = 0;
        public bool PreserveInterior = true, RemoveIslands = true;
        public int KeepLargestComponents = 1, MinKeepArea = 200000, MinKeepSpan = 800;
        public bool LandWall = true;
        public int LandV = 90, LandMinArea = 400;
    }

    static void Log(string m) { Console.WriteLine(m); Console.Out.Flush(); }

    /// <summary>명령줄 → Options(py argparse와 같은 이름)</summary>
    public static Options Parse(string[] a)
    {
        var o = new Options();
        bool gotInput = false;
        for (int i = 0; i < a.Length; i++)
        {
            string k = a[i];
            int I() => int.Parse(a[++i], CultureInfo.InvariantCulture);
            double D() => double.Parse(a[++i], CultureInfo.InvariantCulture);
            switch (k)
            {
                case "-o": case "--output": o.Output = a[++i]; break;
                case "--target-line": o.TargetLine = a[++i]; break;
                case "--margin": o.Margin = I(); break;
                case "--process-scale": o.ProcessScale = D(); break;
                case "--crop": o.Crop = true; break;
                case "--no-crop": o.Crop = false; break;
                case "--crop-pad": o.CropPad = I(); break;
                case "--debug": o.Debug = a[++i]; break;
                case "--upscale-smooth": o.UpscaleSmooth = I(); break;
                case "--mask-smooth": o.MaskSmooth = I(); break;
                case "--mask-threshold": o.MaskThreshold = I(); break;
                case "--final-close": o.FinalClose = I(); break;
                case "--final-close-after-smooth": o.FinalCloseAfterSmooth = I(); break;
                case "--no-edge-gate": o.NoEdgeGate = true; break;
                case "--canny-low": o.CannyLow = I(); break;
                case "--canny-high": o.CannyHigh = I(); break;
                case "--edge-dilate": o.EdgeDilate = I(); break;
                case "--gray-h-min": o.GrayHMin = I(); break;
                case "--gray-h-max": o.GrayHMax = I(); break;
                case "--gray-s-min": o.GraySMin = I(); break;
                case "--gray-s-max": o.GraySMax = I(); break;
                case "--gray-v-min": o.GrayVMin = I(); break;
                case "--gray-v-max": o.GrayVMax = I(); break;
                case "--cyan-h-min": o.CyanHMin = I(); break;
                case "--cyan-h-max": o.CyanHMax = I(); break;
                case "--cyan-s-min": o.CyanSMin = I(); break;
                case "--cyan-v-min": o.CyanVMin = I(); break;
                case "--cyan-v-max": o.CyanVMax = I(); break;
                case "--min-component": o.MinComponent = I(); break;
                case "--min-component-length": o.MinComponentLength = I(); break;
                case "--pre-dilate": o.PreDilate = I(); break;
                case "--close": o.Close = I(); break;
                case "--wall-thickness": o.WallThickness = I(); break;
                case "--clean-boundary-candidates": o.CleanBoundaryCandidates = true; break;
                case "--no-clean-boundary-candidates": o.CleanBoundaryCandidates = false; break;
                case "--min-boundary-candidate-area": o.MinBoundaryCandidateArea = I(); break;
                case "--min-boundary-candidate-span": o.MinBoundaryCandidateSpan = I(); break;
                case "--clean-wall-components": o.CleanWallComponents = true; break;
                case "--no-clean-wall-components": o.CleanWallComponents = false; break;
                case "--min-wall-area": o.MinWallArea = I(); break;
                case "--min-wall-span": o.MinWallSpan = I(); break;
                case "--keep-largest-wall-components": o.KeepLargestWallComponents = I(); break;
                case "--fill-small-holes": o.FillSmallHoles = I(); break;
                case "--preserve-interior": o.PreserveInterior = true; break;
                case "--no-preserve-interior": o.PreserveInterior = false; break;
                case "--remove-islands": o.RemoveIslands = true; break;
                case "--no-remove-islands": o.RemoveIslands = false; break;
                case "--keep-largest-components": o.KeepLargestComponents = I(); break;
                case "--min-keep-area": o.MinKeepArea = I(); break;
                case "--min-keep-span": o.MinKeepSpan = I(); break;
                case "--no-land-wall": o.LandWall = false; break;
                case "--land-v": o.LandV = I(); break;
                case "--land-min-area": o.LandMinArea = I(); break;
                default:
                    if (k.StartsWith('-') || gotInput) throw new ArgumentException($"모르는 인자: {k}");
                    o.Input = k; gotInput = true; break;
            }
        }
        if (!gotInput) throw new ArgumentException("쓰기: trim <붙인 지도> -o trimmed.png [인자...]");
        return o;
    }

    static Mat OddKernel(double size)
    {
        int s = Math.Max(1, Py.RoundEvenInt(size));
        if (s % 2 == 0) s++;
        return Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(s, s));
    }

    static int OddNumber(double size) { int s = Math.Max(1, Py.RoundEvenInt(size)); return s % 2 == 0 ? s + 1 : s; }
    static int ScaledValue(int v, double scale, int min) => v <= 0 ? 0 : Math.Max(min, Py.RoundEvenInt(v * scale));

    static readonly Scalar Def = Cv2.MorphologyDefaultBorderValue();

    /// <summary>py: filter_components(mask, min_area, min_length) — 넓이 또는 긴 변이 기준 이상인 덩어리만 255</summary>
    static Mat FilterComponents(Mat mask, int minArea, int minLength, string tag = "components")
    {
        minArea = Math.Max(0, minArea); minLength = Math.Max(0, minLength);
        using Mat labels = new Mat(), stats = new Mat(), cents = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(mask, labels, stats, cents, PixelConnectivity.Connectivity8);
        var keep = new byte[n]; int kept = 0;
        for (int i = 1; i < n; i++)
        {
            int w = stats.At<int>(i, 2), h = stats.At<int>(i, 3), area = stats.At<int>(i, 4);
            if (area >= minArea || Math.Max(w, h) >= minLength) { keep[i] = 255; kept++; }
        }
        if (tag == "components") Log($"[components] kept={kept}, total={n - 1}");
        return Lookup(labels, keep, out _);
    }

    static Mat Lookup(Mat labels, byte[] keep, out int dummy)
    {
        dummy = 0;
        labels.GetArray(out int[] lab);
        var o = new byte[lab.Length];
        for (int p = 0; p < lab.Length; p++) o[p] = keep[lab[p]];
        var m = new Mat(labels.Rows, labels.Cols, MatType.CV_8UC1);
        m.SetArray(o);
        return m;
    }

    /// <summary>py: filter_boundary_candidate_components / filter_boundary_wall_components / filter_disconnected_keep_islands의 공통 꼴:
    /// 넓이 큰 순 keepLargest개는 늘, 그 밖은 넓이·긴 변 기준</summary>
    static Mat KeepBySize(Mat mask, int minArea, int minSpan, int keepLargest, out int kept, out int removed, out int total)
    {
        using Mat labels = new Mat(), stats = new Mat(), cents = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(mask, labels, stats, cents, PixelConnectivity.Connectivity8);
        var comps = new List<(int I, int Area, int Span)>();
        for (int i = 1; i < n; i++) comps.Add((i, stats.At<int>(i, 4), Math.Max(stats.At<int>(i, 2), stats.At<int>(i, 3))));
        var sorted = comps.OrderByDescending(c => c.Area).ToList(); // py: sorted(reverse=True) 안정
        var ids = sorted.Take(Math.Max(0, keepLargest)).Select(c => c.I).ToHashSet();
        foreach (var c in sorted) if (c.Area >= minArea || c.Span >= minSpan) ids.Add(c.I);
        var keep = new byte[n];
        kept = 0; removed = 0; total = sorted.Count;
        foreach (var c in sorted) { if (ids.Contains(c.I)) { keep[c.I] = 255; kept++; } else removed++; }
        return Lookup(labels, keep, out _);
    }

    static Mat FloodOutside(Mat src01, out int count, bool countExterior = false)
    {
        // src: 255 = 채울 수 있는 곳. 테두리 1px를 255로 두르고 (0,0)에서 128로 채운다(py: cv2.floodFill 기본 4연결, 차이 0)
        using Mat padded = new Mat();
        Cv2.CopyMakeBorder(src01, padded, 1, 1, 1, 1, BorderTypes.Constant, Scalar.All(255));
        using var fm = new Mat(padded.Rows + 2, padded.Cols + 2, MatType.CV_8UC1, Scalar.All(0));
        Cv2.FloodFill(padded, fm, new Point(0, 0), Scalar.All(128), out _, Scalar.All(0), Scalar.All(0), FloodFillFlags.Link4);
        using Mat inner = new Mat(padded, new Rect(1, 1, src01.Cols, src01.Rows));
        var o = new Mat();
        Cv2.Compare(inner, 128, o, CmpTypes.EQ);
        count = 0;
        return o;
    }

    /// <summary>py: main() — 끝 코드 0 = 성공</summary>
    public static int Run(Options args)
    {
        using Mat img = ImageIO.ImreadUnicode(args.Input) ?? throw new FileNotFoundException($"Cannot read input image: {args.Input}");
        var originalSize = new Size(img.Cols, img.Rows);
        Log($"[input] {args.Input} ({originalSize.Width}x{originalSize.Height})");
        Log($"[settings] target_line={args.TargetLine}, margin={args.Margin}px, process_scale={args.ProcessScale.ToString(CultureInfo.InvariantCulture)}");

        // maybe_resize_for_processing
        int H = img.Rows, W = img.Cols;
        double scale = args.ProcessScale <= 0 ? Math.Min(1.0, 4200.0 / Math.Max(W, H)) : Math.Min(1.0, Math.Max(0.05, args.ProcessScale));
        Mat work;
        if (scale >= 0.999) { Log("[scale] process at full resolution"); work = img; scale = 1.0; }
        else
        {
            int nw = Math.Max(1, Py.RoundEvenInt(W * scale)), nh = Math.Max(1, Py.RoundEvenInt(H * scale));
            Log($"[scale] process at {scale.ToString("F3", CultureInfo.InvariantCulture)} scale: {W}x{H} -> {nw}x{nh}");
            work = new Mat();
            Cv2.Resize(img, work, new Size(nw, nh), 0, 0, InterpolationFlags.Area);
        }
        // scale_detection_args
        int close = ScaledValue(args.Close, scale, 3), wallT = ScaledValue(args.WallThickness, scale, 1), pre = ScaledValue(args.PreDilate, scale, 1);
        int minComp = Math.Max(1, Py.RoundEvenInt(args.MinComponent * scale * scale)), minCompLen = ScaledValue(args.MinComponentLength, scale, 1);
        int minCandArea = Math.Max(1, Py.RoundEvenInt(args.MinBoundaryCandidateArea * scale * scale)), minCandSpan = ScaledValue(args.MinBoundaryCandidateSpan, scale, 1);
        int minWallArea = Math.Max(1, Py.RoundEvenInt(args.MinWallArea * scale * scale)), minWallSpan = ScaledValue(args.MinWallSpan, scale, 1);
        Log($"[scale] scaled detection params: close={close}, wall={wallT}, pre_dilate={pre}, min_component={minComp}, "
            + $"min_candidate_area={minCandArea}, min_candidate_span={minCandSpan}, min_wall_area={minWallArea}, min_wall_span={minWallSpan}");

        // detect_boundary_candidates
        Log("[step] HSV/Canny boundary candidate detection...");
        var debugMasks = new List<(string, Mat)>();
        using Mat hsv = new Mat(), gray = new Mat(), blur = new Mat(), edges = new Mat(), edgeGate = new Mat();
        Cv2.CvtColor(work, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.CvtColor(work, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, blur, new Size(3, 3), 0);
        Cv2.Canny(blur, edges, args.CannyLow, args.CannyHigh);
        using (Mat k3 = Mat.Ones(3, 3, MatType.CV_8UC1)) Cv2.Dilate(edges, edgeGate, k3, null, Math.Max(0, args.EdgeDilate), BorderTypes.Constant, Def);
        Mat grayMask = new Mat(), cyanMask = new Mat();
        Cv2.InRange(hsv, new Scalar(args.GrayHMin, args.GraySMin, args.GrayVMin), new Scalar(args.GrayHMax, args.GraySMax, args.GrayVMax), grayMask);
        Cv2.InRange(hsv, new Scalar(args.CyanHMin, args.CyanSMin, args.CyanVMin), new Scalar(args.CyanHMax, 255, args.CyanVMax), cyanMask);
        if (!args.NoEdgeGate)
        {
            Cv2.BitwiseAnd(grayMask, edgeGate, grayMask);
            Cv2.BitwiseAnd(cyanMask, edgeGate, cyanMask);
        }
        Log("[step] connected-component filtering...");
        Mat grayF = FilterComponents(grayMask, minComp, minCompLen), cyanF = FilterComponents(cyanMask, minComp, minCompLen);
        Mat candRaw = args.TargetLine switch
        {
            "gray" => grayF,
            "cyan" => cyanF,
            "both" => Or(grayF, cyanF),
            _ => throw new ArgumentException($"Unknown target line: {args.TargetLine}"),
        };
        Mat cand = candRaw;
        if (args.CleanBoundaryCandidates && (minCandArea > 0 || minCandSpan > 0))
        {
            Log($"[step] filtering boundary candidates: min_area={minCandArea}, min_span={minCandSpan}");
            cand = KeepBySize(candRaw, minCandArea, minCandSpan, 0, out int kept, out int removed, out int total);
            Log($"[candidate-filter] kept={kept}, removed={removed}, total={total}");
        }
        debugMasks.Add(("01_gray_candidate.png", grayF)); debugMasks.Add(("02_cyan_candidate.png", cyanF));
        debugMasks.Add(("03_selected_candidate_raw.png", candRaw)); debugMasks.Add(("04_selected_candidate_filtered.png", cand));
        debugMasks.Add(("05_edge_gate.png", edgeGate));

        // make_boundary_wall
        Log("[step] reconstructing boundary wall...");
        Mat wallRaw = cand.Clone();
        if (pre > 0) using (Mat k = OddKernel(pre)) Cv2.Dilate(wallRaw, wallRaw, k, null, 1, BorderTypes.Constant, Def);
        if (close > 0) using (Mat k = OddKernel(close)) Cv2.MorphologyEx(wallRaw, wallRaw, MorphTypes.Close, k, null, 1, BorderTypes.Constant, Def);
        if (wallT > 0) using (Mat k = OddKernel(wallT)) Cv2.Dilate(wallRaw, wallRaw, k, null, 1, BorderTypes.Constant, Def);
        Mat wall = wallRaw;
        if (args.CleanWallComponents && (minWallArea > 0 || minWallSpan > 0 || args.KeepLargestWallComponents > 0))
        {
            Log($"[step] filtering boundary wall components: min_area={minWallArea}, min_span={minWallSpan}, keep_largest={args.KeepLargestWallComponents}");
            using Mat lbl = new Mat(); int nwc = Cv2.ConnectedComponents(wallRaw, lbl, PixelConnectivity.Connectivity8);
            if (nwc > 1)
            {
                wall = KeepBySize(wallRaw, minWallArea, minWallSpan, args.KeepLargestWallComponents, out int kept, out int removed, out int total);
                Log($"[wall-filter] kept={kept}, removed={removed}, total={total}");
            }
        }
        Mat? landWall = null;
        if (args.LandWall)
        {
            using Mat v = new Mat(), land = new Mat(), opened = new Mat();
            Cv2.ExtractChannel(hsv, v, 2);
            Cv2.Threshold(v, land, args.LandV - 1, 255, ThresholdTypes.Binary); // V >= land_v
            using (Mat k = OddKernel(3)) Cv2.MorphologyEx(land, opened, MorphTypes.Open, k, null, 1, BorderTypes.Constant, Def);
            landWall = FilterComponents(opened, args.LandMinArea, 1_000_000_000);
            var w2 = new Mat(); Cv2.BitwiseOr(wall, landWall, w2); wall = w2;
            Log($"[land-wall] V>={args.LandV}, min_area={args.LandMinArea}: {Cv2.CountNonZero(landWall)}px added");
        }

        // flood_fill_outside_fast
        Log("[step] flooding filling outside...".Replace("flooding filling", "flood filling"));
        using Mat free = new Mat();
        Cv2.Compare(wall, 0, free, CmpTypes.EQ); // 벽이 아닌 곳 255
        Mat outsideSmall = FloodOutside(free, out _);
        Mat insideSmall = new Mat(); Cv2.BitwiseNot(outsideSmall, insideSmall);

        // upsample_inside_mask_smooth
        Log("[step] upscaling inside mask to original resolution...");
        Mat soft = new Mat();
        Cv2.Resize(insideSmall, soft, originalSize, 0, 0, InterpolationFlags.Linear);
        if (args.UpscaleSmooth > 0)
        {
            int k = OddNumber(args.UpscaleSmooth);
            Log($"[step] smoothing upscaled inside mask: Gaussian k={k}");
            Cv2.GaussianBlur(soft, soft, new Size(k, k), 0);
        }
        Mat insideFull = new Mat();
        Cv2.Threshold(soft, insideFull, args.MaskThreshold - 1, 255, ThresholdTypes.Binary); // >= threshold
        soft.Dispose();
        if (args.FinalClose > 0)
        {
            Log($"[step] final close before margin: {args.FinalClose}");
            using Mat k = OddKernel(args.FinalClose);
            Cv2.MorphologyEx(insideFull, insideFull, MorphTypes.Close, k, null, 1, BorderTypes.Constant, Def);
        }

        // apply_fullres_margin
        Mat keep = insideFull.Clone();
        int margin = Math.Max(0, args.Margin);
        if (margin > 0)
        {
            Log($"[step] applying original-resolution margin: {margin}px");
            using Mat outsideD = new Mat(), dist = new Mat(), near = new Mat();
            Cv2.Compare(insideFull, 0, outsideD, CmpTypes.EQ);
            Cv2.DistanceTransform(outsideD, dist, DistanceTypes.L2, DistanceTransformMasks.Mask3);
            Cv2.Compare(dist, margin, near, CmpTypes.LE);
            Cv2.BitwiseOr(keep, near, keep);
        }
        // smooth_final_keep_mask
        if (args.MaskSmooth > 0)
        {
            int k = OddNumber(args.MaskSmooth);
            Log($"[step] smoothing final keep mask: Gaussian k={k}, threshold={args.MaskThreshold}");
            using Mat bl = new Mat();
            Cv2.GaussianBlur(keep, bl, new Size(k, k), 0);
            Cv2.Threshold(bl, keep, args.MaskThreshold - 1, 255, ThresholdTypes.Binary);
            if (args.FinalCloseAfterSmooth > 0)
            {
                Log($"[step] final close after smooth: {args.FinalCloseAfterSmooth}");
                using Mat kk = OddKernel(args.FinalCloseAfterSmooth);
                Cv2.MorphologyEx(keep, keep, MorphTypes.Close, kk, null, 1, BorderTypes.Constant, Def);
            }
        }
        if (args.FillSmallHoles > 0) throw new NotSupportedException("--fill-small-holes는 앱이 안 쓴다(기본 0)");
        if (args.PreserveInterior)
        {
            Log("[step] preserving enclosed interior holes...");
            using Mat cut = new Mat(); Cv2.BitwiseNot(keep, cut);
            using Mat exterior = FloodOutside(cut, out _);
            using Mat cut255 = new Mat(), notExt = new Mat(), enclosed = new Mat();
            Cv2.Compare(cut, 255, cut255, CmpTypes.EQ);
            Cv2.BitwiseNot(exterior, notExt);
            Cv2.BitwiseAnd(cut255, notExt, enclosed);
            keep.SetTo(255, enclosed);
            Log($"[preserve] filled enclosed pixels={Cv2.CountNonZero(enclosed)}");
        }
        if (args.RemoveIslands)
        {
            int kl = Math.Max(0, args.KeepLargestComponents), ma = Math.Max(0, args.MinKeepArea), ms = Math.Max(0, args.MinKeepSpan);
            Log("[step] removing disconnected exterior islands...");
            using Mat lbl = new Mat(); int n = Cv2.ConnectedComponents(keep, lbl, PixelConnectivity.Connectivity8);
            if (n <= 1) Log("[islands] only one keep component");
            else
            {
                var k2 = KeepBySize(keep, ma, ms, kl, out int kept, out int removed, out int total);
                keep.Dispose(); keep = k2;
                Log($"[islands] total={total}, kept={kept}, removed={removed}, keep_largest={kl}, min_keep_area={ma}, min_keep_span={ms}");
            }
        }

        Log("[step] applying keep mask to full image...");
        using var result = new Mat(img.Rows, img.Cols, img.Type(), Scalar.All(0));
        img.CopyTo(result, keep);
        Mat outImg = result, keepCropped = keep;
        if (args.Crop)
        {
            using Mat nz = new Mat();
            Cv2.FindNonZero(keep, nz);
            if (!nz.Empty())
            {
                Rect r = Cv2.BoundingRect(nz);
                int x1 = Math.Max(0, r.X - args.CropPad), x2 = Math.Min(W, r.X + r.Width + args.CropPad);
                int y1 = Math.Max(0, r.Y - args.CropPad), y2 = Math.Min(H, r.Y + r.Height + args.CropPad);
                Log($"[crop] x={x1}:{x2}, y={y1}:{y2}");
                var rect = new Rect(x1, y1, x2 - x1, y2 - y1);
                outImg = new Mat(result, rect).Clone();
                keepCropped = new Mat(keep, rect).Clone();
            }
        }
        if (!ImageIO.ImwriteUnicode(args.Output, outImg)) throw new IOException($"Failed to write output: {args.Output}");
        Log($"[done] saved: {args.Output} ({outImg.Cols}x{outImg.Rows})");

        if (args.Debug is string dbg)
        {
            Directory.CreateDirectory(dbg);
            foreach (var (name, m) in debugMasks) ImageIO.ImwriteUnicode(Path.Combine(dbg, name), m);
            ImageIO.ImwriteUnicode(Path.Combine(dbg, "06_boundary_wall_raw_lowres.png"), wallRaw);
            ImageIO.ImwriteUnicode(Path.Combine(dbg, "07_boundary_wall_filtered_lowres.png"), wall);
            ImageIO.ImwriteUnicode(Path.Combine(dbg, "06_outside_fill_lowres.png"), outsideSmall);
            if (landWall is not null) ImageIO.ImwriteUnicode(Path.Combine(dbg, "06_land_wall_lowres.png"), landWall);
            ImageIO.ImwriteUnicode(Path.Combine(dbg, "07_inside_mask_lowres.png"), insideSmall);
            ImageIO.ImwriteUnicode(Path.Combine(dbg, "08_inside_mask_full.png"), insideFull);
            ImageIO.ImwriteUnicode(Path.Combine(dbg, "09_keep_mask_full_after_island_filter.png"), keep);
            using (Mat ov = MakeOverlay(img, keep, new Scalar(0, 255, 255))) ImageIO.ImwriteUnicode(Path.Combine(dbg, "10_keep_overlay_full.jpg"), ov);
            if (args.Crop) ImageIO.ImwriteUnicode(Path.Combine(dbg, "11_keep_mask_cropped.png"), keepCropped);
            Log($"[debug] saved: {dbg}");
        }
        return 0;
    }

    static Mat Or(Mat a, Mat b) { var o = new Mat(); Cv2.BitwiseOr(a, b, o); return o; }

    /// <summary>py: make_overlay(img, mask, color) — 남길 곳에 색을 0.55만큼 섞은 확인용 그림</summary>
    static Mat MakeOverlay(Mat img, Mat mask, Scalar color)
    {
        var overlay = img.Clone();
        using var tinted = new Mat(img.Rows, img.Cols, img.Type(), color);
        using var blended = new Mat();
        Cv2.AddWeighted(img, 1 - 0.55, tinted, 0.55, 0, blended);
        blended.CopyTo(overlay, mask);
        return overlay;
    }

    public static int Main(string[] a)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try { return Run(Parse(a)); }
        catch (Exception e) { Console.Error.WriteLine($"[error] {e.GetType().Name}: {e.Message}"); return 1; }
    }
}
