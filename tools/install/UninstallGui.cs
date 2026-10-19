// UninstallGui.cs — 卸载程序（编译成 dist\uninst.exe，装机后叫「Uninstall.exe」）。
//
// 在链路里的位置：
//   控制面板「程序和功能」→ 卸载  →  HKLM\...\Uninstall\PicToolArchiveSuite 里的
//   UninstallString 指向 <安装目录>\Uninstall.exe  →  就是本程序。
//
// 为什么卸载要"自己复制自己到临时目录再删自己"：
//   exe 正在运行时，它自己那个文件是锁着的，删不掉 —— 而安装目录里第一个要删的就是它。
//   通行做法（也是 Inno/NSIS 的做法）是：
//     ① 原进程（安装目录里那个）弹确认框
//     ② 把自己拷到 %TEMP%\PicToolUninstall\<随机>\，从这里启动子进程
//     ③ 原进程退出，锁随之释放
//     ④ 子进程等原进程真的没了，再连目录带文件一起删；最后用一条**隐藏**的 cmd 收掉
//        自己的临时目录（CreateNoWindow=true，不会闪黑框 —— 这正是旧版那条黑框的教训）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using PicTool;

namespace PicToolSetup
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string purge = null;      // 非空 = 我在临时目录里，负责真正执行删除
            int pid = 0;              // 原进程 pid（要等它退出，文件才解锁）
            bool silent = false;
            string shot = null;       // 诊断：把确认页渲染成 PNG 后退出

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--purge" && i + 1 < args.Length) { purge = args[i + 1].TrimEnd('\\'); i++; }
                else if (args[i] == "--pid" && i + 1 < args.Length) { int.TryParse(args[i + 1], out pid); i++; }
                else if (args[i] == "--silent" || args[i] == "/S") silent = true;
                else if (args[i].StartsWith("--shot=")) shot = args[i].Substring(7);
            }

            // 诊断：把确认页画出来看一眼，不真卸载
            if (shot != null)
            {
                using (UninstForm f2 = new UninstForm(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')))
                {
                    f2.StartPosition = FormStartPosition.Manual;
                    f2.Location = new Point(0, 0);
                    f2.Show();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(250);
                    Application.DoEvents();
                    Bitmap b = new Bitmap(f2.ClientSize.Width, f2.ClientSize.Height);
                    try
                    {
                        f2.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                        b.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    catch { }
                    finally { b.Dispose(); }
                    f2.Close();
                }
                return;
            }

            if (purge != null) { Uninst.RunPurge(purge, pid, silent); return; }
            Uninst.RunConfirm(silent);
        }
    }

    static class Uninst
    {
        /// <summary>确认页：列出会删掉什么，确认后复制自己到临时目录并接管。</summary>
        public static void RunConfirm(bool silent)
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            bool known = IsOurInstall(dir);

            if (!silent)
            {
                if (!known)
                {
                    DialogResult r = MessageBox.Show(
                        "这个位置看不到本软件的安装标记文件：\r\n" + dir + "\r\n\r\n" +
                        "继续卸载会删除该目录下的程序文件。确定要继续吗？",
                        SetupInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r != DialogResult.Yes) return;
                }
                else
                {
                    UninstForm f = new UninstForm(dir);
                    if (f.ShowDialog() != DialogResult.OK) return;
                }

                if (IsMainAppRunning())
                {
                    DialogResult r = MessageBox.Show(
                        "「" + SetupInfo.Name + "」正在运行，需要先关闭它才能卸载。\r\n\r\n" +
                        "点「是」自动关闭并继续卸载。",
                        SetupInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (r != DialogResult.Yes) return;
                    KillMainApp();
                }
            }

            // 复制自己到临时目录，交给"离开安装目录的那个副本"去做真正的删除
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "PicToolUninstall",
                                          Guid.NewGuid().ToString("N").Substring(0, 12));
                Directory.CreateDirectory(tmp);
                string copy = Path.Combine(tmp, SetupInfo.UninstallerName);
                File.Copy(Application.ExecutablePath, copy, true);

                ProcessStartInfo psi = new ProcessStartInfo(copy);
                psi.UseShellExecute = true;
                psi.Arguments = "--purge \"" + dir + "\" --pid " +
                                Process.GetCurrentProcess().Id + (silent ? " --silent" : "");
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("卸载程序启动失败：" + ex.Message, SetupInfo.Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            // 本进程退出 → 安装目录里的 Uninstall.exe 解锁，子进程可以删掉整个目录
        }

        /// <summary>真正干活的那个副本：删快捷方式、注册表入口、安装目录。</summary>
        public static void RunPurge(string dir, int pid, bool silent)
        {
            // ① 等原进程退出
            if (pid > 0)
            {
                try { Process.GetProcessById(pid).WaitForExit(20000); }
                catch { }
            }

            // ② 等安装目录里的 Uninstall.exe 解锁（杀软/索引器有时会多占一会儿）
            string selfInDir = Path.Combine(dir, SetupInfo.UninstallerName);
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    if (!File.Exists(selfInDir)) break;
                    File.Delete(selfInDir);
                    break;
                }
                catch { Thread.Sleep(250); }
            }

            List<string> problems = new List<string>();
            if (silent)
            {
                Purge(dir, problems);
                // 静默模式没有 PurgeForm，收尾得自己来 —— 交互模式下是
                // PurgeForm.OnFormClosed 调它。漏了这一步，%TEMP%\PicToolUninstall
                // 会一直留着那份 400KB 的副本（控制面板的 QuietUninstallString、
                // 以及脚本化卸载走的都是这条路，2026-09-21 实测发现）。
                SelfCleanupHidden();
                return;
            }

            PurgeForm f = new PurgeForm(dir, problems);
            Application.Run(f);
        }

        /// <summary>卸载的实际动作（可被 UI 或静默模式调用）。</summary>
        public static void Purge(string dir, List<string> problems)
        {
            KillMainApp();

            // 快捷方式（所有用户 + 当前用户都清一遍，兼容旧版本装到当前用户的情况）
            DeleteShortcuts(problems);

            // 注册表卸载入口
            DeleteRegistry(problems);

            // 安装目录
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Directory.Delete(dir, true);
                    if (!Directory.Exists(dir)) break;
                }
                catch (Exception ex)
                {
                    if (i == 2) problems.Add("安装目录没能完全删除：" + ex.Message);
                    Thread.Sleep(400);
                }
            }
        }

        // ---------------- 细节 ----------------

        public static bool IsOurInstall(string dir)
        {
            try
            {
                return File.Exists(Path.Combine(dir, SetupInfo.MarkerFile)) &&
                       File.Exists(Path.Combine(dir, SetupInfo.MainExe));
            }
            catch { return false; }
        }

        public static bool IsMainAppRunning()
        {
            try
            {
                string want = SetupInfo.MainExe;
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        string f = p.MainModule == null ? null : p.MainModule.FileName;
                        if (f != null && string.Equals(Path.GetFileName(f), want, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
            return false;
        }

        public static void KillMainApp()
        {
            try
            {
                string want = SetupInfo.MainExe;
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        string f = p.MainModule == null ? null : p.MainModule.FileName;
                        if (f != null && string.Equals(Path.GetFileName(f), want, StringComparison.OrdinalIgnoreCase))
                        {
                            p.CloseMainWindow();
                            if (!p.WaitForExit(3000)) p.Kill();
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }

        static void DeleteShortcuts(List<string> problems)
        {
            List<string> lnks = new List<string>();
            lnks.Add(Combine(Environment.SpecialFolder.CommonDesktopDirectory, SetupInfo.DesktopShortcut));
            lnks.Add(Combine(Environment.SpecialFolder.DesktopDirectory, SetupInfo.DesktopShortcut));
            foreach (string p in lnks) TryDeleteFile(p, problems);

            List<string> dirs = new List<string>();
            dirs.Add(Combine(Environment.SpecialFolder.CommonStartMenu, "Programs\\" + SetupInfo.StartMenuFolder));
            dirs.Add(Combine(Environment.SpecialFolder.StartMenu, "Programs\\" + SetupInfo.StartMenuFolder));
            foreach (string p in dirs) TryDeleteDir(p, problems);
        }

        static void DeleteRegistry(List<string> problems)
        {
            DeleteKey(RegistryView.Registry64, problems);
            DeleteKey(RegistryView.Registry32, problems);
        }

        static void DeleteKey(RegistryView view, List<string> problems)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                {
                    using (RegistryKey k = baseKey.OpenSubKey(SetupInfo.UninstallKeyPath, false))
                    {
                        if (k == null) return;      // 本来就没有，不用清
                    }
                    baseKey.DeleteSubKeyTree(SetupInfo.UninstallKeyPath, false);
                }
            }
            catch (Exception ex)
            {
                problems.Add("注册表卸载项未能清除（" + view + "）：" + ex.Message);
            }
        }

        static string Combine(Environment.SpecialFolder folder, string rel)
        {
            try { return Path.Combine(Environment.GetFolderPath(folder), rel); }
            catch { return null; }
        }

        static void TryDeleteFile(string path, List<string> problems)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { problems.Add("没能删除 " + path + "：" + ex.Message); }
        }

        static void TryDeleteDir(string path, List<string> problems)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch (Exception ex) { problems.Add("没能删除 " + path + "：" + ex.Message); }
        }

        /// <summary>用一条隐藏的 cmd 收掉临时目录并自删（CreateNoWindow ⇒ 不会闪黑框）。</summary>
        public static void SelfCleanupHidden()
        {
            try
            {
                string me = Application.ExecutablePath;
                string tmp = Path.GetDirectoryName(me);
                string bat = Path.Combine(Path.GetTempPath(),
                    "pictool-uninst-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".cmd");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("ping -n 3 127.0.0.1 >nul");
                sb.AppendLine("del /f /q \"" + me + "\" >nul 2>nul");
                sb.AppendLine("rd /s /q \"" + tmp + "\" >nul 2>nul");
                // 再收掉 %TEMP%\PicToolUninstall 这个空壳。rd 不带 /s 只删空目录，
                // 所以别的实例还在用（或用户手动放了东西）时它会安静失败，不会误删。
                string parent = Path.GetDirectoryName(tmp);
                if (!string.IsNullOrEmpty(parent)) sb.AppendLine("rd \"" + parent + "\" >nul 2>nul");
                sb.AppendLine("del /f /q \"%~f0\" >nul 2>nul");
                File.WriteAllText(bat, sb.ToString(), Encoding.Default);

                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;          // 关键：不创建控制台窗口
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch { }
        }
    }

    // ================= 确认页 =================
    class UninstForm : Form
    {
        const int W = 560;
        const int H = 320;

        public UninstForm(string dir)
        {
            Text = SetupInfo.Name + " 卸载程序";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(W, H);
            BackColor = Color.White;
            Font = Theme.F12;

            Label t = new Label();
            t.AutoSize = false;
            t.Location = new Point(24, 22);
            t.Size = new Size(W - 48, 26);
            t.Font = Theme.F15B;
            t.ForeColor = Theme.Text;
            t.Text = "卸载 " + SetupInfo.Name;
            Controls.Add(t);

            Label sub = new Label();
            sub.AutoSize = false;
            sub.Location = new Point(24, 56);
            sub.Size = new Size(W - 48, 20);
            sub.Font = Theme.F11;
            sub.ForeColor = Theme.TextMuted;
            sub.Text = "将从本机移除以下内容：";
            Controls.Add(sub);

            Label body = new Label();
            body.AutoSize = false;
            body.Location = new Point(24, 84);
            // 2026-09-28：底部重排。原来 warn（红字提示）y=H-68 与按钮行 y=H-58
            // **垂直重叠 10px**——红字和「卸载」按钮挤在同一水平带里（用户截图圈出）。
            // 现在三层各占一行互不侵犯：正文 → 红字提示 → 按钮，间隔 14px。
            body.Size = new Size(W - 48, H - 84 - 100);
            body.Font = Theme.F12;
            body.ForeColor = Theme.TextSub;
            body.Text = "· 程序文件：" + dir + "\r\n" +
                        "· 桌面与开始菜单快捷方式\r\n" +
                        "· 控制面板「程序和功能」里的本软件条目\r\n\r\n" +
                        "您用本软件处理过的图片文件不会被删除。";
            Controls.Add(body);

            Label warn = new Label();
            warn.AutoSize = false;
            warn.Location = new Point(24, H - 92);
            warn.Size = new Size(W - 48, 20);
            warn.Font = Theme.F11;
            warn.ForeColor = Theme.Danger;
            warn.Text = "卸载后如仍需使用，需要重新获取安装包安装。";
            Controls.Add(warn);

            FlatButton cancel = new FlatButton();
            cancel.Text = "取消";
            cancel.Size = new Size(96, 34);
            cancel.Location = new Point(W - 24 - 132 - 8 - 96, H - 24 - 34);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            FlatButton go = new FlatButton();
            go.Primary = true;
            go.Text = "卸载";
            go.Size = new Size(132, 34);
            go.Location = new Point(W - 24 - 132, H - 24 - 34);
            go.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(go);

            AcceptButton = go;
            CancelButton = cancel;
        }
    }

    // ================= 执行页（在临时目录里跑的副本） =================
    class PurgeForm : Form
    {
        const int W = 480;
        const int H = 172;

        readonly string dir;
        readonly List<string> problems;
        Label lblStatus, lblDetail;
        FlatProgress bar;
        bool finished;

        public PurgeForm(string installDir, List<string> problems)
        {
            dir = installDir;
            this.problems = problems;

            Text = SetupInfo.Name + " 卸载程序";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(W, H);
            BackColor = Color.White;
            Font = Theme.F12;

            Label t = new Label();
            t.AutoSize = false;
            t.Location = new Point(24, 22);
            t.Size = new Size(W - 48, 26);
            t.Font = Theme.F15B;
            t.ForeColor = Theme.Text;
            t.Text = "正在卸载";
            Controls.Add(t);

            lblStatus = new Label();
            lblStatus.AutoSize = false;
            lblStatus.Location = new Point(24, 62);
            lblStatus.Size = new Size(W - 48, 20);
            lblStatus.Font = Theme.F11;
            lblStatus.ForeColor = Theme.TextMuted;
            lblStatus.Text = "正在移除程序文件…";
            Controls.Add(lblStatus);

            bar = new FlatProgress();
            bar.Marquee = true;
            bar.Location = new Point(24, 92);
            bar.Width = W - 48;
            Controls.Add(bar);

            lblDetail = new Label();
            lblDetail.AutoSize = false;
            lblDetail.Location = new Point(24, 112);
            lblDetail.Size = new Size(W - 48, 40);
            lblDetail.Font = Theme.F11;
            lblDetail.ForeColor = Theme.TextMuted;
            lblDetail.Text = dir;
            Controls.Add(lblDetail);

            System.Windows.Forms.Timer marquee = new System.Windows.Forms.Timer();
            marquee.Interval = 40;
            marquee.Tick += delegate { if (bar.Marquee) bar.Advance(); };
            marquee.Start();

            Thread th = new Thread(delegate ()
            {
                try { Uninst.Purge(dir, problems); }
                catch (Exception ex) { problems.Add(ex.Message); }
                try { BeginInvoke(new MethodInvoker(Done)); } catch { }
            });
            th.IsBackground = true;
            th.Start();
        }

        void Done()
        {
            if (finished) return;
            finished = true;
            bar.Marquee = false;
            bar.Value = 100;

            if (problems.Count == 0)
            {
                lblStatus.ForeColor = Theme.Green;
                lblStatus.Text = "卸载完成";
                lblDetail.Text = "程序文件与快捷方式已移除，感谢使用。";
            }
            else
            {
                lblStatus.ForeColor = Theme.Danger;
                lblStatus.Text = "卸载完成，但有 " + problems.Count + " 项没能删除";
                string s = "";
                for (int i = 0; i < problems.Count && i < 2; i++) s += problems[i] + "\r\n";
                lblDetail.Text = s + "（这些通常是程序正在运行或被安全软件占用，重启后可手动删除）";
            }

            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 1400;
            t.Tick += delegate
            {
                t.Stop(); t.Dispose();
                Close();
            };
            t.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Uninst.SelfCleanupHidden();
        }
    }
}
