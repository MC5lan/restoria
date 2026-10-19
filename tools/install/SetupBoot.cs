// SetupBoot.cs — 单文件安装包的引导层（安装程序侧的"第一屏"）。
//
// 背景（为什么要这一层）：
//   旧版发行链路是 WinRAR 自解压（SFX）解压到临时目录 → 运行 setup.cmd（命令行）
//   → 命令行再拉起图形安装界面。用户双击后会**先看到一个黑底命令行窗口**，
//   等图形界面起来才把它藏掉 —— 观感不像正规软件，且 WinRAR 包的图标/版本信息都是
//   WinRAR 自己的，右键属性一片空白。
//
//   现在：安装文件（约 200MB）直接以自定义容器追加在本 exe 末尾（见 Payload.cs），
//   双击后由本文件负责：
//     1) 弹一个白底进度窗口，把载荷解压到 %TEMP%\PicToolSetup\<随机>\
//     2) 在同一进程内进入安装向导（SetupForm）——全程没有任何命令行窗口
//     3) 向导结束后（含提权重装那种"父等子"的情况）删掉临时目录
//   全程只依赖 .NET Framework 4.x 自带能力，不引入任何第三方工具。
//
// 开发态：如果 exe 末尾没有载荷（直接跑 dist\setup-boot.exe），就当普通安装器用，
//         以 exe 所在目录为安装源 —— 这样 --shot 之类的诊断照旧能用。
using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using PicTool;

namespace PicToolSetup
{
    static class Boot
    {
        public const string TempRootName = "PicToolSetup";

        public static bool HasPayload(string exePath)
        {
            long off, len;
            return Payload.TryReadFooter(exePath, out off, out len);
        }

        /// <summary>随机临时目录（避免多用户/多实例互相踩）。</summary>
        public static string NewExtractDir()
        {
            string root = Path.Combine(Path.GetTempPath(), TempRootName);
            string dir = Path.Combine(root, Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>解压载荷，带白底进度窗口。失败抛异常（由调用方提示）。</summary>
        public static void ExtractWithUi(string exePath, string destDir)
        {
            BootProgressForm ui = new BootProgressForm();
            Exception error = null;
            Thread t = new Thread(delegate ()
            {
                try
                {
                    Payload.Extract(exePath, destDir, delegate (int idx, int total, string name, long done, long totalBytes)
                    {
                        ui.SetProgress(idx, total, name, done, totalBytes);
                    });
                }
                catch (Exception ex) { error = ex; }
                finally
                {
                    try { ui.BeginInvoke(new MethodInvoker(delegate { ui.AllowClose(); })); }
                    catch { }
                }
            });
            t.IsBackground = true;
            t.Start();
            Application.Run(ui);
            if (error != null) throw error;
        }

        /// <summary>收尾删除临时目录（尽力而为：文件被占用时重试几次）。</summary>
        public static void Cleanup(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (!Directory.Exists(dir)) break;
                    Directory.Delete(dir, true);
                    break;
                }
                catch { Thread.Sleep(300); }
            }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }

            // 顺手收掉 %TEMP%\PicToolSetup 这个空壳：只删"空的"，别的实例还在用时不碰。
            try
            {
                string parent = Path.GetDirectoryName(dir.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)
                    && Directory.GetFileSystemEntries(parent).Length == 0)
                {
                    Directory.Delete(parent, false);
                }
            }
            catch { }
        }

        /// <summary>把载荷清单写到文件（打包脚本用它校验"包内到底装了什么"）。</summary>
        public static void WriteListing(string exePath, string outFile)
        {
            StringBuilder sb = new StringBuilder();
            long off, len;
            if (!Payload.TryReadFooter(exePath, out off, out len))
            {
                sb.AppendLine("NO-PAYLOAD");
            }
            else
            {
                System.Collections.Generic.List<PayloadEntry> es = Payload.List(exePath);
                long total = 0;
                foreach (PayloadEntry e in es) total += e.RawLength;
                sb.AppendLine("payload-offset=" + off);
                sb.AppendLine("payload-length=" + len);
                sb.AppendLine("entries=" + es.Count);
                sb.AppendLine("total-raw=" + total);
                foreach (PayloadEntry e in es)
                    sb.AppendLine("  " + e.RawLength + "\t" + (e.Method == 1 ? "deflate" : "store") + "\t" + e.Name);
            }
            File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
        }
    }

    /// <summary>解压时的白底进度窗口（与安装向导同一套视觉）。</summary>
    class BootProgressForm : Form
    {
        const int W = 480;
        Label lblTitle, lblText, lblDetail;
        FlatProgress bar;
        bool allowClose;

        public BootProgressForm()
        {
            Text = "Restoria 安装程序";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(W, 152);
            BackColor = Color.White;
            Font = Theme.F12;

            try
            {
                PictureBox pic = new PictureBox();
                pic.Location = new Point(24, 20);
                pic.Size = new Size(28, 28);
                pic.SizeMode = PictureBoxSizeMode.StretchImage;
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) pic.Image = ico.ToBitmap();
                Controls.Add(pic);
            }
            catch { }

            lblTitle = new Label();
            lblTitle.AutoSize = false;
            lblTitle.Location = new Point(64, 22);
            lblTitle.Size = new Size(W - 88, 24);
            lblTitle.Font = Theme.F15B;
            lblTitle.ForeColor = Theme.Text;
            lblTitle.BackColor = Color.Transparent;
            lblTitle.Text = "正在准备安装文件";
            Controls.Add(lblTitle);

            lblText = new Label();
            lblText.AutoSize = false;
            lblText.Location = new Point(24, 62);
            lblText.Size = new Size(W - 48, 20);
            lblText.Font = Theme.F11;
            lblText.ForeColor = Theme.TextMuted;
            lblText.BackColor = Color.Transparent;
            lblText.Text = "正在解压安装文件，请稍候…";
            Controls.Add(lblText);

            bar = new FlatProgress();
            bar.Location = new Point(24, 90);
            bar.Width = W - 48;
            Controls.Add(bar);

            lblDetail = new Label();
            lblDetail.AutoSize = false;
            lblDetail.Location = new Point(24, 112);
            lblDetail.Size = new Size(W - 48, 20);
            lblDetail.Font = Theme.F11;
            lblDetail.ForeColor = Theme.TextMuted;
            lblDetail.BackColor = Color.Transparent;
            lblDetail.Text = "";
            Controls.Add(lblDetail);
        }

        public void AllowClose() { allowClose = true; Close(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowClose) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }

        public void SetProgress(int idx, int total, string name, long done, long totalBytes)
        {
            if (!IsHandleCreated) return;
            try
            {
                BeginInvoke(new MethodInvoker(delegate ()
                {
                    if (totalBytes > 0)
                        bar.Value = (int)Math.Min(100, done * 100 / totalBytes);
                    if (!string.IsNullOrEmpty(name) && idx <= total)
                        lblText.Text = "正在解压（" + idx + "/" + total + "）" + Short(name);
                    if (totalBytes > 0)
                        lblDetail.Text = Mb(done) + " / " + Mb(totalBytes);
                }));
            }
            catch { }
        }

        static string Short(string path)
        {
            if (path == null) return "";
            string s = path.Replace('\\', '/');
            if (s.Length <= 42) return s;
            return "…" + s.Substring(s.Length - 41);
        }

        static string Mb(long b) { return ((double)b / 1048576.0).ToString("0.0") + " MB"; }
    }
}
