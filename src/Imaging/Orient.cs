// Orient.cs — 归档流水线的方向检测（几何版）与纯印章页判定
//
// 职责两件事：
//   ① 横躺（90°/270°）与倒置（180°）的检测与纠正 —— **纯几何、无损旋转**
//   ② 纯印章页识别（整页只有章、没有正文 → 归档流水线整条跳过，用户手动修）
//
// 为什么镜像不在这里判：真实公文带落款/缩进/参差行，几何统计会误报
//   （实测 upright 页被判成镜像）；镜像的可靠证据是"OCR 读不顺"，在 Ocr.cs。
//
// 为什么 180° 用"标点重力"判：中文标点（，。、；）在字格里都沉在**下半**，
//   页面倒过来它们集体跑到行带上半 —— 统计"标点尺寸连通域中心落在行带下
//   半"的占比，正放 >0.5，倒放 <0.4，界线分明（实测正放 0.50~0.55，倒放 0.35）。
//   （RapidOCR 的行方向分类器能扛 180° 旋转所以 OCR 判不了倒置——几何是唯一证据，
//     与 Python 原型 6 例自测的结论一致。）
//
// 红线遵守：像素访问全走 BitmapBridge；90°/180° 旋转用 Imaging.Rotate90（无损重排）。
using System;
using System.Collections.Generic;
using System.Drawing;

namespace PicTool
{
    public static class Orient
    {
        public sealed class Result
        {
            public int TurnsCw;          // 需要顺时针转几个 90°（0~3）
            public bool Sideways;        // 检测到横躺
            public bool UpsideDown;      // 检测到倒置
            public int RowBands, ColBands;
            public bool PunctSuppressed;   // 恒为 true：标点重力判据已废弃，无孔页不再据此倒置
            public double InkRatio;      // 墨迹占比
            public bool GiantGrid;       // 表格/表单页（格线连成巨型组件）
            public int HoleL, HoleR, HoleT, HoleB;   // 各边带装订孔计数
            // 装订孔已经给出**明确结论**（含"孔在左 = 正放"）→ 调用方不必再找别的证据
            // （极致档的 OCR 四向定向只在 HoleConfirmed=false 时才跑，
            //   既省时间，也避免 OCR 把孔已经判定的正放页又转一次）
            public bool HoleConfirmed;
            public string Note = "";
        }

        // ---------------- 检测 ----------------

