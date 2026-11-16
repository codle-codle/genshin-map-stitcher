using System.Text.Json.Nodes;
using GenshinMap.Core.Vision;
using OpenCvSharp;
using static GenshinMap.Core.Capture.CaptureConsts;
using CV = GenshinMap.Core.Vision.CaptureVision;
using gs = GenshinMap.Core.Vision.GridParts;
using mo = GenshinMap.Core.Vision.MapObservation;

namespace GenshinMap.Core.Capture;

public sealed partial class FastCapture
{
    /// <summary>찍기가 'error'로 끝났을 때 예외 전체(시험 도구가 보여 준다)</summary>
    public string? LastError { get; private set; }

    /// <summary>py: settle(image, vx, vy) → (마지막 화면, 기다리는 동안 보는 자리가 움직인 양, 끝난 까닭 still·blind·lost·capped)</summary>
    (Mat Image, (double X, double Y) Sv, string Status) Settle(Mat image, double vx = 0.0, double vy = 0.0)
    {
        if (!screen.IsReal && !screen.MovesByItself) return (image, (0.0, 0.0), "still");
        double start = time.Time(), mx = 0.0, my = 0.0;
        bool seen = false;
        (double T, double X, double Y)? calm = null;
        bool blind = false;
        var samples = new List<(double T, double X, double Y)> { (0.0, 0.0, 0.0) };
        string? status = null;
        while (time.Time() - start < SETTLE_MAX)
        {
            Check();
            time.Sleep(0.15);
            Mat nxt = Grab();
            Motion? motion = mo.LkMotion(image, nxt, ui.Small, SETTLE_CENTER);
            if (motion is not null && motion.Status == "ambiguous")
            {
                lkAmbiguous++;
                dragLkAmbiguous++;
                if (dragLkAmbiguous == 1) Log.Add("  촘촘한 LK 합의가 둘 이상이라 모름(각 40점 이상)");
            }
            double[]? d = motion?.Shift;
            if (d is null && (vx != 0 || vy != 0))
            {
                int ax = Math.Abs(vy) > Math.Abs(vx) ? 1 : 0;
                var r = Slide(image, nxt, ax, -SLIDE_REACH, SLIDE_REACH);
                if (r.D is not null)
                {
                    d = [0.0, 0.0]; d[ax] = -r.D.Value;
                    Inc("settle_slides");
                }
            }
            image = nxt;
            double now = time.Time() - start;
            if (d is null)
            {
                if (!seen) { status = "blind"; break; }
                blind = true; calm = null;
                continue;
            }
            blind = false;
            mx -= d[0]; my -= d[1];
            samples.Add((now, mx, my));
            if (Py.Hypot(d[0], d[1]) >= mo.LK_STILL_PX) { seen = true; calm = null; continue; }
            if (!seen)
            {
                if (Py.Hypot(d[0], d[1]) < mo.LK_STILL_PX) { status = "still"; break; }
                seen = true;
            }
            if (calm is null) calm = (now, mx, my);
            else if (now - calm.Value.T >= SETTLE_CONFIRM)
            {
                if (Py.Hypot(mx - calm.Value.X, my - calm.Value.Y) < SETTLE_PX) { status = "still"; break; }
                calm = (now, mx, my);
            }
        }
        status ??= seen ? (blind ? "lost" : "capped") : "still";
        if (!seen) return (image, (0.0, 0.0), status);
        double length = Py.Hypot(vx, vy);
        bool back = length != 0 && (mx * vx + my * vy) / length < -BOUNCE_PX;
        var perSec = new List<double>();
        (double X, double Y) last = (0.0, 0.0);
        int kk = 1;
        foreach (var (t, x, y) in samples.Skip(1))
            while (t >= kk) { perSec.Add(Py.Hypot(x - last.X, y - last.Y)); last = (x, y); kk++; }
        string why = status switch { "still" => "멈춤 확인", "lost" => "움직이다가 무늬가 안 보여 그만 기다림", _ => "아직 움직이는 채로 그만 기다림" };
        Log.Add($"  튕김 기다림 {time.Time() - start:F2}초(그동안 지도가 {Py.Hypot(mx, my):F0}px 움직임" + (back ? ", 끈 반대쪽으로" : "") + $", {why})"
                + (perSec.Count > 0 ? " 1초마다 " + string.Join("/", perSec.Select(v => v.ToString("F0"))) + "px" : ""));
        return (image, (mx, my), status);
    }

