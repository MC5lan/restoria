// BitmapBridge.cs — Bitmap ↔ PixelBuffer 的唯一通道
//
// 🔴 全项目 LockBits 只允许出现在这个文件里（集中管理的架构约束，避免分散加锁导致异常路径锁死位图）。
//
// 四个方法都是"整块拷贝"语义：进桥拷出来、出桥拷回去。代价是每个算子两次
// 全图 memcpy（Marshal.Copy，内存带宽级，200dpi A4 一次约 1~2ms），换来的是
// "异常路径锁死位图"的问题**在结构上不可能再发生** —— 拷贝只发生在
// try 内的第一行/最后一行，中途任何异常都不触碰 GDI 锁。
// 后续优化方向：把出桥拷贝消掉（算子串联复用同一块 buffer）。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PicTool
{
    public static class BitmapBridge
    {
        // ---------- 24bpp 图像 ----------

        // 位图 → 缓冲（拷贝）。bpp 给 3（24bpp）、4（32bpp ARGB）；源格式不符时内部克隆转换。
        public static PixelBuffer ToBuffer(Bitmap src, int bpp)
        {
            int w = src.Width, h = src.Height;
            PixelBuffer buf = new PixelBuffer(w, h, bpp);
            PixelFormat want = bpp == 3 ? PixelFormat.Format24bppRgb :
                               bpp == 4 ? PixelFormat.Format32bppArgb : PixelFormat.Format8bppIndexed;
            Bitmap s = src;
            bool cloned = false;
            if (s.PixelFormat != want)
            {
                s = src.Clone(new Rectangle(0, 0, w, h), want);
                cloned = true;                          // 克隆副本用完即弃；原 src 不动
            }
            BitmapData d = s.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, want);
            try { CopyIn(d, buf.Data, buf.Stride, w, h, buf.Bpp); }
            finally { s.UnlockBits(d); }
            if (cloned) s.Dispose();
            return buf;
        }

        // 兼容重载：默认 24bpp
        public static PixelBuffer ToBuffer(Bitmap src) { return ToBuffer(src, 3); }

        // 缓冲 → 写回位图（尺寸/bpp 必须一致；按目标实际格式写入）
        public static void WriteBack(Bitmap dst, PixelBuffer buf)
        {
            if (dst.Width != buf.Width || dst.Height != buf.Height)
                throw new ArgumentException("WriteBack: 尺寸不一致 " + dst.Width + "x" + dst.Height + " vs " + buf.Width + "x" + buf.Height);
            BitmapData d = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, dst.PixelFormat);
            try { CopyOut(buf.Data, buf.Stride, d, dst.Width, dst.Height, buf.Bpp); }
            finally { dst.UnlockBits(d); }
        }

        // 缓冲 → 新的 24bpp 位图（PerspectiveWarp 这类"生成新图"的算子用）
        public static Bitmap BufferToImage(PixelBuffer buf)
        {
            if (buf.Bpp != 3) throw new ArgumentException("BufferToImage: 需要 24bpp 缓冲");
            Bitmap bmp = new Bitmap(buf.Width, buf.Height, PixelFormat.Format24bppRgb);
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, buf.Width, buf.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try { CopyOut(buf.Data, buf.Stride, d, buf.Width, buf.Height, buf.Bpp); }
            finally { bmp.UnlockBits(d); }
            return bmp;
        }

        // 缓冲 → 新的 32bpp ARGB 位图（印章叠加层用，保留 alpha）
        public static Bitmap BufferToOverlay(PixelBuffer buf)
        {
            if (buf.Bpp != 4) throw new ArgumentException("BufferToOverlay: 需要 32bpp 缓冲");
            Bitmap bmp = new Bitmap(buf.Width, buf.Height, PixelFormat.Format32bppArgb);
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, buf.Width, buf.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { CopyOut(buf.Data, buf.Stride, d, buf.Width, buf.Height, buf.Bpp); }
            finally { bmp.UnlockBits(d); }
            return bmp;
        }

        // ---------- 8bpp 掩膜 ----------

        // 8bpp 位图 → 缓冲
        public static PixelBuffer MaskToBuffer(Bitmap mask)
        {
            int w = mask.Width, h = mask.Height;
            PixelBuffer buf = new PixelBuffer(w, h, 1);
            Bitmap s = mask;
            bool cloned = false;
            if (s.PixelFormat != PixelFormat.Format8bppIndexed)
            {
                s = mask.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format8bppIndexed);
                cloned = true;
            }
            BitmapData d = s.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format8bppIndexed);
            try { CopyIn(d, buf.Data, buf.Stride, w, h, buf.Bpp); }
            finally { s.UnlockBits(d); }
            if (cloned) s.Dispose();
            return buf;
        }

        // 缓冲 → 新的 8bpp 位图（带灰度调色板；255=洞 的约定与 Imaging.MakeMask 一致）
        public static Bitmap BufferToMask(PixelBuffer buf)
        {
            Bitmap bmp = new Bitmap(buf.Width, buf.Height, PixelFormat.Format8bppIndexed);
            ColorPalette pal = bmp.Palette;
            for (int i = 0; i < 256; i++) pal.Entries[i] = Color.FromArgb(i, i, i);
            bmp.Palette = pal;
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, buf.Width, buf.Height), ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
            try { CopyOut(buf.Data, buf.Stride, d, buf.Width, buf.Height, buf.Bpp); }
            finally { bmp.UnlockBits(d); }
            return bmp;
        }

        // ---------- 拷贝内核 ----------

        // BitmapData → byte[]。行距一致时一次整块 memcpy；不一致时逐行。
        // （Buffer.MemoryCopy = CRT memcpy，.NET 4.5+；Marshal.Copy 没有"指针→指针"重载）
        static unsafe void CopyIn(BitmapData d, byte[] data, int stride, int w, int h, int bpp)
        {
            fixed (byte* dp = data)
            {
                byte* sp = (byte*)d.Scan0;
                if (d.Stride == stride)
                {
                    long n = (long)stride * h;
                    Buffer.MemoryCopy(sp, dp, n, n);
                }
                else
                {
                    int rowBytes = w * bpp;
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy(sp + y * d.Stride, dp + y * stride, rowBytes, rowBytes);
                }
            }
        }

        static unsafe void CopyOut(byte[] data, int stride, BitmapData d, int w, int h, int bpp)
        {
            fixed (byte* dp = data)
            {
                byte* sp = (byte*)d.Scan0;
                if (d.Stride == stride)
                {
                    long n = (long)stride * h;
                    Buffer.MemoryCopy(dp, sp, n, n);
                }
                else
                {
                    int rowBytes = w * bpp;
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy(dp + y * stride, sp + y * d.Stride, rowBytes, rowBytes);
                }
            }
        }
    }
}