        public static Result Detect(Bitmap page)
        {
            Result r = new Result();
            int w = page.Width, h = page.Height;
            PixelBuffer buf = BitmapBridge.ToBuffer(page, 3);

            // 墨迹掩膜：深色 或 偏红（红头/红章也是内容）
            bool[] ink = new bool[w * h];
            int[] rowSum = new int[h];
            int[] colSum = new int[w];
            int chromaPx = 0;                     // 强彩色像素数（判"彩色封面页"）
            for (int y = 0; y < h; y++)
            {
                int ro = y * buf.Stride;
                for (int x = 0; x < w; x++)
                {
                    int p = ro + x * 3;
                    int b = buf.Data[p], g = buf.Data[p + 1], rr = buf.Data[p + 2];
                    int mx = rr > g ? rr : g; if (b > mx) mx = b;
                    int mn = rr < g ? rr : g; if (b < mn) mn = b;
                    if (mx - mn > 60) chromaPx++;
                    bool inkPx = (rr * 299 + g * 587 + b * 114) / 1000 < 170 ||
                                 (rr - g > 50 && rr - b > 50 && rr > 120);
                    if (inkPx)
                    {
                        ink[y * w + x] = true;
                        rowSum[y]++;
                        colSum[x]++;
                    }
                }
            }

            // 🔴 彩色封面页守卫：红封面/彩印图版页整页 30%+
            //    是饱和彩色，没有装订孔这类物理证据，几何统计（行带/标点重力）
            //    在这类页上不可靠 —— 实测 实测样本（红封面）被误转 180°。
            //    策略：不自动转向（宁可不转，由人工判断），只清杂点/调色。
            if (chromaPx > 0.25 * (double)w * h)
            {
                r.TurnsCw = 0;
                r.Note = "彩色封面页不自动转向（彩色占比 " +
                         (100.0 * chromaPx / ((double)w * h)).ToString("0") + "%）";
                return r;
            }

            // 巨型连通域记录（供表格守卫用）+ 墨量统计（透印背面页墨很少）。
            // 装订孔判向（物理证据，优先级最高）：案卷扫描件每页都有装订孔——
            // 近圆、实心、深色，正放时在**左**边，倒置时在右，横躺时在上/下。
            // 透印背面页的孔照样清晰。这个信号不受内容类型影响（正文页/表格页/
            // 背面页通吃），而几何统计在透印页/表格页上会翻车
            // （实测 0006 被误转 90°、0013 端正表格页被误转 180°、0020 空白背面被误转 90°）。
            // 判据：近圆实心组件，直径 15~70px，长宽比 0.5~2，位于左右 12% /
            // 上下 10% 边带内且**避开四角**（角上的黑三角不是孔）；某侧 ≥2 孔
            // 且多于对侧 → 定向。孔不给结论时才走几何判据兜底。
            List<Comp> comps = LabelComps(ink, w, h);
            long inkCount = 0, maxCompArea = 0;
            foreach (Comp c in comps)
            {
                inkCount += c.Area;
                long a = (long)(c.X1 - c.X0 + 1) * (c.Y1 - c.Y0 + 1);
                if (a > maxCompArea) maxCompArea = a;
            }
            double inkRatio = inkCount / (double)((long)w * h);
            r.InkRatio = inkRatio;
            // 🔴 装订孔形状门控（加强，0实测样本 汉字冒充孔导致误转 180°）：
            //    旧判据"15~70px + 填充率≥0.5 + 近方"会把**汉字**（实测"月份/合计/备注"
            //    = 26x26、填充率 0.51~0.57）判成孔。真孔是**圆盘**，加三条硬特征：
            //      ①填充率 ≥0.60（圆的理想值 π/4≈0.785；汉字多在 0.5 上下）
            //      ②包围盒四角必须空（圆的外接方框四角是纸；汉字四角常有笔画）
            //      ③同侧候选要"成列"：孔心横/纵坐标差 ≤14px、孔径互差 ≤30%、
            //        且间距 ≥120px（真孔是等距冲孔；汉字散落各处凑不成列）
            List<Comp> holeOk = new List<Comp>();
            foreach (Comp c in comps)
            {
                int cw = c.X1 - c.X0 + 1, chh = c.Y1 - c.Y0 + 1;
                if (cw < 15 || cw > 70 || chh < 15 || chh > 70) continue;
                if ((double)c.Area / (cw * chh) < 0.60) continue;         // ① 圆盘填充率
                double asp = cw / (double)chh;
                if (asp < 0.5 || asp > 2.0) continue;                     // 近圆
                int ccx = (c.X0 + c.X1) / 2, ccy = (c.Y0 + c.Y1) / 2;
                bool cornerZone = (ccx < w * 0.10 || ccx > w * 0.90) &&
                                  (ccy < h * 0.10 || ccy > h * 0.90);
                if (cornerZone) continue;                                 // 角上的黑三角/黑块不是孔
                if (!InkFreeCorners(ink, w, h, c)) continue;              // ② 四角必须空
                bool inLR = (c.X1 < w * 0.12 || c.X0 > w * 0.88) && ccy > h * 0.12 && ccy < h * 0.88;
                bool inTB = (c.Y1 < h * 0.10 || c.Y0 > h * 0.90) && ccx > w * 0.12 && ccx < w * 0.88;
                if (inLR || inTB) holeOk.Add(c);
            }
            int holeL = 0, holeR = 0, holeT = 0, holeB = 0;
            for (int a = 0; a < holeOk.Count; a++)
            {
                Comp c = holeOk[a];
                int ccx = (c.X0 + c.X1) / 2, ccy = (c.Y0 + c.Y1) / 2;
                int cw = c.X1 - c.X0 + 1, chh = c.Y1 - c.Y0 + 1;
                // ③ 与同侧另一个候选"成列"才算：坐标对齐 + 孔径接近 + 间距够大
                bool paired = false;
                for (int b = 0; b < holeOk.Count && !paired; b++)
                {
                    if (a == b) continue;
                    Comp d = holeOk[b];
                    int dcx = (d.X0 + d.X1) / 2, dcy = (d.Y0 + d.Y1) / 2;
                    int dw = d.X1 - d.X0 + 1, dh = d.Y1 - d.Y0 + 1;
                    bool sizeOk = Math.Max(cw, chh) <= 1.3 * Math.Min(dw, dh) &&
                                  Math.Min(cw, chh) >= (1.0 / 1.3) * Math.Max(dw, dh);
                    bool lr = c.X1 < w * 0.12 && d.X1 < w * 0.12 && Math.Abs(ccx - dcx) <= 14 &&
                              Math.Abs(ccy - dcy) >= 120;
                    bool rr = c.X0 > w * 0.88 && d.X0 > w * 0.88 && Math.Abs(ccx - dcx) <= 14 &&
                              Math.Abs(ccy - dcy) >= 120;
                    bool tb = c.Y1 < h * 0.10 && d.Y1 < h * 0.10 && Math.Abs(ccy - dcy) <= 14 &&
                              Math.Abs(ccx - dcx) >= 120;
                    bool bb = c.Y0 > h * 0.90 && d.Y0 > h * 0.90 && Math.Abs(ccy - dcy) <= 14 &&
                              Math.Abs(ccx - dcx) >= 120;
                    if (sizeOk && (lr || rr || tb || bb)) paired = true;
                }
                if (!paired) continue;
                if (c.X1 < w * 0.12) holeL++;
                else if (c.X0 > w * 0.88) holeR++;
                else if (c.Y1 < h * 0.10) holeT++;
                else if (c.Y0 > h * 0.90) holeB++;
            }
            r.HoleL = holeL; r.HoleR = holeR; r.HoleT = holeT; r.HoleB = holeB;
            // 孔在左侧成列 = **正放的直接证据**（与"孔在右=倒置"对称的第四个分支）。
            // 补：此前孔在左会一路落到"无孔证据 → 不转向"，
            // 于是极致档分不清"已经确认正放"和"压根没证据"，白白多跑一遍 OCR。
            if (holeL >= 2 && holeL > holeR && holeL >= holeT && holeL >= holeB)
            {
                r.TurnsCw = 0;
                r.HoleConfirmed = true;
                r.Note = "装订孔在左(" + holeL + "个) → 正放";
                return r;
            }
            if (holeR >= 2 && holeR > holeL && holeR >= holeT && holeR >= holeB)
            {
                r.TurnsCw = 2;
                r.HoleConfirmed = true;
                r.Note = "装订孔在右(" + holeR + "个) → 判倒置 180°";
                return r;
            }
            if (holeT >= 2 && holeT > holeB && holeT > holeL && holeT > holeR)
            {
                r.TurnsCw = 3;   // 孔在上 = 页面被顺时针转了 90° 后进纸 → 再转 3 次 90° 转正
                r.HoleConfirmed = true;
                r.Note = "装订孔在上(" + holeT + "个) → 判横躺 270°";
                return r;
            }
            if (holeB >= 2 && holeB > holeT && holeB > holeL && holeB > holeR)
            {
                r.TurnsCw = 1;   // 孔在下 = 逆时针横躺 → 转一个 90° 转正
                r.HoleConfirmed = true;
                r.Note = "装订孔在下(" + holeB + "个) → 判横躺 90°";
                return r;
            }
            // 走到这里 = 四种孔位分支都没命中：要么一个孔都没有，要么有孔但不成列
            //（证据不足，例如只有 1 个孔）。这批页**不再有任何几何兜底** ——
            // 定案是"无孔不转向"，理由见下面的 2026-09-30 实测。

            // 巨型连通域守卫：表格/登记表的格线全部相交，连成一个横跨大半页的
            // 巨型组件。这是本页的重要标记，会写进 Note 供排查。
            bool giantGrid = maxCompArea > 0.40 * (long)w * h;
            r.GiantGrid = giantGrid;

            // 行带/列带计数。**注意：这两个值现在只作诊断输出，不再参与转向决策**
            //   （定案见下面的"无孔不转向"）。保留它们是因为排查现场问题时，
            //   知道这页有几个行带/列带能立刻判断是不是表格页 —— 例如实测 0006
            //   是"行带 5/列带 10"，正是竖格线造成的假横躺信号。
            //   曾经据此定过阈值（rowBands ≤ 2 才判横躺），但阈值在空白表格页上
            //   完全失效（空表竖格线天然造出 6~13 条列带），故已废弃。
            int rowBands = CountBands(rowSum);
            int colBands = CountBands(colSum);
            r.RowBands = rowBands;
            r.ColBands = colBands;
            r.GiantGrid = giantGrid;

            // 🔴 无孔不转向（实战定案，0实测样本 等 15 页端正页被误转）：
            //    几何判据（行带/列带计数、标点重力）在**空白表格页**上全线失效 ——
            //    空表的竖格线天然造出 6~13 条"列带"（看着像横躺）、稀疏格线交点
            //    在上下半页均匀分布（标点重力等于抛硬币）。这批 15 页无孔页里，
            //    几何给出的转向**全部是误判**。
            //    定案：**转向只认装订孔这个物理证据**（孔在右=180 / 上=270 / 下=90 /
            //    左=不转，前面已按孔返回）。无孔页一律不转 —— 宁可漏纠正（界面
            //    一键旋转/手动），绝不整页转错。判据值仍写进 Note 供排查。
            r.PunctSuppressed = true;
            r.Sideways = false;
            r.UpsideDown = false;
            r.TurnsCw = 0;
            r.Note = "无装订孔证据 → 不转向（孔 L" + holeL + "/R" + holeR + "/T" + holeT + "/B" + holeB +
                     " 墨 " + (inkRatio * 100).ToString("0.00") + "%" +
                     (giantGrid ? " [表格页]" : "") +
                     " 行带 " + rowBands + "/列带 " + colBands + "）";
            return r;
        }


