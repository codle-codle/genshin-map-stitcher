using System.Globalization;
using System.Numerics;

namespace GenshinMap.Core;

/// <summary>
/// 파이썬·NumPy와 답이 같아야 하는 숫자 도우미.
/// C# 기본 연산자를 그대로 쓰면 음수·딱 절반에서 답이 갈린다. 옮긴 코드에서는 이 함수만 쓴다.
/// 개발할 때 파이썬이 만든 정답표와 비트까지 견줬다.
/// </summary>
public static class Py
{
    // ---- a // b (정수): 파이썬은 아래로 내림, C# '/'는 0 쪽으로 자른다. -7 // 2 = -4 (C# -3)
    public static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if (a % b != 0 && ((a < 0) != (b < 0))) q--;
        return q;
    }

    public static long FloorDiv(long a, long b)
    {
        long q = a / b;
        if (a % b != 0 && ((a < 0) != (b < 0))) q--;
        return q;
    }

    // ---- a % b (정수): 파이썬은 나누는 수의 부호를 따른다. -3 % 8 = 5 (C# -3)
    public static int Mod(int a, int b)
    {
        if (b == -1) return 0; // C#은 int.MinValue % -1에서 넘침 예외, 파이썬은 0(정답표가 잡음)
        int r = a % b;
        if (r != 0 && ((r < 0) != (b < 0))) r += b;
        return r;
    }

    public static long Mod(long a, long b)
    {
        if (b == -1) return 0;
        long r = a % b;
        if (r != 0 && ((r < 0) != (b < 0))) r += b;
        return r;
    }

    // ---- a // b, a % b (실수): CPython floatobject.c _float_div_mod 그대로
    static void FloatDivMod(double vx, double wx, out double floordiv, out double mod)
    {
        if (wx == 0.0) throw new DivideByZeroException("float floor division by zero");
        mod = vx % wx; // C# double '%'는 C fmod와 같다(0 쪽 자름)
        double div = (vx - mod) / wx;
        if (mod != 0.0)
        {
            if ((wx < 0) != (mod < 0))
            {
                mod += wx;
                div -= 1.0;
            }
        }
        else
        {
            mod = Math.CopySign(0.0, wx);
        }
        if (div != 0.0)
        {
            floordiv = Math.Floor(div);
            if (div - floordiv > 0.5) floordiv += 1.0;
        }
        else
        {
            floordiv = Math.CopySign(0.0, vx / wx);
        }
    }

    public static double FloorDiv(double a, double b)
    {
        FloatDivMod(a, b, out double q, out _);
        return q;
    }

    public static double Mod(double a, double b)
    {
        FloatDivMod(a, b, out _, out double r);
        return r;
    }

    // ---- round(x): 딱 절반이면 짝수 쪽(2.5 → 2, -2.5 → -2), 정수를 돌려준다. np.rint도 같다.
    public static long RoundEven(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x))
            throw new OverflowException($"cannot convert float {x} to integer");
        return checked((long)Math.Round(x, MidpointRounding.ToEven));
    }

    public static int RoundEvenInt(double x) => checked((int)RoundEven(x));

    /// <summary>np.rint: 실수로 돌려주는 짝수 쪽 반올림(-0.4 → -0.0).</summary>
    public static double Rint(double x) => Math.Round(x, MidpointRounding.ToEven);

    // ---- round(x, n): 파이썬은 x의 '정확한 2진 값'을 소수 n자리로 반올림한다(딱 절반만 짝수 쪽).
    // round(2.675, 2) = 2.67 (2.675는 실제로 2.67499…라서). C# Math.Round(x, n)은 2.68을 줄 수 있어 쓰지 않는다.
    public static double Round(double x, int ndigits)
    {
        if (ndigits < 0) throw new NotSupportedException("음수 자릿수는 옮긴 코드에 없다");
        if (ndigits > 323 || double.IsNaN(x) || double.IsInfinity(x) || x == 0.0) return x;

        long bits = BitConverter.DoubleToInt64Bits(x);
        bool neg = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long frac = bits & 0xFFFFFFFFFFFFFL;
        long mant;
        if (exp == 0) { mant = frac; exp = -1074; }           // 비정규 수
        else { mant = frac | (1L << 52); exp -= 1075; }       // x = mant * 2^exp
        if (exp >= 0) return x;                               // 정수라 바뀌지 않는다

        BigInteger num = new BigInteger(mant) * BigInteger.Pow(10, ndigits);
        BigInteger den = BigInteger.One << (-exp);
        BigInteger q = BigInteger.DivRem(num, den, out BigInteger r);
        int cmp = (r * 2).CompareTo(den);
        if (cmp > 0 || (cmp == 0 && !q.IsEven)) q += 1;

        string s = (neg ? "-" : "") + q.ToString(CultureInfo.InvariantCulture) + "E-" + ndigits.ToString(CultureInfo.InvariantCulture);
        double y = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); // .NET Core 3.0+는 바르게 반올림해 읽는다
        return q.IsZero ? Math.CopySign(0.0, x) : y;
    }

    // ---- math.floor / math.ceil: 파이썬은 정수를 돌려준다. (int)x는 floor가 아니다(음수).
    public static long FloorToLong(double x) => checked((long)Math.Floor(x));
    public static long CeilToLong(double x) => checked((long)Math.Ceiling(x));

    // ---- max(a, b) / min(a, b) (실수 둘): 파이썬은 뒤가 '더 클(작을) 때만' 뒤를 준다. NaN이 섞이면 Math.Max는 NaN, 파이썬은 앞의 값.
    public static double Max(double a, double b) => b > a ? b : a;
    public static double Min(double a, double b) => b < a ? b : a;

    // ---- math.hypot / math.dist: CPython 3.13 vector_norm 그대로(FMA로 오차 없이 제곱하고 마지막에 한 번 고친다).
    // Math.Sqrt(x*x+y*y)는 끝자리가 다를 수 있다. 문턱 비교(도착·벽·150px)에 쓰이니 비트까지 맞춘다(5-1, 정답표로 확인).
    static readonly double DBL_MIN = BitConverter.Int64BitsToDouble(0x0010000000000000L);

    static double VectorNorm(Span<double> vec, double max, bool foundNan)
    {
        if (double.IsInfinity(max)) return max;
        if (foundNan) return double.NaN;
        if (max == 0.0 || vec.Length <= 1) return max;
        int maxE = Math.ILogB(max) + 1; // frexp(max, &max_e)
        if (maxE < -1023)
        {
            for (int i = 0; i < vec.Length; i++) vec[i] /= DBL_MIN;
            return DBL_MIN * VectorNorm(vec, max / DBL_MIN, foundNan);
        }
        double scale = Math.ScaleB(1.0, -maxE);
        double csum = 1.0, frac1 = 0.0, frac2 = 0.0;
        double prHi, prLo, smHi, smLo, z, x;
        for (int i = 0; i < vec.Length; i++)
        {
            x = vec[i] * scale;
            prHi = x * x; prLo = Math.FusedMultiplyAdd(x, x, -prHi);
            smHi = csum + prHi; z = smHi - csum; smLo = prHi - z;
            csum = smHi; frac1 += prLo; frac2 += smLo;
        }
        double h = Math.Sqrt(csum - 1.0 + (frac1 + frac2));
        prHi = -h * h; prLo = Math.FusedMultiplyAdd(-h, h, -prHi);
        smHi = csum + prHi; z = smHi - csum; smLo = prHi - z;
        csum = smHi; frac1 += prLo; frac2 += smLo;
        x = csum - 1.0 + (frac1 + frac2);
        h += x / (2.0 * h);
        return h / scale;
    }

    public static double Hypot(double x, double y)
    {
        Span<double> v = stackalloc double[2];
        v[0] = Math.Abs(x); v[1] = Math.Abs(y);
        bool nan = double.IsNaN(v[0]) || double.IsNaN(v[1]);
        double max = 0.0;
        if (v[0] > max) max = v[0];
        if (v[1] > max) max = v[1];
        return VectorNorm(v, max, nan);
    }

    public static double Dist((double X, double Y) p, (double X, double Y) q) => Hypot(p.X - q.X, p.Y - q.Y);

    // ---- 파이썬 자르기 a[lo:hi]의 실제 범위(음수는 끝에서 셈, 넘치면 잘림). 돌려주는 hi는 lo 이상.
    public static (int Lo, int Hi) Slice(int lo, int hi, int n)
    {
        if (lo < 0) { lo += n; if (lo < 0) lo = 0; } else if (lo > n) lo = n;
        if (hi < 0) { hi += n; if (hi < 0) hi = 0; } else if (hi > n) hi = n;
        return (lo, Math.Max(lo, hi));
    }

    // ---- NumPy float64 합(np.sum, mean·std 안의 합): DOUBLE_pairwise_sum 그대로(8개 묶음, 128 블록). 5-1에서 순서 확인.
    public static double PairwiseSum(ReadOnlySpan<double> a)
    {
        int n = a.Length;
        if (n < 8)
        {
            double res = 0.0;
            for (int i = 0; i < n; i++) res += a[i];
            return res;
        }
        if (n <= 128)
        {
            double r0 = a[0], r1 = a[1], r2 = a[2], r3 = a[3], r4 = a[4], r5 = a[5], r6 = a[6], r7 = a[7];
            int i = 8;
            for (; i < n - (n % 8); i += 8)
            {
                r0 += a[i]; r1 += a[i + 1]; r2 += a[i + 2]; r3 += a[i + 3];
                r4 += a[i + 4]; r5 += a[i + 5]; r6 += a[i + 6]; r7 += a[i + 7];
            }
            double res = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
            for (; i < n; i++) res += a[i];
            return res;
        }
        int n2 = n / 2;
        n2 -= n2 % 8;
        return PairwiseSum(a[..n2]) + PairwiseSum(a[n2..]);
    }

    /// <summary>NumPy float32 합(np.sum·mean의 합, float32로 쌓음): pairwise를 float32로. 11/15 확인(30/30)</summary>
    public static float PairwiseSumF32(ReadOnlySpan<float> a)
    {
        int n = a.Length;
        if (n < 8)
        {
            float res = 0f;
            for (int i = 0; i < n; i++) res += a[i];
            return res;
        }
        if (n <= 128)
        {
            float r0 = a[0], r1 = a[1], r2 = a[2], r3 = a[3], r4 = a[4], r5 = a[5], r6 = a[6], r7 = a[7];
            int i = 8;
            for (; i < n - (n % 8); i += 8)
            {
                r0 += a[i]; r1 += a[i + 1]; r2 += a[i + 2]; r3 += a[i + 3];
                r4 += a[i + 4]; r5 += a[i + 5]; r6 += a[i + 6]; r7 += a[i + 7];
            }
            float res = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
            for (; i < n; i++) res += a[i];
            return res;
        }
        int n2 = n / 2;
        n2 -= n2 % 8;
        return PairwiseSumF32(a[..n2]) + PairwiseSumF32(a[n2..]);
    }

    /// <summary>np.mean(float32 배열) → float32</summary>
    public static float MeanF32(ReadOnlySpan<float> a) => PairwiseSumF32(a) / a.Length;

    /// <summary>np.sum(float32 배열, dtype=np.float64): 형을 바꾸며 8192개 버퍼로 나눠 pairwise, 덩어리는 차례로 더한다(11/15 확인 60/60)</summary>
    public static double SumF32AsF64(ReadOnlySpan<float> a)
    {
        double acc = 0.0;
        var buf = new double[Math.Min(8192, Math.Max(1, a.Length))];
        for (int s = 0; s < a.Length; s += 8192)
        {
            int m = Math.Min(8192, a.Length - s);
            for (int i = 0; i < m; i++) buf[i] = a[s + i];
            acc += PairwiseSum(buf.AsSpan(0, m));
        }
        return acc;
    }

    /// <summary>np.percentile(float32 배열, q) — 결과도 float32(gamma를 float32로 바꿔 float32로 잇는다, 11/15 확인)</summary>
    public static float PercentileF32(IEnumerable<float> values, double q)
    {
        float[] a = values.ToArray();
        if (a.Length == 0) return float.NaN;
        foreach (float v in a) if (float.IsNaN(v)) return float.NaN;
        Array.Sort(a);
        int n = a.Length;
        double virt = (n - 1) * (q / 100.0);
        double prevF = Math.Floor(virt);
        int prev = (int)Math.Clamp(prevF, 0, n - 1);
        int next = (int)Math.Clamp(prevF + 1, 0, n - 1);
        float t = (float)(virt - prevF);
        float lo = a[prev], hi = a[next];
        float d = hi - lo;
        return t >= 0.5f ? hi - d * (1 - t) : lo + d * t;
    }

    /// <summary>np.std(uint8 2차원 조각): 평균 = 합/개수, 편차 제곱을 줄 순서로 pairwise 합 → /개수 → 제곱근. 빈 조각이면 NaN.
    /// data는 너비 stride의 행 우선 바이트(Mat.GetArray), 범위는 Slice로 이미 정리된 것.</summary>
    public static double StdU8(byte[] data, int stride, int rowLo, int rowHi, int colLo, int colHi)
    {
        int rows = rowHi - rowLo, cols = colHi - colLo, n = rows * cols;
        if (n <= 0) return double.NaN;
        double s = 0.0;
        for (int y = rowLo; y < rowHi; y++)
            for (int x = colLo; x < colHi; x++) s += data[y * stride + x]; // 정수 합이라 순서와 상관없이 정확
        double mean = s / n;
        double[] d = new double[n];
        int k = 0;
        for (int y = rowLo; y < rowHi; y++)
            for (int x = colLo; x < colHi; x++) { double v = data[y * stride + x] - mean; d[k++] = v * v; }
        return Math.Sqrt(PairwiseSum(d) / n);
    }

    // ---- np.median: 짝수 개면 가운데 둘의 평균, NaN이 하나라도 있으면 NaN
    public static double Median(IEnumerable<double> values)
    {
        double[] a = values.ToArray();
        if (a.Length == 0) return double.NaN;
        foreach (double v in a) if (double.IsNaN(v)) return double.NaN;
        Array.Sort(a); // 실수 값만 보므로 안정 정렬이 아니어도 답이 같다
        int n = a.Length;
        if ((n & 1) == 1) return a[n / 2];
        return (a[n / 2 - 1] + a[n / 2]) / 2.0;
    }

    /// <summary>np.median(float32 배열): 평균을 float32로 계산한다.</summary>
    public static float Median(IEnumerable<float> values)
    {
        float[] a = values.ToArray();
        if (a.Length == 0) return float.NaN;
        foreach (float v in a) if (float.IsNaN(v)) return float.NaN;
        Array.Sort(a);
        int n = a.Length;
        if ((n & 1) == 1) return a[n / 2];
        float s = a[n / 2 - 1] + a[n / 2];
        return s / 2f;
    }

    /// <summary>np.nanmedian: NaN을 빼고 가운데 값. 다 NaN이면 NaN.</summary>
    public static double NanMedian(IEnumerable<double> values) => Median(values.Where(v => !double.IsNaN(v)));

    /// <summary>np.percentile(a, q) 기본 방식('linear'). q는 0~100.</summary>
    public static double Percentile(IEnumerable<double> values, double q)
    {
        double[] a = values.ToArray();
        if (a.Length == 0) return double.NaN;
        foreach (double v in a) if (double.IsNaN(v)) return double.NaN;
        Array.Sort(a);
        int n = a.Length;
        double virt = (n - 1) * (q / 100.0);
        double prevF = Math.Floor(virt);
        int prev = (int)Math.Clamp(prevF, 0, n - 1);
        int next = (int)Math.Clamp(prevF + 1, 0, n - 1);
        double t = virt - prevF;
        double lo = a[prev], hi = a[next];
        double d = hi - lo;
        // NumPy _lerp: t >= 0.5면 위쪽에서 뺀다(끝자리까지 같게)
        return t >= 0.5 ? hi - d * (1 - t) : lo + d * t;
    }
}
