// DocClean.Edge.cs -- DocClean 分文件（八）：页边垃圾清除。
// 装订孔 / 贴边黑条 / 黑角 / 卷边折痕；finalPass 只清孔与角块，不碰贴边细长条。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace PicTool
{
    public static partial class DocClean
    {
        // ==================== 五、页边垃圾清除====================
        //
        // 清三类东西（实测样本，用户红框圈的三样）：
        //   ① 装订孔：近圆实心深色斑 Φ15~70，位于左右 12% 边带中部（避四角）
        //   ② 贴边黑条：与画框相连的实心长条（扫描背板/上一页边缘压进来）
        //   ③ 黑角：页角上的实心黑三角/黑块（折角/背板露出）
        // 为什么现有步骤清不掉：
        //   FlattenDarkBackground 只管"四周是黑色背板"的页 —— 本批纸满幅、边框采样
        //   是亮的，开头就 return 0；孔/角是纸面上的孤岛，漫水从画框出发够不着；
        //   去黑点的尺寸上限/孤立判据对它们不生效或被"邻近墨迹保护"放过。
        //
        // 安全边界（绝不误伤正文）：
        //   · 只认**实心**（fill ≥ 0.5）—— 汉字/手写的笔画填充率只有 0.2~0.4；
        //   · 贴边条/黑角面积 ≤ 页面积 5%，且要么"细"（短边 ≤ 短边/16）、要么在
        //     四角 18%×18% 角区内；
        //   · 装订孔限边带内、限尺寸 —— 与方向检测（Orient）用的是同一套签名，
        //     所以本步骤必须排在方向检测**之后**（孔要先拿来判方向）。
        // 填色：取组件包围盒外扩 10px 的环上纸面像素的中位色（不是死白，
        //   纸面偏黄时不会留下刺眼的白补丁）；环上没有可用像素则退白。
        public static unsafe int RemoveEdgeJunk(Bitmap src)
        {
            return RemoveEdgeJunk(src, false);
        }

        public static unsafe int RemoveEdgeJunk(Bitmap src, bool finalPass)
        {
            // finalPass = 流水线末尾的终扫（回二值化之后）：图已是纯黑白，
            // 只清"孔簇 + 角落实心块"，**跳过贴边细长条** —— 终图上表格线
            // 也贴着画框，细长条规则会把表格线吃掉。
            int w = src.Width, h = src.Height;
            if (w < 64 || h < 64) return 0;
            int paintedJunk = 0;
            PixelBuffer buf = BitmapBridge.ToBuffer(src);
            int n = w * h;
            bool[] ink = new bool[n];
            bool[] deep = new bool[n];     // 亮度 <80 的"真黑"像素（区分装订孔与灰色手写）
            for (int y = 0; y < h; y++)
            {
                int ro = y * buf.Stride;
                for (int x = 0; x < w; x++)
                {
                    int p = ro + x * 3;
                    int lum = (buf.Data[p + 2] * 77 + buf.Data[p + 1] * 150 + buf.Data[p] * 29) >> 8;
                    ink[y * w + x] = lum < 150;
                    // "真黑"（深墨）与"灰"分开记：装订孔/墨疙瘩是**近黑**（实测核心亮度 0，
                    // 整个团里 20~47% 的像素亮度 <80）；铅笔手写是**中灰**（亮度 50~70 起，
                    // <80 的只占 2~6%）。小孔判据靠这条把"灰手写"挡在外面。
                    if (lum < 80) deep[y * w + x] = true;
                }
            }

            // 🔴 终扫前置（实测 0030 左上角）：扫描夹子/纸夹在角上
            //    与页面边缘的杂线**粘连成一个组件**（实测 0030：夹子+杂线合成
            //    1651x75 的大组件、fill 仅 0.12），任何"按组件"的规则都套不上 ——
            //    必须在连域标注**之前**用像素级手段拆：角落区（宽 9% × 高 4.5%）
            //    内找"实心核心"（5×5 全暗，细线腐蚀后只剩夹子本体），核心距
            //    边框 ≤15px 才认定是夹子（正文/印章不会顶到纸边），核心周边
            //    14px 内的墨点全部涂白。细线的残余部分由下方"顶边细长线"规则收尾。
            if (finalPass)
            {
                int zw = Math.Max(80, w * 9 / 100), zh = Math.Max(60, h * 45 / 1000);
                int[] cxs = { 0, w - 1 }, cys = { 0, h - 1 };
                for (int qi = 0; qi < 2; qi++)
                    for (int qj = 0; qj < 2; qj++)
                    {
                        int qx = cxs[qi], qy = cys[qj];
                        List<int> cores = new List<int>();
                        for (int yy = 0; yy < zh; yy++)
                        {
                            int ay = qy == 0 ? yy : h - 1 - yy;
                            for (int xx = 0; xx < zw; xx++)
                            {
                                int ax = qx == 0 ? xx : w - 1 - xx;
                                int i = ay * w + ax;
                                if (!ink[i]) continue;
                                bool solid = true;
                                for (int dy = -2; dy <= 2 && solid; dy++)
                                    for (int dx = -2; dx <= 2; dx++)
                                    {
                                        int nx = ax + dx, ny = ay + dy;
                                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                        if (!ink[ny * w + nx]) { solid = false; break; }
                                    }
                                if (solid && (xx <= 15 || yy <= 15)) cores.Add(i);
                            }
                        }
                        if (cores.Count < 30) continue;   // 实心核心太小，不是夹子
                        for (int yy = 0; yy < zh; yy++)
                        {
                            int ay = qy == 0 ? yy : h - 1 - yy;
                            for (int xx = 0; xx < zw; xx++)
                            {
                                int ax = qx == 0 ? xx : w - 1 - xx;
                                int i = ay * w + ax;
                                if (!ink[i]) continue;
                                bool near = false;
                                for (int k = 0; k < cores.Count && !near; k++)
                                {
                                    int cy2 = cores[k] / w, cx2 = cores[k] % w;
                                    if (Math.Abs(cy2 - ay) <= 14 && Math.Abs(cx2 - ax) <= 14) near = true;
                                }
                                if (!near) continue;
                                ink[i] = false;
                                int p = ay * buf.Stride + ax * 3;
                                buf.Data[p] = buf.Data[p + 1] = buf.Data[p + 2] = 255;
                                paintedJunk++;
                            }
                        }
                    }
            }

            // 🔴 像素级稠密块清除（"像素级消除"）：与线条/文字
            //    **粘连**的装订孔、墨疙瘩，整个组件删会把线/字一起带走 —— 改为
            //    形态学"开运算"按像素拆：先用 (2R+1)² 方核腐蚀，只有稠密区能留下
            //    核心（细笔画 3~8px、表格线 2~4px 一律被腐蚀掉），核心再按形状
            //    门控（面积≥30、包围盒≤100、近圆 fill≥0.45），最后只抹"核心±(R+2)"，
            //    粘连的线与字完好。这就是"只吃垃圾像素、不整块删"的实现。
            //    核心掩膜 coreMask 还复用于"装订孔候选"的实心门控（见下）。
            int R = 5;                          // 核心半径：细笔画 <11px 全被腐蚀掉
            byte[] coreMask = new byte[n];
            byte[] coreMask2 = new byte[n];     // R=2 的小核（专治 Φ10~25 小针孔，见下）
            {
                byte[] hz = new byte[n];
                for (int y = 0; y < h; y++)         // 水平腐蚀
                {
                    int ro = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (!ink[ro + x]) continue;
                        bool ok = true;
                        for (int d = -R; d <= R; d++)
                        {
                            int nx = x + d;
                            if (nx < 0 || nx >= w || !ink[ro + nx]) { ok = false; break; }
                        }
                        if (ok) hz[ro + x] = 1;
                    }
                }
                for (int y = 0; y < h; y++)         // 垂直腐蚀
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (hz[i] == 0) continue;
                        bool ok = true;
                        for (int d = -R; d <= R; d++)
                        {
                            int ny = y + d;
                            if (ny < 0 || ny >= h || hz[ny * w + x] == 0) { ok = false; break; }
                        }
                        if (ok) coreMask[i] = 1;
                    }

                // ★ 小核掩膜（R=2，5×5 腐蚀）。
                //   为什么需要：Φ10~25 的**装订针孔**在 R=5 里根本没有核心（R=5 要求 11×11 全墨），
                //   于是整类漏检。实测"头"批 7 页每页残留 1~4 个（形如 10×17 / fill 0.73 的实心深点，
                //   集中在左侧边带 x≈40~115），人眼一眼就能看见。
                //   小核只喂"小孔渣"候选的门控，**不参与稠密块抹除**（那套必须 R=5 才不啃细笔画）。
                const int R2 = 2;
                byte[] hz2 = new byte[n];
                // 注：coreMask2 声明在块外（装订孔候选要用它）
                for (int y = 0; y < h; y++)
                {
                    int ro = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (!ink[ro + x]) continue;
                        bool ok = true;
                        for (int d = -R2; d <= R2; d++)
                        {
                            int nx = x + d;
                            if (nx < 0 || nx >= w || !ink[ro + nx]) { ok = false; break; }
                        }
                        if (ok) hz2[ro + x] = 1;
                    }
                }
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (hz2[i] == 0) continue;
                        bool ok = true;
                        for (int d = -R2; d <= R2; d++)
                        {
                            int ny = y + d;
                            if (ny < 0 || ny >= h || hz2[ny * w + x] == 0) { ok = false; break; }
                        }
                        if (ok) coreMask2[i] = 1;
                    }
            // 稠密块"像素级抹除"只在终扫做（终图纯黑白，抹得最干净）；
            // 非终扫时 coreMask 只用于装订孔候选的实心门控。
            if (finalPass)
            {
                // 核心只在左右边带（孔/垃圾带）里认；连域后按形状门控
                List<int> corePix = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    if (coreMask[i] == 0) continue;
                    int cx = i % w;
                    if (cx < w * 0.14 || cx > w * 0.86) corePix.Add(i);
                }
                int[] coreId = new int[n];
                int[] cstack = new int[n];
                foreach (int seed in corePix)
                {
                    if (coreId[seed] != 0) continue;
                    int top2 = 0, x0 = w, x1 = -1, y0b = h, y1b = -1, area = 0;
                    cstack[top2++] = seed; coreId[seed] = 1;
                    List<int> members = new List<int>();
                    while (top2 > 0)
                    {
                        int cur = cstack[--top2];
                        int cx = cur % w, cy = cur / w;
                        members.Add(cur);
                        area++;
                        if (cx < x0) x0 = cx; if (cx > x1) x1 = cx;
                        if (cy < y0b) y0b = cy; if (cy > y1b) y1b = cy;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = cx + dx, ny = cy + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                int ni = ny * w + nx;
                                if (coreMask[ni] != 0 && coreId[ni] == 0) { coreId[ni] = 1; cstack[top2++] = ni; }
                            }
                    }
                    int bwq = x1 - x0 + 1, bhq = y1b - y0b + 1;
                    double fq = (double)area / (bwq * bhq);
                    double ratio = Math.Max(bwq, bhq) / (double)Math.Max(1, Math.Min(bwq, bhq));
                    bool blobLike = area >= 30 && Math.Max(bwq, bhq) <= 100 && ratio <= 2.2 && fq >= 0.45;
                    if (!blobLike) continue;
                    // 只抹"核心 ± (R+2)"里的墨 —— 粘连的细线与笔画（被腐蚀掉、不在核心）留下
                    foreach (int cp in members)
                    {
                        int cx = cp % w, cy = cp / w;
                        for (int dy = -(R + 2); dy <= R + 2; dy++)
                            for (int dx = -(R + 2); dx <= R + 2; dx++)
                            {
                                int nx = cx + dx, ny = cy + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                int ni = ny * w + nx;
                                if (!ink[ni]) continue;
                                ink[ni] = false;
                                int p = ny * buf.Stride + nx * 3;
                                buf.Data[p] = buf.Data[p + 1] = buf.Data[p + 2] = 255;
                                paintedJunk++;
                            }
                    }
                }
            }   // <- 闭合 finalPass（稠密块抹除）
            }   // <- 闭合腐蚀计算块（coreMask，两种模式都算）

            // 连通域（8 连通洪填）：compId 从 1 开始，0 = 非墨
            int[] compId = new int[n];
            int[] stack = new int[n];
            List<int[]> comps = new List<int[]>();   // 每项 {x0,y0,x1,y1,area}
            for (int seed = 0; seed < n; seed++)
            {
                if (!ink[seed] || compId[seed] != 0) continue;
                int id = comps.Count + 1;
                int top = 0;
                stack[top++] = seed;
                compId[seed] = id;
                int x0 = w, x1 = -1, y0 = h, y1 = -1, area = 0;
                while (top > 0)
                {
                    int cur = stack[--top];
                    int cx = cur % w, cy = cur / w;
                    area++;
                    if (cx < x0) x0 = cx;
                    if (cx > x1) x1 = cx;
                    if (cy < y0) y0 = cy;
                    if (cy > y1) y1 = cy;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            int ni = ny * w + nx;
                            if (ink[ni] && compId[ni] == 0) { compId[ni] = id; stack[top++] = ni; }
                        }
                }
                comps.Add(new int[] { x0, y0, x1, y1, area });
            }

            // 各组件"实心核心"的像素数与包围盒（装订孔门控的核心判据：
            // 真孔的核心是一个**实心团**（近圆、饱满）；粗体汉字/手写粗笔的核心
            // 是细长条或四个角块（包围盒细长、填充率低）—— 根治
            // "放大/粗体字被当孔整列清掉"。同时统计核心的 bbox 与填充率）
            int[] coreCnt = new int[comps.Count];
            int[] cX0 = new int[comps.Count], cX1 = new int[comps.Count];
            int[] cY0 = new int[comps.Count], cY1 = new int[comps.Count];
            for (int i = 0; i < comps.Count; i++) { cX0[i] = w; cX1[i] = -1; cY0[i] = h; cY1[i] = -1; }
            // 小核（R=2）的同款统计，供"小针孔"候选门控用
            int[] coreCnt2 = new int[comps.Count];
            int[] dX0 = new int[comps.Count], dX1 = new int[comps.Count];
            int[] dY0 = new int[comps.Count], dY1 = new int[comps.Count];
            int[] deepCnt = new int[comps.Count];      // 组件里"真黑"（亮度<80）的像素数
            for (int i = 0; i < comps.Count; i++) { dX0[i] = w; dX1[i] = -1; dY0[i] = h; dY1[i] = -1; }
            for (int i = 0; i < n; i++)
            {
                int id = compId[i];
                if (id <= 0) continue;
                int k = id - 1, cx = i % w, cy = i / w;
                if (deep[i]) deepCnt[k]++;
                if (coreMask[i] != 0)
                {
                    coreCnt[k]++;
                    if (cx < cX0[k]) cX0[k] = cx;
                    if (cx > cX1[k]) cX1[k] = cx;
                    if (cy < cY0[k]) cY0[k] = cy;
                    if (cy > cY1[k]) cY1[k] = cy;
                }
                if (coreMask2[i] != 0)
                {
                    coreCnt2[k]++;
                    if (cx < dX0[k]) dX0[k] = cx;
                    if (cx > dX1[k]) dX1[k] = cx;
                    if (cy < dY0[k]) dY0[k] = cy;
                    if (cy > dY1[k]) dY1[k] = cy;
                }
            }

            // ---- 第一遍：分类（先只做判定，不 painting —— 装订孔要成簇互证）----
            // 🔴 卷边折痕/划痕（实测"卷边折痕也要清"）：页角上的
            //    斜向细长痕（实测 0016 两段 -24°/-30°、0034 一道 -28°）。判据四条
            //    缺一不可：①整体位于四角区（横 28% × 纵 12%）②面积 150~15000、
            //    bbox ≤400px ③填充率 ≤0.30（细痕在方框里只占一线）④**主轴斜向**
            //    （二阶矩算长轴：伸长度 ≥2.5 且与水平/垂直夹角 15°~75°）——
            //    全批盲扫 53 页命中里 50 页是横平竖直的表格边框线，只有斜向
            //    判据能把它们干净分开（表格线夹角 ≈0°/90°，一律保留）。
            bool[] crease = new bool[comps.Count];
            bool[] smallCand = new bool[comps.Count];   // 走"小针孔"路径进来的候选（出口更严）
            bool[] ringCand = new bool[comps.Count];    // 走"环状孔"路径进来的候选
            bool[] cornerTainted = new bool[4];   // 左上/右上/左下/右下 出现过折痕
            if (finalPass)
            {
                bool[] cand = new bool[comps.Count];
                for (int ci = 0; ci < comps.Count; ci++)
                {
                    int[] c = comps[ci];
                    int cw = c[2] - c[0] + 1, chh = c[3] - c[1] + 1, area = c[4];
                    double fill = (double)area / (cw * chh);
                    if (area < 150 || area > 15000 || fill > 0.30) continue;
                    if (Math.Max(cw, chh) > 400) continue;
                    bool tl = c[2] < w * 0.28 && c[3] < h * 0.12;
                    bool tr = c[0] > w * 0.72 && c[3] < h * 0.12;
                    bool bl = c[2] < w * 0.28 && c[1] > h * 0.88;
                    bool br = c[0] > w * 0.72 && c[1] > h * 0.88;
                    if (tl || tr || bl || br) cand[ci] = true;
                }
                // 单遍累加候选组件的二阶矩
                int[] cnt = new int[comps.Count];
                double[] sx = new double[comps.Count], sy = new double[comps.Count];
                double[] sxx = new double[comps.Count], syy = new double[comps.Count], sxy = new double[comps.Count];
                for (int i = 0; i < n; i++)
                {
                    int id = compId[i];
                    if (id == 0 || !cand[id - 1]) continue;
                    int cy = i / w, cx = i % w;
                    cnt[id - 1]++;
                    sx[id - 1] += cx; sy[id - 1] += cy;
                    sxx[id - 1] += (double)cx * cx;
                    syy[id - 1] += (double)cy * cy;
                    sxy[id - 1] += (double)cx * cy;
                }
                for (int ci = 0; ci < comps.Count; ci++)
                {
                    if (!cand[ci] || cnt[ci] < 100) continue;
                    double mx = sx[ci] / cnt[ci], my = sy[ci] / cnt[ci];
                    double xx = sxx[ci] / cnt[ci] - mx * mx;
                    double yy = syy[ci] / cnt[ci] - my * my;
                    double xy = sxy[ci] / cnt[ci] - mx * my;
                    double trc = xx + yy, det = xx * yy - xy * xy;
                    double disc = Math.Max(0, trc * trc / 4 - det);
                    double l1 = trc / 2 + Math.Sqrt(disc);
                    double l2 = Math.Max(trc / 2 - Math.Sqrt(disc), 1e-6);
                    double elong = Math.Sqrt(l1 / l2);
                    double angDeg = 0.5 * Math.Atan2(2 * xy, xx - yy) * 180.0 / Math.PI;
                    double adiag = Math.Min(Math.Abs(angDeg), Math.Abs(90 - Math.Abs(angDeg)));
                    if (elong >= 2.5 && adiag >= 15 && adiag <= 75)
                    {
                        crease[ci] = true;
                        int[] c = comps[ci];
                        int zone = c[2] < w * 0.28 ? (c[3] < h * 0.12 ? 0 : 2)
                                                    : (c[3] < h * 0.12 ? 1 : 3);
                        cornerTainted[zone] = true;
                    }
                }
            }
            List<int> toPaint = new List<int>();
            List<int> holeCand = new List<int>();
            for (int ci = 0; ci < comps.Count; ci++)
            {
                int[] c = comps[ci];
                int cw = c[2] - c[0] + 1, chh = c[3] - c[1] + 1, area = c[4];
                double fill = (double)area / (cw * chh);
                int ccx = (c[0] + c[2]) / 2, ccy = (c[1] + c[3]) / 2;
                bool touchesFrame = c[0] <= 2 || c[1] <= 2 || c[2] >= w - 3 || c[3] >= h - 3;
                bool inCorner = c[0] < w * 0.18 && c[1] < h * 0.18 ? true :
                                c[2] > w * 0.82 && c[1] < h * 0.18 ? true :
                                c[0] < w * 0.18 && c[3] > h * 0.82 ? true :
                                c[2] > w * 0.82 && c[3] > h * 0.82;

                // 卷边折痕命中 → 清
                if (crease[ci]) { toPaint.Add(ci); continue; }
                // 折痕污染的角落区：痕旁的小渣点（area ≤300）一并清
                //（如 0016 痕右侧 14x7 的渣点；大组件/表格本体不受影响）
                if (finalPass && cornerTainted != null && area <= 300)
                {
                    int zone = c[2] < w * 0.28 ? (c[3] < h * 0.12 ? 0 : 2)
                                                : (c[0] > w * 0.72 ? (c[3] < h * 0.12 ? 1 : 3) : -1);
                    if (zone >= 0 && cornerTainted[zone]) { toPaint.Add(ci); continue; }
                }

                bool isEdgeJunk = !finalPass && touchesFrame && fill >= 0.5 && area <= (long)w * h / 20 &&
                                  (Math.Min(cw, chh) <= Math.Min(w, h) / 16 || inCorner);
                if (finalPass && touchesFrame && inCorner && fill >= 0.5 && area <= (long)w * h / 50)
                    isEdgeJunk = true;

                // （边带清空规则已改版：从"整带清空"收窄为"孔轨道内清碎渣"，
                //   实现移到下方"装订孔成簇互证"之后 —— 那时才知道孔在哪、有几枚。
                //   旧版把左右 8% 边带里所有组件删掉，紧裁页会整列吃字。）

                // 🔴 顶/底边带残渣（实测 0030/0016 底部）：批量扫描时
                //    页面最底边会带进**邻页的文字残迹**（0030 一排碎片在 0.7% 处、
                //    0016 的残迹在 1.9~2.2% 处，都不贴框、不在左右边带）。
                //    判据：整体位于顶/底 2.5% 边带内 + 高 ≤25px + 宽 ≤ 页宽 25%
                //    （排除通栏表格边框线）。扩带前全批预扫：2.5% 扩区里只有
                //    实测样本 共 8 个米粒大的渣，没有真内容。
                if (finalPass && area >= 15 && chh <= 25 && cw <= w / 4 &&
                    (c[3] < h * 0.025 || c[1] > h * 0.975))
                    toPaint.Add(ci);
                // 中扁断线（实测 0030 剩 472x5、0025 剩 589x5）：高 ≤8px 的碎片
                // 放宽到 6/10 页宽
                if (finalPass && area >= 15 && chh <= 8 && cw <= w * 6 / 10 &&
                    (c[3] < h * 0.015 || c[1] > h * 0.985))
                    toPaint.Add(ci);
                // 宽断带（实测 0052 底部 1369x21 的撕边残条）：顶/底 1.5% 边带内、
                // 高 ≤25px、宽 ≥3/4 页宽的**断裂带**（fill ≤0.5 —— 真表格边框是
                // 实心的 fill>0.6，不会被误伤）
                if (finalPass && area >= 15 && chh <= 25 && cw >= w * 3 / 4 && fill <= 0.5 &&
                    (c[3] < h * 0.015 || c[1] > h * 0.985))
                    toPaint.Add(ci);

                // 🔴 顶/底贴边细长线（0030 左上角夹子的"残线"）：扫描边框线/
                //    折痕印会横贯整页、又细又扁（fill ≤0.3），与表格边框的区别：
                //    它贴着页面最边缘（顶 3.5% 以内）且几乎全宽。角夹子本体已被
                //    前置像素级清除拆掉，剩下的细线在这里收尾。
                if (finalPass && cw >= w * 9 / 10 && chh <= 40 && fill <= 0.3 &&
                    (c[3] < h * 0.035 || c[1] > h * 0.965))
                    toPaint.Add(ci);

                if (isEdgeJunk) { toPaint.Add(ci); continue; }

                // 装订孔候选（放宽版）：撕边孔会被撕裂成 2~3 块碎片，fill 0.32~0.46 ——
                // 装订线文字的 fill 只有 0.25~0.26（实测），0.30 是两者的分界线。
                // 尺寸放宽到 12~110px（撕边连通域 60~85px）。误伤风险交给
                // "成簇互证"兜底：
                //   ①同一页同一侧 ≥2 个候选横向上排成一列（孔心偏差 ≤90px）→ 全清；
                //   ②孤零零一个候选 → 只有"小而实"（15~70px、fill ≥0.5）才清。
                // 🔴 实心团核心门控（根治"整块吃字"）：
                //    · coreCnt ≥25：真孔是实心圆盘，(2R+1)² 腐蚀后必留核心
                //    · 核心包围盒长宽比 ≤2.2 且核心填充率 ≥0.35：孔的核心是**实心团**；
                //      粗体汉字/手写粗笔的核心是细长条（长宽比大）或四个角块
                //      （填充率低）—— 形态上直接分开。这就是 A4 放大后笔画粗过
                //      腐蚀核、字符与孔"看起来一样"时的终极区分判据。
                bool coreBlob = false;
                if (coreCnt[ci] >= 25)
                {
                    int cbw = cX1[ci] - cX0[ci] + 1, cbh = cY1[ci] - cY0[ci] + 1;
                    double cFill = (double)coreCnt[ci] / (cbw * cbh);
                    double cRatio = Math.Max(cbw, cbh) / (double)Math.Max(1, Math.Min(cbw, cbh));
                    coreBlob = cRatio <= 2.2 && cFill >= 0.35;
                }
                // 左右 12% / 上下 10% 边带全高（全宽）都算 —— 实测残留集中在
                // 右边带的上下角（0005/0023），限制"中部"会漏
                bool inBand = (c[2] < w * 0.12 || c[0] > w * 0.88 ||
                               c[3] < h * 0.10 || c[1] > h * 0.90);

                // ---- 大孔路径（原判据，保持不动）----
                if (cw >= 12 && cw <= 110 && chh >= 12 && chh <= 110 &&
                    fill >= 0.35 &&
                    cw / (double)chh >= 0.35 && cw / (double)chh <= 2.8 &&
                    coreBlob)
                {
                    if (inBand) holeCand.Add(ci);
                    continue;
                }

                // ---- 小针孔路径（新增）----
                // Φ10~25 的装订针孔在 R=5 腐蚀里**没有核心**（coreCnt 恒为 0）→ 上面那条
                // 永远命不中。实测"头"批 7 页每页残留 1~4 个：10×17 / fill 0.73 的实心深点，
                // 集中在左侧边带 x≈40~115，人眼一眼就能看见（实测"装订孔有残留"）。
                // 门控（比大孔严，因为它靠小核"看起来像"）：
                //   · 尺寸 6~40px（再大就走大孔路径了）
                //   · fill ≥ 0.55 —— 汉字/文字的填充率只有 0.2~0.45，够不到这条
                //   · 长宽比 ≤ 2.5（排除下划线/表格线残段）
                //   · 小核（5×5 腐蚀）仍有 ≥6px 核心、且核心紧实 ≥0.5
                //   · **必须是"真黑"**：组件里亮度 <80 的像素 ≥8 个且占比 ≥12%
                //     （实测加的这条最要紧：装订孔/墨疙瘩近黑 —— 孔内亮度 0、
                //      整团 20~47% 的像素 <80；铅笔手写是中灰 —— 首像素 50~70、<80 的只占 2~6%。
                //      没有这条，页边手写的 "0.15" 里那个 "0" 会被当孔的 228px 团整块抹掉。）
                // 而且**不许走"孤候选"放宽**（见下方出口处），必须成簇或"极小极实"。
                bool smallBlob = false;
                if (coreCnt2[ci] >= 6)
                {
                    int dbw = dX1[ci] - dX0[ci] + 1, dbh = dY1[ci] - dY0[ci] + 1;
                    double dFill = (double)coreCnt2[ci] / (dbw * dbh);
                    double dRatio = Math.Max(dbw, dbh) / (double)Math.Max(1, Math.Min(dbw, dbh));
                    smallBlob = dRatio <= 2.5 && dFill >= 0.5;
                }
                int area1 = c[4];
                bool reallyDark = deepCnt[ci] >= 8 && deepCnt[ci] * 100 >= area1 * 12;
                if (cw >= 6 && cw <= 40 && chh >= 6 && chh <= 40 &&
                    fill >= 0.50 && reallyDark &&
                    cw / (double)chh >= 0.4 && cw / (double)chh <= 2.8 &&
                    smallBlob && inBand)
                {
                    holeCand.Add(ci);
                    smallCand[ci] = true;
                }

                // ---- 环状孔路径（新增，实测 实测样本 左侧 4 块）----
                // 扫描孔是**透光**的：二值图上不是实心圆而是"圆环"（中心亮、纸的撕裂
                // 边缘黑），实测填充率只有 0.21~0.27 —— 大孔路径（fill≥0.35+实心核心）
                // 和小针孔路径（fill≥0.50）都够不着，整类漏检。
                // 判据：尺寸 20~110、fill 0.10~0.45、宽高比 0.35~2.8、真黑 ≥8%、
                //   **包围盒中心有亮洞**（中心 1/3 区域 ≥30% 非墨）—— 这是"环"独有的
                //   形状证据。装订线手写（铅笔灰）被真黑门槛挡住；表格框外的黑字
                //   没有居中的洞。成簇互证（下方）继续兜底。
                if (cw >= 20 && cw <= 110 && chh >= 20 && chh <= 110 &&
                    fill >= 0.10 && fill <= 0.45 &&
                    cw / (double)chh >= 0.35 && cw / (double)chh <= 2.8 &&
                    deepCnt[ci] >= 8 && deepCnt[ci] * 100 >= area1 * 8 && inBand)
                {
                    int hw = Math.Max(4, cw / 3), hh = Math.Max(4, chh / 3);
                    int hx0 = ccx - hw / 2, hy0 = ccy - hh / 2;
                    int holeN = 0, holeAll = 0;
                    for (int yy = hy0; yy < hy0 + hh; yy++)
                        for (int xx = hx0; xx < hx0 + hw; xx++)
                        {
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            holeAll++;
                            if (!ink[yy * w + xx]) holeN++;
                        }
                    if (holeAll >= 16 && holeN * 10 >= holeAll * 3)
                    {
                        holeCand.Add(ci);
                        ringCand[ci] = true;
                    }
                }
            }

            // ---- 装订孔成簇互证 ----
            // 先数每侧候选数：真装订孔一页 2~3 枚（最多 4），若某侧冒出 >6 个
            // "候孔"，那不是孔列而是**字符列**（放大/粗体字在腐蚀测试里与孔同形）
            // —— 该侧一律不成簇、不清（防"整块吃字"）。
            int candLeft = 0, candRight = 0, candTop = 0, candBottom = 0;
            for (int a = 0; a < holeCand.Count; a++)
            {
                int[] ca0 = comps[holeCand[a]];
                if (ca0[2] < w * 0.12) candLeft++;
                if (ca0[0] > w * 0.88) candRight++;
                if (ca0[3] < h * 0.10) candTop++;
                if (ca0[1] > h * 0.90) candBottom++;
            }
            bool leftOk = candLeft >= 2 && candLeft <= 6;
            bool rightOk = candRight >= 2 && candRight <= 6;
            bool topOk = candTop >= 2 && candTop <= 6;
            bool bottomOk = candBottom >= 2 && candBottom <= 6;
            for (int a = 0; a < holeCand.Count; a++)
            {
                int[] ca = comps[holeCand[a]];
                int cax = (ca[0] + ca[2]) / 2;
                bool clustered = false;
                for (int b = 0; b < holeCand.Count; b++)
                {
                    if (a == b) continue;
                    int[] cb = comps[holeCand[b]];
                    int cbx = (cb[0] + cb[2]) / 2;
                    // 同一侧（左右同为一边带；上下同理）且孔心横向（或纵向）排成一列
                    bool sameSideLR = ((leftOk && ca[2] < w * 0.12 && cb[2] < w * 0.12) ||
                                       (rightOk && ca[0] > w * 0.88 && cb[0] > w * 0.88)) &&
                                      Math.Abs(cax - cbx) <= 90;
                    bool sameSideTB = ((topOk && ca[3] < h * 0.10 && cb[3] < h * 0.10) ||
                                       (bottomOk && ca[1] > h * 0.90 && cb[1] > h * 0.90)) &&
                                      Math.Abs((ca[1] + ca[3]) / 2 - (cb[1] + cb[3]) / 2) <= 90;
                    if (sameSideLR || sameSideTB) { clustered = true; break; }
                }
                if (clustered) { toPaint.Add(holeCand[a]); continue; }
                // 孤候选：小而实才清（装订线文字 fill 0.25~0.26，0.40 是分界线）
                int cw1 = ca[2] - ca[0] + 1, ch1 = ca[3] - ca[1] + 1;
                double f1 = (double)ca[4] / (cw1 * ch1);
                // 小针孔路径的候选**不适用**这条放宽（它本来就是靠小核"看着像"，单枪匹马时
                // 必须"极小极实"才敢动，否则宁留）
                bool loneOk = smallCand[holeCand[a]]
                    ? (Math.Max(cw1, ch1) <= 30 && f1 >= 0.60)
                    : (cw1 >= 12 && cw1 <= 70 && ch1 >= 12 && ch1 <= 70 && f1 >= 0.40);
                if (loneOk) toPaint.Add(holeCand[a]);
            }

            // ---- 孔轨道内碎渣清除：已按"宁留勿删"原则**取消**----
            // 曾试"孔轨道 x 范围内面积 ≤600 的小组件全清"（本意清装订线标签字与
            // 撕边残片）。但紧裁页里**正文第一列与装订孔同处一个 x 区间**，
            // 字符面积 300~600 与标签字同档，规则无法区分 —— 实测合成紧裁页
            // 第一列字会被再吃一遍。故彻底取消"按位置清字"，只保留能证明是垃圾
            // 的形态：①装订孔（③成簇互证 + 圆实心形状门控）②稠密墨块
            // （上面的像素级开运算）③顶/底边带残渣（正文有版心，不贴边）。
            // 残留的"装订线"标签字若有碍观瞻，用界面修补笔手动点掉即可。


            // ---- 第二遍：涂抹 ----
            int paintedComps = 0;
            foreach (int ci in toPaint)
            {
                int[] c = comps[ci];

                // 环上纸面中位色（外扩 10px，取亮于 140 的像素）
                int ex0 = Math.Max(0, c[0] - 10), ex1 = Math.Min(w - 1, c[2] + 10);
                int ey0 = Math.Max(0, c[1] - 10), ey1 = Math.Min(h - 1, c[3] + 10);
                List<byte> rs = new List<byte>(), gs = new List<byte>(), bs = new List<byte>();
                for (int yy = ey0; yy <= ey1; yy++)
                    for (int xx = ex0; xx <= ex1; xx++)
                    {
                        int i = yy * w + xx;
                        if (compId[i] == ci + 1 || ink[i]) continue;
                        int p = yy * buf.Stride + xx * 3;
                        int lum = (buf.Data[p + 2] * 77 + buf.Data[p + 1] * 150 + buf.Data[p] * 29) >> 8;
                        if (lum < 140) continue;
                        rs.Add(buf.Data[p]); gs.Add(buf.Data[p + 1]); bs.Add(buf.Data[p + 2]);
                    }
                byte pr = 255, pg = 255, pb = 255;
                if (rs.Count >= 20)
                {
                    byte[] t = rs.ToArray(); Array.Sort(t); pr = t[t.Length / 2];
                    t = gs.ToArray(); Array.Sort(t); pg = t[t.Length / 2];
                    t = bs.ToArray(); Array.Sort(t); pb = t[t.Length / 2];
                }

                // 涂抹：组件像素 + 2px 邻域（吃掉 JPEG 黑晕）
                for (int yy = Math.Max(0, c[1] - 2); yy <= Math.Min(h - 1, c[3] + 2); yy++)
                    for (int xx = Math.Max(0, c[0] - 2); xx <= Math.Min(w - 1, c[2] + 2); xx++)
                    {
                        int i = yy * w + xx;
                        bool hit = false;
                        for (int dy = -2; dy <= 2 && !hit; dy++)
                            for (int dx = -2; dx <= 2 && !hit; dx++)
                            {
                                int nx = xx + dx, ny = yy + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                if (compId[ny * w + nx] == ci + 1) hit = true;
                            }
                        if (!hit) continue;
                        int p = yy * buf.Stride + xx * 3;
                        buf.Data[p] = pr; buf.Data[p + 1] = pg; buf.Data[p + 2] = pb;
                    }
                paintedComps++;
            }

            if (paintedComps > 0 || paintedJunk > 0) BitmapBridge.WriteBack(src, buf);
            return paintedComps + paintedJunk / 50 + (paintedJunk > 0 ? 1 : 0);
        }
    }
}
