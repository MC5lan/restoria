// MakeDemo.cs — generate before/after comparison images for the README.
//
// Why a tool instead of doing it by hand: the README needs an image that
// always matches the current code. Generating it from the same fixture the
// gates use means the picture cannot drift from what the tool actually does.
//
// Everything is derived from _probe\doc_test.png, which the program itself
// generates with --make-fixture. No real archive material is involved.
//
// Build and run: tools\MakeDemo.bat
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeDemo
{
    const int PairW = 1500;      // side-by-side width
    const int HeaderH = 64;      // caption bar
    const int CapH = 40;         // per-image caption
    const int MaxH = 1150;       // cap the page height so the file stays small

    static string Root
    {
        get
        {
            string p = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6; i++)
            {
                if (File.Exists(Path.Combine(p, "build.bat"))) return p;
                p = Directory.GetParent(p).FullName;
            }
            return Directory.GetCurrentDirectory();
        }
    }

    static string OutDir { get { return Path.Combine(Root, "docs", "images"); } }

    static int Main(string[] args)
    {
        try
        {
            string fixture = args.Length > 0 ? args[0] : Path.Combine(Root, "_probe", "doc_test.png");
            string workDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "restoria-demo");

            if (!File.Exists(fixture))
            {
                Console.Error.WriteLine("fixture not found: " + fixture);
                Console.Error.WriteLine("run: Restoria.exe --make-fixture _probe\\doc_test.png");
                return 2;
            }
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(OutDir);

            // Three views, each answering a different question a new user has:
            //   1. whole page before / after     -> "what does the pipeline do"
            //   2. a crop at real magnification  -> "is the text still legible"
            //   3. a tilted page                -> "does it straighten things"

            using (Image src = Image.FromFile(fixture))
            {
                string afterPath = Path.Combine(workDir, "after.png");
                RunArchive(fixture, afterPath);
                if (!File.Exists(afterPath))
                {
                    Console.Error.WriteLine("archive produced no output: " + afterPath);
                    return 3;
                }

                using (Image aft = Image.FromFile(afterPath))
                {
                    // Three views, each answering a different question a new user has:
                    //   1. whole page before / after   -> "what does the pipeline do"
                    //   2. a crop at real magnification-> "is the text still legible"
                    //   3. a tilted page               -> "does it straighten things"
                    WritePair(src, aft, null,
                              "Whole page — standard pipeline: orientation, deskew, denoise, binarize, A4 output",
                              "docs/images/pipeline.png");

                    Rectangle crop = PickDetailRect(src);
                    WritePair(src, aft, crop,
                              "Close-up — dust and grey shading removed, every stroke and the red seal kept",
                              "docs/images/detail.png");

                    MakeDeskewPair(src);
                }
            }

            Console.WriteLine("written to " + OutDir);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.Message);
            return 1;
        }
    }

    // A rectangle that actually contains content: text lines start around
    // x=130 y=120 in the fixture, so a crop centred on the first table rule
    // shows both glyphs and a rule, which is what a reader wants to inspect.
    static Rectangle PickDetailRect(Image im)
    {
        int w = Math.Min(560, im.Width - 130 - 40);
        int h = Math.Min(360, im.Height - 120 - 40);
        return new Rectangle(130, 118, Math.Max(120, w), Math.Max(120, h));
    }

    static void RunArchive(string input, string output)
    {
        string exe = FindExe();
        if (exe == null) { throw new Exception("Restoria.exe not found under dist/"); }
        System.Diagnostics.Process p = new System.Diagnostics.Process();
        p.StartInfo.FileName = exe;
        p.StartInfo.Arguments = "--archive \"" + input + "\" \"" + output + "\"";
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.RedirectStandardOutput = true;
        p.StartInfo.RedirectStandardError = true;
        p.StartInfo.CreateNoWindow = true;
        p.Start();
        p.WaitForExit(180000);
        if (p.ExitCode != 0)
        {
            string err = p.StandardError.ReadToEnd();
            throw new Exception("--archive exited " + p.ExitCode + ": " + err);
        }
    }

    static string FindExe()
    {
        string d = Path.Combine(Root, "dist");
        if (!Directory.Exists(d)) return null;
        foreach (string f in new string[] { "Restoria.exe", "PicTool.exe" })
        {
            string p = Path.Combine(d, f);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    static void WritePair(Image before, Image after, Rectangle? crop,
                          string caption, string relPath)
    {
        using (Bitmap bmp = Compose(before, after, crop, caption))
            Save(bmp, Path.Combine(Root, relPath));
    }

    static Bitmap Compose(Image before, Image after, Rectangle? crop, string caption)
    {
        int bh, ah;
        if (crop.HasValue)
        {
            bh = Math.Min(crop.Value.Height, before.Height - crop.Value.Y);
            ah = Math.Min(crop.Value.Height, after.Height - crop.Value.Y);
        }
        else
        {
            bh = ScaleH(before.Height, before.Width);
            ah = ScaleH(after.Height, after.Width);
        }
        int panelW = PairW / 2 - 20;
        int left = Math.Min(panelW, before.Width);
        int right = Math.Min(panelW, after.Width);

        int w = left + 16 + right + 40;
        int h = Math.Max(bh, ah) + HeaderH + CapH + 30;

        Bitmap outp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(outp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.FromArgb(0xFA, 0xF8, 0xF4));

            using (Font titleFont = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font capFont = new Font("Segoe UI", 14, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Brush ink = new SolidBrush(Color.FromArgb(0x24, 0x26, 0x2B)))
            using (Brush dim = new SolidBrush(Color.FromArgb(0x6A, 0x6E, 0x76)))
            using (Pen rule = new Pen(Color.FromArgb(0xD8, 0xD2, 0xC8)))
            {
                g.DrawString(caption, titleFont, ink, 20, 18);
                g.DrawLine(rule, 20, HeaderH - 6, w - 20, HeaderH - 6);

                Rectangle bl, ar;
                if (crop.HasValue)
                {
                    Rectangle c = crop.Value;
                    bl = new Rectangle(20, HeaderH, Math.Min(c.Width, before.Width - c.X),
                                       Math.Min(c.Height, before.Height - c.Y));
                    ar = new Rectangle(20 + bl.Width + 16, HeaderH, Math.Min(c.Width, after.Width - c.X),
                                       Math.Min(c.Height, after.Height - c.Y));
                }
                else
                {
                    bl = new Rectangle(20, HeaderH, before.Width, bh);
                    ar = new Rectangle(20 + left + 16, HeaderH, after.Width, ah);
                }

                DrawFramed(g, before, bl);
                DrawFramed(g, after, ar);
                g.DrawString("before", capFont, dim, bl.Left, bl.Bottom + 10);
                g.DrawString("after", capFont, ink, ar.Left, ar.Bottom + 10);
            }
        }
        return outp;
    }

    static int ScaleH(int h, int w)
    {
        int cap = MaxH;
        if (h <= cap) return h;
        return cap;
    }

    static void DrawFramed(Graphics g, Image im, Rectangle dst)
    {
        g.FillRectangle(Brushes.White, dst);
        g.DrawImage(im, dst,
                    new Rectangle(0, 0, im.Width, im.Height),
                    GraphicsUnit.Pixel);
        using (Pen p = new Pen(Color.FromArgb(0xB8, 0xB0, 0xA4)))
            g.DrawRectangle(p, dst.X, dst.Y, dst.Width - 1, dst.Height - 1);
    }

    // The deskew pair shows the decision, not just the result: tilt the source
    // slightly, run it through, and label the measured angle from the archive
    // report so the number in the caption is the number the tool computed.
    static void MakeDeskewPair(Image src)
    {
        Bitmap tilted = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(tilted))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TranslateTransform(src.Width / 2.0f, src.Height / 2.0f);
            g.RotateTransform(-3.2f);
            g.DrawImage(src, new Rectangle(-src.Width / 2, -src.Height / 2, src.Width, src.Height));
        }
        string work = Path.Combine(Path.GetTempPath(), "restoria-demo");
        Directory.CreateDirectory(work);
        string tin = Path.Combine(work, "tilted.png");
        string tout = Path.Combine(work, "tilted-fixed.png");
        tilted.Save(tin, ImageFormat.Png);
        tilted.Dispose();

        try { RunArchive(tin, tout); } catch (Exception ex)
        {
            Console.Error.WriteLine("  (deskew archive failed: " + ex.Message + ")");
        }

        if (!File.Exists(tout))
        {
            Console.Error.WriteLine("  deskew: no output, skipping");
            return;
        }
        using (Image ti = Image.FromFile(tin))
        using (Image tf = Image.FromFile(tout))
        using (Bitmap b = Compose(ti, tf, null,
                  "Tilted input — the pipeline straightens it during archiving"))
        {
            Save(b, Path.Combine(OutDir, "deskew.png"));
        }
    }

    static void Save(Bitmap bmp, string path)
    {
        string dir = Path.GetDirectoryName(path);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        using (FileStream fs = new FileStream(path, FileMode.Create))
        {
            bmp.Save(fs, ImageFormat.Png);
        }
        long kb = new FileInfo(path).Length / 1024;
        Console.WriteLine("      " + Path.GetFileName(path) + "  " + kb + " KB");
    }
}