        // 包围盒四角是否无墨（真孔的圆盘外接方框四角是纸；汉字四角常有笔画）：
        // 每角取 4x4 小块，墨点数 ≤3 才算"空"。
        static bool InkFreeCorners(bool[] ink, int w, int h, Comp c)
        {
            int[,] pts = { { c.X0, c.Y0 }, { c.X1 - 3, c.Y0 }, { c.X0, c.Y1 - 3 }, { c.X1 - 3, c.Y1 - 3 } };
            for (int k = 0; k < 4; k++)
            {
                int cnt = 0;
                for (int dy = 0; dy < 4; dy++)
                    for (int dx = 0; dx < 4; dx++)
                    {
                        int x = pts[k, 0] + dx, y = pts[k, 1] + dy;
                        if (x < 0 || y < 0 || x >= w || y >= h) continue;
                        if (ink[y * w + x]) cnt++;
                    }
                if (cnt > 3) return false;
            }
            return true;
        }

        // 投影里的"带"数：阈值为峰值 5%（至少 3），相邻带间隔 <8px 算同一条，
        // 宽度 <8px 的窄带忽略（噪声）。文字行带的典型宽度是字高（几十像素）。
        static int CountBands(int[] prof)
        {
            int max = 0;
            for (int i = 0; i < prof.Length; i++) if (prof[i] > max) max = prof[i];
            if (max == 0) return 0;
            int thresh = Math.Max(3, max * 5 / 100);
            int bands = 0, run = 0, gap = 0;
            for (int i = 0; i < prof.Length; i++)
            {
                if (prof[i] > thresh)
                {
                    run++;
                    gap = 0;
                }
                else
                {
                    if (run > 0)
                    {
                        gap++;
                        if (gap >= 8) { if (run >= 8) bands++; run = 0; gap = 0; }
                    }
                }
            }
            if (run >= 8) bands++;
            return bands;
        }

