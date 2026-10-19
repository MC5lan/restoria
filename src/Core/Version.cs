// Version.cs — 版本号唯一出处。
//
// 改版本号只动这一处：程序集版本、标题栏显示、安装脚本读的文件版本，全都跟着走。
// 版本规则（语义化版本）：主版本.次版本.修订
//   · 主版本：大功能或界面结构变化
//   · 次版本：新功能
//   · 修订：bug 修复
//
// 各版本的功能与修复记录见 CHANGELOG.md。
using System.Reflection;

[assembly: AssemblyVersion("1.5.6.0")]
[assembly: AssemblyFileVersion("1.5.6.0")]
[assembly: AssemblyInformationalVersion("1.5.6")]

[assembly: AssemblyTitle("Restoria")]
[assembly: AssemblyProduct("Restoria")]
[assembly: AssemblyDescription("Restoria: scan restoration, deskew, denoise, binarization, archiving and AI inpainting in one tool")]
[assembly: AssemblyCompany("Restoria")]
[assembly: AssemblyCopyright("Copyright © 2026 MC5lan. Licensed under MIT.")]

namespace PicTool
{
    public static class AppInfo
    {
        public const string Version = "1.5.6";
        public const string Name = "Restoria";
        public const string Title = Name + " " + Version;   // 窗口标题（含版本号）
        // 发行版元信息（用于安装器/卸载入口/关于页）
        public const string Publisher = "Restoria";
        public const string AppId = "PicToolArchiveSuite";   // 卸载注册表键名/升级路径用（ASCII）
        public const string HomePage = "";
    }
}
