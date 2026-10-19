// MainForm.Page.Scan.cs - 主窗体（partial）：扫描页
// P5 第三刀：页面类化——控件与构建逻辑自治（与 BatchPage 同模式）；
// 扫描业务方法（RefreshDevices/RunScan 等）引用主窗体状态密集，留在下半段窗体侧。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm
    {
        void BuildScanPage()
        {
            pageScan = new ScanPage(this);          // 构造器内完成 NewPage 的等价注册
            pnlScan = pageScan;                     // 切页/滚动机制仍通过 pnlScan 走
            pageScan.Build();
        }

        // 扫描页：一个 Panel 子类，自己管自己的控件。要加控件改这里，不要回主窗体。
        public sealed class ScanPage : Panel
        {
            readonly IPageHost _mf;

            internal FlatButton btnDevPrev, btnDevNext, btnDevRefresh;
            internal Label lblScanDev;
            internal Segmented segScanDpi, segScanColor, segScanTarget;
            internal Segmented segScanFmt;                  // 输出格式（JPG/PNG/跟随扫描仪）
            internal Label lblScanDir;
            internal FlatButton btnPickScanDir, btnOpenScanDir;
            internal FlatButton btnScanRun, btnScanCancel;
            internal FlatProgress scanProgress;
            internal Label lblScanResult;
            internal Panel scanHintCard;

            internal ScanPage(IPageHost mf)
            {
                _mf = mf;
                // 与 NewPage() 相同的页面注册（Bounds/锚定/背景/默认隐藏/挂进内容区）
                Bounds = new Rectangle(0, 0, _mf.content.Width, _mf.content.Height);
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                BackColor = Theme.Panel;
                Visible = false;
                _mf.content.Controls.Add(this);
            }

            internal void Build()
            {
                int x = Theme.Pad, y = 12;
                y = _mf.PageHead(this, "扫描", "接上扫描仪直接扫进程序，存到指定文件夹", x, y, _mf.Cw);

                if (!Scanner.Available)
                {
                    // WIA 不可用只影响扫描。本页到此为止 —— 文件工具 已独立成页，
                    // 不能再建到扫描页上（旧写法会把 pnlFiles 指到扫描页，导致页面切换错乱）。
                    scanHintCard = _mf.Hint(this, x, y, _mf.Cw, 76,
                        "这台电脑的 Windows 图像采集（WIA）组件不可用，无法接入扫描仪。\n其余功能不受影响。");
                    y += 88;
                    _mf.PageEnd(this);
                    return;
                }

                _mf.Lbl(this, "扫描设备", x, y, _mf.Cw);
                y += 20;
                // 设备选择条：左箭头 + 设备名（浅底块）+ 右箭头，整体像一个选择器
                btnDevPrev = _mf.Btn(this, null, x, y, 30, 30, "chevronLeft", FlatButton.Kinds.Ghost, delegate { _mf.CycleDevice(-1); });
                btnDevNext = _mf.Btn(this, null, x + _mf.Cw - 30, y, 30, 30, "chevronRight", FlatButton.Kinds.Ghost, delegate { _mf.CycleDevice(+1); });
                lblScanDev = new Label();
                lblScanDev.Text = "正在查找设备…";
                lblScanDev.Font = Theme.F12B;
                lblScanDev.ForeColor = Theme.Text;
                lblScanDev.TextAlign = ContentAlignment.MiddleCenter;
                lblScanDev.AutoSize = false;
                lblScanDev.AutoEllipsis = true;
                lblScanDev.BackColor = Theme.FieldBg;
                lblScanDev.Bounds = new Rectangle(x + 34, y, _mf.Cw - 68, 30);
                Controls.Add(lblScanDev);
                y += 36;
                btnDevRefresh = _mf.Btn(this, "刷新设备列表", x, y, _mf.Cw, 30, "refresh", FlatButton.Kinds.Ghost, delegate { _mf.RefreshDevices(true); });
                y += 38;

                _mf.Lbl(this, "分辨率", x, y, _mf.Cw);
                y += 20;
                segScanDpi = new Segmented();
                segScanDpi.Items = new string[] { "150dpi", "200dpi", "300dpi" };
                segScanDpi.Bounds = new Rectangle(x, y, _mf.Cw, 32);
                Controls.Add(segScanDpi);
                segScanDpi.SelectedIndex = 2;      // 归档默认 300dpi，与裁剪页 A4 一致
                y += 40;

                _mf.Lbl(this, "色彩", x, y, _mf.Cw);
                y += 20;
                segScanColor = new Segmented();
                segScanColor.Items = new string[] { "彩色", "灰度", "黑白" };
                segScanColor.Bounds = new Rectangle(x, y, _mf.Cw, 32);
                Controls.Add(segScanColor);
                segScanColor.SelectedIndex = 0;
                y += 40;

                _mf.Lbl(this, "保存到", x, y, _mf.Cw);
                y += 20;
                segScanTarget = new Segmented();
                segScanTarget.Items = new string[] { "当前文件夹", "指定文件夹" };
                segScanTarget.Bounds = new Rectangle(x, y, _mf.Cw, 32);
                segScanTarget.SelectedChanged += delegate { _mf.UpdateScanDirLabel(); };
                Controls.Add(segScanTarget);
                segScanTarget.SelectedIndex = 0;
                y += 40;

                // ★ 输出格式（实测 C 思路）：默认跟随扫描仪，用户主动选才转。
                //   不少扫描仪驱动默认输出 BMP/TIF（一页几十 MB），选 JPG/PNG 则扫完自动转存。
                _mf.Lbl(this, "输出格式", x, y, _mf.Cw);
                y += 20;
                segScanFmt = new Segmented();
                segScanFmt.Items = new string[] { "跟随扫描仪", "JPG", "PNG", "TIF" };
                segScanFmt.Bounds = new Rectangle(x, y, _mf.Cw, 32);
                Controls.Add(segScanFmt);
                segScanFmt.SelectedIndex = 0;
                y += 40;

                lblScanDir = new Label();
                lblScanDir.AutoSize = false;
                lblScanDir.AutoEllipsis = true;
                lblScanDir.ForeColor = Theme.TextSub;
                lblScanDir.Font = Theme.F11;
                lblScanDir.Bounds = new Rectangle(x, y, _mf.Cw, 16);
                _mf.UpdateScanDirLabel();
                Controls.Add(lblScanDir);
                y += 20;
                btnPickScanDir = _mf.Btn(this, "选择文件夹", x, y, (_mf.Cw - 8) / 2, 30, null, FlatButton.Kinds.Ghost, delegate { _mf.PickScanDir(); });
                btnOpenScanDir = _mf.Btn(this, "打开", x + (_mf.Cw - 8) / 2 + 8, y, (_mf.Cw - 8) / 2, 30, "external", FlatButton.Kinds.Subtle, delegate { _mf.OpenScanDir(); });
                y += 38;

                btnScanRun = _mf.Btn(this, "开始扫描", x, y, _mf.Cw, 40, "page", FlatButton.Kinds.Primary, delegate { _mf.RunScan(); });
                btnScanCancel = _mf.Btn(this, "取消扫描", x, y, _mf.Cw, 40, null, FlatButton.Kinds.Danger, delegate { _mf.scanCancel = true; });
                btnScanCancel.Visible = false;
                y += 46;
                scanProgress = new FlatProgress();
                scanProgress.Bounds = new Rectangle(x, y, _mf.Cw, 6);
                scanProgress.Visible = false;   // 空闲隐藏：空轨道就是一条多余的横线（与归档页 progress 同一处理，）
                Controls.Add(scanProgress);
                y += 12;
                lblScanResult = new Label();
                lblScanResult.AutoSize = false;
                lblScanResult.ForeColor = Theme.TextSub;
                lblScanResult.Font = Theme.F11;
                lblScanResult.Bounds = new Rectangle(x, y, _mf.Cw, 44);
                Controls.Add(lblScanResult);
                y += 48;

                _mf.PageEnd(this);
            }
        }

        // ================= 扫描逻辑（窗体侧，引用主窗体状态密集故不进类）=================

        public void RefreshDevices(bool report)
        {
            if (busy) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                Scanner.ResetProbe();
                // 用户主动刷新时才去拉 32 位辅助进程（约 1~3 秒）；自动加载只查本进程，免得拖慢启动
                scanDevices = Scanner.ListDevices(report);
                scanDevIdx = 0;
            }
            finally { Cursor = Cursors.Default; }
            UpdateDeviceLabel();
            if (!report) return;
            if (scanDevices.Count == 0) { Status("没有找到扫描仪"); ExplainNoScanner(); }
            else Status(Scanner.Using32BitHelper
                ? "找到 " + scanDevices.Count + " 台扫描设备（32 位兼容方式）"
                : "找到 " + scanDevices.Count + " 台扫描设备");
        }

        // 「没找到扫描仪」不该只甩一句提示 —— 把查到的原因摆出来，并让用户一键跑详细诊断
        void ExplainNoScanner()
        {
            StringBuilder m = new StringBuilder("没有找到扫描仪。\n\n");
            string[] lines;
            try { lines = Scanner.BriefDiagnosis(); }
            catch (Exception ex) { lines = new string[] { "诊断出错：" + ex.Message }; }
            foreach (string s in lines) m.AppendLine(s);
            m.AppendLine();
            m.AppendLine("要不要现在做一次详细诊断？（会依次检查 WIA 组件、WIA 服务、系统成像设备、");
            m.AppendLine("TWAIN 数据源、以及 32 位视角，然后把结论给你）");
            if (MessageBox.Show(this, m.ToString(), "没有找到扫描仪",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Cursor = Cursors.WaitCursor;
            string rep;
            try { rep = Scanner.Diagnose(); }
            finally { Cursor = Cursors.Default; }
            string path = Path.Combine(Path.GetTempPath(), "PicTool-scan-diag.txt");
            try { File.WriteAllText(path, rep, System.Text.Encoding.UTF8); } catch { }
            Msg(rep + "\n\n已保存到：" + path);
        }

        void UpdateDeviceLabel()
        {
            if (lblScanDev == null) return;
            if (scanDevices == null || scanDevices.Count == 0)
            {
                lblScanDev.Text = "未找到设备";
                if (btnDevPrev != null) btnDevPrev.Enabled = false;
                if (btnDevNext != null) btnDevNext.Enabled = false;
                return;
            }
            scanDevIdx = Math.Max(0, Math.Min(scanDevIdx, scanDevices.Count - 1));
            lblScanDev.Text = scanDevices[scanDevIdx].Name
                + (scanDevices[scanDevIdx].Via32Bit ? "（32 位兼容）" : "");
            bool multi = scanDevices.Count > 1;
            btnDevPrev.Enabled = multi;
            btnDevNext.Enabled = multi;
        }

        public void CycleDevice(int delta)
        {
            if (busy) return;   // 扫描进行中不切设备（与 RefreshDevices/RunScan 同一守卫，）
            if (scanDevices == null || scanDevices.Count == 0) return;
            scanDevIdx = (scanDevIdx + delta + scanDevices.Count) % scanDevices.Count;
            UpdateDeviceLabel();
        }

        public void UpdateScanDirLabel()
        {
            if (lblScanDir == null) return;
            if (segScanTarget == null || segScanTarget.SelectedIndex == 0)
                lblScanDir.Text = currentFolder != null ? currentFolder : "（还没打开文件夹——请先点工具栏【打开文件夹】）";
            else
                lblScanDir.Text = scanDir != null ? scanDir : "（点下面【选择文件夹】指定保存位置）";
        }

        public void PickScanDir()
        {
            if (busy) return;   // 扫描进行中不弹文件夹选择框（）
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "选择扫描结果的保存文件夹";
                d.ShowNewFolderButton = true;
                if (scanDir != null && Directory.Exists(scanDir)) d.SelectedPath = scanDir;
                else if (currentFolder != null) d.SelectedPath = currentFolder;
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    scanDir = d.SelectedPath;
                    UpdateScanDirLabel();
                }
            }
        }

        public void OpenScanDir()
        {
            if (busy) return;   // 扫描进行中不开资源管理器（）
            string dir = segScanTarget != null && segScanTarget.SelectedIndex == 1 ? scanDir : currentFolder;
            if (dir == null || !Directory.Exists(dir)) { Status("这个文件夹还不存在"); return; }
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
        }

        public void RunScan()
        {
            if (busy) return;
            if (scanDevices == null || scanDevices.Count == 0) RefreshDevices(true);
            if (scanDevices == null || scanDevices.Count == 0) return;   // ExplainNoScanner 已经说明原因
            ScanDeviceInfo dev = scanDevices[Math.Max(0, Math.Min(scanDevIdx, scanDevices.Count - 1))];

            string folder = segScanTarget.SelectedIndex == 0 ? currentFolder : scanDir;
            if (folder == null || folder.Length == 0 || !Directory.Exists(folder))
            {
                if (segScanTarget.SelectedIndex == 0)
                {
                    Msg("还没打开文件夹。请先点工具栏【打开文件夹】，或把“保存到”切换为“指定文件夹”。");
                    return;
                }
                PickScanDir();
                folder = scanDir;
                if (folder == null || !Directory.Exists(folder)) return;
            }

            ScanOptions opt = new ScanOptions();
            opt.DeviceId = dev.Id;
            opt.Dpi = segScanDpi.SelectedIndex == 0 ? 150 : (segScanDpi.SelectedIndex == 1 ? 200 : 300);
            opt.ColorMode = segScanColor.SelectedIndex;
            opt.OutFolder = folder;
            opt.Prefix = "扫描";
            // ★ 输出格式（实测 C 思路）：默认跟随扫描仪不动，主动选才转存
            opt.ForceExt = segScanFmt.SelectedIndex == 1 ? ".jpg" : (segScanFmt.SelectedIndex == 2 ? ".png" : (segScanFmt.SelectedIndex == 3 ? ".tif" : null));

            btnScanRun.Visible = false;
            btnScanCancel.Visible = true;
            scanCancel = false;
            busy = true;
            progressNote = "正在连接扫描仪...";
            Status(progressNote);
            scanProgress.Value = 0;
            scanProgress.Visible = true;     // 跑起来才出现（空闲隐藏，见构建处）
            lblScanResult.Text = "";
            long sw = Environment.TickCount;

            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                try { System.Threading.Thread.CurrentThread.SetApartmentState(System.Threading.ApartmentState.STA); } catch { }
                List<string> files = null;
                Exception err = null;
                try
                {
                    files = Scanner.Scan(opt, delegate(int pages, string note)
                    {
                        progressNote = note;
                        try
                        {
                            BeginInvoke((MethodInvoker)delegate
                            {
                                Status(note);
                                scanProgress.Value = Math.Min(90, pages * 10);
                            });
                        } catch { }
                    }, delegate() { return scanCancel; });
                }
                catch (Exception ex) { err = ex; Diag.Log(ex, "扫描"); }

                // 窗体可能已经关闭（句柄销毁）：BeginInvoke 会抛异常，后台线程的
                // 未处理异常在 .NET 2.0 下直接终止进程，必须接住
                try
                {
                BeginInvoke((MethodInvoker)delegate
                {
                    busy = false;
                    btnScanRun.Visible = true;
                    btnScanCancel.Visible = false;
                    scanProgress.Visible = false;   // 收尾即隐藏（与归档页对称：进度只属于"正在跑"）
                    scanCancel = false;             // 收尾复位，与归档页 archiveCancel 对称（防跨任务污染）
                    double sec = (Environment.TickCount - sw) / 1000.0;
                    if (err != null) { scanProgress.Value = 0; Status("扫描失败"); Msg(Explain(err)); return; }
                    if (files != null && files.Count > 0)
                    {
                        scanProgress.Value = 100;
                        lblScanResult.Text = "已保存 " + files.Count + " 页到：\r\n" + folder +
                            (scanCancel ? "\r\n（已取消，剩余纸张未扫描）" : "");
                        Status("扫描完成：" + files.Count + " 页，耗时 " + sec.ToString("0.0") + " 秒");
                        if (currentFolder != null && string.Compare(currentFolder, folder, true) == 0)
                            BuildWorkspace(currentFolder, false);   // 扫进当前文件夹：刷新列表，立即可编辑
                        else if (segScanTarget.SelectedIndex == 1)
                            UpdateScanDirLabel();
                    }
                    else
                    {
                        scanProgress.Value = 0;
                        lblScanResult.Text = "没有扫描到页面。";
                        Status(scanCancel ? "已取消" : "没有扫描到页面");
                    }
                });
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