        // 连通域（8 连通，栈式洪填）：只回外接框与面积
        sealed class Comp { public int X0, Y0, X1, Y1, Area; }

        static List<Comp> LabelComps(bool[] ink, int w, int h)
        {
            List<Comp> comps = new List<Comp>();
            bool[] vis = new bool[w * h];
            int[] stack = new int[w * h];
            for (int seed = 0; seed < ink.Length; seed++)
            {
                if (!ink[seed] || vis[seed]) continue;
                int sp = 0;
                stack[sp++] = seed;
                vis[seed] = true;
                Comp c = new Comp();
                c.X0 = w; c.Y0 = h; c.X1 = -1; c.Y1 = -1;
                while (sp > 0)
                {
                    int cur = stack[--sp];
                    int cx = cur % w, cy = cur / w;
                    c.Area++;
                    if (cx < c.X0) c.X0 = cx;
                    if (cx > c.X1) c.X1 = cx;
                    if (cy < c.Y0) c.Y0 = cy;
                    if (cy > c.Y1) c.Y1 = cy;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            int ni = ny * w + nx;
                            if (ink[ni] && !vis[ni]) { vis[ni] = true; stack[sp++] = ni; }
                        }
                }
                comps.Add(c);
            }
            return comps;
        }

        // [已废弃 2026-10，实测证伪] PunctGravitySaysUpsideDown —— "标点重力"倒置判据。
//
// 实现已删除（原 53 行）。留下这段说明，是为了防止有人凭直觉把它捡回来：
// 它假设标点（句号、逗号）落在文字行的下缘，倒置页的标点重心会偏上。
// 实战把这个假设推翻了 —— 2026-09-30 的 15 页无孔端正页上，标点重心等于
// 抛硬币，几何判据给出的转向**全部是误判**。现在无孔页一律不转向
// （见 Detect 里的"无孔不转向"定案），标点数据只作为诊断值写进 Note。
//
// 同时废弃的还有Sharpness()（行/列投影锐度比对）。理由：合成文字页的行/列
// 锐度比只有 1.19，真实扫描页是 12000 倍 —— 差 4 个数量级的判据无法定出
// 一个跨样本稳定的固定阈值。改用 CountBands 的"带计数"，判据稳健得多。

// ---------------- 应用 ----------------

