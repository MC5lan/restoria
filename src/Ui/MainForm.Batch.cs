// MainForm.Batch.cs — 主窗体（partial）：批量处理
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm : Form
    {
        // ================= 批量 =================

        public void PickOutDir()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "选择批量处理结果的输出文件夹";
                d.ShowNewFolderButton = true;
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    outDir = d.SelectedPath;
                    lblOutDir.Text = outDir;
                }
            }
        }

        public void OpenOutDir()
        {
            if (outDir == null || !Directory.Exists(outDir)) { Status("输出文件夹还不存在"); return; }
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + outDir + "\""); } catch { }
        }


        // ================= 一键归档 =================

        // 整树归档的输出子文件夹名：成品放在"图片所在的那个文件夹"里新建的"已归档"里，
        // 原图一律不动（递归时每个子文件夹各有一个）
        const string ArchivedDirName = "已归档";

        // 一个待归档任务 = 一张图 + 它自己的输出目录
        // （单张/单层归档时所有任务同目录；整树归档时各归各的"已归档"）
        class ArchiveJob { public string File; public string OutFolder; }

        // 老扫描件 → 标准 A4 白底黑字（九步流水线 + 印章保红，见 docs/一键归档流水线方案.md）。
        // 升级为**整树归档**：递归收集当前文件夹及所有子文件夹里的图片，
        // 成品放到各图片所在文件夹的"已归档"里。入口三道关：
        //   ① 路径保险丝（FileTools.IsArchiveAllowed）—— 桌面/盘根/系统目录直接拒绝；
        //   ② 确认框 —— 报出"多少个文件夹、多少张图"，点了"开始归档"才动手；
        //   ③ 全程只新增文件，不删不改原图。
        // 线程模型与 RunBatch 相同；BeginInvoke 处处要接异常 —— 窗体关闭后回调会抛，
        // 后台线程的未处理异常会直接终止进程（RunBatch 里同一条教训）。
        public void RunArchive()
        {
            RunArchive(currentFolder);
        }

        public void RunArchive(string folder)
        {
            if (busy) return;
            if (folder == null || !Directory.Exists(folder)) { Status("文件夹不存在"); return; }

            // ① 保险丝：危险路径直接拒绝（附小白能看懂的原因）
            string reason;
            if (!FileTools.IsArchiveAllowed(folder, out reason)) { Msg(reason); return; }

            // ② 递归收集任务
            List<ArchiveJob> jobs = new List<ArchiveJob>();
            int imgFolders = 0, existCount = 0;
            bool capped = false;
            string err = CollectArchiveJobs(folder, true, MAX_FILES, jobs, ref imgFolders, ref existCount, ref capped);
            if (err != null) { Msg(err); return; }
            if (jobs.Count == 0 && existCount == 0) { Status("这个文件夹（含子文件夹）里没有图片"); return; }
            if (jobs.Count == 0)
            {
                Status("都归档过了：" + existCount + " 张成品已在各自的\"已归档\"里。想重新归档就先删掉那些\"已归档\"文件夹。");
                return;
            }

            // ③ 确认框：报清楚规模和规则，点了才动手
            string ask = "在这个文件夹树里归档？\n\n" + folder +
                "\n\n· 涉及 " + imgFolders + " 个有图片的文件夹，共 " + jobs.Count + " 张图" +
                (existCount > 0 ? "\n· 已归档过 " + existCount + " 张，自动跳过" : "") +
                (capped ? "\n· 已达单次上限 " + MAX_FILES + " 张，本次先处理这一批" : "") +
                "\n· 原图不动，成品放到各图片所在文件夹里新建的\"" + ArchivedDirName + "\"里" +
                "\n· 中途取消可以再跑一遍，只会补还没归档的";
            using (ConfirmBox dlg = new ConfirmBox("归档整个文件夹（含子文件夹）", ask, "开始归档"))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
            }

            // 切到树根再开工：完成后刷新正好落在这里（成品在各子文件夹的"已归档"里）
            if (!string.Equals(currentFolder, folder, StringComparison.OrdinalIgnoreCase))
                OpenFolderPath(folder);
            ArchiveJobs(jobs, "各图片所在文件夹的\"" + ArchivedDirName + "\"", folder);
        }

        // 递归收集整树归档任务：
        //   · 输出目录 = 每张图所在文件夹\已归档（原图不动）
        //   · 跳过输出目录本身（「已归档」/旧版「_已处理」）—— 否则成品会被二次归档
        //   · 目标已存在 = 上次已归档过 → 不再收，计入 existCount
        //     （中途取消再跑一遍只会补缺的，已归档的不重复处理 —— 用户选定方案）
        //   · 隐藏/系统/重解析点目录跳过（回收站、System Volume Information、链接环）
        // 返回错误信息（根目录读不了才报错，子目录读不了就跳过），null = 正常。
        string CollectArchiveJobs(string folder, bool isRoot, int maxTotal,
            List<ArchiveJob> jobs, ref int imgFolders, ref int existCount, ref bool capped)
        {
            if (jobs.Count >= maxTotal) { capped = true; return null; }

            string[] files;
            try { files = BatchProcessor.ListImages(folder, MAX_FILES); }
            catch (Exception ex)
            {
                if (isRoot) return ex.Message;      // 根目录读不了：报错
                return null;                         // 个别子目录进不去：跳过，不整体失败
            }

            string outFolder = Path.Combine(folder, ArchivedDirName);
            int here = 0;
            foreach (string f in files)
            {
                here++;
                if (jobs.Count >= maxTotal) { capped = true; break; }
                if (File.Exists(Path.Combine(outFolder, Path.GetFileName(f)))) { existCount++; continue; }
                ArchiveJob j = new ArchiveJob();
                j.File = f;
                j.OutFolder = outFolder;
                jobs.Add(j);
            }
            if (here > 0) imgFolders++;

            string[] subs;
            try { subs = Directory.GetDirectories(folder); }
            catch { return null; }
            Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
            foreach (string s in subs)
            {
                string name = Path.GetFileName(s);
                // ★ ：输出文件夹统一叫「已归档」（以前保存/批量另存叫「_已处理」，
                //   同一份数据冒出两个文件夹，用户明确要求合并成一个）。旧名同样跳过，
                //   免得老目录里的成品被当成"还没归档"再处理一遍。
                // （逐项核查时删掉这里一条与下行完全重复的「已归档」判定 —— 死行，
                //   删掉不改行为；归档路径仍以 tools\verify_guard.py / 并行回归逐字节背书。）
                if (name.Equals(ArchivedDirName, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("_已处理", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    FileAttributes attr = File.GetAttributes(s);
                    if ((attr & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                        continue;
                }
                catch { continue; }
                string err = CollectArchiveJobs(s, false, maxTotal, jobs, ref imgFolders, ref existCount, ref capped);
                if (err != null) return err;
            }
            return null;
        }

    }
}
