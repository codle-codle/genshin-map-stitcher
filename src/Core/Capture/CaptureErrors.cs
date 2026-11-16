namespace GenshinMap.Core.Capture;

// py: auto_capture의 찍기 흐름 예외(이름 그대로). 찍기 루프는 예외로 흐름을 바꾼다.

/// <summary>py: FocusPause — 원신 창이 전면에서 벗어남(실제 판만)</summary>
public class FocusPause : Exception { public FocusPause(string? m = null) : base(m ?? "focus") { } }

/// <summary>py: CaptureHalt — 더 찍으면 위험해서 멈춤(까닭을 사용자에게 보여 준다)</summary>
public class CaptureHalt : Exception { public CaptureHalt(string m) : base(m) { } }

/// <summary>py: CalibrationFailed(CaptureHalt)</summary>
public class CalibrationFailed : CaptureHalt { public CalibrationFailed() : base("시작 보정 실패") { } }

/// <summary>py: ZoomChanged(CaptureHalt)</summary>
public class ZoomChanged : CaptureHalt { public ZoomChanged() : base("확대가 바뀜") { } }

/// <summary>py: BlackScreen — 진짜 화면이 통째로 새까맣게 찍힘</summary>
public class BlackScreen : Exception { public BlackScreen() : base("검은 화면") { } }

/// <summary>py: LimitReached — 시간·끌기 상한</summary>
public class LimitReached : Exception { public LimitReached(string m) : base(m) { } }

/// <summary>py: Stopped — 사용자가 멈춤(End·Esc·멈춤 단추)</summary>
public class Stopped : Exception { public Stopped(string? m = null) : base(m ?? "멈춤") { } }
