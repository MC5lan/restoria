// SkewProbe.cs — 倾斜检测诊断（CLI: --skew-test <图片或文件夹> [页数]）
//
// 起因：DetectSkewAngle 的"黑边保险"只在**确实有黑边**时才动手，
// 所以它有两个必须被证明的性质：
//   ① 正常页（边框是纸白）零行为变化 —— 否则就是拿全量档案陪绑；
//   ② 有黑边时确实救得回来 —— 否则白加。
//
// 这里就在**真实样本**上把两件事同时量出来：对每页跑 6 个数（原图 / 人为加黑框 /
// 转 -3° 留黑角，各跑"有保险|无保险"两遍），并把结果落一份报告。
// 报告落 %TEMP%\PicTool-skewtest.txt（本进程是 winexe，没有控制台可读）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace PicTool
{
    public static class SkewProbe
    {
        public static string RunAll(string[] args)
        {
            string target = null;
            int maxPages = 12;
            if (args != null && args.Length >= 2) target = args[1];
            if (args != null && args.Length >= 3) { try { maxPages = int.Parse(args[2]); } catch { maxPages = 12; } }

            List<string> files = new List<string>();
            try
            {
                if (target != null && Directory.Exists(target))
                {
                    string[] all = Directory.GetFiles(target);
                    Array.Sort(all);
                    for (int i = 0; i < all.Length && files.Count < maxPages; i++)
                        if (IsImage(all[i])) files.Add(all[i]);
                }
                else if (target != null && File.Exists(target)) files.Add(target);
            }
            catch { }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("倾斜检测诊断 --skew-test（" + AppInfo.Version + "）");
            sb.AppendLine("样本: " + (target == null ? "(未给)" : target) + "   取 " + files.Count + " 页");
            sb.AppendLine();
            sb.AppendLine("每页 6 列：");
            sb.AppendLine("  原图(保险) 原图(无) | 加黑框(保险) 加黑框(无) | 转-3°留黑角(保险) 转-3°留黑角(无)");
            sb.AppendLine("期望：①第1列==第2列（正常页保险不启用，逐位一致）");
            sb.AppendLine("      ②第3列≈第1列，第4列明显偏离（证明保险在起作用）");
            sb.AppendLine("      ③第5列 |角|≈3，第6列偏离（真实内容 + 真实黑角）");
            sb.AppendLine();

            int total = 0, sameNoGuard = 0, framedGuardOk = 0, framedNoGuardBad = 0;
            int rotGuardOk = 0, rotNoGuardOk = 0, rotUsable = 0;
            const double KNOWN = 3.0;

            for (int i = 0; i < files.Count; i++)
            {
                Bitmap src = null, framed = null, rot = null;
                string name = Path.GetFileName(files[i]);
                try
                {
                    using (Bitmap raw = new Bitmap(files[i])) src = Imaging.Ensure24bpp(new Bitmap(raw));

                    double a1 = Enhance.DetectSkewAngle(src, true);
                    double a2 = Enhance.DetectSkewAngle(src, false);

                    framed = AddBlackFrame(src, 14);
                    double b1 = Enhance.DetectSkewAngle(framed, true);
                    double b2 = Enhance.DetectSkewAngle(framed, false);

                    rot = Imaging.RotateFine(src, -KNOWN, false);   // 不改裁 → 真实黑角
                    double c1 = Enhance.DetectSkewAngle(rot, true);
                    double c2 = Enhance.DetectSkewAngle(rot, false);

                    total++;
                    if (Math.Abs(a1 - a2) < 0.001) sameNoGuard++;
                    if (Math.Abs(b1 - a1) < 0.5) framedGuardOk++;
                    if (Math.Abs(b2 - a1) >= 1.0) framedNoGuardBad++;
                    // 这一页内容够不够"能测"：无黑角版（=原图转后）测得出来才算有效样本
                    double usable = Enhance.DetectSkewAngle(Imaging.RotateFine(src, -KNOWN, true), true);
                    if (Math.Abs(Math.Abs(usable) - KNOWN) < 0.9)
                    {
                        rotUsable++;
                        if (Math.Abs(Math.Abs(c1) - KNOWN) < 0.9) rotGuardOk++;
                        if (Math.Abs(Math.Abs(c2) - KNOWN) < 0.9) rotNoGuardOk++;
                    }

                    sb.AppendLine(string.Format("{0,-17} {1,7:0.00} {2,7:0.00} | {3,7:0.00} {4,7:0.00} | {5,7:0.00} {6,7:0.00}",
                        name, a1, a2, b1, b2, c1, c2));
                }
                catch (Exception ex)
                {
                    sb.AppendLine(name + "   失败: " + ex.Message);
                }
                finally
                {
                    if (src != null) src.Dispose();
                    if (framed != null) framed.Dispose();
                    if (rot != null) rot.Dispose();
                }
            }

            sb.AppendLine();
            sb.AppendLine("小结");
            sb.AppendLine("  正常页有/无保险逐位一致        : " + sameNoGuard + " / " + total);
            sb.AppendLine("  加黑框后仍与原图一致(有保险)    : " + framedGuardOk + " / " + total);
            sb.AppendLine("  加黑框后偏离原图(无保险)        : " + framedNoGuardBad + " / " + total);
            sb.AppendLine("  转-3°留黑角 能测对(有保险)     : " + rotGuardOk + " / " + rotUsable + "  (可测页 " + rotUsable + "/" + total + ")");
            sb.AppendLine("  转-3°留黑角 能测对(无保险)     : " + rotNoGuardOk + " / " + rotUsable);

            // 报告正文交回给调用方落盘（Program.cs 统一写 %TEMP%\PicTool-skewtest.txt）
            return sb.ToString();
        }

        static bool IsImage(string p)
        {
            string e = Path.GetExtension(p).ToLowerInvariant();
            return e == ".jpg" || e == ".jpeg" || e == ".png" || e == ".bmp" || e == ".tif" || e == ".tiff";
        }

        // 四周加一圈纯黑（模拟扫描仪黑背景露边 / 黑框）
        static Bitmap AddBlackFrame(Bitmap src, int px)
        {
            Bitmap dst = new Bitmap(src.Width + px * 2, src.Height + px * 2, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.Clear(Color.Black);
                g.DrawImage(src, new Rectangle(px, px, src.Width, src.Height));
            }
            return dst;
        }
    }
}
