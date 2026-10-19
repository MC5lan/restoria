// AssemblySetup.cs — 安装程序（setup-boot.exe）的程序集属性。
//
// 这些 attribute 决定 Windows「右键 → 属性 → 详细信息」里显示什么。
// 与卸载器（AssemblyUninst.cs）、主程序（src\Core\Version.cs）保持同一产品名/公司/版权，
// 改版本时三处一起改（build_setup.ps1 会校验，不一致直接报错）。
using System.Reflection;

[assembly: AssemblyVersion("1.5.7.0")]
[assembly: AssemblyFileVersion("1.5.7.0")]
[assembly: AssemblyInformationalVersion("1.5.7")]
[assembly: AssemblyTitle("Restoria 安装程序")]
[assembly: AssemblyProduct("Restoria 安装程序")]
[assembly: AssemblyDescription("Restoria 单文件安装程序")]
[assembly: AssemblyCompany("Restoria")]
[assembly: AssemblyCopyright("Copyright (c) 2026 MC5lan. Licensed under MIT.")]
