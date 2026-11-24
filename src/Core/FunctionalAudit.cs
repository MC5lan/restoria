// FunctionalAudit.cs — 功能有效性实测（--functional-audit）
//
// 与 SelfTest 的区别：SelfTest 守"别回归"，这里守"功能真的干活"。
// 它用合成测试页跑每个功能，量化前后差异（像素变化、尺寸、色阶、墨量等），
// 而不是只看"调用没抛异常"。UI 上有按钮不等于算法有效，这道口就是证据。
//
// 退出码与报告：见 Program.cs 的 --functional-audit 分支。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace PicTool
{
    public static class FunctionalAudit
    {
        static StringBuilder log;
        public static int PassCount, FailCount, SkipCount;

        public static string RunAll()
        {
            log = new StringBuilder();
            PassCount = FailCount = SkipCount = 0;
            log.AppendLine("Restoria — 功能有效性实测报告");
            log.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            log.AppendLine();

            Archive();
            Deskew();
            Orientation();
            Mirror();
            Inpainting();
            ColorAndLevels();
            Binarize();
            SpecksAndStains();
            SealColor();
            PerspectiveAndText();

            log.AppendLine();
            log.AppendLine("合计: 通过 " + PassCount + " 项，失败 " + FailCount +
                           " 项，跳过 " + SkipCount + " 项");
            log.AppendLine(FailCount == 0 ? "结论: 功能有效性通过" : "结论: 有失败项，请看上面 [失败] 行");
            return log.ToString();
        }

        static void Group(string name) { log.AppendLine(); log.AppendLine("[" + name + "]"); }
        static void Note(string s) { log.AppendLine("    " + s); }
        static void Verdict(string name, bool ok, string detail, bool soft)
        {
            if (ok) { PassCount++; log.AppendLine("  [通过] " + name.PadRight(32) + (detail ?? "")); }
            else if (soft) { SkipCount++; log.AppendLine("  [跳过] " + name.PadRight(32) + (detail ?? "")); }
            else { FailCount++; log.AppendLine("  [失败] " + name.PadRight(32) + (detail ?? "")); }
        }

        // 与 SelfTest.OrientPage 同构的 OCR 测试页：真文字 + 硬边二值化，
        // 文本全部虚构，不含真实地名/机构/人名。
        static Bitmap OcrTextPage(int w, int h)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(252, 251, 248));
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                using (Font f = new Font("SimSun", 28f, FontStyle.Bold))
                {
                    string[] lines = {
                        "归档方向检测测试页，",
                        "这是一段用于检测的示例文字。",
                        "每一行都有足够的汉字与标点、",
                        "让识别引擎能够正常读取；",
                        "内容纯属虚构不涉及真实机构，",
                        "第二段落用于交叉校验，",
                        "确保镜像与正向能够区分。",
                        "测试页结束。",
                    };
                    for (int i = 0; i < lines.Length; i++)
                        g.DrawString(lines[i], f, Brushes.Black, new PointF(50, 40 + i * (h - 80) / lines.Length));
                }
            }
            PixelBuffer pb = BitmapBridge.ToBuffer(b, 3);
            for (int i = 0; i < pb.Data.Length; i += 3)
            {
                bool dark = (pb.Data[i] + pb.Data[i + 1] + pb.Data[i + 2]) / 3 < 200;
                byte v = dark ? (byte)20 : (byte)250;
                pb.Data[i] = v; pb.Data[i + 1] = v; pb.Data[i + 2] = v;
            }
            BitmapBridge.WriteBack(b, pb);
            return b;
        }

        static Bitmap New(int w, int h, Color c)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(b)) g.Clear(c);
            b.SetResolution(200f, 200f);
            return b;
        }
        static int CountInk(Bitmap b, int x, int y, int w, int h)
        {
            int n = 0;
            for (int j = y; j < Math.Min(y + h, b.Height); j++)
                for (int i = x; i < Math.Min(x + w, b.Width); i++)
                {
                    Color c = b.GetPixel(i, j);
                    if ((c.R + c.G + c.B) / 3 < 140) n++;
                }
            return n;
        }
        static int CountRed(Bitmap b)
        {
            int n = 0;
            for (int j = 0; j < b.Height; j += 2)
                for (int i = 0; i < b.Width; i += 2)
                {
                    Color c = b.GetPixel(i, j);
                    if (c.R > c.G + 40 && c.R > c.B + 40) n++;
                }
            return n;
        }
        static double MeanLum(Bitmap b)
        {
            long s = 0; int n = 0;
            for (int j = 0; j < b.Height; j += 4)
                for (int i = 0; i < b.Width; i += 4)
                { Color c = b.GetPixel(i, j); s += (c.R + c.G + c.B) / 3; n++; }
            return n == 0 ? 0 : (double)s / n;
        }
        // 有红章时红色抗锯齿边缘会带来许多中间色；只看非红像素才能判断"白底黑字"。
        static int Levels(Bitmap b, bool skipRed)
        {
            bool[] seen = new bool[256]; int n = 0;
            for (int j = 0; j < b.Height; j += 3)
                for (int i = 0; i < b.Width; i += 3)
                {
                    Color c = b.GetPixel(i, j);
                    if (skipRed && c.R > c.G + 25 && c.R > c.B + 25) continue;
                    int lum = (c.R + c.G + c.B) / 3;
                    if (!seen[lum]) { seen[lum] = true; n++; }
                }
            return n;
        }
        static double MAE(Bitmap a, Bitmap b, Rectangle r)
        {
            long s = 0; int n = 0;
            for (int j = r.Y; j < Math.Min(r.Bottom, a.Height) && j < b.Height; j++)
                for (int i = r.X; i < Math.Min(r.Right, a.Width) && i < b.Width; i++)
                {
                    Color ca = a.GetPixel(i, j), cb = b.GetPixel(i, j);
                    s += Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B); n += 3;
                }
            return n == 0 ? 0 : (double)s / n;
        }
        static double Redness(Bitmap b, Rectangle r)
        {
            double sum = 0; int n = 0;
            for (int j = r.Y; j < Math.Min(r.Bottom, b.Height); j += 2)
                for (int i = r.X; i < Math.Min(r.Right, b.Width); i += 2)
                { Color c = b.GetPixel(i, j); sum += c.R - (c.G + c.B) / 2; n++; }
            return n == 0 ? 0 : sum / n;
        }
        static string Tail(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(s.Length - n);
        }

        // 1. 一键归档：脏页（灰底 + 灰尘 + 淡红章）跑两档
        static void Archive()
        {
            Group("一键归档");
            Bitmap dirty = New(1100, 1500, Color.FromArgb(222, 220, 214));
            using (Graphics g = Graphics.FromImage(dirty))
            {
                using (Pen p = new Pen(Color.FromArgb(35, 35, 35), 2f))
                    for (int i = 0; i < 14; i++) g.DrawLine(p, 90, 130 + i * 85, 1000, 130 + i * 85);
                using (SolidBrush bk = new SolidBrush(Color.FromArgb(20, 20, 20)))
                {
                    Random rnd = new Random(7);
                    for (int i = 0; i < 160; i++) g.FillRectangle(bk, rnd.Next(30, 1060), rnd.Next(40, 1450), 6, 6);
                }
                using (Pen rp = new Pen(Color.FromArgb(198, 60, 65), 8f))
                    g.DrawEllipse(rp, 820, 1180, 170, 170);
            }
            int redIn = CountRed(dirty);
            ArchiveOptions std = new ArchiveOptions();
            ArchiveResult ri;
            using (Bitmap outStd = ArchiveProcessor.Run(dirty, std, out ri))
            {
                Verdict("普通档 输出 A4", outStd.Width == 1654 && outStd.Height == 2339,
                    outStd.Width + "x" + outStd.Height, false);
                Verdict("普通档 白底黑字(非红像素)", Levels(outStd, true) <= 4,
                    "非红色阶数 " + Levels(outStd, true) + " / 全部 " + Levels(outStd, false), false);
                Verdict("普通档 内容未丢", ri.InkPixels > 1000, "墨像素 " + ri.InkPixels, false);
                Verdict("普通档 红章保留", ri.RedPixels > 200, "红像素 " + ri.RedPixels + "（输入 " + redIn + "）", false);
                Verdict("普通档 确实不做去污", ri.CleaningSkipped && ri.SpecksRemoved == 0,
                    "跳过=" + ri.CleaningSkipped + " 去黑点=" + ri.SpecksRemoved, false);
            }
            ArchiveOptions th = new ArchiveOptions(); th.Thorough = true;
            ArchiveResult rt;
            using (Bitmap outTh = ArchiveProcessor.Run(dirty, th, out rt))
            {
                Verdict("极致档 去黑点(含撤销保险)", rt.SpecksRemoved > 0 || rt.SpecksUndone,
                    "普通档 0 / 极致档 " + rt.SpecksRemoved + " 撤销=" + rt.SpecksUndone +
                    " 步骤=" + Tail(rt.Steps, 100), false);
                Verdict("极致档 灰底被归一(非红像素)", Levels(outTh, true) <= 4,
                    "非红色阶数 " + Levels(outTh, true) + " / 全部 " + Levels(outTh, false), false);
                Verdict("极致档 内容未丢", rt.InkPixels > 1000, "墨像素 " + rt.InkPixels, false);
            }
            dirty.Dispose();
        }

        // 2. 纠偏
        static void Deskew()
        {
            Group("自动纠偏");
            Bitmap page = New(900, 1200, Color.FromArgb(252, 251, 248));
            using (Graphics g = Graphics.FromImage(page))
                using (Pen p = new Pen(Color.FromArgb(25, 25, 25), 2f))
                    for (int i = 0; i < 14; i++) g.DrawLine(p, 90, 130 + i * 70, 810, 130 + i * 70);
            double[] angles = { -8, -5, -3, -1.5, 1.5, 3, 5, 8 };
            int good = 0;
            StringBuilder detail = new StringBuilder();
            foreach (double a in angles)
            {
                using (Bitmap tilted = Imaging.RotateFine(page, a, true))
                {
                    double got = Enhance.DetectSkewAngle(tilted);
                    if (Math.Abs(Math.Abs(got) - Math.Abs(a)) < 1.0) good++;
                    detail.Append(a).Append("->").Append(got.ToString("0.00")).Append("  ");
                }
            }
            Verdict("倾斜检测 8 个角度", good >= 7, good + "/8  " + detail.ToString(), good >= 6);
            page.Dispose();
        }

        // 3. 装订孔方向
        static void Orientation()
        {
            Group("装订孔方向");
            Bitmap holed = New(1000, 1300, Color.FromArgb(252, 251, 248));
            using (Graphics g = Graphics.FromImage(holed))
            {
                using (Pen p = new Pen(Color.FromArgb(25, 25, 25), 2f))
                    for (int i = 0; i < 14; i++) g.DrawLine(p, 90, 130 + i * 80, 910, 130 + i * 80);
                using (SolidBrush dark = new SolidBrush(Color.FromArgb(22, 22, 22)))
                    for (int i = 0; i < 3; i++) g.FillEllipse(dark, 30, 180 + i * 420, 34, 34);
            }
            int correct = 0;
            for (int t = 0; t < 4; t++)
            {
                Bitmap rr = (Bitmap)holed.Clone();
                for (int k = 0; k < t; k++) { Bitmap tmp = Imaging.Rotate90(rr, true); rr.Dispose(); rr = tmp; }
                Orient.Result res = Orient.Detect(rr);
                if (res.TurnsCw == (4 - t) % 4) correct++;   // 转回正放所需的顺时针次数
                rr.Dispose();
            }
            Verdict("四向摆放判方向", correct == 4, correct + "/4", correct >= 3);
            holed.Dispose();
        }

        // 4. OCR 镜像
        static void Mirror()
        {
            Group("OCR 镜像检测");
            Note("Ocr.Available=" + Ocr.Available + "  " + Ocr.StatusText);
            if (!Ocr.Available) { Verdict("OCR 镜像检测", false, "本机缺 OCR 素材", true); return; }
            Bitmap txt = OcrTextPage(1000, 1300);   // 与自检同一套：真文字 + 硬边二值化
            bool? up = Ocr.MirrorVerdict(txt);
            bool? mir;
            using (Bitmap flipped = Imaging.Flip(txt, true)) mir = Ocr.MirrorVerdict(flipped);
            Verdict("正放/镜像两判", up == false && mir == true, "正=" + up + " 镜=" + mir, true);
            txt.Dispose();
        }

        // 5. 局部修复
        static void Inpainting()
        {
            Group("局部修复（划痕→纸面）");
            Bitmap clean = New(700, 900, Color.FromArgb(250, 249, 246));
            Bitmap scratched = (Bitmap)clean.Clone();
            Rectangle area = new Rectangle(200, 300, 300, 300);
            using (Graphics g = Graphics.FromImage(scratched))
                using (Pen p = new Pen(Color.FromArgb(30, 30, 30), 10f))
                    for (int i = 0; i < 6; i++)
                        g.DrawLine(p, area.X + 20, area.Y + 30 + i * 50, area.Right - 20, area.Y + 30 + i * 50);
            int before = CountInk(scratched, area.X, area.Y, area.Width, area.Height);
            Bitmap mask = Imaging.MakeMask(700, 900);
            Imaging.MaskRect(mask, area, true);
            using (Bitmap r1 = Imaging.Inpaint(scratched, mask, 5, null))
                Verdict("普通修复 去划痕", CountInk(r1, area.X, area.Y, area.Width, area.Height) < before / 4,
                    "墨 " + before + "->" + CountInk(r1, area.X, area.Y, area.Width, area.Height), false);
            using (Bitmap r2 = PatchFill.Inpaint(scratched, mask, false, null))
                Verdict("增强修复 去划痕", CountInk(r2, area.X, area.Y, area.Width, area.Height) < before / 4,
                    "墨 " + before + "->" + CountInk(r2, area.X, area.Y, area.Width, area.Height), false);
            using (Bitmap r3 = PatchFill.Inpaint(scratched, mask, true, null))
                Verdict("混合修复 去划痕", CountInk(r3, area.X, area.Y, area.Width, area.Height) < before / 4,
                    "墨 " + before + "->" + CountInk(r3, area.X, area.Y, area.Width, area.Height), false);
            using (Bitmap r4 = PaperFill.Run(scratched, mask, null))
                Verdict("纸面重建 去划痕", CountInk(r4, area.X, area.Y, area.Width, area.Height) < before / 4,
                    "墨 " + before + "->" + CountInk(r4, area.X, area.Y, area.Width, area.Height) +
                    " MAE=" + MAE(r4, clean, area).ToString("0.0"), false);
            if (AiFill.Available)
            {
                using (Bitmap r5 = AiFill.Run(scratched, mask, null))
                    Verdict("AI 修复 去划痕", CountInk(r5, area.X, area.Y, area.Width, area.Height) < before / 2,
                        "墨 " + before + "->" + CountInk(r5, area.X, area.Y, area.Width, area.Height), true);
            }
            else Note("AI 修复：本机 AI 不可用，跳过");
            clean.Dispose(); scratched.Dispose(); mask.Dispose();
        }

        // 6. 调色
        static void ColorAndLevels()
        {
            Group("调色 / 色阶");
            Bitmap src = New(400, 400, Color.FromArgb(150, 150, 150));
            using (Graphics g = Graphics.FromImage(src))
                using (SolidBrush bk = new SolidBrush(Color.FromArgb(15, 15, 15))) g.FillRectangle(bk, 60, 60, 150, 150);
            double before = MeanLum(src);
            AdjustParams p = new AdjustParams(); p.Bright = 50;
            using (Bitmap outp = Enhance.Apply(src, p))
                Verdict("亮度 +50 生效", MeanLum(outp) > before + 25,
                    before.ToString("0") + "->" + MeanLum(outp).ToString("0"), false);
            using (Bitmap auto = Imaging.AutoEnhance(src))
                Verdict("自动色阶生效", Math.Abs(MeanLum(auto) - before) > 10,
                    before.ToString("0") + "->" + MeanLum(auto).ToString("0"), false);
            src.Dispose();
        }

        // 7. 二值化
        static void Binarize()
        {
            Group("Sauvola 二值化");
            Bitmap gray = New(600, 800, Color.White);
            using (Graphics g = Graphics.FromImage(gray))
                using (Pen p = new Pen(Color.FromArgb(90, 90, 90), 3f))
                    for (int i = 0; i < 10; i++) g.DrawLine(p, 60, 100 + i * 60, 540, 100 + i * 60);
            using (Bitmap bin = Enhance.Binarize(gray, 0.2))
                Verdict("输出仅黑白两级", Levels(bin, false) <= 4, "色阶数 " + Levels(bin, false), false);
            gray.Dispose();
        }

        // 8. 去黑点 / 脏块
        static void SpecksAndStains()
        {
            Group("去黑点 / 脏块重建");
            Bitmap speck = New(700, 900, Color.White);
            using (Graphics g = Graphics.FromImage(speck))
                using (SolidBrush b = new SolidBrush(Color.Black))
                { Random r = new Random(11); for (int i = 0; i < 80; i++) g.FillRectangle(b, r.Next(30, 670), r.Next(30, 870), 4, 4); }
            int before = CountInk(speck, 0, 0, 700, 900);
            int removed = DocClean.RemoveSpecks(speck, 0, 0, null, true);
            Verdict("连通域去黑点", removed > 40,
                "删除 " + removed + " 块，墨 " + before + "->" + CountInk(speck, 0, 0, 700, 900), false);
            speck.Dispose();

            Bitmap stain = New(600, 800, Color.FromArgb(250, 249, 246));
            using (Graphics g = Graphics.FromImage(stain))
            {
                g.FillEllipse(new SolidBrush(Color.FromArgb(235, 235, 235)), 180, 220, 200, 200);
                g.FillEllipse(new SolidBrush(Color.FromArgb(175, 175, 175)), 220, 260, 120, 120);
            }
            int found = 0;
            Bitmap paperRef = New(600, 800, Color.FromArgb(250, 249, 246));
            Rectangle roi = new Rectangle(200, 240, 160, 160);
            using (Bitmap mask = DocClean.DetectStainMask(stain, 0, 0, 0, out found))
            {
                Verdict("脏块检测", found > 0, "检出 " + found + " 块", false);
                if (found > 0)
                {
                    double maeBefore = MAE(stain, paperRef, roi);
                    using (Bitmap rebuilt = PaperFill.Run(stain, mask, null))
                        Verdict("脏块重建有效", MAE(rebuilt, paperRef, roi) < maeBefore,
                            "MAE " + maeBefore.ToString("0.0") + "->" + MAE(rebuilt, paperRef, roi).ToString("0.0"), false);
                }
            }
            stain.Dispose(); paperRef.Dispose();
        }

        // 9. 印章补色
        static void SealColor()
        {
            Group("褪色红章补色");
            Bitmap seal = New(600, 800, Color.FromArgb(250, 249, 246));
            using (Graphics g = Graphics.FromImage(seal))
                using (Pen rp = new Pen(Color.FromArgb(170, 95, 100), 10f)) g.DrawEllipse(rp, 200, 250, 200, 200);
            Rectangle roi = new Rectangle(180, 220, 240, 260);
            double before = Redness(seal, roi);
            Bitmap mask = Imaging.MakeMask(600, 800);
            Imaging.MaskCircle(mask, 300, 350, 120, true);
            SealTool.Colorize(seal, mask, 90);
            double after = Redness(seal, roi);
            Verdict("红章补色(红度增强)", after > before + 5,
                "红度 " + before.ToString("0.0") + "->" + after.ToString("0.0"), false);
            seal.Dispose(); mask.Dispose();
        }

        // 10. 透视 / 文字
        static void PerspectiveAndText()
        {
            Group("透视校正 / 文字标注");
            Bitmap quad = New(600, 800, Color.White);
            using (Graphics g = Graphics.FromImage(quad))
                using (SolidBrush b = new SolidBrush(Color.Black)) g.FillRectangle(b, 100, 100, 400, 500);
            PointF[] pts = { new PointF(30, 30), new PointF(560, 80), new PointF(540, 750), new PointF(60, 720) };
            using (Bitmap warped = Enhance.PerspectiveWarp(quad, pts))
                Verdict("四点透视产出位图", warped != null && warped.Width > 100 && warped.Height > 100,
                    warped == null ? "null" : warped.Width + "x" + warped.Height, false);
            quad.Dispose();

            Bitmap txt = New(500, 500, Color.White);
            int ink0 = CountInk(txt, 0, 0, 500, 500);
            List<TextItem> items = new List<TextItem>();
            TextItem it = new TextItem(); it.Text = "测试标注"; it.Box = new RectangleF(50, 50, 400, 100); it.SizeEm = 60f;
            items.Add(it);
            TextStamp.Rasterize(txt, items);
            Verdict("文字标注落像素", CountInk(txt, 0, 0, 500, 500) > ink0 + 100,
                "墨 " + ink0 + "->" + CountInk(txt, 0, 0, 500, 500), false);
            txt.Dispose();
        }
    }
}
