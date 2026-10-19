// Icons.cs — 16px 线条矢量图标（全部用 GDI+ 手绘，不依赖任何图片资源）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PicTool
{
    static class Icons
    {
        public static void Draw(Graphics g, string name, Rectangle box, Color color, float stroke)
        {
            if (name == null || name.Length == 0) return;
            GraphicsState st = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen p = new Pen(color, stroke))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    p.LineJoin = LineJoin.Round;
                    DrawPath(g, p, name, box);
                }
            }
            finally { g.Restore(st); }   // 异常也要还原 Graphics 状态
        }

        static float X(Rectangle r, float v) { return r.X + v * (r.Width - 1) / 16f; }
        static float Y(Rectangle r, float v) { return r.Y + v * (r.Height - 1) / 16f; }
        static PointF P(Rectangle r, float x, float y) { return new PointF(X(r, x), Y(r, y)); }

        static void DrawPath(Graphics g, Pen p, string n, Rectangle r)
        {
            float w = (r.Width - 1) / 16f;
            switch (n)
            {
                case "info":
                    {
                        // 信息：圆圈 + 竖线 + 上点（i）
                        float cx = X(r, 8), cy = Y(r, 8), rr = 6.2f * w;
                        g.DrawEllipse(p, cx - rr, cy - rr, rr * 2, rr * 2);
                        g.DrawLine(p, cx, cy - 2.6f * w, cx, cy + 3.0f * w);
                        g.DrawEllipse(p, cx - 0.55f * w, cy - 4.6f * w, 1.1f * w, 1.1f * w);
                        break;
                    }
                case "sun":
                    {
                        // 太阳：中心圆 + 8 条射线（16px 网格，半径按网格缩放）
                        float cx = X(r, 8), cy = Y(r, 8), rr = 3.1f * w;
                        g.DrawEllipse(p, cx - rr, cy - rr, rr * 2, rr * 2);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4.0;
                            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
                            g.DrawLine(p, cx + c * 5.3f * w, cy + s * 5.3f * w,
                                          cx + c * 7.0f * w, cy + s * 7.0f * w);
                        }
                        break;
                    }
                case "moon":
                    {
                        // 月牙：一段外弧 + 一段内弧反向闭合
                        GraphicsPath path = new GraphicsPath();
                        float cx = X(r, 9.5f), cy = Y(r, 8), rr = 6.2f * w;
                        path.AddArc(cx - rr, cy - rr, rr * 2, rr * 2, 55f, 250f);
                        path.AddArc(cx - rr * 0.72f, cy - rr * 0.62f, rr * 1.55f, rr * 1.55f, 305f, -205f);
                        path.CloseFigure();
                        g.DrawPath(p, path);
                        path.Dispose();
                        break;
                    }
                case "folder":
                    {
                        GraphicsPath path = new GraphicsPath();
                        path.AddLines(new PointF[] {
                            P(r,2,12.5f), P(r,2,4), P(r,6,4), P(r,7.4f,6), P(r,14,6), P(r,14,12.5f) });
                        path.CloseFigure();
                        g.DrawPath(p, path);
                        path.Dispose();
                        break;
                    }
                case "image":
                    {
                        Theme.StrokeRound(g, Rect16(r, 2, 3, 12, 10), (int)(2 * w), p.Color, p.Width);
                        g.DrawEllipse(p, X(r, 4.2f), Y(r, 5.2f), 2 * w, 2 * w);
                        g.DrawLines(p, new PointF[] {
                            P(r,3.2f,12.2f), P(r,7,8.2f), P(r,9.6f,10.8f), P(r,11.6f,8.8f), P(r,13.6f,11.2f) });
                        break;
                    }
                case "undo":
                    {
                        g.DrawArc(p, X(r, 3.5f), Y(r, 4.5f), 9 * w, 8 * w, 200, 250);
                        g.DrawLines(p, new PointF[] { P(r,3.2f,8.4f), P(r,2.6f,4.6f), P(r,6.4f,4.4f) });
                        break;
                    }
                case "redo":
                    {
                        g.DrawArc(p, X(r, 3.5f), Y(r, 4.5f), 9 * w, 8 * w, -90, 250);
                        g.DrawLines(p, new PointF[] { P(r,12.8f,8.4f), P(r,13.4f,4.6f), P(r,9.6f,4.4f) });
                        break;
                    }
                case "save":
                    {
                        GraphicsPath path = new GraphicsPath();
                        path.AddLines(new PointF[] { P(r,3,3), P(r,11.6f,3), P(r,13,4.4f), P(r,13,13), P(r,3,13) });
                        path.CloseFigure();
                        g.DrawPath(p, path);
                        path.Dispose();
                        g.DrawRectangle(p, X(r, 5.4f), Y(r, 3), 5.2f * w, 3.6f * w);       // 上滑盖
                        g.DrawRectangle(p, X(r, 5), Y(r, 9), 6 * w, 4 * w);               // 下部抽屉
                        break;
                    }
                case "brush":
                    {
                        g.DrawLine(p, P(r, 12.6f, 3.4f), P(r, 7.2f, 8.8f));
                        GraphicsPath path = new GraphicsPath();
                        path.AddLines(new PointF[] { P(r,6.4f,9.6f), P(r,3.2f,13.2f), P(r,8.4f,11.4f) });
                        path.CloseFigure();
                        g.DrawPath(p, path);
                        path.Dispose();
                        break;
                    }
                case "marquee":
                    {
                        using (Pen d = new Pen(p.Color, p.Width))
                        {
                            d.DashPattern = new float[] { 2.2f, 1.8f };
                            g.DrawRectangle(d, X(r, 2.4f), Y(r, 3.4f), 11.2f * w, 9.2f * w);
                        }
                        break;
                    }
                case "eraser":
                    {
                        GraphicsPath path = new GraphicsPath();
                        path.AddLines(new PointF[] { P(r,3,10.4f), P(r,9.2f,4), P(r,13,7.8f), P(r,7.4f,13.4f) });
                        path.CloseFigure();
                        g.DrawPath(p, path);
                        path.Dispose();
                        g.DrawLine(p, P(r,6.2f,7), P(r,10,10.8f));
                        g.DrawLine(p, P(r,7.4f,13.4f), P(r,13.4f,13.4f));
                        break;
                    }
                case "wand":
                    {
                        g.DrawLine(p, P(r,3,13), P(r,10,6));
                        g.DrawLine(p, P(r,12.4f,2.4f), P(r,12.4f,5.2f));
                        g.DrawLine(p, P(r,11,3.8f), P(r,13.8f,3.8f));
                        g.DrawLine(p, P(r,13.2f,8.6f), P(r,13.2f,10.6f));
                        g.DrawLine(p, P(r,12.2f,9.6f), P(r,14.2f,9.6f));
                        break;
                    }
                case "rotate":
                    {
                        g.DrawArc(p, X(r, 3.4f), Y(r, 3.4f), 9.2f * w, 9.2f * w, 110, 280);
                        g.DrawLines(p, new PointF[] { P(r,6.2f,2.6f), P(r,2.8f,4.4f), P(r,5.6f,6.6f) });
                        break;
                    }
                case "palette":
                    {
                        GraphicsPath path = new GraphicsPath();
                        path.AddArc(X(r, 2), Y(r, 2), 12 * w, 12 * w, 40, 300);
                        path.AddLine(P(r, 8, 2.2f), P(r, 8, 8));
                        path.CloseFigure();
                        g.DrawPath(p, path);
                        path.Dispose();
                        using (SolidBrush b = new SolidBrush(p.Color))
                        {
                            g.FillEllipse(b, X(r, 5.2f), Y(r, 5.6f), 1.7f * w, 1.7f * w);
                            g.FillEllipse(b, X(r, 8.6f), Y(r, 8.4f), 1.7f * w, 1.7f * w);
                            g.FillEllipse(b, X(r, 10.6f), Y(r, 5.4f), 1.7f * w, 1.7f * w);
                        }
                        break;
                    }
                case "flipH":
                    {
                        using (Pen d = new Pen(p.Color, p.Width))
                        {
                            d.DashPattern = new float[] { 1.8f, 1.6f };
                            g.DrawLine(d, X(r, 8), Y(r, 2), X(r, 8), Y(r, 14));
                        }
                        g.DrawPolygon(p, new PointF[] { P(r, 2, 4), P(r, 6.4f, 8), P(r, 2, 12) });
                        g.DrawPolygon(p, new PointF[] { P(r, 14, 4), P(r, 9.6f, 8), P(r, 14, 12) });
                        break;
                    }
                case "flipV":
                    {
                        using (Pen d = new Pen(p.Color, p.Width))
                        {
                            d.DashPattern = new float[] { 1.8f, 1.6f };
                            g.DrawLine(d, X(r, 2), Y(r, 8), X(r, 14), Y(r, 8));
                        }
                        g.DrawPolygon(p, new PointF[] { P(r, 4, 2), P(r, 8, 6.4f), P(r, 12, 2) });
                        g.DrawPolygon(p, new PointF[] { P(r, 4, 14), P(r, 8, 9.6f), P(r, 12, 14) });
                        break;
                    }
                case "fit":
                    {
                        // 四角括号 + 内缩小方框：比"方框+四箭头"在 16px 下清楚得多，也不会被误认成"关闭"
                        float m = 2.2f, len = 3.4f, e = 13.8f;
                        g.DrawLines(p, new PointF[] { P(r, m + len, m), P(r, m, m), P(r, m, m + len) });
                        g.DrawLines(p, new PointF[] { P(r, e - len, m), P(r, e, m), P(r, e, m + len) });
                        g.DrawLines(p, new PointF[] { P(r, m, e - len), P(r, m, e), P(r, m + len, e) });
                        g.DrawLines(p, new PointF[] { P(r, e, e - len), P(r, e, e), P(r, e - len, e) });
                        g.DrawRectangle(p, X(r, 6), Y(r, 6), (r.Width - 1) * 4f / 16f, (r.Height - 1) * 4f / 16f);
                        break;
                    }
                case "zoomIn":
                    {
                        g.DrawEllipse(p, X(r, 2.4f), Y(r, 2.4f), 8 * w, 8 * w);
                        g.DrawLine(p, P(r, 10.6f, 10.6f), P(r, 13.6f, 13.6f));
                        g.DrawLine(p, P(r, 6.4f, 4.6f), P(r, 6.4f, 8.2f));
                        g.DrawLine(p, P(r, 4.6f, 6.4f), P(r, 8.2f, 6.4f));
                        break;
                    }
                case "zoomOut":
                    {
                        g.DrawEllipse(p, X(r, 2.4f), Y(r, 2.4f), 8 * w, 8 * w);
                        g.DrawLine(p, P(r, 10.6f, 10.6f), P(r, 13.6f, 13.6f));
                        g.DrawLine(p, P(r, 4.6f, 6.4f), P(r, 8.2f, 6.4f));
                        break;
                    }
                case "plus":
                    g.DrawLine(p, P(r, 8, 3.4f), P(r, 8, 12.6f));
                    g.DrawLine(p, P(r, 3.4f, 8), P(r, 12.6f, 8));
                    break;
                case "minus":
                    g.DrawLine(p, P(r, 3.4f, 8), P(r, 12.6f, 8));
                    break;
                case "close":
                    g.DrawLine(p, P(r, 4, 4), P(r, 12, 12));
                    g.DrawLine(p, P(r, 12, 4), P(r, 4, 12));
                    break;
                case "check":
                    g.DrawLines(p, new PointF[] { P(r, 3.4f, 8.4f), P(r, 6.6f, 11.6f), P(r, 12.6f, 4.6f) });
                    break;
                case "chevronDown":
                    g.DrawLines(p, new PointF[] { P(r, 4.4f, 6.2f), P(r, 8, 9.8f), P(r, 11.6f, 6.2f) });
                    break;
                case "chevronRight":
                    g.DrawLines(p, new PointF[] { P(r, 6.4f, 3.6f), P(r, 10.4f, 8), P(r, 6.4f, 12.4f) });
                    break;
                case "chevronLeft":
                    g.DrawLines(p, new PointF[] { P(r, 9.6f, 3.6f), P(r, 5.6f, 8), P(r, 9.6f, 12.4f) });
                    break;
                case "alignLeft":
                    for (int i = 0; i < 3; i++)
                    {
                        float lw = i == 1 ? 8.4f : 12.4f;
                        g.DrawLine(p, X(r, 1.8f), Y(r, 4.4f + i * 3.6f), X(r, 1.8f + lw), Y(r, 4.4f + i * 3.6f));
                    }
                    break;
                case "alignCenter":
                    for (int i = 0; i < 3; i++)
                    {
                        float lw = i == 1 ? 8.4f : 12.4f;
                        float x0 = 8f - lw / 2f;
                        g.DrawLine(p, X(r, x0), Y(r, 4.4f + i * 3.6f), X(r, x0 + lw), Y(r, 4.4f + i * 3.6f));
                    }
                    break;
                case "alignRight":
                    for (int i = 0; i < 3; i++)
                    {
                        float lw = i == 1 ? 8.4f : 12.4f;
                        g.DrawLine(p, X(r, 14.2f - lw), Y(r, 4.4f + i * 3.6f), X(r, 14.2f), Y(r, 4.4f + i * 3.6f));
                    }
                    break;
                case "up":
                    g.DrawLines(p, new PointF[] { P(r, 3.6f, 9.6f), P(r, 8, 5.2f), P(r, 12.4f, 9.6f) });
                    break;
                case "refresh":
                    {
                        g.DrawArc(p, X(r, 2.6f), Y(r, 2.6f), 10.8f * w, 10.8f * w, 40, 270);
                        g.DrawLines(p, new PointF[] { P(r, 2.4f, 6.2f), P(r, 5.6f, 3.2f), P(r, 6.8f, 6.6f) });
                        break;
                    }
                case "external":
                    {
                        Theme.StrokeRound(g, Rect16(r, 2.6f, 5.4f, 8, 8), (int)(1.8f * w), p.Color, p.Width);
                        g.DrawLine(p, P(r, 8.6f, 7.4f), P(r, 13.4f, 2.6f));
                        g.DrawLines(p, new PointF[] { P(r, 9.4f, 2.6f), P(r, 13.4f, 2.6f), P(r, 13.4f, 6.6f) });
                        break;
                    }
                case "warning":
                    {
                        GraphicsPath path = new GraphicsPath();
                        path.AddLines(new PointF[] { P(r, 8, 2.4f), P(r, 14, 13), P(r, 2, 13) });
                        path.CloseFigure();
                        g.DrawPath(p, path);
                        path.Dispose();
                        g.DrawLine(p, P(r, 8, 6.4f), P(r, 8, 9.6f));
                        using (SolidBrush b = new SolidBrush(p.Color))
                            g.FillEllipse(b, X(r, 7.4f), Y(r, 10.8f), 1.3f * w, 1.3f * w);
                        break;
                    }
                case "layers":
                    {
                        g.DrawPolygon(p, new PointF[] { P(r, 8, 2.6f), P(r, 14, 6), P(r, 8, 9.4f), P(r, 2, 6) });
                        g.DrawLines(p, new PointF[] { P(r, 2.4f, 9.2f), P(r, 8, 12.4f), P(r, 13.6f, 9.2f) });
                        break;
                    }
                case "keyboard":
                    {
                        // 键盘：外框 + 两排键 + 空格键
                        g.DrawRectangle(p, X(r, 1.6f), Y(r, 4), (r.Width - 1) * 12.8f / 16f, (r.Height - 1) * 8f / 16f);
                        for (int k = 0; k < 3; k++)
                            g.DrawLine(p, X(r, 4 + k * 3.6f), Y(r, 6.4f), X(r, 4 + k * 3.6f), Y(r, 7.6f));
                        for (int k = 0; k < 2; k++)
                            g.DrawLine(p, X(r, 5.8f + k * 3.6f), Y(r, 9.2f), X(r, 5.8f + k * 3.6f), Y(r, 10.4f));
                        g.DrawLine(p, X(r, 4), Y(r, 9.8f), X(r, 7), Y(r, 9.8f));
                        break;
                    }
                case "crop":
                    {
                        // 裁剪框：两条横线 + 两条竖线交叉出框
                        g.DrawLine(p, X(r, 5), Y(r, 1.6f), X(r, 5), Y(r, 11));
                        g.DrawLine(p, X(r, 1.6f), Y(r, 5), X(r, 11), Y(r, 5));
                        g.DrawLine(p, X(r, 11), Y(r, 5), X(r, 11), Y(r, 14.4f));
                        g.DrawLine(p, X(r, 5), Y(r, 11), X(r, 14.4f), Y(r, 11));
                        break;
                    }
                case "page":
                    {
                        // 纸张 + 折角 + 两行字
                        g.DrawLines(p, new PointF[] {
                            P(r, 3.5f, 1.8f), P(r, 9.6f, 1.8f), P(r, 12.8f, 5), P(r, 12.8f, 14.2f), P(r, 3.5f, 14.2f), P(r, 3.5f, 1.8f) });
                        g.DrawLines(p, new PointF[] { P(r, 9.6f, 1.8f), P(r, 9.6f, 5), P(r, 12.8f, 5) });
                        g.DrawLine(p, X(r, 5.8f), Y(r, 8.4f), X(r, 10.4f), Y(r, 8.4f));
                        g.DrawLine(p, X(r, 5.8f), Y(r, 11), X(r, 8.6f), Y(r, 11));
                        break;
                    }
                case "stamp":
                    {
                        // 印章：章体 + 章柄 + 底部印泥线
                        g.DrawLines(p, new PointF[] {
                            P(r, 6.4f, 2.4f), P(r, 6.4f, 6.4f), P(r, 9.6f, 6.4f), P(r, 9.6f, 2.4f) });
                        g.DrawLines(p, new PointF[] { P(r, 4.6f, 9.2f), P(r, 4.6f, 6.4f), P(r, 11.4f, 6.4f), P(r, 11.4f, 9.2f) });
                        Theme.HLine(g, (int)X(r, 2.8f), (int)X(r, 13.2f), (int)Y(r, 11.6f), p.Color);
                        Theme.HLine(g, (int)X(r, 4.2f), (int)X(r, 11.8f), (int)Y(r, 13.6f), p.Color);
                        break;
                    }
                case "batch":
                    {
                        // 批处理：两张叠放纸张 + 对勾
                        g.DrawLines(p, new PointF[] {
                            P(r, 5.4f, 1.8f), P(r, 10.2f, 1.8f), P(r, 12.6f, 4.2f), P(r, 12.6f, 6.6f) });
                        g.DrawLines(p, new PointF[] {
                            P(r, 3.4f, 4.6f), P(r, 3.4f, 14.2f), P(r, 10.6f, 14.2f), P(r, 10.6f, 4.6f), P(r, 8.2f, 4.6f), P(r, 3.4f, 4.6f) });
                        g.DrawLines(p, new PointF[] { P(r, 5.2f, 9.4f), P(r, 6.8f, 11), P(r, 9.4f, 7.6f) });
                        break;
                    }
                case "scan":
                    {
                        // 扫描仪：机盖 + 光束线 + 底座出纸
                        g.DrawLines(p, new PointF[] {
                            P(r, 3.4f, 2.6f), P(r, 12.6f, 2.6f), P(r, 12.6f, 7), P(r, 3.4f, 7), P(r, 3.4f, 2.6f) });
                        g.DrawLine(p, X(r, 1.6f), Y(r, 9.4f), X(r, 14.4f), Y(r, 9.4f));
                        g.DrawLines(p, new PointF[] { P(r, 3.4f, 9.4f), P(r, 3.4f, 13.4f), P(r, 12.6f, 13.4f), P(r, 12.6f, 9.4f) });
                        g.FillEllipse(p.Brush, X(r, 11.2f), Y(r, 4.4f), 0.9f * w, 0.9f * w);
                        break;
                    }
                case "files":
                    {
                        // 文件管理：两张纸叠放
                        g.DrawLines(p, new PointF[] {
                            P(r, 6.4f, 1.8f), P(r, 10.4f, 1.8f), P(r, 12.8f, 4.2f), P(r, 12.8f, 10.6f) });
                        g.DrawLines(p, new PointF[] {
                            P(r, 3.2f, 4.6f), P(r, 3.2f, 14.2f), P(r, 10.2f, 14.2f), P(r, 10.2f, 4.6f), P(r, 8.4f, 4.6f), P(r, 6.4f, 4.6f), P(r, 3.2f, 4.6f) });
                        break;
                    }
                case "text":
                    {
                        // 文字：衬线 T
                        g.DrawLine(p, X(r, 3.2f), Y(r, 3.2f), X(r, 12.8f), Y(r, 3.2f));
                        g.DrawLine(p, X(r, 8), Y(r, 3.2f), X(r, 8), Y(r, 13f));
                        g.DrawLine(p, X(r, 5.8f), Y(r, 13.4f), X(r, 10.2f), Y(r, 13.4f));
                        break;
                    }
            }
        }

        static Rectangle Rect16(Rectangle r, float x, float y, float w, float h)
        {
            return new Rectangle(
                (int)X(r, x), (int)Y(r, y),
                Math.Max(1, (int)(w * (r.Width - 1) / 16f)),
                Math.Max(1, (int)(h * (r.Height - 1) / 16f)));
        }
    }
}
