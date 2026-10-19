// PixelBuffer.cs — 无 GDI 句柄的像素缓冲（取代分散的 Bitmap LockBits 操作）
//
// 为什么要有它：
//   全项目 70+ 处 LockBits 直接操作 Bitmap，其中 33 处的解锁不在 finally 里 ——
//   位图一旦在处理中途抛异常（大图内存不足最典型）就永久锁死。
//   把像素数据搬进普通 byte[] 后，"资源"变成 GC 管的数组；GDI 锁只存在于
//   BitmapBridge 一处，由一处 try/finally 统一保证。
//   这也是 SIMD 向量化唯一的落脚点：Vector<T> 只能作用于数组，作用不了锁句柄。
//
// 布局与 GDI+ 完全一致：24bpp = BGR 交错、行 4 字节对齐 ⇒ 现有算法把
// `d.Stride` 换成 `buf.Stride` 即可，逐字节逻辑零变化。
using System;

namespace PicTool
{
    public sealed class PixelBuffer
    {
        public readonly byte[] Data;
        public readonly int Width;
        public readonly int Height;
        public readonly int Stride;
        public readonly int Bpp;          // 3 = 24bpp BGR（图像）；1 = 8bpp（掩膜）；4 = 32bpp BGRA（叠加层）

        public PixelBuffer(int w, int h, int bpp)
        {
            if (w <= 0 || h <= 0) throw new ArgumentException("PixelBuffer: 尺寸非法 " + w + "x" + h);
            if (bpp != 3 && bpp != 1 && bpp != 4) throw new ArgumentException("PixelBuffer: 仅支持 24bpp/8bpp/32bpp");
            Width = w;
            Height = h;
            Bpp = bpp;
            Stride = (w * bpp + 3) & ~3;   // 与 GDI+ 相同的 4 字节行对齐
            Data = new byte[Stride * h];
        }
    }
}
