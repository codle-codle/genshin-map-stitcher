using System.Globalization;
using System.Text;
using GenshinMap.Core;
using OpenCvSharp;

namespace GenshinMap.Cli;

static class Program
{
    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = new UTF8Encoding(false);

        if (args.Length == 0) return Usage();
        try
        {
            return args[0] switch
            {
                "info" => Info(),
                "imgcopy" => ImgCopy(args[1..]),
                "stitch" => GenshinMap.Core.Stitch.StitchCommand.Run(args[1..]),
                "trim" => GenshinMap.Core.Trim.EdgeTrimmer.Main(args[1..]),
                "engine" => GenshinMap.Core.Stitch.EngineStitch.Main(args[1..]),
                _ => Usage(),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[error] {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("쓰기: GenshinMapCli info | imgcopy <읽을 그림> <쓸 그림> [--mode color|gray|unchanged] [--png-compression n] | stitch <사진 폴더> <capture_grid.json> -o <결과> | trim <그림> -o <결과> [선택지] | engine <사진 폴더> -o <결과> [선택지]");
        return 2;
    }

    static int Info()
    {
        Console.WriteLine($"opencv {Cv2.GetVersionString()}");
        Console.WriteLine($"dotnet {Environment.Version}");
        Console.WriteLine($"culture {CultureInfo.CurrentCulture.Name}|{(1.5).ToString()}");
        return 0;
    }

    /// <summary>5-0 합격 시험: 그림을 바이트로 읽어 풀고 PNG로 묶어 쓴다. 파이썬 비교기가 화소를 견준다.</summary>
    static int ImgCopy(string[] a)
    {
        if (a.Length < 2) return Usage();
        ImreadModes mode = ImreadModes.Color;
        int? comp = null;
        for (int i = 2; i < a.Length; i++)
        {
            if (a[i] == "--mode")
                mode = a[++i] switch { "gray" => ImreadModes.Grayscale, "unchanged" => ImreadModes.Unchanged, _ => ImreadModes.Color };
            else if (a[i] == "--png-compression")
                comp = int.Parse(a[++i], CultureInfo.InvariantCulture);
        }
        using Mat? img = ImageIO.ImreadUnicode(a[0], mode);
        if (img is null)
        {
            Console.Error.WriteLine($"못 읽음: {a[0]}");
            return 1;
        }
        bool ok;
        if (comp is int c) { ImageIO.WritePng(a[1], img, c); ok = true; }
        else ok = ImageIO.ImwriteUnicode(a[1], img);
        Console.WriteLine($"{(ok ? "ok" : "fail")} {img.Width}x{img.Height} ch={img.Channels()} depth={img.Depth()}");
        return ok ? 0 : 1;
    }
}
