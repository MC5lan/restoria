// TextStamp.cs — 文字元素（添加/编辑文字功能的底座）
//
// 设计（「文字元素层」方案）：
//  * 文字先作为 TextItem 数据对象挂在 Doc.Texts 上，画布实时渲染、可选中/拖动/改样式；
//    显式「烙印」或保存前才由 Rasterize 落像素 —— 这样"改已有文字/层级"才有得做。
//  * 本文件只依赖 System.Drawing（与 Imaging 其它文件同级），禁止引用 System.Windows.Forms。
//  * 字号单位是图像像素（GraphicsUnit.Pixel）：档案扫描件 200/300dpi，用"点"会失真，
//    像素字号让预览与烙印严格一致、且与缩放无关。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;

namespace PicTool
{
    // 一段可编辑的文字元素。Box 是图像坐标下的包围盒（拖动/选中的句柄来源）。
    public class TextItem
    {
        public string Text = "";
        public string FontName = "宋体";
        public float SizeEm = 36f;          // 图像像素
        public bool Bold, Italic, Underline;
        public Color Color = Color.FromArgb(32, 30, 28);   // 墨黑
        public int Align;                   // 0=左 1=中 2=右（多行内部对齐）
        public RectangleF Box;

        public TextItem Clone()
        {
            TextItem t = new TextItem();
            t.Text = Text;
            t.FontName = FontName;
            t.SizeEm = SizeEm;
            t.Bold = Bold; t.Italic = Italic; t.Underline = Underline;
            t.Color = Color;
            t.Align = Align;
            t.Box = Box;
            return t;
        }
    }

    public static class TextStamp
    {
        const float MinSize = 8f;
        const float MaxSize = 400f;

        static Bitmap measureBmp;
        static Graphics measureG;

        static Graphics MeasureG()
        {
            if (measureG == null)
            {
                measureBmp = new Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                measureG = Graphics.FromImage(measureBmp);
            }
            return measureG;
        }

        public static FontStyle StyleOf(TextItem t)
        {
            FontStyle fs = FontStyle.Regular;
            if (t.Bold) fs |= FontStyle.Bold;
            if (t.Italic) fs |= FontStyle.Italic;
            if (t.Underline) fs |= FontStyle.Underline;
            return fs;
        }

        // 构造渲染用字体。字体名不存在时 GDI+ 自动回退默认族（.NET 2.0 不抛异常）。
        public static Font MakeFont(TextItem t, float sizeEm)
        {
            float s = sizeEm < 1f ? 1f : sizeEm;
            return new Font(t.FontName, s, StyleOf(t), GraphicsUnit.Pixel);
        }

        // 按当前文本重新量包围盒（保持 X,Y 不动）。多行按 '\n' 显式分行，
        // 关自动换行（NoWrap）：GDI+ 的自动换行宽度不可控，显式分行行为可预期。
        public static void UpdateBox(TextItem t)
        {
            SizeF sz = MeasureText(t);
            float w = Math.Max(12f, sz.Width + 4f);
            float h = Math.Max((float)(t.SizeEm * 1.2), sz.Height + 2f);
            t.Box = new RectangleF(t.Box.X, t.Box.Y, w, h);
        }

        public static SizeF MeasureText(TextItem t)
        {
            using (Font f = MakeFont(t, t.SizeEm))
            using (StringFormat sf = FormatOf(t))
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                return MeasureG().MeasureString(t.Text == null ? "" : t.Text, f, new PointF(0, 0), sf);
            }
        }

        public static StringFormat FormatOf(TextItem t)
        {
            StringFormat sf = new StringFormat(StringFormatFlags.NoWrap);
            sf.Alignment = (StringAlignment)t.Align;
            sf.LineAlignment = StringAlignment.Near;
            return sf;
        }

        // 把所有文字元素按列表序画到位图上（烙印）。列表序 = 层级，后面的画在上面。
        public static void Rasterize(Bitmap dst, List<TextItem> items)
        {
            if (dst == null || items == null || items.Count == 0) return;
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                for (int i = 0; i < items.Count; i++) DrawItem(g, items[i]);
            }
        }

        public static void DrawItem(Graphics g, TextItem t)
        {
            using (Font f = MakeFont(t, t.SizeEm))
            using (SolidBrush b = new SolidBrush(t.Color))
            using (StringFormat sf = FormatOf(t))
            {
                g.DrawString(t.Text == null ? "" : t.Text, f, b, t.Box, sf);
            }
        }

        // 命中测试：从最上层（列表尾）往下找，返回索引，未命中 -1
        public static int HitTest(List<TextItem> items, float x, float y)
        {
            if (items == null) return -1;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                RectangleF b = items[i].Box;
                if (x >= b.X && x <= b.Right && y >= b.Y && y <= b.Bottom) return i;
            }
            return -1;
        }

        public static float ClampSize(float s) { return s < MinSize ? MinSize : (s > MaxSize ? MaxSize : s); }
    }
}
