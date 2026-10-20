// MeasureFont.cs — does the UI font change the sizes the layout snapshot records?
//
// The snapshot stores the measured width and height of every AutoSize control.
// Those come from text measurement, so they change when the font changes. That
// makes the baseline machine-dependent, which is the hypothesis for why CI
// fails while the author's machine passes.
//
// This measures the actual PageHead strings in each candidate face and prints
// the numbers. If they differ, the hypothesis is confirmed and the snapshot has
// to stop recording font-dependent measurements.
//
// Build: tools\MeasureFont.bat
using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;

static class MeasureFont
{
    static readonly string[] Samples = new string[]
    {
        "涂掉污点、水印、划痕，再点【开始修复】",
        "框选范围裁剪；拍歪的用四点透视拉正",
        "90° 快转、任意角度纠偏、镜像翻转",
        "一键处理整个文件夹：方向 / 去污 / ",
        "接上扫描仪直接扫进程序，存到指定文件夹"
    };

    static readonly string[] Faces = new string[]
    {
        "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI"
    };

    static void Main()
    {
        System.Text.StringBuilder o = new System.Text.StringBuilder();

        o.AppendLine("=== which UI candidate faces are installed ===");
        string[] installed;
        using (InstalledFontCollection col = new InstalledFontCollection())
        {
            FontFamily[] fams = col.Families;
            installed = new string[fams.Length];
            for (int i = 0; i < fams.Length; i++) installed[i] = fams[i].Name;
        }
        foreach (string f in Faces)
        {
            bool has = Array.IndexOf(installed, f) >= 0;
            o.AppendLine("  " + f.PadRight(22) + (has ? "installed" : "MISSING"));
        }
        int cjk = 0;
        foreach (string n in installed)
            if (n.IndexOf("YaHei", StringComparison.OrdinalIgnoreCase) >= 0
             || n.IndexOf("SimSun", StringComparison.OrdinalIgnoreCase) >= 0
             || n.IndexOf("SimHei", StringComparison.OrdinalIgnoreCase) >= 0
             || n.IndexOf("Noto", StringComparison.OrdinalIgnoreCase) >= 0) cjk++;
        o.AppendLine("  CJK-capable faces found: " + cjk);

        o.AppendLine();
        o.AppendLine("=== measured size at 8.25pt (Theme.F11) ===");
        // Same construction Theme uses: new Font(face, 8.25f)
        using (Bitmap bmp = new Bitmap(1, 1))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            string hdr = "  " + "text".PadRight(12);
            foreach (string f in Faces) hdr += f.PadRight(26);
            o.AppendLine(hdr);

            foreach (string s in Samples)
            {
                string row = "  " + s.Substring(0, Math.Min(8, s.Length)).PadRight(12);
                foreach (string f in Faces)
                {
                    try
                    {
                        using (Font font = new Font(f, 8.25f))
                        {
                            SizeF m = g.MeasureString(s, font);
                            row += string.Format("{0}x{1} h={2}", Math.Round(m.Width), Math.Round(m.Height), font.Height).PadRight(26);
                        }
                    }
                    catch { row += "(failed)".PadRight(26); }
                }
                o.AppendLine(row);
            }
        }

        o.AppendLine();
        o.AppendLine("=== the number the snapshot records as height=NN ===");
        foreach (string f in Faces)
        {
            try
            {
                using (Font font = new Font(f, 8.25f))
                    o.AppendLine("  " + f.PadRight(22) + "Font.Height = " + font.Height);
            }
            catch { o.AppendLine("  " + f.PadRight(22) + "(failed)"); }
        }

        o.AppendLine();
        o.AppendLine("If the heights differ between faces, the baseline is machine-dependent");
        o.AppendLine("and the layout gate can only work on machines with the same fonts.");

        string outp = Path.Combine(Path.GetTempPath(), "restoria-font-measure.txt");
        File.WriteAllText(outp, o.ToString(), System.Text.Encoding.UTF8);
        Console.WriteLine("written: " + outp);
        Console.WriteLine(o.ToString());
    }
}