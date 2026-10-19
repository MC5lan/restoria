// MainForm.Theme.cs — 主窗体（partial）：主题按钮 / 切换 / 应用
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm : Form
    {
        // ---------------- 主题 ----------------

        void UpdateThemeButton()
        {
            if (btnTheme == null) return;
            btnTheme.Icon = Theme.IsDark ? "sun" : "moon";    // 按钮显示的是"切过去会变成的样子"
            tips.SetToolTip(btnTheme, Theme.IsDark
                ? "当前是深色工作台，点一下切回浅色（墨蓝·纸感）"
                : "当前是浅色界面，点一下切成深色工作台");
        }

        void ToggleTheme()
        {
            Theme.Toggle();                       // 内部会触发 Changed → ApplyTheme
            UiSettings.Dark = Theme.IsDark;
            UiSettings.Save();
            UpdateThemeButton();
            Toast.Show(this, Theme.IsDark ? "已切换到深色工作台" : "已切换到浅色界面", Toast.Kinds.Info);
        }

        void ApplyTheme(Palette old)
        {
            UpdateThemeButton();
            Theme.RemapColors(this, old);         // 写死在控件上的旧颜色 → 新令牌
            BackColor = Theme.WindowBg;
            Theme.RefreshTree(this);
            if (ws != null) ws.Invalidate(true);
            if (canvas != null) canvas.Invalidate();
        }

        // 图标按钮（正方形）
        FlatButton BtnIcon(Panel host, string icon, int x, int y, EventHandler onClick)
        {
            FlatButton b = new FlatButton();
            b.Icon = icon; b.IconOnly = true; b.Kind = FlatButton.Kinds.Ghost;
            b.Bounds = new Rectangle(x, y, 30, 32);
            b.Click += onClick;
            host.Controls.Add(b);
            return b;
        }

    }
}
