// BatchProcessor.cs — 文件夹批量处理（后台线程执行）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace PicTool
{
    public delegate bool CancelProbe();

    public class BatchOptions
    {
        public bool ExifUpright = true;      // 按 EXIF 方向转正（LoadFile 默认已做，此开关仅用于展示语义）
        public bool FlipH;
        public bool FlipV;
        public double RotateAngle;           // 任意角度（度）
        public bool AutoCropRotate = true;
        public bool AutoDeskew;              // 自动纠偏（投影法检测文字行角度，±12°）
        public bool UseColor;
        public double Bright, Contr, Sat, Temp;
        public bool UseLevels;               // 色阶 + 锐化（跟"应用当前调色参数"一起下发）
        public double Black = 0, White = 255, Gamma = 1.0, Sharpen = 0;
        public bool Binarize;                // 黑白化存档（Sauvola 自适应二值化 + 去孤点）
        public string OutDir;
        public bool Overwrite;               // true = 直接覆盖原文件
    }

    public class BatchResult
    {
        public int Total, Ok, Fail;
        public List<string> Errors = new List<string>();
    }

    public static class BatchProcessor
    {
        static readonly string[] Exts = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

        public static string[] ListImages(string folder, int maxCount)
        {
            List<string> files = new List<string>();
            try
            {
                DirectoryInfo di = new DirectoryInfo(folder);
                FileInfo[] fis = di.GetFiles();
                Array.Sort(fis, delegate(FileInfo a, FileInfo b) { return string.Compare(a.Name, b.Name, true); });
                foreach (FileInfo fi in fis)
                {
                    if (Array.IndexOf(Exts, fi.Extension.ToLowerInvariant()) < 0) continue;
                    files.Add(fi.FullName);
                    if (files.Count >= maxCount) break;
                }
            }
            catch (Exception ex)
            {
                throw new Exception("无法读取文件夹：" + ex.Message);
            }
            return files.ToArray();
        }

        // 在后台线程调用。progress: 0-100；cancelled: 返回 true 中止
        // 多核机器上按 CPU 数并行处理（每张图各用各的位图，互不共享，GDI+ 层面安全）
        public static void Run(string[] files, BatchOptions opt, ProgressHandler progress,
                               CancelProbe cancelled, BatchResult result)
        {
            result.Total = files.Length;
            int workers = SysInfo.WorkerThreads;
            if (workers > files.Length) workers = files.Length;
            if (workers <= 1)
            {
                for (int i = 0; i < files.Length; i++)
                {
                    if (cancelled != null && cancelled()) return;
                    One(files[i], opt, result);
                    if (progress != null) progress((i + 1) * 100 / Math.Max(1, files.Length));
                }
                return;
            }

            int nextIdx = 0, doneCount = 0;
            object gate = new object();
            System.Threading.Thread[] ts = new System.Threading.Thread[workers];
            for (int k = 0; k < workers; k++)
            {
                System.Threading.Thread t = new System.Threading.Thread(delegate()
                {
                    while (true)
                    {
                        if (cancelled != null && cancelled()) return;
                        int i = System.Threading.Interlocked.Increment(ref nextIdx) - 1;
                        if (i >= files.Length) return;
                        One(files[i], opt, result);
                        int d = System.Threading.Interlocked.Increment(ref doneCount);
                        // progress 回调用锁串行化：多 worker 并发进入时
                        // 回调方（UI 侧 BeginInvoke）不该被并发重入
                        if (progress != null)
                            lock (gate) { progress(d * 100 / Math.Max(1, files.Length)); }
                    }
                });
                t.IsBackground = true;
                ts[k] = t;
                t.Start();
            }
            for (int k = 0; k < workers; k++) ts[k].Join();
        }

        // 处理单张：失败只记录不中断（result 的计数与错误列表加锁保护）
        static void One(string src, BatchOptions opt, BatchResult result)
        {
            try
            {
                using (Bitmap bmp = Imaging.LoadFile(src))   // LoadFile 已按 EXIF 转正
                {
                    Bitmap cur = bmp;
                    try
                    {
                        // 顺序：先转正（大角度手动 + 小角度自动），再翻转，再调色，最后黑白化。
                        // 二值化放最后：在"干净的白底黑字"上做阈值，效果最稳。
                        if (Math.Abs(opt.RotateAngle) >= 0.01)
                        {
                            Bitmap n = Imaging.RotateFine(cur, opt.RotateAngle, opt.AutoCropRotate);
                            if (n != cur) cur.Dispose();
                            cur = n;
                        }
                        if (opt.AutoDeskew)
                        {
                            double ang = Enhance.DetectSkewAngle(cur);
                            if (Math.Abs(ang) >= 0.08)
                            {
                                Bitmap n = Imaging.RotateFine(cur, ang, opt.AutoCropRotate);
                                if (n != cur) cur.Dispose();
                                cur = n;
                            }
                        }
                        if (opt.FlipH) { Bitmap n = Imaging.Flip(cur, true); if (n != cur) cur.Dispose(); cur = n; }
                        if (opt.FlipV) { Bitmap n = Imaging.Flip(cur, false); if (n != cur) cur.Dispose(); cur = n; }
                        if (opt.UseColor)
                        {
                            Bitmap n = Imaging.ColorAdjust(cur, opt.Bright, opt.Contr, opt.Sat, opt.Temp);
                            if (n != cur) cur.Dispose();
                            cur = n;
                            if (opt.UseLevels)
                            {
                                AdjustParams p = new AdjustParams();
                                p.Black = opt.Black; p.White = opt.White;
                                p.Gamma = opt.Gamma; p.Sharpen = opt.Sharpen;
                                if (!p.IsNeutral())
                                {
                                    Bitmap m = Enhance.Apply(cur, p);
                                    if (m != cur) cur.Dispose();
                                    cur = m;
                                }
                            }
                        }
                        if (opt.Binarize)
                        {
                            Bitmap n = Enhance.Binarize(cur, 0.2);
                            if (n != cur) cur.Dispose();
                            cur = n;
                        }
                        string dst = opt.Overwrite ? src : Path.Combine(opt.OutDir, Path.GetFileName(src));
                        if (!Imaging.CanOverwrite(dst))
                            throw new Exception("该格式不支持覆盖原文件，请改用另存方式。");
                        Imaging.SaveImage(cur, dst, 92);
                    }
                    finally
                    {
                        if (cur != bmp) cur.Dispose();
                    }
                }
                lock (result) { result.Ok++; }
            }
            catch (Exception ex)
            {
                lock (result)
                {
                    result.Fail++;
                    result.Errors.Add(Path.GetFileName(src) + "：" + ex.Message);
                }
            }
        }
    }
}