    /// <summary>py: confirm_still(item, reason, guess, image) — SETTLE_CONFIRM 뒤 다시 찍어 같은 기준과 견준다</summary>
    bool ConfirmStill(ImageItem item, string reason = "blind", (double X, double Y)? guess = null, Mat? image = null)
    {
        double t0 = time.Time();
        Check();
        time.Sleep(SETTLE_CONFIRM);
        Mat again = Grab();
        var it2 = live.Item(again, ui);
        double? moved = null; string mode = "직전 사진"; bool sawAnchor = false;
        var g = guess ?? pos;
        try
        {
            var refs = live.Tiles.OrderBy(t => Py.Hypot(t.Pos.X - g.X, t.Pos.Y - g.Y)).Take(6).ToList();
            foreach (var (_, rf, _) in refs)
            {
                Check();
                var first = gs.MatchPair(rf, item, 0, 1, gs.WEAK_INLIERS);
                if (first is null) continue;
                sawAnchor = true;
                mode = "같은 저장 사진";
                var second = gs.MatchPair(rf, it2, 0, 1, gs.WEAK_INLIERS);
                if (second is not null)
                {
                    var a = gs.OffsetOf(first); var b = gs.OffsetOf(second);
                    moved = Py.Hypot(a.X - b.X, a.Y - b.Y); mode = "같은 저장 사진";
                    break;
                }
            }
            if (moved is null && !sawAnchor)
            {
                if (image is not null)
                {
                    mode = "직전 화면 가운데";
                    var lk = mo.LkMotion(image, again, ui.Small, SETTLE_CENTER);
                    if (lk is not null && lk.Status == "valid") moved = Py.Hypot(lk.Shift![0], lk.Shift[1]);
                }
                if (moved is null)
                {
                    var pm = gs.MatchPair(item, it2, 0, 1, gs.WEAK_INLIERS);
                    if (pm is not null) { var o = gs.OffsetOf(pm); moved = Py.Hypot(o.X, o.Y); }
                }
            }
        }
        finally { gs.Forget(it2); }
        bool confirmed = moved is not null && moved < SETTLE_PX;
        settleConfirmation = new JsonObject
        {
            ["reason"] = reason, ["confirmed"] = confirmed, ["moved_px"] = moved, ["reference"] = mode, ["seconds"] = time.Time() - t0,
        };
        Log.Add($"  멈춤 재확인({reason}): {SETTLE_CONFIRM}초 뒤 {mode} 특징점 → " + (moved is null ? "못 맞춤" : $"{moved:F1}px 움직임"));
        return confirmed;
    }

    /// <summary>py: interrupted(kind, start, before_count)</summary>
    void Interrupted(string kind, (double X, double Y) start, int beforeCount)
    {
        if (!screen.DragStarted) return;
        if (moves == beforeCount)
        {
            moves++;
            if (kind == "calib") calibMoves++;
        }
        if (rows.Count == 0 || rows[^1]["n"]!.GetValue<int>() != moves)
            AddRow(new JsonObject
            {
                ["n"] = moves, ["kind"] = kind, ["action"] = kind, ["press_xy"] = Arr(start), ["confirmed"] = false, ["k"] = k,
                ["label"] = "중단된 끌기", ["row"] = null,
            });
    }

    /// <summary>py: fresh_after_pause()</summary>
    Mat FreshAfterPause()
    {
        while (true)
        {
            if (!screen.WasPaused) screen.WaitResume();
            screen.WasPaused = false;
            try { return Grab(); }
            catch (FocusPause) { }
        }
    }

