// DocClean.Seal.cs -- DocClean 分文件（四）：印章掩膜。
// 掩膜 I/O 与膨胀（MaskToBytes/BytesToMask/DilateMask）+ 印章掩膜过滤 FilterSealMask。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace PicTool
{
    public static partial class DocClean
    {
        // ==================== 三之二、印章掩膜工具 ====================
        //
        // 印章豁免要把 SealTool 产出的 8bpp 掩膜交给二值化与去污用，
        // 而那两步跑的是 byte[]（快得多），所以先转一次。

        // 8bpp 掩膜（>=128 为真）→ w*h 的 byte[]（1/0）
        public static byte[] MaskToBytes(Bitmap mask, int w, int h)
        {
            byte[] o = new byte[w * h];
            PixelBuffer buf = BitmapBridge.MaskToBuffer(mask);
            for (int y = 0; y < h; y++)
            {
                int off = y * w, ro = y * buf.Stride;
                for (int x = 0; x < w; x++) o[off + x] = (byte)(buf.Data[ro + x] >= 128 ? 1 : 0);
            }
            return o;
        }

        // w*h 的 byte[]（非 0 为真）→ 8bpp 掩膜（供 SealTool.Colorize 使用）
        public static Bitmap BytesToMask(byte[] m, int w, int h)
        {
            PixelBuffer buf = new PixelBuffer(w, h, 1);
            for (int y = 0; y < h; y++)
            {
                int o = y * w, ro = y * buf.Stride;
                for (int x = 0; x < w; x++) buf.Data[ro + x] = (byte)(m[o + x] != 0 ? 255 : 0);
            }
            return BitmapBridge.BufferToMask(buf);
        }

        // 掩膜外扩 r 像素（方形结构元，行列分离）。
        // **必须外扩**：二值化按局部窗口算阈值，贴着章体边缘的一圈会被"啃"掉，
        // 结果章的外环出缺口。留几像素给它啃。
        public static byte[] DilateMask(byte[] m, int w, int h, int r)
        {
            if (r <= 0 || m == null) return m;
            byte[] a = new byte[w * h];
            for (int y = 0; y < h; y++)          // 横向
            {
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    int x0 = x - r < 0 ? 0 : x - r, x1 = x + r >= w ? w - 1 : x + r;
                    byte v = 0;
                    for (int xx = x0; xx <= x1 && v == 0; xx++) v = m[o + xx];
                    a[o + x] = v;
                }
            }
            byte[] b = new byte[w * h];
            // 纵向改**行优先**（y 在外、邻行在内层）：原写法内层跨行跳 w 字节读，
            // 整幅大图时几乎等于随机访问。语义一字未改（仍是窗口内按 y 从小到大取第一个非 0）。
            for (int y = 0; y < h; y++)
            {
                int y0 = y - r < 0 ? 0 : y - r, y1 = y + r >= h ? h - 1 : y + r;
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    byte v = 0;
                    for (int yy = y0; yy <= y1 && v == 0; yy++) v = a[yy * w + x];
                    b[o + x] = v;
                }
            }
            return b;
        }


        // ==================== 三之三、印章掩膜过滤（自动模式必用） ====================
        //
        // 为什么必须有这一步：
        //   SealTool 是给"用户在画面上框选一块区域，再点识别"设计的 —— 那时搜索范围很小，
        //   形状线索（墨迹连通域 + 方形/圆环判定）是很可靠的补充。
        //   但归档流水线要在**整页**上自动找章，同样的形状线索会把汉字、表格格子判成章。
        //   实测：一份**完全没有印章**的灰文档被判出 24 枚，后果是大片文字被当章涂红、
        //   而且被跳过黑白化（因为印章区要保留彩色）—— 这是会毁掉整页的误检。
        //
        // 收法：**必须有成片的红色像素**。
        //   红度线索本来就是两条线索里最可靠的那条；形状线索只是补充。
        //   再加一条尺寸门槛（真章至少 2cm 见方）。
        //   褪色但仍有红味的章能过；**纯灰的章过不了** —— 那种请到印章页手工处理
        //   （手工框选时形状线索可靠，这正是 SealTool 原本的用法）。
        //
        // 返回过滤后的掩膜；kept 输出保留的连通域个数。
        public static unsafe byte[] FilterSealMask(Bitmap src, byte[] mask, int w, int h,
                                                  int redBar, int minRed, int minSide, out int kept)
        {
            kept = 0;
            if (mask == null) return null;
            if (redBar <= 0) redBar = 40;
            if (minRed <= 0) minRed = Math.Max(120, w * h / 30000);
            if (minSide <= 0) minSide = Math.Min(w, h) / 15;

            // 源图红度
            byte[] red = new byte[w * h];
            PixelBuffer rbuf = BitmapBridge.ToBuffer(src);
            fixed (byte* bp = rbuf.Data)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = bp + y * rbuf.Stride;
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte* p = row + x * 3;
                        int b = p[0], g = p[1], r = p[2];
                        int mx = g > b ? g : b;
                        if (r - mx >= redBar) red[o + x] = 1;
                    }
                }
            }

            byte[] outM = new byte[w * h];
            int[] stack = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (mask[o + x] == 0 || mask[o + x] == 2) continue;

                    int rd = 0, wr = 0;
                    int minX = x, maxX = x, minY = y, maxY = y;
                    mask[o + x] = 2;                       // 2 = 已处理
                    stack[wr++] = o + x;
                    while (rd < wr)
                    {
                        int cur = stack[rd++];
                        int cx = cur % w, cy = cur / w;
                        if (cx < minX) minX = cx; else if (cx > maxX) maxX = cx;
                        if (cy < minY) minY = cy; else if (cy > maxY) maxY = cy;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ny = cy + dy;
                            if (ny < 0 || ny >= h) continue;
                            int baseIdx = ny * w;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = cx + dx;
                                if (nx < 0 || nx >= w) continue;
                                int ni = baseIdx + nx;
                                if (mask[ni] == 1) { mask[ni] = 2; stack[wr++] = ni; }
                            }
                        }
                    }

                    int bw2 = maxX - minX + 1, bh2 = maxY - minY + 1;
                    int nRed = 0;
                    for (int yy = minY; yy <= maxY; yy++)
                    {
                        int ro = yy * w;
                        for (int xx = minX; xx <= maxX; xx++) if (red[ro + xx] != 0) nRed++;
                    }

                    // 🔴 两道守卫（实战教训，实测样本 0035/0051）：
                    //    假章能靠页角一个小红点凑够 minRed（120 个红像素）——只要它的
                    //    包围盒够大。真公章的红色是**成片的**（环带内红色密度 10~30%），
                    //    且最大也就占页面几个百分点。所以：
                    //    ① 包围盒 ≤ 页面积 10%（真章 bbox 最大约 3~5%）
                    //    ② 框内红色密度 ≥ 2%（假章往往 <0.1%）
                    double redDensity = nRed / (double)((long)bw2 * bh2);
                    bool ok = nRed >= minRed && bw2 >= minSide && bh2 >= minSide &&
                              (long)bw2 * bh2 <= (long)w * h / 10 && redDensity >= 0.02;
                    if (ok)
                    {
                        kept++;
                        for (int i = 0; i < wr; i++) outM[stack[i]] = 1;
                    }
                }
            }
            return outM;
        }

    }
}
