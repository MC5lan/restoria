// MainForm.History.cs — 主窗体（partial）：撤销还原 / 保存
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm : Form
    {
        // ================= 撤销 / 还原 =================

        void DoUndo()
        {
            Doc d = Cur;
            if (!GuardAction(d)) return;   // 统一守卫
            // 撤光了的提示（修：原来静默 return，配合 UNDO_MAX=2 的旧设计
            // 加重了"只能撤销两次"的困惑——现在明确告诉用户撤到头了）
            if (d.Undo.Count == 0) { Status("没有更早的修改可撤销了"); return; }
            EditRecord r = d.Undo[d.Undo.Count - 1];
            d.Undo.RemoveAt(d.Undo.Count - 1);
            d.Edits = true;
            bool any = false;
            if (r.Img != null)
            {
                Bitmap abandoned = d.Current;
                d.Current = r.Img;
                r.Img = null;
                canvas.SetImage(d.Current, d.Overlay, false);   // 先切换，再释放旧位图
                if (abandoned != null && abandoned != d.Current) abandoned.Dispose();
                ResetAdjustSessions(true);   // 撤销/重做后图像状态变了：滑条归零并作废基准
                                             // （原来连调两次，是复制粘贴残留 —— 函数幂等，删一次，）
                histDoc = null;              // 直方图随图像回退重算
                InvalidatePreview();
                RegenerateThumb(d);
                any = true;
            }
            if (r.Texts != null)
            {
                d.Texts = r.Texts;           // 快照本身就是独立拷贝，直接交还
                r.Texts = null;
                SyncCanvasTexts();
                any = true;
            }
            r.Dispose();
            if (!any) { Status("没有可撤销的操作"); return; }
            textSnapshot = CloneTexts(d.Texts);   // 提交点快照对齐到已还原状态
            RefreshHeader();
            RefreshTextInspector();
            ws.Invalidate();
            Status("已撤销");
        }

        void DoRevert()
        {
            Doc d = Cur;
            if (!GuardAction(d)) return;   // 统一守卫
            if (!d.Edits && d.Undo.Count == 0) { Status("当前图片没有修改"); return; }
            if (MessageBox.Show(this, "放弃对这张图片的全部修改，恢复到刚打开时的状态？",
                "还原", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            Bitmap abandoned = d.Current;
            Bitmap fresh = Imaging.Ensure24bpp(new Bitmap(d.Original));
            canvas.SetImage(fresh, null, true);             // 先脱离旧位图
            d.Current = fresh;
            for (int i = 0; i < d.Undo.Count; i++) d.Undo[i].Dispose();
            d.Undo.Clear();
            d.ClearMask();
            d.Texts.Clear();             // 文字元素一并还原（未烙印的都不算数了）
            SyncCanvasTexts();
            textSnapshot = new List<TextItem>();
            d.Edits = false;
            if (abandoned != null && abandoned != d.Current) abandoned.Dispose();
            ResetAdjustSessions(true);   // 还原到初始状态：滑条归零并作废基准
            histDoc = null;              // 直方图随图像还原重算
            InvalidatePreview();
            RegenerateThumb(d);
            RefreshHeader();
            ws.Invalidate();
            Status("已还原");
        }

        // ================= 保存 =================

        public void OnSaveModeChanged(bool overwrite, Segmented source)
        {
            if (syncingSaveMode) return;
            overwriteMode = overwrite;
            syncingSaveMode = true;
            if (source != segSave) segSave.SelectedIndex = overwrite ? 1 : 0;
            // （批量处理页删除后，segBatchOut 的联动随之移除）
            syncingSaveMode = false;
            RefreshHeader();
            Status(overwrite ? "保存方式：直接覆盖原图（请谨慎操作）" : "保存方式：另存到 " + ArchivedDirName + " 文件夹");
        }

        // 覆盖前确认；返回 false 表示取消
        bool ConfirmOverwrite(string what)
        {
            if (overwriteWarned) return true;
            using (OverwriteConfirm dlg = new OverwriteConfirm(what))
            {
                DialogResult r = dlg.ShowDialog(this);
                if (r != DialogResult.OK) return false;
                if (dlg.DontAskAgain) overwriteWarned = true;
                return true;
            }
        }

        void SaveCurrent()
        {
            Doc d = Cur;
            if (d == null || busy) { Status("请先打开图片"); return; }
            FlattenIfNeeded(d);   // 保存兜底：忘了点烙印也不会把没字的图存出去
            string dst;
            if (overwriteMode)
            {
                if (d.FilePath == null || !Imaging.CanOverwrite(d.FilePath))
                {
                    Msg("这个格式不支持直接覆盖，请用【另存为】。");
                    return;
                }
                if (!ConfirmOverwrite("即将覆盖原文件：\n" + d.FilePath + "\n\n原图将被替换，此操作不可撤销。")) return;
                dst = d.FilePath;
            }
            else
            {
                try { if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir); }
                catch (Exception ex) { Msg("无法创建输出文件夹：" + ex.Message); return; }
                dst = Path.Combine(outDir, d.Name);
            }

            try
            {
                long sw = Environment.TickCount;
                Imaging.SaveImage(d.Current, dst, 92);
                d.Edits = false;
                Recovery.Clear(d.FilePath);      // 已正式保存：恢复记录就不需要了
                ws.Invalidate();
                RefreshHeader();
                double sec = (Environment.TickCount - sw) / 1000.0;
                Status((overwriteMode ? "已覆盖原图：" : "已保存到“" + ArchivedDirName + "”：") + d.Name +
                       "（" + sec.ToString("0.0") + " 秒）");
                Toast.Show(this, overwriteMode ? "已覆盖原图" : "已保存", Toast.Kinds.Ok,
                    d.Name + "（" + sec.ToString("0.0") + " 秒）");
            }
            catch (Exception ex)
            {
                Msg("保存失败：" + ex.Message);
                Toast.Show(this, "保存失败", Toast.Kinds.Error, ex.Message);
            }
        }

        void SaveCurrentAs()
        {
            Doc d = Cur;
            if (d == null || busy) { Status("请先打开图片"); return; }
            FlattenIfNeeded(d);   // 保存兜底：同 SaveCurrent
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.FileName = d.Name;
                sfd.InitialDirectory = Directory.Exists(outDir) ? outDir :
                    (d.FilePath != null ? Path.GetDirectoryName(d.FilePath) : "");
                sfd.Filter = "JPEG 图片|*.jpg|PNG 图片|*.png|BMP 图片|*.bmp";
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Imaging.SaveImage(d.Current, sfd.FileName, 92);
                    d.Edits = false;
                    Recovery.Clear(sfd.FileName);   // 已正式保存：恢复记录就不需要了
                    ws.Invalidate();
                    RefreshHeader();
                    Status("已保存：" + Path.GetFileName(sfd.FileName));
                }
                catch (Exception ex) { Msg("保存失败：" + ex.Message); }
            }
        }

    }
}