    /// <summary>py: observe_zoom(image, reference) — 확대가 바뀌었나. 두 번 확인되면 ZoomChanged</summary>
    (Mat Image, RawPair Fit) ObserveZoom(Mat image, ImageItem reference)
    {
        zoomChecks++;
        zoomUnknown = zoomUnknownStreak != 0;
        var current = mo.Features(image);
        var fit = mo.RawPairOf(reference, current);
        string kind = "immediate";
        if (!fit.Ok && zoomRefFeatures is not null && !ReferenceEquals(reference, zoomRefFeatures))
        {
            var alternate = mo.RawPairOf(zoomRefFeatures, current);
            if (alternate.Ok) { fit = alternate; reference = zoomRefFeatures; kind = "last_trusted"; }
        }
        var diagnostic = new JsonObject { ["n"] = moves, ["reference"] = kind };
        foreach (var kv in Summary(fit)) diagnostic[kv.Key] = kv.Value?.DeepClone();
        model.ZoomDiagnostics.Add(diagnostic);
        if (fit.Ok && Changed(fit))
        {
            time.Sleep(.3);
            Check();
            image = Grab();
            current = mo.Features(image);
            var confirmed = mo.RawPairOf(reference, current);
            diagnostic["confirmation"] = Summary(confirmed);
            if (confirmed.Ok && Changed(confirmed)) throw new ZoomChanged();
            fit = confirmed;
        }
        if (!fit.Ok && (diagnostic["confirmation"] is not null || zoomUnknownStreak != 0))
        {
            var reobs = new JsonArray();
            diagnostic["reobservations"] = reobs;
            var refs = new List<ImageItem> { reference };
            refs.AddRange(live.Tiles.OrderBy(t => Py.Dist(t.Pos, pos)).Take(3).Where(t => !ReferenceEquals(t.Item, reference)).Select(t => t.Item));
            bool broke = false;
            for (int it = 0; it < 2; it++)
            {
                time.Sleep(.3);
                Check();
                image = Grab();
                current = mo.Features(image);
                var fits = refs.Select(r => mo.RawPairOf(r, current)).ToList();
                reobs.Add(new JsonArray(fits.Select(f => (JsonNode?)Summary(f)).ToArray()));
                var valid = fits.Where(f => f.Ok).ToList();
                var changed = valid.Select(Changed).ToList();
                if (valid.Count > 0 && changed.All(c => c)) throw new ZoomChanged();
                if (valid.Count > 0 && !changed.Any(c => c))
                {
                    fit = valid[0];
                    foreach (var f in valid) if (f.Inliers > fit.Inliers) fit = f; // py: max(valid, key=inliers) — 같으면 앞의 것
                    broke = true;
                    break;
                }
            }
            if (!broke)
            {
                zoomUnknownStreak++;
                zoomUnknown = true;
                diagnostic["unresolved_streak"] = zoomUnknownStreak;
                if (zoomUnknownStreak >= 3)
                    throw new CaptureHalt("확대 의심을 3번 연속 확인하지 못해 멈췄어요. 지형이 보이는 곳에서 다시 시작해 주세요.");
                fit = new RawPair { Ok = false, Reason = "zoom_unconfirmed" };
            }
        }
        if (fit.Ok)
        {
            zoomUnknown = false;
            zoomUnknownStreak = 0;
            zoomRefImage = image; zoomRefFeatures = current;
        }
        return (image, fit);
    }

