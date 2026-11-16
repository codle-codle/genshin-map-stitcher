using OpenCvSharp;

namespace GenshinMap.Core;

/// <summary>
/// 한글·긴 경로에서 그림 읽고 쓰기. 파일 이름을 OpenCV에 넘기지 않고 바이트로 읽어 풀고, 바이트로 묶어 쓴다.
/// py: 엔진.genshin_map_stitcher.imread_unicode / imwrite_unicode
/// </summary>
public static class ImageIO
{
    /// <summary>Mat의 원시 바이트(채널이 섞인 그대로, 행 우선). GetArray는 채널 수가 맞는 형만 받아서 이걸 쓴다.</summary>
    public static byte[] RawBytes(Mat m)
    {
        using Mat c = m.IsContinuous() ? m.Clone() : m.Clone();
        var b = new byte[c.Total() * c.ElemSize()];
        System.Runtime.InteropServices.Marshal.Copy(c.Data, b, 0, b.Length);
        return b;
    }

    /// <summary>float 채널 그림(CV_32FC1~4)의 값을 채널이 섞인 그대로</summary>
    public static float[] RawFloats(Mat m)
    {
        using Mat c = m.Clone();
        var f = new float[c.Total() * c.Channels()];
        System.Runtime.InteropServices.Marshal.Copy(c.Data, f, 0, f.Length);
        return f;
    }

    public static void SetRaw(Mat m, byte[] b) => System.Runtime.InteropServices.Marshal.Copy(b, 0, m.Data, b.Length);
    public static void SetRaw(Mat m, float[] f) => System.Runtime.InteropServices.Marshal.Copy(f, 0, m.Data, f.Length);

    /// <summary>못 읽으면 null(파이썬 None).</summary>
    public static Mat? ImreadUnicode(string path, ImreadModes mode = ImreadModes.Color)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception exc)
        {
            Console.WriteLine($"[WARN] Cannot read image bytes: {path} ({exc.Message})");
            return null;
        }
        if (data.Length == 0) return null;
        Mat m = Cv2.ImDecode(data, mode);
        if (m.Empty())
        {
            m.Dispose();
            return null;
        }
        return m;
    }

    /// <summary>확장자에 따라 JPEG 품질 95, PNG 압축 3(파이썬과 같은 값). 성공하면 true.</summary>
    public static bool ImwriteUnicode(string path, Mat image)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            ImageEncodingParam[] prms = ext switch
            {
                ".jpg" or ".jpeg" => [new ImageEncodingParam(ImwriteFlags.JpegQuality, 95)],
                ".png" => [new ImageEncodingParam(ImwriteFlags.PngCompression, 3)],
                _ => [],
            };
            if (!Cv2.ImEncode(ext, image, out byte[] buf, prms)) return false;
            File.WriteAllBytes(path, buf);
            return true;
        }
        catch (Exception exc)
        {
            Console.WriteLine($"[WARN] Cannot write image: {path} ({exc.Message})");
            return false;
        }
    }

    /// <summary>cv2.imencode(".png", img, [IMWRITE_PNG_COMPRESSION, level]) 뒤 tofile과 같다. 찍기 사진은 level 1.</summary>
    public static void WritePng(string path, Mat image, int compression)
    {
        if (!Cv2.ImEncode(".png", image, out byte[] buf, [new ImageEncodingParam(ImwriteFlags.PngCompression, compression)]))
            throw new IOException($"PNG 묶기 실패: {path}");
        File.WriteAllBytes(path, buf);
    }
}
