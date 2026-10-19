// IPageHost.cs — 页面 ↔ 主窗体的依赖契约
//
// 为什么要有它：九个页面类此前以嵌套类身份直通主窗体 private 成员 —— 耦合真实存在却看不见。
// 本接口把"页面需要主窗体做什么"显式成文：
//   · 页面类只持有 IPageHost 引用，凡引用超界成员 = 编译错（编译器兜底）；
//   · 主窗体实现接口（方法 public 隐式实现，字段经显式属性包装）；
//   · 后续任何页面想加依赖，先在这里加一条 —— 契约变更从此可见、可审。
// 成员全集来自对九个页面类引用的统计。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PicTool
{
    internal interface IPageHost
    {
        // ---------- 状态（属性包装）----------
        bool archiveCancel { get; set; }
        bool archiveThorough { get; set; }
        CanvasControl canvas { get; }
        Panel content { get; }
        int Cw { get; }
        int pendingPreview { get; set; }
        System.Windows.Forms.Timer previewTimer { get; }
        bool scanCancel { get; set; }
        bool syncingText { get; set; }
        ToolTip tips { get; }

        // ---------- UI 工厂 ----------
        void ApplyTextStyle();
        void ArchiveCurrentImage();
        FlatButton Btn(Panel host, string text, int x, int y, int w, int h, string icon, FlatButton.Kinds kind, EventHandler onClick);
        void ClearMask();
        void CommitColor();
        void CommitRotate();
        void CommitTexts();
        void CycleDevice(int delta);
        void DeleteSelText();
        void Divider(Panel host, int x, int y, int w);
        void DoAutoDeskew();
        void DoAutoEnhance();
        void DoBurnTexts();
        void DoCropToSelection();
        void DoFlip(bool horizontal);
        void DoInpaint();
        void DoMkFolders();
        void DoPerspective();
        void DoRegionColorize();
        void DoRename();
        void DoRotate90(bool clockwise);
        void DoSealColorize();
        void DoSealDetect();
        void DoToA4(bool cropFirst);
        void DoTrimBlank();
        Panel Hint(Panel host, int x, int y, int w, int h, string text);
        void InvalidatePreview();
        Label Lbl(Panel host, string text, int x, int y, int w);
        Label LblVal(Panel host, int x, int y, int w);
        void MakeValueEditable(Label value, FlatSlider s, Func<double, string> toEditor, Func<string, double?> fromEditor);
        void MoveSelText(int delta);
        void OnSaveModeChanged(bool overwrite, Segmented source);
        void OpenOutDir();
        void OpenScanDir();
        void PageEnd(Panel host);
        void PageH(Panel p, int h);
        int PageHead(Panel host, string title, string sub, int x, int y, int w);
        void PickOutDir();
        void PickScanDir();
        void RefreshDevices(bool report);
        void RefreshSealOverlay();
        void RefreshTextInspector();
        void ResetColorSliders(bool redraw);
        void RunArchive();
        void RunScan();
        void SetTool(int i);
        void StartPerspective();
        void Status(string s);
        Label SubLabel(Panel host, string text, int x, int y, int w);
        FlatSwitch Switch(Panel host, string text, int x, int y, int w, bool chk, int idx);
        void UndoRename();
        void UpdateFixHint();
        void UpdateRenamePreview();
        void UpdateRenameRows();
        void UpdateScanDirLabel();
    }
}