    /// <summary>py: calibrate() — 끌기 비율 g를 재고 칸 크기·화면 요소를 정한다</summary>
    void Calibrate()
    {
        var photometric = new List<(float[] Ra, float[] Rb, float[] Z)?>();
        var nominal = new DragModel(1.0, 0.0);
        var validSamples = new List<CalibSample>();
        Mat image = null!;
        for (int axis = 0; axis < 2; axis++)
        {
            ClearUiFrames(); uiMoving.SetTo(0); photometric.Clear();
            var accepted = new List<CalibSample>();
            bool restart = true;
            while (restart)
            {
                restart = false;
                accepted = new List<CalibSample>();
                image = IdleGrab();
                var (x1, y1, x2, y2) = Geometry.ContentBox(w, h);
                double lng = Py.Min(900.0, (axis == 0 ? x2 - x1 : y2 - y1) - 48);
                double shrt = Py.Min(400.0, lng * .55);
                if (shrt <= mo.D0 || lng - shrt < 32) continue;
                var feats = mo.Features(image);
                var scores = new List<int>();
                foreach (int sgn in new[] { -1, 1 })
                {
                    int cnt = 0;
                    foreach (var kp in feats.KeyPoints)
                    {
                        // py: np.array(kp.pt)은 float64, 더하기도 float64
                        double px = kp.Pt.X, py = kp.Pt.Y;
                        if (axis == 0) px += sgn * lng * 1.3; else py += sgn * lng * 1.3;
                        if (px > x1 && px < x2 && py > y1 && py < y2) cnt++;
                    }
                    scores.Add(cnt);
                }
                int direction = scores[1] > scores[0] ? 1 : -1; // py: (-1,1)[argmax] — 같으면 앞(-1)
                foreach (double length in new[] { shrt, -shrt, lng, -lng })
                {
                    Check();
                    double[] vector = [0.0, 0.0]; vector[axis] = -direction * length;
                    var spot = Geometry.PressSpot(image, vector[0], vector[1], [], nominal, ui.Full);
                    if (spot is null) break;
                    var (start, end) = Geometry.DragPoints(w, h, vector[0], vector[1], spot.Value.Shift, spot.Value.Along, nominal);
                    Mat? heldBefore = null, heldHold = null;
                    (double X, double Y) cursorStart = default, cursorEnd = default;
                    void OnPress()
                    {
                        if (screen.IsReal) time.Sleep(.2);
                        heldBefore = Grab(); cursorStart = screen.Cursor();
                    }
                    void OnHold() { heldHold = Grab(); cursorEnd = screen.Cursor(); }
                    int beforeCount = moves;
                    screen.DragStarted = false;
                    bool brokeOut = false;
                    try
                    {
                        screen.Drag(start.X, start.Y, end.X, end.Y, OnPress, OnHold);
                        moves++; calibMoves++;
                        time.Sleep(screen.IsReal ? .6 : .05);
                        bool settled;
                        (image, _, settled) = SettleAfterDrag(Grab(), 0, 0);
                        var a = mo.Features(heldBefore ?? throw new KeyNotFoundException("before"));
                        var b = mo.Features(heldHold ?? throw new KeyNotFoundException("hold"));
                        var fit = mo.RawPairOf(a, b);
                        var release = mo.RawPairOf(b, mo.Features(image));
                        var sample = new CalibSample
                        {
                            S = mo.GainSample(fit, cursorStart, cursorEnd, release), Fit = Summary(fit), Release = Summary(release),
                            MouseStart = cursorStart, MouseEnd = cursorEnd,
                        };
                        if (!settled) { sample.S.Accepted = false; sample.S.Reason = "unsettled"; }
                        sample.Axis = axis; sample.RequestedMousePx = Math.Abs(length); sample.Selected = false;
                        accepted.Add(sample); model.Calibration.Add(sample);
                        using (Mat mm = mo.MovingMask(fit, uiMoving.Rows, uiMoving.Cols)) Cv2.BitwiseOr(uiMoving, mm, uiMoving);
                        foreach (Mat im in new[] { heldBefore, heldHold })
                        {
                            using Mat gray = new Mat();
                            Cv2.CvtColor(im, gray, ColorConversionCodes.BGR2GRAY);
                            uiFrames.Add(gs.UiSmall(gray));
                        }
                        photometric.Add(mo.BrightnessSamples(heldBefore, heldHold, fit));
                        (double X, double Y) movement = fit.Shift is { } sh ? (-sh[0], -sh[1]) : (-0.0, -0.0);
                        var row = new JsonObject
                        {
                            ["n"] = moves, ["kind"] = "calib", ["action"] = "calib", ["press_xy"] = Arr(start), ["confirmed"] = false, ["k"] = 0,
                            ["label"] = "보정", ["row"] = null, ["sample"] = null, ["settle_confirmation"] = settleConfirmation?.DeepClone(),
                            ["lk_ambiguous"] = dragLkAmbiguous,
                        };
                        AddRow(row); calibRows.Add((row, sample));
                        trace?.Invoke(vector[0], vector[1], movement, "calib");
                    }
                    catch (FocusPause)
                    {
                        Interrupted("calib", start, beforeCount);
                        image = FreshAfterPause(); restart = true;
                        ClearUiFrames(); uiMoving.SetTo(0); photometric.Clear();
                        validSamples.Clear();
                        brokeOut = true;
                    }
                    finally
                    {
                        Interrupted("calib", start, beforeCount);
                    }
                    if (brokeOut) break;
                }
            }
            var valid = accepted.Where(s => s.S.Accepted).ToList();
            validSamples.AddRange(valid);
            var selected = validSamples;
            double? gain = mo.CalibrationGain(selected.Select(s => s.S).ToList());
            if (gain is null)
            {
                selected = valid;
                gain = mo.CalibrationGain(selected.Select(s => s.S).ToList());
            }
            if (gain is not null)
            {
                foreach (var s in selected) s.Selected = true;
                model.G = gain;
                stImage = image;
                break;
            }
        }
        if (model.G is null) throw new CalibrationFailed();
        var (bx1, by1, bx2, by2) = Geometry.ContentBox(w, h);
        sx = Py.Min(sx, model.G.Value * (bx2 - bx1 - 48 - mo.D0));
        sy = Py.Min(sy, model.G.Value * (by2 - by1 - 48 - mo.D0));
        using (Mat? learned = gs.LearnStaticUi(LastUiFrames()))
        using (Mat? mask = gs.BoxStaticUi(learned, (sx, sy), uiMoving))
            CV.SetStaticUi(ui, mask, w, h);
        uiMoving.SetTo(0);
        bool ok = false;
        for (int it = 0; it < 24; it++)
        {
            bool badx = new[] { -1, 1 }.Any(sgn => Geometry.PressSpot(image, sgn * sx, 0, [], model.Drag, ui.Full) is null);
            bool bady = new[] { -1, 1 }.Any(sgn => Geometry.PressSpot(image, 0, sgn * sy, [], model.Drag, ui.Full) is null);
            if (!badx && !bady) { ok = true; break; }
            if (badx) sx *= .9;
            if (bady) sy *= .9;
        }
        if (!ok) throw new CaptureHalt("안전한 끌기 경로가 없어 멈췄어요.");
        live.Step = (sx, sy);
        topology.Step = (sx, sy);
        topology.Tolerance = (Py.Min(sx / 2, Py.Max(0.0, ((bx2 - bx1) - sx) / 2)), Py.Min(sy / 2, Py.Max(0.0, ((by2 - by1) - sy) / 2)));
        model.Step = (sx, sy);
        model.Brightness = mo.BrightnessStrength(photometric);
        zoomRefImage = image; zoomRefFeatures = mo.Features(image);
        Log.Add($"시작 보정 g={model.G.Value:F6}, d0={mo.D0}마우스px, 칸 {sx:F1}×{sy:F1}, 밝기 세기 {model.Brightness.Value.Strength:F2}");
    }

