// AssemblyUninst.cs — 卸载程序（uninst.exe / 装机后叫「Uninstall.exe」）的程序集属性。
//
// 与安装器分开写的原因：两个 exe 的"文件说明"不一样，控制面板/
// 「右键属性 → 详细信息」要能一眼区分是安装程序还是卸载程序。
using System.Reflection;

[assembly: AssemblyVersion("1.5.7.0")]
[assembly: AssemblyFileVersion("1.5.7.0")]
[assembly: AssemblyInformationalVersion("1.5.7")]
[assembly: AssemblyTitle("Restoria 卸载程序")]
[assembly: AssemblyProduct("Restoria 卸载程序")]
[assembly: AssemblyDescription("Restoria 卸载程序")]
[assembly: AssemblyCompany("Restoria")]
[assembly: AssemblyCopyright("Copyright (c) 2026 MC5lan. Licensed under MIT.")]
