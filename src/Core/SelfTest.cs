// SelfTest.cs — 无界面自检（M1 结构重构开始引入的回归安全网）
//
// 目的：重构/改功能之前先跑一遍，把"核心算法还对不对"变成一条命令，
// 不依赖 UI、不依赖人眼。所有用例只用临时目录，不碰用户的图片与 keys.ini。
//
// 用法：exe --selftest          → 报告写到 %TEMP%\PicTool-selftest.txt，全过退出码 0
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PicTool
{
    static class SelfTest
    {
        delegate void TestAction();
        static StringBuilder log;
        static int nPass, nFail;
        public static int FailCount { get { return nFail; } }

        static void Case(string name, bool ok) { Case(name, ok, null); }

        // 已知问题：明确记录、不影响退出码（当回归测试用），但必须写在报告里，不允许悄悄放过
        static void Known(string name, string detail)
        {
            log.AppendLine("  [已知] " + name + (detail == null ? "" : "   " + detail));
        }

        static void Case(string name, bool ok, string detail)
        {
            if (ok) { nPass++; log.AppendLine("  [通过] " + name + (detail == null ? "" : "   " + detail)); }
            else { nFail++; log.AppendLine("  [失败] " + name + (detail == null ? "" : "   " + detail)); }
        }

        static void Throws(string name, TestAction action)
        {
            try { action(); Case(name, true, null); }
            catch (Exception ex) { Case(name, false, ex.Message); }
        }

        static Bitmap Doc(int w, int h, int inkLines)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(252, 251, 248));
                using (Pen p = new Pen(Color.FromArgb(30, 30, 30), 2))
                    for (int i = 0; i < inkLines; i++)
                        g.DrawLine(p, 40, 40 + i * 26, w - 40, 40 + i * 26);
            }
            return b;
        }

        static Bitmap TextPage(int w, int h)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(252, 251, 248));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Font f = new Font("SimSun", 18f);
                string[] lines = {
                    "进贤县三里乡人民政府",
                    "党员电化教育工作记录",
                    "需冲洗照片的底片共一十六片",
                    "一九九八年二月二十八日归档",
                    "立卷人：张建国 检查人：李秀英",
                    "照片说明：春节期间活动",
                };
                for (int i = 0; i < lines.Length; i++)
                    g.DrawString(lines[i], f, Brushes.Black, new PointF(40, 60 + i * 90));
                f.Dispose();
            }
            return b;
        }

        // 方向/镜像检测用的大号文字页：字高≈45px（大组件面积能过 150 阈值）、
        // 带中文标点（标点重力判倒置的样本）、行数够多（OCR 检测有框可读）。
        // 渲染必须用 SingleBitPerPixelGridFit（硬边纯黑）：AntiAlias 的灰字太淡，
        // OCR det 只圈到字符中段的碎片框、rec 读成乱码（实测踩过）；
        // 真实扫描件是黑字硬边，这个渲染才与现场一致。
        static Bitmap OrientPage(int w, int h)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(252, 251, 248));
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                Font f = new Font("SimSun", 28f, FontStyle.Bold);
                string[] lines = {
                    "霍城县教育局委员会文件，",
                    "关于印发年度工作考核的通知。",
                    "为了抓好各项工作的落实、",
                    "科学地评价学校的工作成绩；",
                    "经教育局两委会研究决定，",
                    "现将考核实施意见印发给你们。",
                    "请认真遵照执行、并抓好落实。",
                    "二〇〇七年四月二十六日归档",
                };
                for (int i = 0; i < lines.Length; i++)
                    g.DrawString(lines[i], f, Brushes.Black, new PointF(50, 40 + i * (h - 80) / lines.Length));
                f.Dispose();
                // 装订孔 ×3（左边带，正放页的标准形态）：起"转向只认装订孔"
                // —— 方向用例必须有孔才能走真实判据（无孔页一律不转）。
                using (SolidBrush hb = new SolidBrush(Color.FromArgb(22, 22, 22)))
                    for (int k = 0; k < 3; k++)
                        g.FillEllipse(hb, w * 0.03f, h * (0.22f + 0.28f * k), 34, 34);
            }
            // 二值化收口：模拟真实扫描的硬边黑字（抗锯齿的灰字会让 OCR 只读出碎片）
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

        public static string RunAll()
        {
            nPass = 0; nFail = 0;
            log = new StringBuilder();
            log.AppendLine("Restoria — 自检报告");
            log.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            log.AppendLine("环境: " + SysInfo.Describe());
            log.AppendLine();

            string tmp = Path.Combine(Path.GetTempPath(), "PicTool_selftest");
            try { if (!Directory.Exists(tmp)) Directory.CreateDirectory(tmp); } catch { }

            // ---- 运行环境（2.3.0 起硬性要求 .NET 4.8）----
            // 为什么值得单独测：引用的是 4.0 版 mscorlib，所以装了 4.0~4.7 也能启动，
            // 要到 4.6+ 才有 in-box 的 System.Numerics.Vectors（SIMD 的载体）。
            // 这条能提前发现"能跑但运行时太旧"的隐患，免得等到用 SIMD 时才炸。
            log.AppendLine("运行时: " + SysInfo.ClrDetail);
            {
                int rel = SysInfo.Net4xRelease;
                string relText;
                bool relOk;
                if (rel >= 528040) { relOk = true; relText = "4.8+（Release " + rel + "）"; }
                else if (rel > 0) { relOk = false; relText = "Release " + rel + "，低于 4.8 —— 请装 .NET Framework 4.8"; }
                else { relOk = true; relText = "Release 读不到（沙箱或权限所致，不代表没装）"; }
                Case("运行时是 .NET 4.8 或更高", relOk, relText);
            }

            // ---- 位图基础 ----
            Bitmap src = Doc(600, 500, 12);
            Bitmap r1 = Imaging.Rotate90(src, true);
            Bitmap r2 = Imaging.Rotate90(r1, true);
            Bitmap r3 = Imaging.Rotate90(r2, true);
            Bitmap r4 = Imaging.Rotate90(r3, true);
            Case("旋转 90° ×4 回到原样", r4.Width == src.Width && r4.Height == src.Height);
            Case("旋转一次后宽高互换", r1.Width == src.Height && r1.Height == src.Width,
                r1.Width + "x" + r1.Height);

            Bitmap fit = Imaging.ScaleToFit(src, 300);
            Case("按长边缩放", Math.Max(fit.Width, fit.Height) == 300, fit.Width + "x" + fit.Height);

            Bitmap crop = Imaging.Crop(src, new Rectangle(10, 20, 100, 80));
            Case("矩形裁剪尺寸正确", crop.Width == 100 && crop.Height == 80);

            Bitmap fh = Imaging.Flip(src, true);
            Case("水平翻转尺寸不变", fh.Width == src.Width && fh.Height == src.Height);

            // ---- 透视退化输入的守卫 ----
            // 为什么值得四条例：这些输入曾经能穿过守卫写坏内存（NaN 与任何值比较都是 false），
            // 而且它只在"用户手抖点出近共线的四点"时才触发 —— 现场几乎不可能复现。
            {
                bool nanRejected = false, dupRejected = false, lineRejected = false, okWorked = false;

                PointF[] bad = new PointF[] { new PointF(float.NaN, 10f), new PointF(200f, 0f),
                                              new PointF(200f, 200f), new PointF(0f, 200f) };
                try { Enhance.PerspectiveWarp(src, bad); } catch { nanRejected = true; }

                bad = new PointF[] { new PointF(10f, 10f), new PointF(10f, 10f),
                                     new PointF(200f, 200f), new PointF(0f, 200f) };
                try { Enhance.PerspectiveWarp(src, bad); } catch { dupRejected = true; }

                bad = new PointF[] { new PointF(0f, 0f), new PointF(100f, 0f),
                                     new PointF(200f, 0f), new PointF(300f, 0f) };
                try { Enhance.PerspectiveWarp(src, bad); } catch { lineRejected = true; }

                PointF[] good = new PointF[] { new PointF(0f, 0f), new PointF(199f, 0f),
                                               new PointF(199f, 199f), new PointF(0f, 199f) };
                try
                {
                    Bitmap pv = Enhance.PerspectiveWarp(src, good);
                    okWorked = pv != null && pv.Width > 8 && pv.Height > 8;
                    if (pv != null) pv.Dispose();
                }
                catch { }

                Case("透视：NaN 角点被拒绝", nanRejected);
                Case("透视：重合角点被拒绝", dupRejected);
                Case("透视：共线角点被拒绝", lineRejected);
                Case("透视：正常角点仍能校正", okWorked);
            }

            // ---- 掩膜 ----
            Bitmap mask = Imaging.MakeMask(src.Width, src.Height);
            Imaging.MaskRect(mask, new Rectangle(100, 100, 60, 40), true);
            Rectangle mb = Imaging.MaskBounds(mask);
            Case("掩膜边界 = 涂抹矩形", mb.X == 100 && mb.Y == 100 && mb.Width == 60 && mb.Height == 40,
                mb.X + "," + mb.Y + " " + mb.Width + "x" + mb.Height);

            // ---- 倾斜检测 ----
            Bitmap page = TextPage(700, 700);
            Bitmap tilted = Imaging.RotateFine(page, -3.0, true);   // autoCrop=true：别留黑角，
            // 黑角会被 Otsu 当成墨迹，投影法就被带偏（实测：不带 autoCrop 时检测结果直接是 0°）

            try { tilted.Save(Path.Combine(tmp, "tilted.png"), ImageFormat.Png); } catch { }
            // 已知问题（由本自检发现）：小角度倾斜检测不出来 —— 只有当倾角大到
            // 8° 左右才生效（-8°→7.85° 正确，-1.5°/-5° 都返回 0）。用户场景里绝大多数是
            // 1~5° 的轻微歪斜，等于"一键自动纠偏"没作用。待修（M4 算法项）。
            double ang = Enhance.DetectSkewAngle(tilted);
            if (Math.Abs(Math.Abs(ang) - 3.0) < 0.9) Case("倾斜检测（造 -3°）", true, "测得 " + Math.Round(ang, 2) + "°");
            else Known("倾斜检测对 -3° 失效", "测得 " + Math.Round(ang, 2) + "°（小角度不生效，见代码注释里的待修说明）");

            // 黑边保险：同一张图 autoCrop=false 会留下黑角（就是 docs/架构.md 记的
            // "带黑边的图会把黑角当墨迹带偏"）。开了保险应仍测出 ≈3°；把保险关掉（第二参 false）
            // 则退化成 0° —— 一条用例同时证"修好了"和"确实是双保险在起作用"。
            Bitmap blackCorner = Imaging.RotateFine(page, -3.0, false);
            try
            {
                double angGuard = Enhance.DetectSkewAngle(blackCorner, true);
                double angRaw = Enhance.DetectSkewAngle(blackCorner, false);
                Case("倾斜检测：留黑角的图也能测出 -3°（黑边保险）",
                    Math.Abs(Math.Abs(angGuard) - 3.0) < 0.9,
                    "带保险 " + Math.Round(angGuard, 2) + "° / 关保险 " + Math.Round(angRaw, 2) + "°");
            }
            finally { blackCorner.Dispose(); }
            // 观察项：同一套算法在不同倾角 / 墨迹密度下的表现（不参与通过判定，供排查用）
            StringBuilder probe = new StringBuilder();
            double[] test = { -1.5, -5.0, -8.0, 0.0 };
            for (int i = 0; i < test.Length; i++)
            {
                Bitmap tb = Imaging.RotateFine(page, test[i], true);
                probe.Append(test[i] + "°→" + Math.Round(Enhance.DetectSkewAngle(tb), 2) + "°  ");
                tb.Dispose();
            }
            log.AppendLine("  [观察] 倾斜检测抽样: " + probe.ToString());

            // ---- 摩尔纹陷波----
            // ① 常量图：没有周期纹 → RemoveInPlace 必须返回 false（干净页零开销的保证）
            Bitmap constBmp = new Bitmap(256, 512, PixelFormat.Format24bppRgb);
            {
                PixelBuffer cb = BitmapBridge.ToBuffer(constBmp);
                for (int i = 0; i < cb.Data.Length; i++) cb.Data[i] = 200;
                BitmapBridge.WriteBack(constBmp, cb);
            }
            double cper;
            bool chit = MoireFilter.RemoveInPlace(constBmp, out cper);
            Case("摩尔纹: 常量图不误触发", !chit, chit ? "误报周期 " + cper.ToString("0.0") : "未触发");

            // ② 周期 24 行的正弦条纹（幅度 40，叠加灰底）：陷波后竖直剖面 std 应大幅下降
            Bitmap stripe = new Bitmap(256, 4096, PixelFormat.Format24bppRgb);
            {
                PixelBuffer sb = BitmapBridge.ToBuffer(stripe);
                for (int y = 0; y < 4096; y++)
                {
                    byte v = (byte)Math.Round(160 + 40.0 * Math.Sin(2.0 * Math.PI * y / 24.0));
                    int o = y * sb.Stride;
                    for (int x = 0; x < 256; x++) { sb.Data[o + x * 3] = v; sb.Data[o + x * 3 + 1] = v; sb.Data[o + x * 3 + 2] = v; }
                }
                BitmapBridge.WriteBack(stripe, sb);
            }
            double sper;
            bool shit = MoireFilter.RemoveInPlace(stripe, out sper);
            // 处理后取中间一列的竖直剖面 std
            double stdAfter = -1;
            if (shit)
            {
                PixelBuffer sb2 = BitmapBridge.ToBuffer(stripe);
                double sum = 0, sum2 = 0; int n = 0;
                for (int y = 100; y < 3996; y++)
                {
                    int o = y * sb2.Stride + 128 * 3;
                    double v = (sb2.Data[o] + sb2.Data[o + 1] + sb2.Data[o + 2]) / 3.0;
                    sum += v; sum2 += v * v; n++;
                }
                double mean = sum / n;
                stdAfter = Math.Sqrt(sum2 / n - mean * mean);
            }
            Case("摩尔纹: 周期条纹被检出并消除", shit && Math.Abs(sper - 24.0) < 1.5 && stdAfter >= 0 && stdAfter < 12.0,
                shit ? string.Format("周期 {0:0.0} 行, 剖面std {1:0.0}（原 28.3）", sper, stdAfter)
                     : "条纹页未检出（漏报）");

            // ---- 色阶：中性参数应基本不动像素 ----
            Bitmap lv = Imaging.Ensure24bpp(new Bitmap(src));
            byte[] before = Pixels(lv);
            Enhance.LevelsInPlace(lv, 0, 255, 1.0);
            byte[] after = Pixels(lv);
            int maxd = 0;
            for (int i = 0; i < before.Length; i++)
            {
                int d = Math.Abs(before[i] - after[i]);
                if (d > maxd) maxd = d;
            }
            Case("色阶中性参数不动像素", maxd <= 2, "最大偏差 " + maxd + " 灰阶");

            // ---- 二值化：只应剩两种色阶 ----
            Bitmap bin = Enhance.Binarize(Doc(400, 300, 8), 0.2);
            Case("二值化输出仅黑白", OnlyTwoLevels(bin));

            // ---- 修补（PatchFill）：涂抹区被周围纹理填上，尺寸不变 ----
            Bitmap psrc = Doc(500, 400, 10);
            Bitmap pm = Imaging.MakeMask(500, 400);
            Imaging.MaskRect(pm, new Rectangle(200, 180, 80, 60), true);
            Bitmap pf = PatchFill.Inpaint(psrc, pm, false, null);
            Case("修补后尺寸不变", pf.Width == 500 && pf.Height == 400);
            Case("修补填补了涂抹区中心", Brightness(pf, 240, 210) > 150,
                "中心亮度 " + Brightness(pf, 240, 210));

            // ---- 纸面重建（PaperFill）：污渍应被换成与周围同水平的纸面，且洞外不动 ----
            // 这条用例守的是"确定性"这条底线：不生成内容、不编纹理，只还原纸面。
            {
                Bitmap basePage = Doc(600, 460, 9);
                int sx = 250, sy = 200, sw = 150, sh = 120;
                using (Graphics g = Graphics.FromImage(basePage))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(168, 132, 84)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillEllipse(b, sx - 20, sy - 20, sw + 40, sh + 40);   // 一块褐色水渍
                }
                int stainBefore = RegionMean(basePage, sx, sy, sw, sh);
                Bitmap pmask = Imaging.MakeMask(basePage.Width, basePage.Height);
                Imaging.MaskRect(pmask, new Rectangle(sx, sy, sw, sh), true);
                Bitmap filled = PaperFill.Run(basePage, pmask, null);
                int stainAfter = RegionMean(filled, sx, sy, sw, sh);
                int paperLevel = RegionMean(basePage, 40, 400, 120, 40);     // 远处的干净纸面
                Case("纸面重建后尺寸不变", filled.Width == 600 && filled.Height == 460);
                Case("纸面重建把污渍换成纸面", stainAfter - stainBefore > 40 && Math.Abs(stainAfter - paperLevel) < 14,
                    "污渍处 " + stainBefore + " → " + stainAfter + "，远处纸面 " + paperLevel);
                // 离掩膜足够远的地方必须逐像素不动（外扩 + 羽化都发生在掩膜附近）
                Rectangle keepClean = new Rectangle(0, 0, filled.Width, filled.Height);
                keepClean = Rectangle.Inflate(new Rectangle(sx, sy, sw, sh), 60, 60);
                int changed = CountDiffOutside(filled, basePage, keepClean);
                Case("纸面重建不动涂抹区之外", changed == 0, "被改动像素 " + changed + " 个");
                filled.Dispose(); pmask.Dispose(); basePage.Dispose();
            }

            // ---- 直线续接：表格线要接回来，一行文字不能被误判成直线 ----
            {
                // Doc(w,h,n) 画的是 n 条贯穿全宽的横线（当作表格线）；洞口横跨其中两条
                Bitmap lpage = Doc(600, 460, 9);
                Bitmap lmask = Imaging.MakeMask(600, 460);
                Imaging.MaskRect(lmask, new Rectangle(250, 180, 120, 60), true);
                PaperFill.LastLinesContinued = 0;
                Bitmap lfilled = PaperFill.Run(lpage, lmask, null);
                Case("直线续接认出横穿的表格线", PaperFill.LastLinesContinued >= 2,
                    "认出 " + PaperFill.LastLinesContinued + " 条"
                    + (PaperFill.LastLinesInfo == null ? "" : "：" + PaperFill.LastLinesInfo));

                // 洞里 x=310 这一列，应该重新出现至少两段暗像素（就是那两条线）
                int dark = 0;
                for (int y = 180; y < 240; y++) if (Brightness(lfilled, 310, y) < 150) dark++;
                Case("被抹掉的表格线回到洞里", dark >= 4, "洞内该列暗像素 " + dark + " 个（两条线各约 2px）");

                // 纯文字页：一行文字有几十个断口，绝不能当成直线画成一整条
                Bitmap tpage = TextPage(700, 600);
                Bitmap tmask = Imaging.MakeMask(700, 600);
                Imaging.MaskRect(tmask, new Rectangle(120, 40, 460, 420), true);
                PaperFill.LastLinesContinued = 0;
                Bitmap tfilled = PaperFill.Run(tpage, tmask, null);
                Case("文字行不被误判成直线", PaperFill.LastLinesContinued == 0,
                    "误判 " + PaperFill.LastLinesContinued + " 条");

                lpage.Dispose(); lmask.Dispose(); lfilled.Dispose();
                tpage.Dispose(); tmask.Dispose(); tfilled.Dispose();
            }

            // ---- 扫描链路：诊断必须给得出原因、「32 位回退」那套 PE 改写必须真的生效 ----
            // 守两件事：看不到扫描仪时用户能知道为什么；以及「64 位看不见 32 位驱动」这个老坑。
            {
                // ① 异常翻译要剥掉反射的壳，把真正的 HRESULT 报出来
                Exception wrapped = new System.Reflection.TargetInvocationException(
                    new COMException("找不到成员。", unchecked((int)0x80020003)));
                string dsc = Scanner.Describe(wrapped);
                Case("扫描异常翻译出真实 HRESULT", dsc.IndexOf("80020003") >= 0,
                    dsc.Substring(0, Math.Min(70, dsc.Length)));

                // ② 32 位副本：复制自身 + 置 CLR 头的 32BITREQUIRED 位
                string p32;
                bool marked;
                try { marked = Scanner.Verify32BitCopy(out p32); }
                catch (Exception ex) { marked = false; p32 = ex.Message; }
                Case("32 位副本已就绪且被标成 32 位", marked, p32);

                // ③ 诊断本身不能抛异常（它要能在任何机器上跑）
                string rep = Scanner.Diagnose();
                Case("扫描诊断能跑完并给出结论",
                    rep != null && rep.IndexOf("结论：") > 0 && rep.IndexOf("WIA 组件") > 0,
                    rep == null ? "空报告" : ("报告 " + rep.Length + " 字"));
            }

            // ---- 异常恢复：崩溃后要能把"改了还没存"的图找回来 ----
            {
                Recovery.Start();
                Bitmap rpage = Doc(300, 220, 6);
                Recovery.Note(rpage, Path.Combine(tmp, "恢复测试.jpg"));
                Recovery.FlushForTest();                        // 正常路径由定时器异步写，这里同步写完
                List<List<Recovery.Item>> pend = Recovery.PendingSessions();
                int found = 0;
                foreach (List<Recovery.Item> s in pend) found += s.Count;
                Case("恢复区记下未保存的图", found >= 1, "待恢复 " + found + " 张");
                if (found >= 1)
                {
                    string rdir = Path.Combine(tmp, "recovered");
                    try { if (Directory.Exists(rdir)) Directory.Delete(rdir, true); } catch { }   // 上次跑剩下的会干扰计数
                    Recovery.RestoreTo(rdir, pend[0]);
                    string[] pngs = Directory.GetFiles(rdir, "*.png");
                    bool ok = pngs.Length >= 1;
                    if (ok)
                    {
                        Bitmap back = Imaging.LoadFile(pngs[0]);   // 恢复出来的必须能读
                        ok = back.Width == 300 && back.Height == 220;
                        if (back != null) back.Dispose();
                    }
                    Case("恢复出的是无损 PNG 且可读回", ok, pngs.Length + " 个文件");
                }
                Recovery.DiscardAll();
                Case("丢弃后恢复区为空", Recovery.PendingSessions().Count == 0);
                rpage.Dispose();
                Recovery.Shutdown();
            }

            // ---- 文件工具：重命名计划（不落盘） ----
            string[] files = { "a.jpg", "b.jpg", "c.jpg" };
            System.Collections.Generic.List<RenameOp> plan = FileTools.PlanNumbered(files, "档案", 1);
            Case("重命名计划条数正确", plan != null && plan.Count == 3);
            if (plan != null && plan.Count == 3)
                Case("重命名计划名不重复且带前缀",
                    plan[0].NewName != plan[1].NewName && plan[0].NewName != plan[2].NewName &&
                    plan[0].NewName.IndexOf("档案") == 0, plan[0].NewName);

            // ---- 归档路径保险丝（整树归档：危险路径必须拦住） ----
            // 只测"拒绝"不需要动磁盘；"放行"用盘根下的探针目录（建不出就当沙箱限制跳过）。
            {
                string r;
                string driveRoot = Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory);
                Case("保险丝拦盘符根目录", driveRoot == null || !FileTools.IsArchiveAllowed(driveRoot, out r));

                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                Case("保险丝拦系统目录", string.IsNullOrEmpty(winDir) || !FileTools.IsArchiveAllowed(winDir, out r));

                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Case("保险丝拦用户主目录", string.IsNullOrEmpty(userProfile) || !FileTools.IsArchiveAllowed(userProfile, out r));

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                Case("保险丝拦桌面", string.IsNullOrEmpty(desktop) || !FileTools.IsArchiveAllowed(desktop, out r));

                // 临时目录通常在用户主目录底下 —— 拒绝它正好同时验证"子目录也在拦截范围内"
                //（TEMP 被重定向到别处时不硬性断言，避免环境差异造成假失败）
                string tempDir = Path.GetTempPath().TrimEnd('\\', '/');
                bool tempUnderUser = !string.IsNullOrEmpty(userProfile) &&
                    (tempDir.StartsWith(userProfile + "\\", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(tempDir, userProfile, StringComparison.OrdinalIgnoreCase));
                Case("保险丝拦用户目录下的子目录", !tempUnderUser || !FileTools.IsArchiveAllowed(tempDir, out r), tempDir);

                // 放行用例：盘根下建一个探针目录（同盘其它位置），测完即删
                string fuseProbe = Path.Combine(driveRoot ?? "C:\\", "PicTool_selftest_probe_ok");
                bool fuseCreated = false;
                try { Directory.CreateDirectory(fuseProbe); fuseCreated = Directory.Exists(fuseProbe); } catch { }
                if (fuseCreated)
                {
                    Case("保险丝放行普通数据文件夹", FileTools.IsArchiveAllowed(fuseProbe, out r), r ?? "");
                    string fuseSub = Path.Combine(fuseProbe, "卷一");
                    try { Directory.CreateDirectory(fuseSub); } catch { }
                    Case("保险丝放行其子文件夹", Directory.Exists(fuseSub) && FileTools.IsArchiveAllowed(fuseSub, out r), r ?? "");
                    try { Directory.Delete(fuseSub, false); } catch { }
                    try { Directory.Delete(fuseProbe, false); } catch { }
                }
                else
                {
                    Case("保险丝放行普通数据文件夹", true, "盘根建不了探针目录（沙箱限制），跳过");
                }
            }

            // ---- 存图 / 读图往返 ----
            string png = Path.Combine(tmp, "roundtrip.png");
            Throws("保存图片", delegate { Imaging.SaveImage(src, png, 90); });
            if (File.Exists(png))
            {
                Bitmap back = Imaging.LoadFile(png);
                Case("读回尺寸一致", back.Width == src.Width && back.Height == src.Height);
                back.Dispose();
            }

            // ---- 文字烙印（TextStamp，「文字元素层」） ----
            Bitmap tbase = Doc(400, 300, 0);
            List<TextItem> tis = new List<TextItem>();
            TextItem ti = new TextItem();
            ti.Text = "档案编号 0123\n第二行中文";
            ti.FontName = "宋体";
            ti.SizeEm = 24f;
            ti.Box = new RectangleF(30f, 30f, 1f, 1f);
            TextStamp.UpdateBox(ti);
            tis.Add(ti);
            Case("文字包围盒按内容量出", ti.Box.Width > 12f && ti.Box.Height > ti.SizeEm,
                ti.Box.Width.ToString("0") + "x" + ti.Box.Height.ToString("0"));
            TextItem ti2 = ti.Clone();
            ti2.Text = "改";
            Case("文字克隆互不影响", ti.Text == "档案编号 0123\n第二行中文" && ti2.Text == "改");
            Case("文字命中测试（框内）", TextStamp.HitTest(tis, ti.Box.X + ti.Box.Width / 2, ti.Box.Y + ti.Box.Height / 2) == 0);
            Case("文字命中测试（空白处不中）", TextStamp.HitTest(tis, 2f, 2f) == -1);
            Bitmap tA = Imaging.Ensure24bpp(new Bitmap(tbase)), tB = Imaging.Ensure24bpp(new Bitmap(tbase));
            TextStamp.Rasterize(tA, tis);
            TextStamp.Rasterize(tB, tis);
            Case("文字烙印可复现（两次逐像素一致）", PixelsEqual(tA, tB));
            Bitmap tC = Imaging.Ensure24bpp(new Bitmap(tbase));
            TextStamp.Rasterize(tC, tis);
            Case("文字烙印确实改变了画面", !PixelsEqual(tC, tbase));
            Bitmap tEmpty = Imaging.Ensure24bpp(new Bitmap(tbase));
            TextStamp.Rasterize(tEmpty, new List<TextItem>());
            Case("空白列表烙印不动画面", PixelsEqual(tbase, tEmpty));
            tA.Dispose(); tB.Dispose(); tC.Dispose(); tEmpty.Dispose(); tbase.Dispose();

            // ---- 区域上色（调色页）：掩码内墨迹染成目标色，纸张与掩码外不动 ----
            {
                // 白纸 + 黑方块（20..80）当"文字"；掩膜只盖住方块一半（x<=50）
                Bitmap ink = new Bitmap(100, 60, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(ink)) { g.Clear(Color.White); g.FillRectangle(Brushes.Black, 20, 20, 60, 20); }
                Bitmap mk = new Bitmap(100, 60, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(mk)) { g.Clear(Color.Black); g.FillRectangle(Brushes.White, 0, 0, 50, 60); }
                Color red = Color.FromArgb(200, 26, 32);

                SealTool.Colorize(ink, mk, red, 100);
                Color inMask = ink.GetPixel(35, 30);      // 掩膜内的浓墨 → 应变成红
                Color outMask = ink.GetPixel(70, 30);     // 掩膜外的浓墨 → 仍是黑
                Color paper = ink.GetPixel(10, 10);       // 纸张 → 一个字节都不动
                Case("上色：掩膜内墨迹染成目标色",
                    inMask.R > inMask.B * 2 && inMask.G < inMask.R, inMask.R + "," + inMask.G + "," + inMask.B);
                Case("上色：掩膜外墨迹不动", outMask.R < 60 && outMask.G < 60 && outMask.B < 60);
                Case("上色：纸张像素不动", paper.R == 255 && paper.G == 255 && paper.B == 255);

                // 黑色目标：掩膜内灰字明显变深、掩膜外不动（描黑；中间灰按墨量染大半留小半，见 Colorize 注释）
                using (Graphics g = Graphics.FromImage(ink)) { g.Clear(Color.White); g.FillRectangle(Brushes.Gray, 20, 20, 60, 20); }
                SealTool.Colorize(ink, mk, Color.FromArgb(30, 30, 30), 100);
                Color dark = ink.GetPixel(35, 30);
                Case("上色：描黑后掩膜内明显变深", dark.R < 100 && dark.R < 128, "128 → " + dark.R);
                ink.Dispose(); mk.Dispose();
            }

            // ================= 一键归档流水线 =================
            // 守什么：①光照归一化真能把"左灰右白"拉成均匀白底，且不动笔画
            //         ②黑点过滤只清空白处的孤立黑点，**紧挨文字的标点绝不能误删**
            //         ③dpi 推断不被 96/72 这类默认值骗到
            //         ④输出确实是标准 A4、确实是纯黑白、且内容没被拉变形

            // ---- 光照归一化：同一张纸"左灰右白" ----
            {
                Bitmap uneven = new Bitmap(400, 200, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(uneven))
                {
                    using (System.Drawing.Drawing2D.LinearGradientBrush lb =
                        new System.Drawing.Drawing2D.LinearGradientBrush(
                            new Rectangle(0, 0, 400, 200),
                            Color.FromArgb(150, 150, 150), Color.FromArgb(252, 252, 252), 0f))
                    { g.FillRectangle(lb, 0, 0, 400, 200); }
                    using (Pen p = new Pen(Color.FromArgb(30, 30, 30), 4))
                        for (int i = 0; i < 4; i++) g.DrawLine(p, 40, 40 + i * 40, 360, 40 + i * 40);
                }
                int bgL0 = RegionMean(uneven, 5, 5, 25, 25);
                int bgR0 = RegionMean(uneven, 370, 170, 25, 25);
                Bitmap norm = DocClean.NormalizeIllumination(uneven, 1.0, 2.5);
                int bgL1 = RegionMean(norm, 5, 5, 25, 25);
                int bgR1 = RegionMean(norm, 370, 170, 25, 25);
                int ink1 = RegionMean(norm, 180, 38, 40, 4);
                Case("光照归一化：原本偏灰的一侧被拉到近白", bgL1 >= 235,
                    "左 " + bgL0 + " → " + bgL1);
                Case("光照归一化：原本就白的一侧也没被拉爆", bgR1 >= 235,
                    "右 " + bgR0 + " → " + bgR1);
                Case("光照归一化：笔画仍然是深的（没把字洗掉）", ink1 >= 0 && ink1 < 130,
                    "笔画亮度 " + ink1);
                uneven.Dispose(); norm.Dispose();
            }

            // ---- 连通域黑点过滤：孤立黑点要清，紧挨文字的标点不能动 ----
            {
                Bitmap pg = new Bitmap(300, 200, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(pg))
                {
                    g.Clear(Color.White);
                    using (SolidBrush b = new SolidBrush(Color.Black))
                    {
                        g.FillRectangle(b, 30, 40, 200, 14);    // "文字"笔画：大连通域
                        g.FillRectangle(b, 233, 44, 6, 6);      // 标点：小，但离文字只有 3px
                        g.FillRectangle(b, 40, 120, 3, 3);      // 黑点 1：空白处
                        g.FillRectangle(b, 120, 150, 4, 4);     // 黑点 2：空白处
                        g.FillRectangle(b, 220, 100, 2, 2);     // 黑点 3：空白处
                    }
                }
                // maxArea=60 让标点(36px)也成为候选，gap=6 让"贴近文字"这条判据生效
                int removed = DocClean.RemoveSpecks(pg, 60, 6);
                Case("去黑点：空白处的三处黑点被清掉", removed == 3, "实清 " + removed + " 处");
                Case("去黑点：紧挨文字的标点未被误删",
                    Brightness(pg, 235, 46) < 128, "标点处亮度 " + Brightness(pg, 235, 46));
                Case("去黑点：文字笔画未被误删",
                    Brightness(pg, 130, 46) < 128, "笔画处亮度 " + Brightness(pg, 130, 46));
                Case("去黑点：被清掉的三个位置确实变白了",
                    Brightness(pg, 41, 121) > 200 && Brightness(pg, 121, 151) > 200 && Brightness(pg, 221, 101) > 200,
                    "三处亮度 " + Brightness(pg, 41, 121) + " / " + Brightness(pg, 121, 151) + " / " + Brightness(pg, 221, 101));
                pg.Dispose();
            }

            // ---- 去黑点（大阈值档）：真实档案里霉斑面积和汉字重叠，只能靠"上下文"分开 ----
            // 这条守的是最容易退化的地方：阈值一旦放大，必须确保"成行的字"仍被保护住。
            // ⚠️ 起"字"用描边方块建模（fill≈0.47，同真实汉字的笔画型）：
            //    v2 算法把"实心大块"当灰尘清（真实文字 fill≤0.65 不会命中），
            //    旧建模用的实心方块本身就是算法要清的对象，不能再当文字用例。
            {
                Bitmap big = new Bitmap(400, 260, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.Clear(Color.White);
                    using (SolidBrush b = new SolidBrush(Color.Black))
                    using (Pen p = new Pen(Color.Black, 3f))
                    {
                        // 一行"汉字"：22x22 描边方块，间距 4px（200dpi 下的真实字距）
                        for (int i = 0; i < 4; i++) g.DrawRectangle(p, 30 + i * 26, 40, 22, 22);
                        // 空白处的孤立大斑：实心 22x22，周围什么都没有
                        g.FillRectangle(b, 300, 180, 22, 22);
                    }
                }
                // 阈值 600：22x22=484 会进入候选，于是全靠"上下文"判
                int removed = DocClean.RemoveSpecks(big, 600, 6);
                // 取样点在描边方块的笔画上（y=41 是顶边 3px 笔画的中线；内部是白的）
                Case("去黑点（大阈值）：成行的字块被保住（靠 4px 间距的上下文）",
                    Brightness(big, 40, 41) < 128 && Brightness(big, 118, 41) < 128,
                    "行内两处亮度 " + Brightness(big, 40, 41) + " / " + Brightness(big, 118, 41));
                Case("去黑点（大阈值）：空白处的孤立大斑被清掉",
                    Brightness(big, 310, 190) > 200, "孤立处亮度 " + Brightness(big, 310, 190));
                Case("去黑点（大阈值）：只清掉那 1 处", removed == 1, "实清 " + removed);
                big.Dispose();
            }

            // ---- dpi 推断：不能被默认值骗到 ----
            {
                Bitmap meta = Doc(300, 420, 4);      // GDI+ 新建位图的 dpi 默认是 96
                int gd = ArchiveProcessor.GuessDpi(meta);
                Case("dpi 推断：元数据是 96/72 这类默认值时按 200 兜底", gd == 200, "得 " + gd);
                meta.Dispose();

                Bitmap real = new Bitmap(1200, 1700, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(real))
                {
                    g.Clear(Color.FromArgb(252, 251, 248));
                    using (Pen p = new Pen(Color.FromArgb(30, 30, 30), 3))
                        for (int i = 0; i < 10; i++) g.DrawLine(p, 80, 120 + i * 120, 1120, 120 + i * 120);
                }
                real.SetResolution(200f, 200f);
                int gd2 = ArchiveProcessor.GuessDpi(real);
                Case("dpi 推断：真实的 200dpi 元数据被采信", gd2 == 200, "得 " + gd2);

                // ---- 端到端：跑完整条流水线 ----
                ArchiveOptions ao = new ArchiveOptions();
                ArchiveResult ares;
                Bitmap outBmp = ArchiveProcessor.Run(real, ao, out ares);
                Size a4 = Imaging.A4Size(200, false);
                Case("一键归档：输出尺寸就是标准 A4（200dpi）",
                    outBmp.Width == a4.Width && outBmp.Height == a4.Height,
                    outBmp.Width + "x" + outBmp.Height + "，应为 " + a4.Width + "x" + a4.Height);
                Case("一键归档：白底是真的白（255）",
                    Brightness(outBmp, a4.Width / 2, 20) >= 250,
                    "顶部背景亮度 " + Brightness(outBmp, a4.Width / 2, 20));
                Case("一键归档：输出只应剩黑白两种色阶", OnlyTwoLevels(outBmp), "步骤: " + ares.Steps);
                Case("一键归档：内容没被弄丢（输出有墨）", ares.InkPixels > 1000,
                    "非白像素 " + ares.InkPixels);
                real.Dispose(); outBmp.Dispose();
            }

            // ---- A4 版面：等比缩放，绝不拉变形 ----
            {
                Bitmap sq = new Bitmap(600, 600, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(sq))
                {
                    g.Clear(Color.White);
                    using (SolidBrush b = new SolidBrush(Color.Black))
                        g.FillRectangle(b, 100, 100, 400, 400);     // 正方形内容
                }
                sq.SetResolution(200f, 200f);
                ArchiveOptions ao = new ArchiveOptions();
                ao.Deskew = false; ao.AutoCrop = false; ao.NormalizeLight = false;
                ao.AutoLevels = false; ao.Binarize = false; ao.RemoveSpecks = false;
                ArchiveResult ares;
                Bitmap sqA4 = ArchiveProcessor.Run(sq, ao, out ares);
                Rectangle cb;
                Bitmap trimmed = Imaging.TrimBlank(sqA4, 40, out cb);
                double ratio = cb.Height > 0 ? cb.Width / (double)cb.Height : 0;
                Case("A4 版面：等比缩放，正方形内容放进 A4 后仍是正方形",
                    cb.Width > 0 && cb.Height > 0 && Math.Abs(ratio - 1.0) < 0.02,
                    "内容外接框 " + cb.Width + "x" + cb.Height + "，比例 " + ratio.ToString("0.000"));
                sq.Dispose(); sqA4.Dispose(); trimmed.Dispose();
            }

            // ---- 归档档位：极致档的去黑点更保守----
            // 用户原话："宁愿放弃消除小黑点，也要把字保留全了"。三条硬断言：
            //   ① 极致档清掉的块数不多于普通档；
            //   ② "字那么大"的孤立实心块：普通档清掉、极致档必须留着；
            //   ③ 页面被尘点铺满时，极致档的撤销保险整步撤销（宁可留着黑点）。
            {
                Func<Bitmap> makeSpeckPage = delegate()
                {
                    Bitmap b = new Bitmap(1200, 1700, PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        g.Clear(Color.White);
                        g.SmoothingMode = SmoothingMode.None;
                        // 正文用 40 个"十"字代替：fill 低、高 30，正好是"笔画型组件"，
                        // 让 hasRealText 门控成立（R1/R1b 才有机会出手）
                        using (Pen p = new Pen(Color.Black, 2))
                            for (int r = 0; r < 5; r++)
                                for (int c = 0; c < 8; c++)
                                {
                                    int cx = 120 + c * 130, cy = 900 + r * 130;
                                    g.DrawLine(p, cx - 10, cy, cx + 10, cy);
                                    g.DrawLine(p, cx, cy - 15, cx, cy + 15);
                                }
                        using (SolidBrush bk = new SolidBrush(Color.Black))
                        {
                            g.FillRectangle(bk, 200, 300, 40, 40);   // "字那么大"的孤立实心块
                            g.FillRectangle(bk, 600, 300, 6, 6);     // 三个孤立尘点
                            g.FillRectangle(bk, 800, 420, 6, 6);
                            g.FillRectangle(bk, 400, 620, 6, 6);
                        }
                    }
                    return b;
                };

                Bitmap sp1 = makeSpeckPage();
                int nNormal = DocClean.RemoveSpecks(sp1, 0, 0, null, true);
                int maxNormal = DocClean.LastRemovedMaxArea;
                sp1.Dispose();

                Bitmap sp2 = makeSpeckPage();
                int nSafe = DocClean.RemoveSpecks(sp2, 0, 0, null, true, true);
                int maxSafe = DocClean.LastRemovedMaxArea;
                sp2.Dispose();

                Case("去黑点：普通档能清掉孤立尘点", nNormal >= 3, "清掉 " + nNormal + " 块");
                Case("去黑点：极致档清掉的数量不多于普通档（更保守）",
                    nSafe <= nNormal, "普通 " + nNormal + " 块 / 极致 " + nSafe + " 块");
                Case("去黑点：极致档不碰「字那么大」的实心块（普通档会清）",
                    maxNormal >= 1600 && maxSafe < 400,
                    "最大单块 普通 " + maxNormal + "px / 极致 " + maxSafe + "px");
            }

            // ---- 撤销保险：尘点铺满页面时极致档整步撤销去黑点 ----
            {
                Bitmap dust = new Bitmap(1200, 1700, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(dust))
                {
                    g.Clear(Color.White);
                    g.SmoothingMode = SmoothingMode.None;
                    using (Font f = new Font("SimSun", 24f))
                        for (int i = 0; i < 6; i++)
                            g.DrawString("Restoria流水线测试", f, Brushes.Black,
                                new PointF(60, 1200 + (float)i * 60));
                    using (SolidBrush bk = new SolidBrush(Color.Black))
                        for (int r = 0; r < 15; r++)
                            for (int c = 0; c < 20; c++)
                                g.FillRectangle(bk, 40 + c * 55, 60 + r * 70, 6, 6);
                }
                dust.SetResolution(200f, 200f);

                ArchiveOptions od = new ArchiveOptions();
                ArchiveResult rd;
                Bitmap odOut = ArchiveProcessor.Run(dust, od, out rd);
                Case("撤销保险：普通档照常清掉这一页的尘点", rd.SpecksRemoved > 0,
                    "清掉 " + rd.SpecksRemoved + " 处");
                odOut.Dispose();

                ArchiveOptions ot = new ArchiveOptions();
                ot.Thorough = true;
                ArchiveResult rt;
                Bitmap otOut = ArchiveProcessor.Run(dust, ot, out rt);
                Case("撤销保险：尘点铺满时极致档整步撤销去黑点（宁可留着黑点也不吃字）",
                    rt.SpecksUndone && rt.SpecksRemoved == 0,
                    "撤销=" + rt.SpecksUndone + " 剩余清除 " + rt.SpecksRemoved + "  步骤: " + rt.Steps);
                otOut.Dispose();
                dust.Dispose();
            }

            // ---- A4 版面自动匹配内容的横竖----
            {
                Bitmap wide = new Bitmap(1700, 1200, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(wide))
                {
                    g.Clear(Color.White);
                    using (SolidBrush b = new SolidBrush(Color.Black))
                        g.FillRectangle(b, 300, 300, 1100, 600);   // 内容明显是横的
                }
                wide.SetResolution(200f, 200f);
                ArchiveOptions ow = new ArchiveOptions();
                ArchiveResult rw;
                Bitmap owOut = ArchiveProcessor.Run(wide, ow, out rw);
                Size a4Land = Imaging.A4Size(200, true);
                Case("A4 版面：内容是横的时候自动走横版 A4（不再硬塞竖版）",
                    owOut.Width == a4Land.Width && owOut.Height == a4Land.Height,
                    owOut.Width + "x" + owOut.Height + "，应为 " + a4Land.Width + "x" + a4Land.Height);
                wide.Dispose(); owOut.Dispose();
            }

            // ---- 去扫描黑框（"每一张图片四周边框部分要修"）----
            // 三条都必须成立：黑框裁掉 / 彩色封面不裁 / 贴边的深色内容不裁。
            {
                // ① 白纸 + 四周 20px 纯黑框
                Bitmap fr = new Bitmap(400, 600, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    g.Clear(Color.White);
                    using (SolidBrush b = new SolidBrush(Color.Black))
                    {
                        g.FillRectangle(b, 0, 0, 400, 20);
                        g.FillRectangle(b, 0, 580, 400, 20);
                        g.FillRectangle(b, 0, 0, 20, 600);
                        g.FillRectangle(b, 380, 0, 20, 600);
                    }
                    using (Pen p = new Pen(Color.Black, 2))
                        g.DrawLine(p, 100, 300, 300, 300);
                }
                Rectangle cut1;
                Bitmap t1 = Imaging.TrimDarkFrame(fr, out cut1);
                Case("去扫描黑框：四周的黑框被裁掉（内容不动）",
                    t1 != null && t1.Width == 360 && t1.Height == 560,
                    t1 == null ? "没裁" : (t1.Width + "x" + t1.Height + "，应为 360x560"));
                if (t1 != null) t1.Dispose();
                fr.Dispose();

                // ② 红色封面：亮度只有 81，按亮度判会被当成黑框整张裁光 —— 这是最大的坑
                Bitmap red = new Bitmap(400, 600, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(red))
                    g.Clear(Color.FromArgb(200, 30, 30));
                Rectangle cut2;
                Bitmap t2 = Imaging.TrimDarkFrame(red, out cut2);
                Case("去扫描黑框：红色封面不被误裁（亮度 81 也很暗，但不是黑框）",
                    t2 == null, t2 == null ? "未裁（正确）" : "被裁成 " + t2.Width + "x" + t2.Height);
                if (t2 != null) t2.Dispose();
                red.Dispose();

                // ③ 贴边的深色内容（一列短横线，占比不到 85%）不裁
                Bitmap side = new Bitmap(400, 600, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(side))
                {
                    g.Clear(Color.White);
                    using (SolidBrush b = new SolidBrush(Color.Black))
                        for (int i = 0; i < 10; i++) g.FillRectangle(b, 0, 20 + i * 56, 20, 30);
                }
                Rectangle cut3;
                Bitmap t3 = Imaging.TrimDarkFrame(side, out cut3);
                Case("去扫描黑框：贴边的深色内容（不连续）不裁",
                    t3 == null, t3 == null ? "未裁（正确）" : "被裁成 " + t3.Width + "x" + t3.Height);
                if (t3 != null) t3.Dispose();
                side.Dispose();
            }

            // ---- 去边缘阴影带：封面页四周的灰色扫描阴影，判"灰"不判"黑" ----
            {
                // ① 红色封面主体 + 四周 30px 灰色阴影带 → 阴影裁掉、红色保留
                Bitmap cov = new Bitmap(400, 600, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(cov))
                {
                    g.Clear(Color.FromArgb(130, 130, 130));              // 阴影（灰，低饱和）
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 30, 30)))
                        g.FillRectangle(b, 30, 30, 340, 540);            // 红色封面主体
                }
                Rectangle sc1;
                Bitmap s1 = Imaging.TrimShadowFrame(cov, out sc1);
                Case("去边缘阴影：封面四周的灰色阴影带被裁掉",
                    s1 != null && s1.Width == 340 && s1.Height == 540,
                    s1 == null ? "没裁" : (s1.Width + "x" + s1.Height + "，应为 340x540"));
                if (s1 != null) s1.Dispose();
                cov.Dispose();

                // ② 纯红色封面（没有阴影）→ 不裁
                Bitmap red2 = new Bitmap(400, 600, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(red2))
                    g.Clear(Color.FromArgb(200, 30, 30));
                Rectangle sc2;
                Bitmap s2 = Imaging.TrimShadowFrame(red2, out sc2);
                Case("去边缘阴影：纯红色封面不被误裁（鲜艳彩色不算阴影）",
                    s2 == null, s2 == null ? "未裁（正确）" : "被裁成 " + s2.Width + "x" + s2.Height);
                if (s2 != null) s2.Dispose();
                red2.Dispose();

                // ③ 纯白纸（普通页）→ 不裁
                Bitmap wp = new Bitmap(400, 600, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(wp))
                    g.Clear(Color.White);
                Rectangle sc3;
                Bitmap s3 = Imaging.TrimShadowFrame(wp, out sc3);
                Case("去边缘阴影：白纸页不受影响",
                    s3 == null, s3 == null ? "未裁（正确）" : "被裁成 " + s3.Width + "x" + s3.Height);
                if (s3 != null) s3.Dispose();
                wp.Dispose();
            }

            // ---- 印章豁免：白底黑字 + 红章保留 ----
            // 关键设计：**同时跑"开豁免"和"关豁免"两遍做对照**。
            // 只测"开了以后有红色"不够 —— 那样无法排除"别的步骤本来就没染色"。
            {
                Bitmap sp = new Bitmap(500, 700, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(sp))
                {
                    g.Clear(Color.FromArgb(250, 249, 245));
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (Pen p = new Pen(Color.FromArgb(20, 20, 20), 3))
                        for (int i = 0; i < 8; i++) g.DrawLine(p, 50, 60 + i * 40, 450, 60 + i * 40);
                    // 红色印章：圆环 + 中心方块（模拟五角星）
                    using (Pen rp = new Pen(Color.FromArgb(190, 40, 45), 8))
                        g.DrawEllipse(rp, 250, 420, 180, 180);
                    using (SolidBrush rb = new SolidBrush(Color.FromArgb(190, 40, 45)))
                        g.FillRectangle(rb, 320, 490, 40, 40);
                }
                sp.SetResolution(200f, 200f);

                ArchiveOptions on = new ArchiveOptions();
                ArchiveResult rOn;
                Bitmap a = ArchiveProcessor.Run(sp, on, out rOn);

                ArchiveOptions off = new ArchiveOptions();
                off.SealKeep = false;
                ArchiveResult rOff;
                Bitmap b = ArchiveProcessor.Run(sp, off, out rOff);

                Case("印章豁免：开启后输出里仍有偏红像素（红章保住了）",
                    rOn.RedPixels > 200,
                    "红像素 " + rOn.RedPixels + "，识别 " + rOn.SealsFound + " 枚");
                Case("印章豁免：关闭后偏红像素归零（证明红是豁免带来的，不是别处漏染）",
                    rOff.RedPixels < 50, "关豁免后红像素 " + rOff.RedPixels);
                Case("印章豁免：白底没被染红（画布角落仍是纯白）",
                    Brightness(a, 4, 4) >= 250, "角落亮度 " + Brightness(a, 4, 4));
                Case("印章豁免：内容没丢（输出仍有大量墨）",
                    rOn.InkPixels > 500, "非白像素 " + rOn.InkPixels + "   步骤: " + rOn.Steps);

                a.Dispose(); b.Dispose(); sp.Dispose();
            }

            // ---- 大面积脏块检测（PaperFill 接入用）----
            // 覆盖三件事：①渐变污渍能检出 ②掩膜盖住污渍中心 ③污渍旁的文字不进掩膜。
            // 注：用"中心深、边缘浅"的渐变斑而非纯色斑 —— 纯色大灰斑连背景场都会被它
            // 拉平（这正是第一次实现 0 检出的原因），真实霉斑从来不是纯色的。
            {
                Bitmap sg = Doc(300, 200, 0);
                using (Graphics g = Graphics.FromImage(sg))
                {
                    g.FillEllipse(new SolidBrush(Color.FromArgb(240, 240, 240)), 180, 60, 80, 80);
                    g.FillEllipse(new SolidBrush(Color.FromArgb(190, 190, 190)), 193, 73, 54, 54);
                    g.FillEllipse(new SolidBrush(Color.FromArgb(150, 150, 150)), 204, 84, 32, 32);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(30, 30, 30)), 40, 80, 20, 40);
                }
                int n = 0;
                Bitmap sm = DocClean.DetectStainMask(sg, 0, 0, 0, out n);
                bool has = sm != null && n >= 1;
                bool coversCenter = false, skipsText = true;
                if (sm != null)
                {
                    PixelBuffer mbuf = BitmapBridge.MaskToBuffer(sm);
                    coversCenter = mbuf.Data[100 * mbuf.Stride + 220] == 255;
                    skipsText = mbuf.Data[100 * mbuf.Stride + 50] == 0;
                    sm.Dispose();
                }
                Case("脏块检测：渐变污渍被检出", has, "检出 " + n + " 块");
                Case("脏块检测：掩膜盖住污渍中心", coversCenter);
                Case("脏块检测：文字位置不进掩膜", skipsText);
                sg.Dispose();
            }

            // ---- 方向检测（"只认装订孔"）----
            // 合成页带 3 个装订孔（左边缘），四种摆放验证孔位判向：
            // 正放(孔左)→不转；顺时针横躺(孔上)→270°；倒置(孔右)→180°；逆时针横躺(孔下)→90°。
            {
                Bitmap opage = OrientPage(1000, 1300);

                Orient.Result r0 = Orient.Detect(opage);
                Case("方向检测：正放页(孔在左)不转", r0.TurnsCw == 0, r0.Note);

                Bitmap cw = Imaging.Rotate90(opage, true);       // 顺时针躺倒 → 孔到上边 → 需转 3 次 90°
                Orient.Result r90 = Orient.Detect(cw);
                Case("方向检测：顺时针横躺(孔在上)判 3×90°", r90.TurnsCw == 3, r90.Note);

                Bitmap upside = Imaging.Rotate90(Imaging.Rotate90(opage, true), true);
                Orient.Result r180 = Orient.Detect(upside);
                Case("方向检测：倒置(孔在右)判 2×90°", r180.TurnsCw == 2, r180.Note);

                Bitmap ccw = Imaging.Rotate90(opage, false);     // 逆时针躺倒 → 孔到下边 → 转 1 次 90°
                Orient.Result rc = Orient.Detect(ccw);
                Case("方向检测：逆时针横躺(孔在下)判 1×90°", rc.TurnsCw == 1, rc.Note);

                // 无孔页一律不转（定案：几何判据在空表格页上全部误判）
                Bitmap noHole = new Bitmap(1000, 1300, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(noHole))
                {
                    g.Clear(Color.FromArgb(252, 251, 248));
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                    using (Font f = new Font("SimSun", 28f, FontStyle.Bold))
                        g.DrawString("卷内文件目录", f, Brushes.Black, new PointF(200, 60));
                }
                Orient.Result rnh = Orient.Detect(noHole);
                Orient.Result rnh180 = Orient.Detect(Imaging.Rotate90(Imaging.Rotate90(noHole, true), true));
                Case("方向检测：无孔页不转向（含倒置摆放）",
                    rnh.TurnsCw == 0 && rnh180.TurnsCw == 0,
                    "正放 " + rnh.TurnsCw + " / 倒置 " + rnh180.TurnsCw);
                noHole.Dispose();

                // 端到端：横躺页（带孔）跑完整条归档流水线，出来必须还是竖版 A4
                Bitmap a4in = Imaging.Rotate90(OrientPage(1000, 1300), true);
                a4in.SetResolution(200f, 200f);
                ArchiveOptions aoo = new ArchiveOptions();
                ArchiveResult aro;
                Bitmap a4out = ArchiveProcessor.Run(a4in, aoo, out aro);
                Case("一键归档：横躺页进来，出口是竖版 A4（方向已纠正）",
                    a4out.Height > a4out.Width && aro.OrientTurns > 0,
                    "输出 " + a4out.Width + "x" + a4out.Height + "，转了 " + aro.OrientTurns * 90 + "°");
                a4out.Dispose(); a4in.Dispose();
                opage.Dispose(); cw.Dispose(); upside.Dispose(); ccw.Dispose();
            }

            // ---- 红色内容保护（实战）：红笔手写 / 红色登记框 / 红头红字
            //      在档案件里**全是内容**，处理完必须还是红的。
            //      曾经的两条丢色路径：①⑦ 二值化只看亮度把红压成黑；②脏块重建把红框当污渍
            //      整块涂成纸面（实测一张红色登记框页红像素 0.217% → 0.001%）。
            {
                Bitmap rpag = new Bitmap(1000, 1300, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(rpag))
                {
                    g.Clear(Color.FromArgb(252, 251, 248));
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    // 红色登记框（模拟封面上的红色印刷表格线 / 红笔框）
                    using (Pen rpen = new Pen(Color.FromArgb(205, 30, 35), 5f))
                    {
                        g.DrawRectangle(rpen, 300, 130, 400, 170);
                        g.DrawLine(rpen, 500, 130, 500, 300);
                        g.DrawLine(rpen, 300, 215, 700, 215);
                    }
                    // 黑色正文（顺带守"黑字没被红保护带跑"）
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                    Font rf = new Font("SimSun", 26f, FontStyle.Bold);
                    string[] rlines = {
                        "霍城县教育局委员会文件，",
                        "关于表彰先进集体和个人的决定。",
                        "经研究决定、予以通报表彰；",
                        "请各处室遵照执行并落实。",
                        "二〇〇七年十月二十六日归档",
                    };
                    for (int i = 0; i < rlines.Length; i++)
                        g.DrawString(rlines[i], rf, Brushes.Black, new PointF(70, 430 + i * 150));
                    rf.Dispose();
                }
                rpag.SetResolution(200f, 200f);
                int redIn = CountRedPixels(rpag, 40);
                ArchiveOptions ropt = new ArchiveOptions();
                ArchiveResult rres;
                Bitmap rout = ArchiveProcessor.Run(rpag, ropt, out rres);
                int redOut = CountRedPixels(rout, 40);
                Case("红色内容保护：红框/红字处理后仍是红色（红像素保留 ≥60%）",
                    redIn > 500 && redOut >= redIn * 6 / 10,
                    "输入红像素 " + redIn + " → 输出 " + redOut);
                rout.Dispose(); rpag.Dispose();
            }

            // ---- 文字邻域保护（最大限度保护文字"）----
            // 二值化会把细笔画打断（淡墨/褪色/缩放灰边），碎片"孤立 + 实心"正好命中
            // R1/R2 → 字的**部件被整块当黑点删掉**（用户："被当成黑点从而消除了字的一部分"）。
            // 这条用例守两侧：紧贴文字的小块**必须保留**、远处的孤立黑点**必须照删**。
            {
                Bitmap sp = new Bitmap(800, 1000, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(sp))
                {
                    g.Clear(Color.White);
                    // "文字主体"：粗笔画方框（模拟汉字的部件），右边缘 x=300
                    using (Pen p = new Pen(Color.Black, 20f))
                        g.DrawRectangle(p, 110, 110, 180, 180);
                    using (SolidBrush b = new SolidBrush(Color.Black))
                    {
                        // 紧贴主体的 18x18 小块（离主体的墨约 3~5px）：模拟被打断的笔画碎片。
                        // 距离是刻意选的：> 孤立判据的 gap(3px) 所以它"看着孤立"，
                        // 但 < 文字邻域半径 gr(~7px) 所以必须被护住 —— 正是这次要防的场景。
                        g.FillRectangle(b, 304, 180, 18, 18);
                        // 远处孤立块（同尺寸）：真正的纸屑，应该被删
                        g.FillRectangle(b, 600, 800, 18, 18);
                    }
                }
                int hugBefore = CountInk(sp, 304, 180, 18, 18);
                int farBefore = CountInk(sp, 600, 800, 18, 18);
                DocClean.RemoveSpecks(sp, 500, 0, null, true);
                int hugAfter = CountInk(sp, 304, 180, 18, 18);
                int farAfter = CountInk(sp, 600, 800, 18, 18);
                Case("去黑点护字：紧贴文字的小块被保留（不再当黑点删）",
                    hugBefore > 200 && hugAfter == hugBefore,
                    "紧贴块 清理前 " + hugBefore + " 墨 → 清理后 " + hugAfter +
                    "；护字命中 " + DocClean.LastTextGuarded + " 处");
                Case("去黑点护字：远处孤立黑点照常清除",
                    farBefore > 200 && farAfter == 0,
                    "远处块 清理前 " + farBefore + " 墨 → 清理后 " + farAfter);
                sp.Dispose();
            }

            // ---- 红框内容保护（"把红框子里的黑色字样块也要保护，不能删"）----
            // 误删机理（登记表页实测）：① 框线是**浅红细线**（如 (250,190,195)，按亮度接近纸面），
            // 在"墨"的语义里不算内容 → 框里的黑笔填写块在 gap 环带内找不到别的墨，被判"孤立"；
            // ② 手写短字块又紧实近方，正好命中 R1"孤立实心斑"。两条一合 = 框里内容整块被抹。
            // 大半径护字（guardR ≈ 短边/100）够不到：格子一大，字块离框线就超过那个半径。
            // 用例守两侧：框内紧实黑块**必须留**、框外同样尺寸的孤立块**照删**。
            {
                // 建两次同样的页：一张跑"有保护"、一张跑"无保护"（对照，证明这条保护是承重的）
                Func<Bitmap> buildRedBox = delegate()
                {
                    Bitmap p = new Bitmap(900, 1100, PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(p))
                    {
                        g.Clear(Color.White);
                        // 浅红细框线（褪色红：亮度高，二值化前根本不算墨）
                        using (Pen rp = new Pen(Color.FromArgb(250, 190, 195), 3f))
                            g.DrawRectangle(rp, 120, 120, 620, 460);
                        using (SolidBrush b = new SolidBrush(Color.Black))
                        {
                            // 框内"字样块"：46x46 实心（离框线 200px，护字半径 9px 够不到）
                            g.FillRectangle(b, 380, 330, 46, 46);
                            // 框外空白处同尺寸块：真垃圾
                            g.FillRectangle(b, 380, 800, 46, 46);
                        }
                        // 正文：给文本类型守卫（hasRealText）提供笔画型组件
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                        using (Font f = new Font("SimSun", 22f))
                            for (int i = 0; i < 3; i++)
                                g.DrawString("霍城县教育局党支部换届选举工作报告", f, Brushes.Black,
                                             new PointF(60, 950 + i * 45));
                    }
                    return p;
                };

                Bitmap rb = buildRedBox();
                int inBefore = CountInk(rb, 380, 330, 46, 46);
                int outBefore = CountInk(rb, 380, 800, 46, 46);
                int encl;
                byte[] em = DocClean.RedEncloseMask(rb, 40, 0, out encl);
                Case("红框内容保护：认得出「被红框围住」的内部",
                    em != null && encl > 200000 && em[800 * 900 + 380] == 0,
                    "内部像素 " + encl + "；框外同位置掩膜值 " +
                    (em == null ? -1 : (int)em[800 * 900 + 380]));

                Bitmap rb2 = (Bitmap)rb.Clone();
                DocClean.RemoveSpecks(rb2, 2000, 0, null, true);              // 无保护（对照）
                int inNoProt = CountInk(rb2, 380, 330, 46, 46);
                DocClean.RemoveSpecks(rb, 2000, 0, em, true);                // 有保护
                int inAfter = CountInk(rb, 380, 330, 46, 46);
                int outAfter = CountInk(rb, 380, 800, 46, 46);
                Case("红框内容保护：无保护时框里的字样块确实会被误删（对照）",
                    inBefore > 1500 && inNoProt == 0,
                    "框内块 清理前 " + inBefore + " 墨 → 无保护清理后 " + inNoProt);
                Case("红框内容保护：加保护后框里的字样块被保住",
                    inAfter == inBefore,
                    "框内块 清理前 " + inBefore + " 墨 → 有保护清理后 " + inAfter);
                Case("红框内容保护：框外的孤立黑块照常清除（保护不外溢）",
                    outBefore > 1500 && outAfter == 0,
                    "框外块 清理前 " + outBefore + " 墨 → 清理后 " + outAfter);
                rb2.Dispose(); rb.Dispose();

                // 整条流水线对照：开/关红框保护，输出的墨量差就是被保住的那块内容
                int inkOn = 0, inkOff = 0;
                Bitmap pOn = buildRedBox(), pOff = buildRedBox();
                pOn.SetResolution(200f, 200f); pOff.SetResolution(200f, 200f);
                ArchiveOptions oa = new ArchiveOptions();
                // 只测"去黑点/脏块重建"这两段对红框内容的处理：关掉镜像(OCR)与方向检测，
                // 免得合成页被 OCR 或几何判据翻转/旋转，让墨量对照失去意义。
                oa.MirrorCheck = false; oa.OrientAuto = false;
                ArchiveResult ra1, ra2;
                Bitmap rOn = ArchiveProcessor.Run(pOn, oa, out ra1);
                inkOn = ra1.InkPixels;
                oa.RedBoxProtect = false;
                Bitmap rOff = ArchiveProcessor.Run(pOff, oa, out ra2);
                inkOff = ra2.InkPixels;
                Case("红框内容保护：整条流水线跑完，框里的字样块仍在（开关墨量对照）",
                    inkOn > inkOff + 800 && ra1.RedPixels > 300,
                    "墨量 开保护 " + inkOn + " vs 关保护 " + inkOff +
                    "（差 " + (inkOn - inkOff) + "）；红像素 " + ra1.RedPixels);
                rOn.Dispose(); rOff.Dispose(); pOn.Dispose(); pOff.Dispose();
            }

            // 页边垃圾清除：顶条/黑角/装订孔被清，正文与装订线文字不动。
            // 守两条安全边界：实心度 ≥0.5 才清（汉字笔画填充率 0.2~0.4 绝不命中）、
            // 贴边条限"细条或角块"。
            {
                Bitmap ep = new Bitmap(800, 1100, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(ep))
                {
                    g.Clear(Color.FromArgb(250, 249, 245));
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                    using (Font f = new Font("SimSun", 22f))
                    {
                        g.DrawString("卷内文件目录", f, Brushes.Black, new PointF(300, 200));
                        g.DrawString("一一九九八年档案", f, Brushes.Black, new PointF(280, 700));
                    }
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(20, 20, 20)))
                    {
                        g.FillRectangle(b, 150, 0, 420, 26);          // 顶边黑条（贴框）
                        g.FillEllipse(b, 28, 300, 30, 30);            // 装订孔 ×3（左边带）
                        g.FillEllipse(b, 28, 550, 30, 30);
                        g.FillEllipse(b, 28, 800, 30, 30);
                    }
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb(30, 30, 30)))
                        g.FillPolygon(tb, new PointF[] { new PointF(0, 1099), new PointF(0, 1000), new PointF(110, 1099) }); // 左下黑角
                }
                Func<Bitmap, Rectangle, int> darkCount = delegate(Bitmap bm, Rectangle rc)
                {
                    PixelBuffer pb2 = BitmapBridge.ToBuffer(bm);
                    int c = 0;
                    for (int y = rc.Y; y < rc.Bottom; y++)
                        for (int x = rc.X; x < rc.Right; x++)
                        {
                            int p = y * pb2.Stride + x * 3;
                            if ((pb2.Data[p + 2] * 77 + pb2.Data[p + 1] * 150 + pb2.Data[p] * 29) >> 8 < 150) c++;
                        }
                    return c;
                };
                Rectangle tr1 = new Rectangle(280, 190, 240, 60), tr2 = new Rectangle(260, 690, 300, 60);
                int textBefore = darkCount(ep, tr1) + darkCount(ep, tr2);
                int junk = DocClean.RemoveEdgeJunk(ep);
                int textAfter = darkCount(ep, tr1) + darkCount(ep, tr2);
                Case("页边垃圾：贴边黑条+黑角+装订孔被清（≥5 处）", junk >= 5, "清了 " + junk + " 处");
                Case("页边垃圾：顶条位置变白", Brightness(ep, 350, 10) > 200, "亮度 " + Brightness(ep, 350, 10));
                Case("页边垃圾：装订孔位置变白",
                    Brightness(ep, 43, 315) > 200 && Brightness(ep, 43, 565) > 200 && Brightness(ep, 43, 815) > 200,
                    "三孔亮度 " + Brightness(ep, 43, 315) + "/" + Brightness(ep, 43, 565) + "/" + Brightness(ep, 43, 815));
                Case("页边垃圾：黑角位置变白", Brightness(ep, 15, 1085) > 200, "亮度 " + Brightness(ep, 15, 1085));
                Case("页边垃圾：正文文字未被误伤（文字区墨量分毫未变）",
                    textBefore > 50 && textAfter == textBefore,
                    "清理前 " + textBefore + " / 清理后 " + textAfter);
                ep.Dispose();
            }

            // 环状装订孔（实测 实测样本 左侧 4 块）：扫描孔是
            // **透光**的 —— 二值图上是"圆环"（中心亮、撕裂边黑），fill 只有 0.2 左右，
            // 实心门控（大孔 ≥0.35 / 小针孔 ≥0.50）整类漏检。新路径：bbox 中心有亮洞。
            {
                Bitmap rp = new Bitmap(800, 1100, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(rp))
                {
                    g.Clear(Color.FromArgb(250, 249, 245));
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(20, 20, 20)))
                    {
                        g.FillEllipse(b, 40, 250, 46, 46);       // 透光装订孔 ×3（外环黑）
                        g.FillEllipse(b, 40, 550, 46, 46);
                        g.FillEllipse(b, 40, 850, 46, 46);
                    }
                    using (SolidBrush w2 = new SolidBrush(Color.FromArgb(250, 249, 245)))
                    {
                        g.FillEllipse(w2, 46, 256, 34, 34);      // 内洞透光（纸色，环厚 ~6px → fill≈0.36）
                        g.FillEllipse(w2, 46, 556, 34, 34);
                        g.FillEllipse(w2, 46, 856, 34, 34);
                    }
                    // 页边浅灰手写（铅笔实际亮度 90~120，"真黑"占比为 0）→ 必须保留
                    using (SolidBrush gb = new SolidBrush(Color.FromArgb(110, 110, 114)))
                    {
                        g.FillRectangle(gb, 90, 380, 8, 45);
                        g.FillRectangle(gb, 90, 380, 22, 6);
                    }
                }
                int junk3 = DocClean.RemoveEdgeJunk(rp, true);
                Case("环状孔：透光装订孔（圆环）被清（成簇 3 孔）",
                    Brightness(rp, 43, 273) > 200 && Brightness(rp, 43, 573) > 200 && Brightness(rp, 43, 873) > 200,
                    "环上三点亮度 " + Brightness(rp, 43, 273) + "/" + Brightness(rp, 43, 573) + "/" + Brightness(rp, 43, 873) + "，清 " + junk3 + " 处");
                Case("环状孔：页边浅灰手写不被误清（真黑占比 0）",
                    Brightness(rp, 94, 400) < 180, "手写处亮度 " + Brightness(rp, 94, 400));
                rp.Dispose();
            }

            // 紧裁页回归（实测"整块吃字"的合成复现）：
            // 纸边裁得紧、第一列字落在左右 12% 边带内 —— 旧版"边带整带清空"与
            // 放宽后的"装订孔成簇"都会把它整列端掉（合成页每行开头被切掉一截）。
            // 现在的判据：①不再按边带整块删 ②孔候选必须有"实心团核心"（粗体/
            // 放大字的细长条核心不达标）。此用例断言：紧裁页文字**一字不少**。
            {
                Bitmap tp = new Bitmap(900, 1200, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(tp))
                {
                    g.Clear(Color.FromArgb(250, 249, 245));
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                    using (Font f = new Font("SimSun", 24f, FontStyle.Bold))
                    {
                        // 文字从 x=6 起（远在 12% 边带内），模拟紧裁扫描件
                        g.DrawString("序号", f, Brushes.Black, new PointF(6, 120));
                        g.DrawString("姓名", f, Brushes.Black, new PointF(6, 380));
                        g.DrawString("日期", f, Brushes.Black, new PointF(6, 640));
                        g.DrawString("页号", f, Brushes.Black, new PointF(6, 900));
                    }
                }
                PixelBuffer pb = BitmapBridge.ToBuffer(tp);
                Func<Rectangle, int> inkOf = delegate(Rectangle rc)
                {
                    int c = 0;
                    for (int y = rc.Y; y < rc.Bottom; y++)
                        for (int x = rc.X; x < rc.Right; x++)
                        {
                            int p = y * pb.Stride + x * 3;
                            if ((pb.Data[p + 2] * 77 + pb.Data[p + 1] * 150 + pb.Data[p] * 29) >> 8 < 150) c++;
                        }
                    return c;
                };
                Rectangle bandL = new Rectangle(0, 100, 100, 900);   // 左 11% 边带
                int tightBefore = inkOf(bandL);
                DocClean.RemoveEdgeJunk(tp, true);
                int tightAfter = inkOf(bandL);
                Case("紧裁页：边带内的文字未被整列清除（墨量分毫未变）",
                    tightBefore > 100 && tightAfter == tightBefore,
                    "清理前 " + tightBefore + " / 清理后 " + tightAfter);
                tp.Dispose();
            }

            // 小针孔残留（实测"装订孔有残留"）：
            // Φ10~25 的针孔在 R=5 腐蚀（要求 11×11 全墨）里**没有核心** → 整类漏检。
            // 实测"头"批 7 页每页残留 1~4 个（10×17 / fill 0.73 的实心深点，左侧边带最集中）。
            // 用例守两侧：①两个"小实心深点"在左边带成列 → 必须清
            //             ②同样大小的**细笔画数字**（铅笔写的 "0.15"，fill 低）→ 必须留
            {
                Bitmap hp = new Bitmap(800, 1100, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(hp))
                {
                    g.Clear(Color.FromArgb(250, 249, 245));
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(30, 30, 30)))
                    {
                        // 两个小针孔（12×17 实心）—— 左边带内、纵向成列
                        g.FillRectangle(b, 40, 300, 12, 17);
                        g.FillRectangle(b, 40, 420, 12, 17);
                    }
                    // 细笔画手写数字（铅笔 "0.15" 那种）：fill 低（< 0.5）→ 必须留。
                    // 刻意摆在**边带之内**（x ≤ 80 < 边带右界 96）：确保它真的被小孔判据看过一遍，
                    // 这个用例才有意义（摆到边带外就等于没测）。
                    using (Pen p = new Pen(Color.FromArgb(110, 110, 110), 3f))
                    {
                        g.DrawEllipse(p, 32, 600, 14, 22);     // "0"
                        g.DrawLine(p, 52, 610, 60, 600);        // "."
                        g.DrawLine(p, 64, 600, 64, 622);        // "1"
                        g.DrawLine(p, 64, 600, 74, 600);
                        g.DrawLine(p, 78, 600, 90, 600);        // "5"
                        g.DrawLine(p, 78, 600, 78, 610);
                        g.DrawLine(p, 78, 610, 90, 610);
                        g.DrawLine(p, 90, 610, 90, 622);
                        g.DrawLine(p, 90, 622, 78, 622);
                    }
                }
                int junk2 = DocClean.RemoveEdgeJunk(hp);
                Func<int> strokeLeft = delegate()
                {
                    PixelBuffer pb2 = BitmapBridge.ToBuffer(hp);
                    int c = 0;
                    for (int y = 595; y < 630; y++)
                        for (int x = 26; x < 96; x++)
                        {
                            int p = y * pb2.Stride + x * 3;
                            if ((pb2.Data[p + 2] * 77 + pb2.Data[p + 1] * 150 + pb2.Data[p] * 29) >> 8 < 150) c++;
                        }
                    return c;
                };
                // 清理后重新取一次（Brightness 用的是像素级读，这里要新 buffer）
                int strokeAfter = strokeLeft();
                Case("小针孔：Φ12~17 的小孔渣被清掉（不再漏检）",
                    Brightness(hp, 45, 308) > 200 && Brightness(hp, 45, 428) > 200 && junk2 >= 2,
                    "两孔亮度 " + Brightness(hp, 45, 308) + "/" + Brightness(hp, 45, 428) + "，清了 " + junk2 + " 处");
                Case("小针孔：同尺寸的细笔画数字（fill 低）未被误清",
                    strokeAfter > 60,
                    "细笔画区剩余墨 " + strokeAfter + " px");
                hp.Dispose();
            }

            // 彩色封面页守卫（实测样本 红封面被洗白的回归）：
            // 整页 25%+ 饱和彩色 → 不自动转向；归档流水线里整页原样跳过。
            {
                Bitmap cp = new Bitmap(800, 1100, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(cp))
                {
                    g.Clear(Color.FromArgb(212, 22, 28));           // 红封面底色
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (Font f = new Font("SimSun", 30f, FontStyle.Bold))
                        g.DrawString("基层党组织组织生活记录本", f, Brushes.White, new PointF(60, 200));
                }
                double ratio = Orient.ColoredRatio(cp);
                Orient.Result cr = Orient.Detect(cp);
                Case("彩色封面：彩色占比识别（>25%）", ratio > 0.5,
                    "彩色占比 " + (ratio * 100).ToString("0") + "%");
                Case("彩色封面：方向不自动转（避免误转 180°）", cr.TurnsCw == 0,
                    "turns=" + cr.TurnsCw + "  " + cr.Note);
                cp.Dispose();
            }

            // 装订孔判向（物理证据）：孔在右 → 180°；孔在左 → 不转。
            // 孔 = 近圆实心深色斑，直径 34px，位于左右边带中部（避开四角）。
            // 注意：测"孔在右"要用**只在右侧有孔**的页（OrientPage 自带 3 个左侧孔，
            // 直接用会左右打平、判不出方向）。
            {
                Bitmap hp = OrientPage(1000, 1300);
                using (Graphics g = Graphics.FromImage(hp))
                using (SolidBrush hb = new SolidBrush(Color.FromArgb(250, 250, 250)))
                {   // 先把合成页自带的左侧 3 孔涂掉，只在右侧画 3 孔
                    for (int k = 0; k < 3; k++)
                        g.FillEllipse(hb, 1000 * 0.03f - 4, 1300 * (0.22f + 0.28f * k) - 4, 42, 42);
                }
                using (Graphics g = Graphics.FromImage(hp))
                using (SolidBrush hb = new SolidBrush(Color.FromArgb(25, 25, 25)))
                {
                    g.FillEllipse(hb, 940, 400, 34, 34);
                    g.FillEllipse(hb, 940, 700, 34, 34);
                    g.FillEllipse(hb, 940, 1000, 34, 34);
                }
                Orient.Result hr = Orient.Detect(hp);
                Case("方向检测：装订孔在右判 180°", hr.TurnsCw == 2, hr.Note);
                hp.Dispose();

                Bitmap hp2 = OrientPage(1000, 1300);
                using (Graphics g = Graphics.FromImage(hp2))
                using (SolidBrush hb2 = new SolidBrush(Color.FromArgb(25, 25, 25)))
                {
                    g.FillEllipse(hb2, 26, 400, 34, 34);
                    g.FillEllipse(hb2, 26, 700, 34, 34);
                    g.FillEllipse(hb2, 26, 1000, 34, 34);
                }
                Orient.Result hl = Orient.Detect(hp2);
                Case("方向检测：装订孔在左判不转", hl.TurnsCw == 0, hl.Note);
                hp2.Dispose();
            }

            // ---- 纯印章页跳过：只有章没有正文的页，原样放行 ----
            {
                Bitmap sealPg = new Bitmap(500, 700, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(sealPg))
                {
                    g.Clear(Color.FromArgb(250, 249, 245));
                    using (Pen rp = new Pen(Color.FromArgb(190, 40, 45), 8))
                        g.DrawEllipse(rp, 160, 260, 180, 180);
                }
                sealPg.SetResolution(200f, 200f);
                ArchiveOptions sopt = new ArchiveOptions();
                ArchiveResult sres;
                Bitmap sout = ArchiveProcessor.Run(sealPg, sopt, out sres);
                // 改：跳过的是"内容处理"，版式照做（排进 A4，尺寸与其余成品一致）。
                // 所以不再断言"尺寸不变/逐像素一致"，改成断言**内容没被破坏**：
                //   · 识别为纯印章页；
                //   · 输出是标准 A4；
                //   · 印章还是红的（没被二值化吃掉）——这才是"跳过"真正要守的东西。
                Case("纯印章页：被识别并跳过内容处理",
                    sres.SkippedSealOnly, sres.Steps);
                Size a4s = Imaging.A4Size(200, false);
                Case("纯印章页：跳过页也排进标准 A4（尺寸与其余成品一致）",
                    sout.Width == a4s.Width && sout.Height == a4s.Height,
                    sout.Width + "x" + sout.Height + "，应为 " + a4s.Width + "x" + a4s.Height);
                Case("纯印章页：印章没被二值化吃掉（输出里仍有偏红像素）",
                    sres.RedPixels > 100, "红像素 " + sres.RedPixels);
                sealPg.Dispose(); sout.Dispose();

                // 对照：有正文的页绝不能被误判成纯印章页（守"跳过"的误伤边界）
                Bitmap textPg = OrientPage(500, 700);
                double dr, rr;
                Case("纯印章页：带正文的页不误判", !Orient.IsSealOnlyPage(textPg, out dr, out rr),
                    "墨 " + (dr * 100).ToString("0.00") + "% 章 " + (rr * 100).ToString("0.00") + "%");
                textPg.Dispose();
            }

            // ---- OCR 镜像检测：读得顺=正的，读不顺翻过来读 ----
            // 没装 OCR 素材（ai\ocr\）时优雅跳过：镜像检测本来就是可选叠加。
            if (Ocr.Available)
            {
                Bitmap opage = OrientPage(1000, 1300);
                bool? vUp = Ocr.MirrorVerdict(opage);
                Case("OCR 镜像检测：正放页判「非镜像」", vUp == false,
                    vUp == null ? "证据不足" : "正常");
                Bitmap mp = Imaging.Flip(opage, true);
                bool? vMir = Ocr.MirrorVerdict(mp);
                Case("OCR 镜像检测：镜像页判「镜像」", vMir == true,
                    vMir == null ? "证据不足" : "检出镜像");
                opage.Dispose(); mp.Dispose();
            }
            else
            {
                Known("OCR 镜像检测用例跳过", Ocr.StatusText);
            }

            // ---- AI 素材状态（没有素材不算失败；这里同时验证"隔离探测"这条安全链） ----
            string ai = AiFill.StatusText;          // 廉价状态：不加载 DLL
            log.AppendLine("  [信息] AI: " + ai);
            if (AiFill.Available)
            {
                bool rt = AiFill.ProbeRuntime();    // 真加载一次，但在子进程里
                log.AppendLine("  [信息] AI 隔离探测: " + (rt ? "通过" : "未通过")
                    + (AiFill.ProbeVersion != null && AiFill.ProbeVersion.Length > 0 ? "  版本 " + AiFill.ProbeVersion : "")
                    + (rt ? "" : "  原因: " + AiFill.RuntimeProbeError));
            }

            // ---- 交互层判据：空格键"什么时候是修复、什么时候是平移" ----
            // 这段判据以前写错（无条件当修复），导致"按住空格+拖动平移"成了死手势
            // （手册写的是"没有修复动作时才是平移"）。抽成纯函数后在这里钉死。
            Case("交互：有修复动作时，空格=开始修复",
                MainForm.DecideInpaintKey(true, System.Windows.Forms.Keys.Space, true)
                    == MainForm.InpaintKeyAction.Fix);
            Case("交互：无修复动作时，空格=按住平移（不再误当修复）",
                MainForm.DecideInpaintKey(false, System.Windows.Forms.Keys.Space, true)
                    == MainForm.InpaintKeyAction.Pan);
            Case("交互：没打开图时空格无从平移，给提示",
                MainForm.DecideInpaintKey(false, System.Windows.Forms.Keys.Space, false)
                    == MainForm.InpaintKeyAction.Hint);
            Case("交互：把修复键改成别的键后，该键无动作时仍只给提示",
                MainForm.DecideInpaintKey(false, System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.P, true)
                    == MainForm.InpaintKeyAction.Hint);

            // ---- 交互层判据：「开始修复」按钮什么时候可点 ----
            // 踩过的坑：心跳里写成 `... && !busy`，修复一开始（≤300ms）就把按钮置灰，
            // 于是"点按钮=取消修复"成了死交互（只能靠再按一次空格）。这里钉死：
            // 忙碌中，只有"这次忙碌就是修复自己"时才保持可点。
            Case("交互：没打开图时，修复按钮不可点",
                !MainForm.DecideInpaintEnabled(false, false, false, false));
            Case("交互：有图但没圈区域时，修复按钮不可点",
                !MainForm.DecideInpaintEnabled(true, false, false, false));
            Case("交互：有图有区域且空闲时，修复按钮可点",
                MainForm.DecideInpaintEnabled(true, true, false, false));
            Case("交互：修复进行中，修复按钮=取消，必须仍可点",
                MainForm.DecideInpaintEnabled(true, true, true, true));
            Case("交互：别的任务忙碌时（不是修复），修复按钮不可点",
                !MainForm.DecideInpaintEnabled(true, true, true, false));

            // ---- 交互层判据：撤销深度与恢复落点 ----
            // 踩过的坑（实测）：①UNDO_MAX=2 → "撤销只能撤销两次"（现已 10 步+字节帽，
            //   全流程由 --undotest 诊断口验证）；②恢复图落在桌面/源目录（现统一
            //   %LOCALAPPDATA%\PicTool\已恢复\<时间戳>）。这里钉死恢复落点的硬约束。
            try
            {
                string rf = MainForm.RecoveryTargetFolder();
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) ?? "";
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ?? "";
                bool inAppData = appData.Length > 0 && rf.StartsWith(appData, StringComparison.OrdinalIgnoreCase);
                bool notDesktop = desk.Length == 0 || !rf.StartsWith(desk, StringComparison.OrdinalIgnoreCase);
                Case("交互：异常恢复的落点在程序自己的 AppData，永不写桌面",
                    inAppData && notDesktop, "落点: " + rf);
            }
            catch (Exception ex)
            {
                Case("交互：异常恢复的落点在程序自己的 AppData，永不写桌面",
                    false, "钩子异常: " + ex.Message);
            }

            // ---- 交互层判据：快捷键设置窗"等待态"的保留键守卫 ----
            // 踩过的坑（本轮）：等待态只排除 Esc/Back/Delete/修饰键，于是
            //   ① Enter/Tab 能被绑走 —— 废掉所有对话框默认键/焦点遍历；
            //   ② 固定功能键（1-4/0/Ctrl+O/F2/Delete）能被绑走 —— 与写死的固定处理双触发。
            // 无头构造设置窗实测：必须拒的一个不放、普通键照常能绑（不是一律拒绝）。
            try
            {
                using (ShortcutSettingsForm sf = new ShortcutSettingsForm())
                {
                    string armGuard = sf.ProbeArmGuards();
                    Case("交互：快捷键等待态拒绝 Enter/Tab 与固定键，普通键仍可绑",
                        armGuard == null, armGuard == null ? null : armGuard);
                }
            }
            catch (Exception ex)
            {
                Case("交互：快捷键等待态拒绝 Enter/Tab 与固定键，普通键仍可绑",
                    false, "钩子本身抛异常: " + ex.Message);
            }

            log.AppendLine();
            log.AppendLine("合计: 通过 " + nPass + " 项，失败 " + nFail + " 项");
            log.AppendLine(nFail == 0 ? "结论: 自检通过" : "结论: 有失败项，请看上面 [失败] 行");
            return log.ToString();
        }

        static byte[] Pixels(Bitmap b)
        {
            PixelBuffer buf = BitmapBridge.ToBuffer(b);
            byte[] o = new byte[b.Width * b.Height * 3];
            for (int y = 0; y < b.Height; y++)
                Buffer.BlockCopy(buf.Data, y * buf.Stride, o, y * b.Width * 3, b.Width * 3);
            return o;
        }

        static bool PixelsEqual(Bitmap a, Bitmap b)
        {
            if (a == null || b == null || a.Width != b.Width || a.Height != b.Height) return false;
            byte[] pa = Pixels(a), pb = Pixels(b);
            for (int i = 0; i < pa.Length; i++) if (pa[i] != pb[i]) return false;
            return true;
        }

        static bool OnlyTwoLevels(Bitmap b)
        {
            int[] seen = new int[256];
            PixelBuffer buf = BitmapBridge.ToBuffer(b);
            for (int y = 0; y < b.Height; y++)
            {
                int ro = y * buf.Stride;
                for (int x = 0; x < b.Width; x++) seen[buf.Data[ro + x * 3]] = 1;
            }
            int n = 0;
            for (int i = 0; i < 256; i++) if (seen[i] != 0) n++;
            return n <= 3;
        }

        static int Brightness(Bitmap b, int x, int y)
        {
            if (x < 0 || y < 0 || x >= b.Width || y >= b.Height) return -1;
            Color c = b.GetPixel(x, y);              // 单点读取：GetPixel 足够，不值得全图过桥
            return (c.B + c.G + c.R) / 3;
        }

        // 数矩形区域内的墨像素（去黑点护字用例用）
        static int CountInk(Bitmap b, int x, int y, int w, int h)
        {
            if (b == null) return 0;
            PixelBuffer pb = BitmapBridge.ToBuffer(b);
            int n = 0;
            for (int yy = y; yy < y + h && yy < b.Height; yy++)
                for (int xx = x; xx < x + w && xx < b.Width; xx++)
                {
                    int p = yy * pb.Stride + xx * 3;
                    if (((pb.Data[p + 2] * 77 + pb.Data[p + 1] * 150 + pb.Data[p] * 29) >> 8) < 128) n++;
                }
            return n;
        }

        // 数"偏红"像素（R - max(G,B) >= margin）—— 红色内容保护的回归判据。
        // 判据与 DocClean.RedProtectMask / ThresholdKeepRed 保持一致。
        static int CountRedPixels(Bitmap b, int margin)
        {
            if (b == null) return 0;
            PixelBuffer pb = BitmapBridge.ToBuffer(b);
            int n = 0;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                {
                    int p = y * pb.Stride + x * 3;
                    int bb = pb.Data[p], g = pb.Data[p + 1], r = pb.Data[p + 2];
                    int mx = g > bb ? g : bb;
                    if (r - mx >= margin) n++;
                }
            return n;
        }

        // 一块矩形区域的平均亮度（24bpp）
        static int RegionMean(Bitmap b, int x, int y, int w, int h)
        {
            Rectangle rc = Rectangle.Intersect(new Rectangle(x, y, w, h), new Rectangle(0, 0, b.Width, b.Height));
            if (rc.Width <= 0 || rc.Height <= 0) return -1;
            PixelBuffer buf = BitmapBridge.ToBuffer(b);
            long sum = 0; long n = 0;
            for (int yy = 0; yy < rc.Height; yy++)
            {
                int ro = (rc.Y + yy) * buf.Stride + rc.X * 3;
                for (int xx = 0; xx < rc.Width; xx++)
                {
                    int p = ro + xx * 3;
                    sum += (buf.Data[p] + buf.Data[p + 1] + buf.Data[p + 2]) / 3;
                    n++;
                }
            }
            return n == 0 ? -1 : (int)(sum / n);
        }

        // 两张同尺寸位图在 mask 之外逐像素不同的个数（24bpp）
        static int CountDiffOutside(Bitmap a, Bitmap b, Rectangle inside)
        {
            if (a.Width != b.Width || a.Height != b.Height) return -1;
            int w = a.Width, h = a.Height;
            int n = 0;
            PixelBuffer ba = BitmapBridge.ToBuffer(a);
            PixelBuffer bb = BitmapBridge.ToBuffer(b);
            for (int y = 0; y < h; y++)
            {
                int ra = y * ba.Stride, rb = y * bb.Stride;
                for (int x = 0; x < w; x++)
                {
                    if (x >= inside.Left && x < inside.Right && y >= inside.Top && y < inside.Bottom) continue;
                    if (ba.Data[ra + x * 3] != bb.Data[rb + x * 3] ||
                        ba.Data[ra + x * 3 + 1] != bb.Data[rb + x * 3 + 1] ||
                        ba.Data[ra + x * 3 + 2] != bb.Data[rb + x * 3 + 2])
                        n++;
                }
            }
            return n;
        }
    }
}
