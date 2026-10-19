// DocClean.Background.cs -- DocClean 分文件（三）：去扫描背景（深色底）。
// 黑盖板/深色垫板/压在深色台面上：从四边漫水填充，把与边框连通的背景像素涂白。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace PicTool
{
    public static partial class DocClean
    {
        // ==================== 三、去扫描背景（深色底） ====================
        //
        // 解决什么：扫描件四周是**深色背景**时（黑盖板、深色垫板、压在深色台面上），
        //   Imaging.TrimBlank 会失效 —— 它取"四角里最亮的那个角"当纸色，而四角全黑，
        //   于是把纸色判成黑、把"内容"判成整页，**什么也不裁**。
        //   更糟的是下一步：倾斜检测把黑角当成墨迹，角度直接判成 0°（见 docs/架构.md 的遗留条目）。
        //   结果是"一键纠正"在这类图上完全不动。
        //
        // 与 TrimBlank 的分工（两者都要用，顺序不能换）：
        //   本函数先跑，只负责**把深色背景涂成白色**，让后面看得见纸；
        //   TrimBlank 后跑，负责把纸自己的白边裁到内容框。
        //   所以这里刻意**不**处理亮背景：亮边框说明那本来就是纸，交给 TrimBlank。
        //
        // 做法：**从画框向内漫水填充**，把与边框连通、且颜色接近背景色的像素涂白。
        //
        // 为什么不用"求外接框再裁"：纸在画面里往往是**斜的**（扫描时没放正），
        //   斜矩形的外接框≈整幅画面，裁不出任何东西 —— 这条路实测行不通。
        //   而那正是本函数要解决的场景（倾斜 + 深色底）。
        //
        // 为什么只涂"与边框连通"的区域：深色底和纸上墨迹可能颜色相近（黑盖板 vs 黑字），
        //   按颜色一刀切会把字也涂掉。限定"必须与画框连通"后，纸内部的墨迹天然安全。
        //
        // 用扫描线漫水填充（一次处理一整段），栈里只放"段"而不是"像素"，
        //   内存与段数成正比，不会出现百万级栈。
        //
        // 返回值：被涂白的像素数（0 = 没动，说明没有深色背景或判断失败）。
        // 原地修改，不换位图实例。
        public static unsafe int FlattenDarkBackground(Bitmap src, int tolerance)
        {
            int w = src.Width, h = src.Height;
            if (w < 32 || h < 32) return 0;

            int depth = Math.Min(Math.Max(2, Math.Min(w, h) / 60), 40);
            const int MAXS = 512;
            int[] sr = new int[MAXS], sg = new int[MAXS], sbb = new int[MAXS];
            int n = 0;
            int step = Math.Max(1, (2 * (w + h)) / MAXS);

            byte[] data = new byte[w * h * 3];        // 复制一份到托管数组，漫水时好访问
            BitmapData d = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                byte* bp = (byte*)d.Scan0;
                for (int y = 0; y < h; y++)
                    System.Runtime.InteropServices.Marshal.Copy(
                        (IntPtr)(bp + y * d.Stride), data, y * w * 3, w * 3);

                // 边框采样
                for (int x = 0; x < w && n < MAXS; x += step)
                {
                    for (int k = 0; k < depth && n < MAXS; k++)
                    {
                        int i1 = (k * w + x) * 3;
                        sr[n] = data[i1 + 2]; sg[n] = data[i1 + 1]; sbb[n] = data[i1]; n++;
                        if (n < MAXS)
                        {
                            int i2 = ((h - 1 - k) * w + x) * 3;
                            sr[n] = data[i2 + 2]; sg[n] = data[i2 + 1]; sbb[n] = data[i2]; n++;
                        }
                    }
                }
                for (int y = 0; y < h && n < MAXS; y += step)
                {
                    for (int k = 0; k < depth && n < MAXS; k++)
                    {
                        int i1 = (y * w + k) * 3;
                        sr[n] = data[i1 + 2]; sg[n] = data[i1 + 1]; sbb[n] = data[i1]; n++;
                        if (n < MAXS)
                        {
                            int i2 = (y * w + (w - 1 - k)) * 3;
                            sr[n] = data[i2 + 2]; sg[n] = data[i2 + 1]; sbb[n] = data[i2]; n++;
                        }
                    }
                }
            }
            finally { src.UnlockBits(d); }
            if (n == 0) return 0;

            // 颜色族聚类，取成员最多的一族当背景色
            int bestCount = 0, bgr = 255, bgg = 255, bgb = 255;
            int tol3 = tolerance * 3;
            for (int i = 0; i < n; i++)
            {
                int cnt = 0; long ar = 0, ag = 0, ab = 0;
                for (int j = 0; j < n; j++)
                {
                    int dd = Math.Abs(sr[j] - sr[i]) + Math.Abs(sg[j] - sg[i]) + Math.Abs(sbb[j] - sbb[i]);
                    if (dd <= tol3) { cnt++; ar += sr[j]; ag += sg[j]; ab += sbb[j]; }
                }
                if (cnt > bestCount)
                {
                    bestCount = cnt;
                    bgr = (int)(ar / cnt); bgg = (int)(ag / cnt); bgb = (int)(ab / cnt);
                }
            }

            int bgLum = (bgr * 77 + bgg * 150 + bgb * 29) >> 8;
            if (bgLum >= 128) return 0;                    // 亮边框 = 那本来就是纸，不归本函数管
            // 🔴 彩色边框 = 文档自身的颜色（红封面/红头），不是扫描背板 —— 直接不干
            //    （深色背板必是中性黑灰；见 IsBg 的彩色保护注释）
            {
                int mx0 = bgr, mn0 = bgr;
                if (bgg > mx0) mx0 = bgg; if (bgg < mn0) mn0 = bgg;
                if (bgb > mx0) mx0 = bgb; if (bgb < mn0) mn0 = bgb;
                if (mx0 - mn0 > 60) return 0;
            }

            // ---- 扫描线漫水填充：从四边开始，把与边框连通的背景像素涂白 ----
            byte[] mark = new byte[w * h];
            int[] stack = new int[1 << 16];
            int sp = 0;

            for (int x = 0; x < w; x++)
            {
                if (IsBg(data, w, h, x, 0, bgr, bgg, bgb, tol3)) stack = Push(stack, ref sp, x);
                if (IsBg(data, w, h, x, h - 1, bgr, bgg, bgb, tol3)) stack = Push(stack, ref sp, x + (h - 1) * w);
            }
            for (int y = 0; y < h; y++)
            {
                if (IsBg(data, w, h, 0, y, bgr, bgg, bgb, tol3)) stack = Push(stack, ref sp, y * w);
                if (IsBg(data, w, h, w - 1, y, bgr, bgg, bgb, tol3)) stack = Push(stack, ref sp, y * w + w - 1);
            }

            int painted = 0;
            while (sp > 0)
            {
                int seed = stack[--sp];
                int sy = seed / w, sx = seed - sy * w;
                if (mark[seed] != 0) continue;
                if (!IsBg(data, w, h, sx, sy, bgr, bgg, bgb, tol3)) continue;

                int xl = sx; while (xl > 0 && mark[sy * w + xl - 1] == 0 && IsBg(data, w, h, xl - 1, sy, bgr, bgg, bgb, tol3)) xl--;
                int xr = sx; while (xr < w - 1 && mark[sy * w + xr + 1] == 0 && IsBg(data, w, h, xr + 1, sy, bgr, bgg, bgb, tol3)) xr++;

                for (int x = xl; x <= xr; x++)
                {
                    mark[sy * w + x] = 1;
                    int o = (sy * w + x) * 3;
                    data[o] = 255; data[o + 1] = 255; data[o + 2] = 255;
                    painted++;
                }
                for (int dy = -1; dy <= 1; dy += 2)
                {
                    int ny = sy + dy;
                    if (ny < 0 || ny >= h) continue;
                    int x = xl;
                    while (x <= xr)
                    {
                        if (mark[ny * w + x] == 0 && IsBg(data, w, h, x, ny, bgr, bgg, bgb, tol3))
                        {
                            stack = Push(stack, ref sp, ny * w + x);
                            while (x <= xr && IsBg(data, w, h, x, ny, bgr, bgg, bgb, tol3)) x++;
                        }
                        x++;
                    }
                }
            }

            if (painted < w * h / 200) return 0;           // 涂掉的太少，当作没有深色背景

            // 写回
            BitmapData d2 = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte* bp = (byte*)d2.Scan0;
                for (int y = 0; y < h; y++)
                    System.Runtime.InteropServices.Marshal.Copy(data, y * w * 3,
                        (IntPtr)(bp + y * d2.Stride), w * 3);
            }
            finally { src.UnlockBits(d2); }
            return painted;
        }

        static int[] Push(int[] st, ref int sp, int v)
        {
            if (sp >= st.Length)
            {
                int[] n2 = new int[st.Length * 2];
                Array.Copy(st, n2, st.Length);
                st = n2;
            }
            st[sp++] = v;
            return st;
        }

        static bool IsBg(byte[] data, int w, int h, int x, int y, int r, int g, int b, int tol3)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            int o = (y * w + x) * 3;
            int dd = Math.Abs(data[o + 2] - r) + Math.Abs(data[o + 1] - g) + Math.Abs(data[o] - b);
            if (dd > tol3) return false;
            // 🔴 彩色保护：深色**扫描背景**是中性色（黑/灰，
            //    R≈G≈B）；红封面/红头文件/大红章是饱和彩色。彩色像素一律不算背景
            //    —— 否则整张红封面会被当"深色背景"刷成白纸（实测 实测样本：
            //    红底 250 万像素被涂白，只留下党徽和贴纸，封面被毁）。
            int mx = data[o + 2], mn = data[o + 2];
            if (data[o + 1] > mx) mx = data[o + 1]; if (data[o + 1] < mn) mn = data[o + 1];
            if (data[o] > mx) mx = data[o]; if (data[o] < mn) mn = data[o];
            if (mx - mn > 60) return false;
            return true;
        }

    }
}