        // 无损旋转 turns×90°（顺时针）。turns=0 时原样返回同一引用。
        public static Bitmap Apply(Bitmap page, int turnsCw)
        {
            Bitmap cur = page;
            for (int i = 0; i < turnsCw; i++)
            {
                Bitmap n = Imaging.Rotate90(cur, true);
                if (n != cur) cur.Dispose();
                cur = n;
            }
            return cur;
        }

        // ---------------- 纯印章页判定 ----------------

        // 强彩色像素占比（色彩饱和度 max-min > 60）—— 判"彩色封面/彩印图版页"。
        // 红封面实测 92%~96%；红头文件的红头带约 5~10%，远低于 25% 阈值。
        public static double ColoredRatio(Bitmap page)
        {
            int w = page.Width, h = page.Height;
            PixelBuffer buf = BitmapBridge.ToBuffer(page, 3);
            long chroma = 0, total = (long)w * h;
            for (int y = 0; y < h; y++)
            {
                int ro = y * buf.Stride;
                for (int x = 0; x < w; x++)
                {
                    int p = ro + x * 3;
                    int b = buf.Data[p], g = buf.Data[p + 1], r = buf.Data[p + 2];
                    int mx = r > g ? r : g; if (b > mx) mx = b;
                    int mn = r < g ? r : g; if (b < mn) mn = b;
                    if (mx - mn > 60) chroma++;
                }
            }
            return total > 0 ? (double)chroma / total : 0;
        }

        // 整页只有章、几乎没有正文：深色(非红)墨 <0.4% 且 红色 >0.1%。
        // 阈值依据：正常文字页非红深色墨占 1.5%~8%；纯章页 <0.1%。
        public static bool IsSealOnlyPage(Bitmap page, out double darkRatio, out double redRatio)
        {
            int w = page.Width, h = page.Height;
            PixelBuffer buf = BitmapBridge.ToBuffer(page, 3);
            long dark = 0, red = 0, total = (long)w * h;
            for (int y = 0; y < h; y++)
            {
                int ro = y * buf.Stride;
                for (int x = 0; x < w; x++)
                {
                    int p = ro + x * 3;
                    int b = buf.Data[p], g = buf.Data[p + 1], rr = buf.Data[p + 2];
                    bool isRed = rr - g > 40 && rr - b > 40 && rr > 110;
                    if (isRed) { red++; continue; }
                    if ((rr * 299 + g * 587 + b * 114) / 1000 < 170) dark++;
                }
            }
            darkRatio = dark / (double)total;
            redRatio = red / (double)total;
            return darkRatio < 0.004 && redRatio > 0.001;
        }
    }
}
