// SetupInfo.cs — 安装/卸载程序共用的产品常量（唯一出处，别再在别处硬编码这些名字）。
//
// 注意：程序集属性（AssemblyVersion / AssemblyTitle …）**不在这里** ——
// 安装器与卸载器是两个独立 exe，属性各不相同：
//   安装器 → tools\install\AssemblySetup.cs
//   卸载器 → tools\install\AssemblyUninst.cs
//
// 版本号与主程序 src\Core\Version.cs 必须一致，build_setup.ps1 会校验，
// 不一致直接报错（免得发行包里两个版本号打架）。
namespace PicToolSetup
{
    /// <summary>安装/卸载知道的产品信息（与主程序 AppInfo 对齐；此处不引用主程序源）。</summary>
    static class SetupInfo
    {
        public const string Version = "1.5.6";
        public const string Name = "Restoria";
        public const string Publisher = "Restoria";
        public const string Copyright = "Copyright (c) 2026 MC5lan. Licensed under MIT.";

        /// <summary>注册表卸载入口的键名（HKLM\...\Uninstall\ 下）。装机后控制面板按它列条目。</summary>
        public const string AppId = "PicToolArchiveSuite";

        /// <summary>主程序文件名（安装后位于安装目录根下）。</summary>
        public const string MainExe = "Restoria.exe";

        /// <summary>卸载器文件名（安装时随包拷进安装目录）。</summary>
        public const string UninstallerName = "Uninstall.exe";

        /// <summary>装机后创建的开始菜单文件夹名（所有用户）。</summary>
        public const string StartMenuFolder = "Restoria";

        /// <summary>桌面快捷方式文件名。</summary>
        public const string DesktopShortcut = "Restoria.lnk";

        /// <summary>卸载入口键路径（不含根；卸载器会同时清 64/32 位两个视图）。</summary>
        public const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId;

        /// <summary>安装器写进安装目录的标记文件：卸载器靠它确认"这里确实是我们装的"。</summary>
        public const string MarkerFile = "archivesuite.install";

        public const string HomePage = "";
    }
}