    List<Mat> LastUiFrames() => uiFrames.Count > 80 ? uiFrames.GetRange(uiFrames.Count - 80, 80) : uiFrames.ToList();
    void ClearUiFrames() { foreach (var m in uiFrames) m.Dispose(); uiFrames.Clear(); }

    /// <summary>py: store(name, image, path) — 기준 사진(PNG 압축 1)을 임시 폴더에, 앱에는 올려 보낸다</summary>
    void Store(string name, Mat image, string? path)
    {
        if (path is not null) ImageIO.WritePng(path, image, 1);
        reporter.Upload(name, image);
    }

    /// <summary>py: absolute_position(image, prediction, deadline) — 저장한 기준 사진으로 지금 자리를 찾는다(시간 한도 안에서만)</summary>
    (double X, double Y)? AbsolutePosition(Mat image, (double X, double Y) prediction, double deadline)
    {
        var available = ReadyAnchors().OrderBy(e => Py.Max(Math.Abs(e.Pos.X - prediction.X) / sx, Math.Abs(e.Pos.Y - prediction.Y) / sy)).ToList();
        if (time.Monotonic() >= deadline) return null;
        var found = FastAnchor.Locate(image, available, prediction, (sx, sy), ui.Full, () => time.Monotonic() >= deadline, screen.Blind);
        return time.Monotonic() < deadline ? found : null;
    }

    /// <summary>py: try_recover(image, guess) — 추정 중 '맞음' 끌기 뒤 가까운 기준 사진으로 진짜 자리를 찾아 본다</summary>
    (double X, double Y)? TryRecover(Mat image, (double X, double Y) guess)
    {
        if (recoverSeconds >= RECOVER_TOTAL) return null;
        double Dist((double X, double Y) q) => Py.Max(Math.Abs(q.X - guess.X) / sx, Math.Abs(q.Y - guess.Y) / sy);
        var near = ReadyAnchors().Where(e => Dist(e.Pos) <= RECOVER_REACH).OrderBy(e => Dist(e.Pos)).Take(2).ToList();
        if (near.Count == 0) return null;
        double t0 = time.Monotonic();
        var found = FastAnchor.Locate(image, near, guess, (sx, sy), ui.Full, () => time.Monotonic() >= t0 + RECOVER_SECONDS, screen.Blind);
        recoverSeconds += time.Monotonic() - t0;
        Inc("recover_tries");
        if (found is null || time.Monotonic() > t0 + RECOVER_SECONDS) return null;
        var delta = (found.Value.X - guess.X, found.Value.Y - guess.Y);
        if (Math.Abs(delta.Item1) > topology.Tolerance.X || Math.Abs(delta.Item2) > topology.Tolerance.Y)
        {
            Inc("recover_rejected");
            return null;
        }
        return found;
    }

