using System.Globalization;
using System.Text;
using GenshinMap.Core.Vision;

namespace GenshinMap.Core.Stitch;

/// <summary>붙이기 명령줄. py: grid_stitch.py &lt;사진 폴더&gt; &lt;capture_grid.json&gt; -o stitched.png [--exclude x1,y1,x2,y2]...
/// 앱은 같은 exe를 'stitch' 인자로 자식 프로세스로 띄운다. 출력 줄은 UTF-8.</summary>
public static class StitchCommand
{
    public static int Run(string[] a)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = new UTF8Encoding(false);
        string? input = null, grid = null, output = null;
        var excludes = new List<(int, int, int, int)>();
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == "-o" || a[i] == "--output") output = a[++i];
            else if (a[i] == "--exclude") excludes.Add(Engine.ParseRect(a[++i]));
            else if (input is null) input = a[i];
            else if (grid is null) grid = a[i];
            else { Console.Error.WriteLine($"모르는 인자: {a[i]}"); return 2; }
        }
        if (input is null || grid is null || output is null)
        {
            Console.Error.WriteLine("쓰기: stitch <사진 폴더> <capture_grid.json> -o stitched.png [--exclude x1,y1,x2,y2]...");
            return 2;
        }
        try
        {
            return GridStitch.Main(new GridStitch.Args { InputDir = input, Grid = grid, Output = output, Exclude = excludes });
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[error] {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }
}
