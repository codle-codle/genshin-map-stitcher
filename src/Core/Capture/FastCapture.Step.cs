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
    static double[] V((double X, double Y) p) => [p.X, p.Y];
    static (double X, double Y) T(double[] v) => (v[0], v[1]);

    /// <summary>py: step(target, capture) — 목표 칸으로 한 번 끈다. 도착했으면 true, 길을 다시 짜야 하면 false</summary>
    bool Step(Cell target, bool capture)
    {
        Check();
        if (moves >= maxCells * 1.5) throw new LimitReached("끌기 상한");
        target = topology.Canonical(target);
        var old = pos; Cell cell = topology.Canonical(cur);
        double[] prediction = V(limits.Clamp((target.X * sx, target.Y * sy)));
        double[] o = V(old);
        double[] vector = [prediction[0] - o[0], prediction[1] - o[1]];
        if (Math.Abs(vector[0]) <= topology.Tolerance.X && Math.Abs(vector[1]) <= topology.Tolerance.Y)
        {
            cur = target;
            noDragArrivals++;
            if (!InCells(target)) Visit(target, stImage);
            return true;
        }
        int routeAxis = Math.Abs((target.Y - cell.Y) * sy) > Math.Abs((target.X - cell.X) * sx) ? 1 : 0;
        int cross = 1 - routeAxis;
        double[] stepXY = [sx, sy];
        if (Math.Abs(vector[cross]) <= Py.Max(ARRIVE_PX, .12 * stepXY[cross]))
        {
            prediction[cross] = o[cross];
            vector = [prediction[0] - o[0], prediction[1] - o[1]];
        }
        int axis = Math.Abs(vector[1]) > Math.Abs(vector[0]) ? 1 : 0;
        int sign = vector[axis] > 0 ? 1 : -1;
        var key = (cell, axis, sign);
        if (TouchesGet(key) >= 3)
        {
            topology.RawBroken.Add((cell, target)); topology.Rebuild(); return false;
        }
        double scale = Py.Max(Py.Max(1.0, Math.Abs(vector[0]) / sx), Math.Abs(vector[1]) / sy);
        bool intermediate = scale > 1.1;
        if (intermediate)
        {
            vector = [vector[0] / scale, vector[1] / scale];
            prediction = [o[0] + vector[0], o[1] + vector[1]];
        }
        var spot = Geometry.PressSpot(stImage, vector[0], vector[1], [], model.Drag, ui.Full)
                   ?? throw new CaptureHalt("안전한 끌기 경로가 없어 멈췄어요.");
        Mat before = stImage; double t0 = time.Monotonic(); int count = moves;
        screen.DragStarted = false;
        try
        {
            using (Timed("drag_and_release_wait")) MoveView(vector[0], vector[1], spot.Shift, spot.Along, model.Drag);
            moves++;
            Check();
            Mat image; double grabStamp;
            using (Timed("grab"))
            {
                double grabBegan = time.Monotonic();
                image = Grab();
                grabStamp = (grabBegan + time.Monotonic()) / 2;
            }
            CompareResult comparison;
            using (Timed("expected_compare"))
            {
                using Mat mask = new Mat(h, w, MatType.CV_8UC1, Scalar.All(255));
                var (x1, y1, x2, y2) = Geometry.ContentBox(w, h);
                mask[new Rect(x1, y1, x2 - x1, y2 - y1)].SetTo(0);
                using (Mat withFixed = gs.WithFixedUi(ui.Small, uiMoving.Rows, uiMoving.Cols))
                using (Mat uf = gs.UiFull(withFixed, w, h))
                    Cv2.BitwiseOr(mask, uf, mask);
                comparison = FastCompare.Compare(before, image, (vector[0], vector[1]), mask);
            }
            string answer = comparison.Answer;
            double? s0 = comparison.Scores is { Length: > 0 } sc ? sc[0] : null;
            if (answer == "맞음" && s0 is not null && s0 < FAST_MIN_SCORE)
            {
                answer = "모름"; Inc("weak_fast_ok");
                Log.Add($"  끌기 {moves}: 빠른 확인 '맞음'이지만 점수 {s0:F3}가 낮아(잡음) '모름'으로 봄");
            }
            bool slow = answer == "다름";
            var rebound = new JsonObject
            {
                ["detected"] = false, ["speed_px_s"] = null, ["predictions"] = new JsonArray(), ["actual_reverse_mouse_px"] = new JsonArray(),
                ["reverse_vectors"] = new JsonArray(), ["reverse_count"] = 0, ["end_pos"] = null, ["wait_fallback"] = false, ["wall_memory"] = null,
            };
            bool reboundDetected = false, reboundStill = false;
            bool needRebound = slow || answer != "맞음" || (!limits.Positions.ContainsKey((axis, sign)) && LeadingVoid(image, axis, sign));
            if (observeRebound && needRebound)
            {
                Window? window;
                using (Timed("rebound_check")) (image, window) = ReboundWindow(image, axis, sign, stamp0: grabStamp);
                if (window is not null && window.Moving)
                {
                    rebound["detected"] = true; rebound["speed_px_s"] = window.SpeedPxS; reboundDetected = true;
                    slow = true;
                    using (Timed("reverse_rebound")) (image, reboundStill) = ReverseRebound(image, axis, sign, window, rebound);
                }
            }
            bool probeArrived = false; double? probeD = null;
            if (answer == "모름" && !slow)
            {
                (double? D, double? Score, double? Second, bool Ambiguous) probe;
                using (Timed("edge_probe")) probe = AxisScan(before, image, axis, sign, Math.Abs(vector[axis]) + 100);
                rebound["probe"] = ScanJson(probe);
                Inc("edge_probes");
                if (probe.D is not null)
                {
                    probeD = probe.D;
                    bool crossOk = Math.Abs(vector[1 - axis]) <= Py.Max(ARRIVE_PX, .12 * Math.Abs(vector[axis]));
                    if (crossOk && Math.Abs(probeD.Value - Math.Abs(vector[axis])) <= Py.Max(ARRIVE_PX, .12 * Math.Abs(vector[axis])))
                    {
                        probeArrived = true;
                        Inc("edge_probe_arrived");
                    }
                    else
                    {
                        slow = true;
                        Inc("edge_probe_slow");
                        Log.Add($"  끌기 {moves}: '모름'이라 재 보니 끈 쪽으로 {probeD:F0}px(끈 양 {Math.Abs(vector[axis]):F0}px) → 천천히 잼");
                    }
                }
            }
            double[] p = (double[])prediction.Clone();
            if (probeArrived) p[axis] = o[axis] + sign * probeD!.Value;
            bool g = guessed || (answer == "모름" && !probeArrived);
            (double X, double Y)? measured = null; bool wall = false; (double X, double Y)? absolute = null; bool settled = true; bool recoveredNow = false;
            double slowStarted = 0;
            if (slow)
            {
                double slowBegan = time.PerfCounter();
                (double X, double Y) sv;
                if (reboundStill) { sv = (0.0, 0.0); settled = true; }
                else (image, sv, settled) = SettleAfterDrag(image, vector[0], vector[1]);
                double settleLimit = time.Monotonic() + SETTLE_MAX;
                while (!settled && settleStatus == "blind" && time.Monotonic() < settleLimit)
                {
                    Check();
                    time.Sleep(.5);
                    image = Grab();
                    (double X, double Y) more;
                    (image, more, settled) = SettleAfterDrag(image, vector[0], vector[1]);
                    sv = (sv.X + more.X, sv.Y + more.Y);
                }
                if (!settled) Log.Add($"  끌기 {moves}: 멈춤을 확인하지 못한 채 계속");
                AddTiming("slow_settle_wait", time.PerfCounter() - slowBegan);
                slowStarted = time.Monotonic();
                measured = CV.ViewShift(ui, before, image, vector[0], vector[1]);
                absolute = AbsolutePosition(image, T(prediction), slowStarted + anchorBudget);
                (double? D, double? Score, double? Second, bool Ambiguous)? scan = null;
                if (absolute is null && measured is null)
                {
                    using (Timed("axis_scan")) scan = AxisScan(before, image, axis, sign, Math.Abs(vector[axis]) + 100);
                    rebound["axis_scan"] = ScanJson(scan.Value);
                    Inc("axis_scans");
                    if (scan.Value.D is not null)
                    {
                        double[] m = [0.0, 0.0]; m[axis] = sign * scan.Value.D.Value; measured = T(m);
                        Inc("axis_scan_ambiguous", scan.Value.Ambiguous ? 1 : 0);
                    }
                    else lengthUnknownDrags.Add(moves);
                }
                if (absolute is (double ax, double ay))
                {
                    p = [ax, ay];
                    g = false;
                    var basis = measured is (double mx, double my) ? (o[0] + mx, o[1] + my) : T(prediction);
                    topology.Correct((ax - basis.Item1, ay - basis.Item2));
                    topology.ConfirmOutside((ax, ay));
                }
                else if (measured is (double mx, double my))
                {
                    p = [o[0] + mx, o[1] + my];
                    bool near = settled && Py.Dist(T(p), T(prediction)) <= Py.Max(ARRIVE_PX, .12 * Py.Hypot(vector[0], vector[1]));
                    g = (reboundDetected || near) ? guessed : true;
                    if (scan is not null && scan.Value.Ambiguous) g = true;
                }
                else g = true;
                if (measured is (double mx2, double my2))
                {
                    double[] mv = [mx2, my2];
                    double shortBy = sign * (prediction[axis] - p[axis]);
                    double[] svv = V(sv);
                    bool bounced = sign * svv[axis] < -BOUNCE_PX;
                    wall = bounced && shortBy > Py.Max(ARRIVE_PX, .12 * Py.Hypot(vector[0], vector[1]));
                    if (!reboundDetected && Math.Abs(mv[axis]) >= NO_MOVE_PX)
                        limits.Observe(old, T(vector), new WallVerdict(!g, wall, T(p)), true);
                    ignored = Py.Hypot(mx2, my2) < NO_MOVE_PX && !wall ? ignored + 1 : 0;
                    if (shortBy > Py.Max(ARRIVE_PX, .12 * Py.Hypot(vector[0], vector[1])))
                        topology.RawTouches[key] = TouchesGet(key) + 1;
                }
                else ignored = 0;
                if (reboundDetected && (measured is not null || absolute is not null)) rebound["end_pos"] = Arr(p);
                if (reboundDetected && settled && (measured is not null || absolute is not null))
                {
                    wall = true;
                    limits.Positions[(axis, sign)] = p[axis];
                    if (g) guessedWalls.Add((axis, sign));
                    else guessedWalls.Remove((axis, sign));
                    limits.Candidates.Remove((axis, sign));
                    rebound["end_pos"] = Arr(p);
                    rebound["wall_memory"] = p[axis];
                }
                topology.Rebuild();
                target = topology.Canonical(target);
                key = (topology.Canonical(cell), axis, sign);
            }
            else ignored = 0;
            double slowExtra = slow ? time.Monotonic() - slowStarted : 0.0;
            if (g && !slow && absolute is null)
            {
                (double X, double Y)? recovered;
                using (Timed("recover_position")) recovered = TryRecover(image, T(p));
                if (recovered is (double rx, double ry))
                {
                    var shift = (rx - p[0], ry - p[1]);
                    double[] sh = [shift.Item1, shift.Item2];
                    foreach (var side in guessedWalls)
                        if (limits.Positions.ContainsKey(side)) limits.Positions[side] += sh[side.Axis];
                    var movedWalls = guessedWalls.OrderBy(s => s.Axis).ThenBy(s => s.Sign).ToList();
                    guessedWalls.Clear();
                    topology.Correct(shift);
                    topology.ConfirmOutside((rx, ry));
                    Log.Add($"  끌기 {moves}: 기준 사진으로 자리를 찾아 추정을 풀었음(앱 자리와 {shift.Item1:+0;-0;+0},{shift.Item2:+0;-0;+0}px"
                            + (movedWalls.Count > 0 ? $", 벽 기억 {movedWalls.Count}곳 같이 옮김" : "") + ")");
                    Inc("recovered");
                    p = [rx, ry]; g = false; absolute = (rx, ry);
                    topology.Rebuild();
                }
            }
            if (slow) AddTiming("slow_position_match", slowExtra);
            if (slow || answer != "맞음" || moves % FAST_ZOOM_EVERY == 0 || zoomUnknownStreak != 0)
            {
                using (Timed("zoom_check")) (image, _) = ObserveZoom(image, mo.Features(before));
            }
            stImage = image; pos = T(p); guessed = g;
            var row = new JsonObject
            {
                ["n"] = moves, ["kind"] = "fast", ["action"] = wall ? "wall" : g ? "unknown" : "visit",
                ["answer"] = answer, ["predicted"] = Arr(prediction), ["guessed"] = g, ["slow"] = slow,
                ["absolute"] = ArrOrNull(absolute), ["slow_extra_seconds"] = slowExtra, ["settled"] = settled,
                ["measured"] = ArrOrNull(measured), ["pos"] = Arr(p), ["vector"] = Arr(vector), ["seconds"] = time.Monotonic() - t0,
                ["confirmed"] = !g, ["k"] = 0, ["label"] = answer, ["row"] = null, ["rebound"] = rebound,
            };
            AddRow(row);
            trace?.Invoke(vector[0], vector[1], measured ?? T(vector), g ? "unknown" : "fast");
            if (ignored >= 3) throw new CaptureHalt("끌기가 연속 3번 먹지 않아 멈췄어요.");
            bool shortMove = measured is not null && sign * (prediction[axis] - p[axis]) > Py.Max(ARRIVE_PX, .12 * Py.Hypot(vector[0], vector[1]));
            if (measured is (double qx, double qy) && Py.Hypot(qx, qy) < NO_MOVE_PX && !wall)
            {
                Replan(image);
                return false;
            }
            if (shortMove)
            {
                if (capture)
                {
                    Cell current = NearestCell(pos);
                    Visit(current, image, answer, T(prediction));
                }
                topology.RawBlocked.Add(target);
                topology.Rebuild();
                Replan(image);
                return false;
            }
            if (slow || intermediate || recoveredNow)
            {
                Cell current = NearestCell(pos);
                if (capture || !InCells(current)) Visit(current, image, answer, T(prediction));
                Replan(image);
                return false;
            }
            cur = target;
            string kind = capture || !InCells(target) ? "drag_with_photo" : "drag_without_photo";
            AddTiming(kind, time.Monotonic() - t0);
            using (Timed("save_queue_and_ui"))
                if (capture || !InCells(target)) Visit(target, image, answer, T(prediction));
            return true;
        }
        catch (FocusPause)
        {
            Interrupted("interrupted", screen.DragStart, count); // py: getattr(screen, "drag_start", (0,0))
            Mat fresh = FreshAfterPause();
            var measured = CV.ViewShift(ui, before, fresh, vector[0], vector[1]) ?? throw new CaptureHalt("전면 복귀 뒤 위치를 확인하지 못해 멈췄어요.");
            stImage = fresh; pos = (old.X + measured.X, old.Y + measured.Y);
            return false;
        }
    }

    /// <summary>py: result(state) → (state, st, log, grid). st·grid는 파이썬과 같은 열쇠의 JSON</summary>
    (string State, JsonObject St, List<string> Log, JsonObject Grid) Result(string state)
    {
        var frontier = new HashSet<Cell>();
        foreach (Cell c in topology.Cells.Keys.ToList())
            for (int d = 0; d < 4; d++) { var (ok, nb) = OpenSide(c, d); if (ok) frontier.Add(nb); }
        foreach (var (row, sample) in calibRows) row["sample"] = sample.ToJson();
        var wallLimits = new JsonObject();
        foreach (var kv in limits.Positions) wallLimits[$"{kv.Key.Axis}:{kv.Key.Sign}"] = kv.Value;
        int guessedPhotos = tiles.Values.Count(t => t["guessed"]!.GetValue<bool>());
        var timingJson = new JsonObject();
        foreach (var kv in timings) timingJson[kv.Key] = new JsonObject { ["calls"] = kv.Value.Calls, ["seconds"] = kv.Value.Seconds };
        var diagnostics = new JsonObject
        {
            ["blocked_cells"] = topology.Blocked.Count, ["broken_routes"] = topology.Broken.Count, ["guessed_photos"] = guessedPhotos,
            ["merged_cells"] = topology.Visits.Count - topology.Cells.Count, ["ghost_cells_removed"] = topology.Ghosts,
            ["wall_limits"] = wallLimits.DeepClone(), ["no_drag_arrivals"] = noDragArrivals, ["zoom_checks"] = zoomChecks,
            ["reverse_drags"] = reverseDrags, ["second_reverse_drags"] = secondReverseDrags, ["rebound_wait_fallbacks"] = reboundWaitFallbacks,
            ["axis_scans"] = Count("axis_scans"), ["axis_scan_ambiguous"] = Count("axis_scan_ambiguous"),
            ["length_unknown_drags"] = new JsonArray(lengthUnknownDrags.Select(v => (JsonNode?)v).ToArray()),
            ["recovered"] = Count("recovered"), ["recover_tries"] = Count("recover_tries"), ["recover_rejected"] = Count("recover_rejected"),
            ["recover_seconds"] = Py.Round(recoverSeconds, 2),
            ["edge_probes"] = Count("edge_probes"), ["edge_probe_arrived"] = Count("edge_probe_arrived"), ["edge_probe_slow"] = Count("edge_probe_slow"),
            ["rebound_slides"] = Count("rebound_slides"), ["settle_slides"] = Count("settle_slides"), ["slides"] = Count("slides"),
            ["weak_fast_ok"] = Count("weak_fast_ok"), ["merge_tolerance"] = Arr(topology.Tolerance),
            ["remaining_cells"] = frontier.Count, ["coverage_complete"] = false, ["coverage_scope"] = "known frontier only; unobserved terrain unknown",
        };
        if (state == "done" && (topology.Blocked.Count > 0 || topology.Broken.Count > 0 || guessedPhotos > 0 || frontier.Count > 0)) state = "partial";
        diagnostics["state"] = state;
        var st = new JsonObject
        {
            ["pos"] = Arr(pos), ["cur"] = new JsonArray(cur.X, cur.Y), ["saved"] = saved, ["skipped"] = skipped, ["moves"] = moves,
            ["calib_moves"] = calibMoves, ["backtracks"] = backtracks, ["guessed"] = guessed, ["unplaced"] = unplaced, ["k"] = k,
            ["lk_ambiguous"] = lkAmbiguous, ["drag_lk_ambiguous"] = dragLkAmbiguous, ["capped_checks"] = cappedChecks,
            ["capped_confirmed"] = cappedConfirmed, ["resettled"] = resettled, ["zoom_unknown"] = zoomUnknown,
            ["zoom_unknown_streak"] = zoomUnknownStreak, ["timings"] = timingJson,
            ["settle_status"] = settleStatus, ["settle_confirmation"] = settleConfirmation?.DeepClone(),
            ["cells"] = topology.Cells.Count,
            ["blocked"] = new JsonArray(topology.Blocked.OrderBy(c => c).Select(c => (JsonNode?)new JsonArray(c.X, c.Y)).ToArray()),
            ["temp_cells"] = new JsonArray(), ["stuck_routes"] = new JsonArray(),
            ["rows"] = new JsonArray(rows.Select(r => (JsonNode?)r.DeepClone()).ToArray()),
            ["remaining_cells_scope"] = "known frontier; total unknown",
            ["capture_seconds"] = time.Monotonic() - began,
        };
        if (haltReason is not null) st["halt_reason"] = haltReason;
        foreach (var kv in diagnostics) st[kv.Key] = kv.Value?.DeepClone();
        Log.Add("fast diagnostics: " + diagnostics.ToJsonString(new System.Text.Json.JsonSerializerOptions
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Log.Insert(0, $"fast {state}: 저장 {saved}, 끌기 {moves}, 남은 갈 곳 {frontier.Count}칸(미탐색 총수 미상)");
        var brightness = model.Brightness is { } b
            ? new JsonObject { ["strength"] = b.Strength, ["reason"] = b.Reason, ["samples"] = b.Samples, ["raw_log_error"] = b.RawLogError, ["corrected_log_error"] = b.CorrectedLogError }
            : new JsonObject();
        var mapModel = new JsonObject
        {
            ["g"] = model.G, ["d0"] = model.D0,
            ["calibration"] = new JsonArray(model.Calibration.Select(s => (JsonNode?)s.ToJson()).ToArray()),
            ["brightness"] = brightness,
            ["zoom_diagnostics"] = new JsonArray(model.ZoomDiagnostics.Select(z => (JsonNode?)z.DeepClone()).ToArray()),
        };
        if (model.Step is { } ms) mapModel["step"] = Arr(ms);
        var tileJson = new JsonObject();
        foreach (var kv in tiles) tileJson[kv.Key] = kv.Value.DeepClone();
        var grid = new JsonObject
        {
            ["version"] = 2, ["screen"] = new JsonArray(w, h), ["step"] = new JsonArray(sx, sy),
            ["drag_model"] = new JsonObject { ["scale"] = model.G, ["loss"] = (model.G ?? 0) * mo.D0 },
            ["map_model"] = mapModel, ["tiles"] = tileJson, ["capture_mode"] = "fast", ["remaining_cells"] = frontier.Count,
            ["diagnostics"] = diagnostics.DeepClone(),
        };
        return (state, st, Log, grid);
    }

    /// <summary>py: run_capture_fast 본문 — 시작 보정 뒤 지그재그로 찍고, 막히면 가장 가까운 열린 칸으로 되돌아간다</summary>
    public (string State, JsonObject St, List<string> Log, JsonObject Grid) Run()
    {
        string state = "error";
        try
        {
            stImage = IdleGrab();
            if (CV.BrightRatio(ui, stImage) < CV.VOID_MAX) return Result("void_start");
            Calibrate();
            Visit(new Cell(0, 0), stImage);
            int hdir = 1, vdir = 1;
            while (true)
            {
                Check();
                if (topology.Cells.Count >= maxCells) { state = "limit"; break; }
                Cell c = cur;
                var (ok, nb) = OpenSide(c, hdir > 0 ? 0 : 1);
                if (ok) { Step(nb, true); continue; }
                (ok, nb) = OpenSide(c, vdir > 0 ? 2 : 3);
                if (ok)
                {
                    if (Step(nb, true)) hdir = -hdir;
                    continue;
                }
                var fr = CellSearch.NearestFrontier(topology, cur);
                if (fr is null) { state = topology.Broken.Count > 0 ? "partial" : "done"; break; }
                var (path, target, d) = fr.Value;
                backtracks++;
                Log.Add($"되돌아가기 {backtracks}: {c} → {target}");
                bool broke = false;
                foreach (Cell cell in path) if (!Step(cell, false)) { broke = true; break; }
                if (!broke && Step(target, true))
                {
                    if (d <= 1) hdir = DIRS[d].Dx;
                    else vdir = DIRS[d].Dy;
                }
            }
        }
        catch (LimitReached e) { state = "partial"; Log.Add(e.Message); }
        catch (ZoomChanged) { state = "zoom_changed"; }
        catch (CalibrationFailed) { state = "calibration_failed"; }
        catch (Stopped) { state = time.Monotonic() - began >= FAST_CAPTURE_SECONDS ? "partial" : "aborted"; }
        catch (BlackScreen) { state = "black"; }
        catch (CaptureHalt e) { state = "halted"; haltReason = e.Message; Log.Add(e.Message); }
        catch (Exception e) { state = "error"; Log.Add($"{e.GetType().Name}({e.Message})"); LastError = e.ToString(); }
        finally
        {
            if (screen.IsReal)
            {
                screen.ReleaseOwn();
                screen.IndicateFinished();
            }
            try { storeTail.Wait(); } catch (Exception) { } // py: uploads.shutdown(wait=True)
            try { Directory.Delete(archive, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (var t in pending) if (t.IsFaulted) throw t.Exception!.InnerException!; // py: for future in pending: future.result()
        return Result(state);
    }
}