    Cell NearestCell((double X, double Y) p) => CellSearch.NearestCell(topology, p);

    bool[] EdgesOrNone(Mat image) => CV.BrightRatio(ui, image) >= CV.VOID_MAX ? CV.EdgesOf(ui, image) : new bool[4];

    /// <summary>py: replan(image)</summary>
    void Replan(Mat image)
    {
        cur = NearestCell(pos);
        if (!InCells(cur)) Visit(cur, image);
        else topology.Record(cur, EdgesOrNone(image), guessed);
    }

    static string Signed2(int v) => (v < 0 ? "-" : "+") + Math.Abs(v).ToString("D2"); // py: f"{v:+03d}"

    /// <summary>py: visit(cell, image, answer, prediction) — 칸을 적고 사진을 남긴다(빈 하늘이면 안 남김)</summary>
    void Visit(Cell cell, Mat image, string answer = "맞음", (double X, double Y)? prediction = null)
    {
        cell = topology.Canonical(cell);
        topology.Record(cell, EdgesOrNone(image), guessed);
        if (CV.BrightRatio(ui, image) < CV.VOID_MAX)
        {
            skipped++;
            return;
        }
        int seq = saved;
        (double X, double Y) center = (cell.X * sx, cell.Y * sy);
        string note = limits.Clamp(center) != center ? "_끝" : "";
        double tol = Py.Max(ARRIVE_PX, .12 * sx);
        if (note == "" && !guessed && Py.Max(Math.Abs(pos.X - cell.X * sx), Math.Abs(pos.Y - cell.Y * sy)) > tol) note = "_끝";
        string name = $"자동_{seq:D4}_x{Signed2(cell.X)}y{Signed2(cell.Y)}{note}.png";
        var tile = new JsonObject
        {
            ["seq"] = seq, ["cell"] = new JsonArray(cell.X, cell.Y), ["x"] = pos.X, ["y"] = pos.Y, ["placed"] = !guessed, ["guessed"] = guessed,
            ["drag_n"] = moves, ["answer"] = answer, ["predicted"] = Arr(prediction ?? pos),
        };
        if (!screen.IsReal && screen.Shown is (double tx, double ty)) tile["truth"] = new JsonArray(tx, ty);
        tiles[name] = tile;
        saved++;
        unplaced += guessed ? 1 : 0;
        string? path = !guessed ? Path.Combine(archive, name) : null;
        Task? done = null;
        if (screen.IsReal)
        {
            // py: 끝난 저장부터 결과를 확인(오류면 여기서 난다)하고, 밀린 게 8개면 멈춘다(16GB PC를 다 쓰지 않게)
            while (pending.Count > 0 && pending.Peek().IsCompleted) { var t = pending.Dequeue(); if (t.IsFaulted) throw t.Exception!.InnerException!; }
            if (pending.Count >= 8) throw new CaptureHalt("사진 저장이 밀려 멈췄어요.");
            string n0 = name; Mat img = image; string? p0 = path;
            done = storeTail = storeTail.ContinueWith(_ => Store(n0, img, p0), TaskScheduler.Default); // 일꾼 하나(py max_workers=1)
            pending.Enqueue(done);
        }
        else Store(name, image, path);
        if (path is not null) anchors.Add((path, pos, done));
        using (Mat gray = new Mat())
        {
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
            uiFrames.Add(gs.UiSmall(gray));
        }
        int n = uiFrames.Count;
        if (n >= gs.UI_MIN_FRAMES && (n <= 8 || n % 4 == 0))
        {
            using (Mat? learned = gs.LearnStaticUi(LastUiFrames()))
            using (Mat? mask = gs.BoxStaticUi(learned, (sx, sy), uiMoving))
                CV.SetStaticUi(ui, mask, w, h);
            uiMoving.SetTo(0);
        }
        if (uiFrames.Count > 80)
        {
            foreach (var m in uiFrames.Take(uiFrames.Count - 80)) m.Dispose();
            uiFrames.RemoveRange(0, uiFrames.Count - 80);
        }
    }

    (bool Open, Cell Neighbor) OpenSide(Cell cell, int d) => CellSearch.OpenSide(topology, cell, d);
}
