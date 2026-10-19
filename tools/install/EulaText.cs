// EulaText.cs — 安装时展示的许可证确认（MIT License）。
//
// 单独一个文件是为了好改：协议正文只在 Text 里，改完重新跑 tools\build_setup.ps1 即可。
// 本软件以 MIT 许可证开源——允许自由使用、修改、合并、发布、分发，只需保留版权声明。
namespace PicToolSetup
{
    static class EulaText
    {
        public const string Title = "许可证确认";

        // 显示在协议框上方的一行摘要（太长用户不会读）
        public const string Summary =
            "本软件以 MIT 许可证开源。继续安装表示您已阅读并同意。";

        public const string Text =
@"Restoria · MIT 许可证

版本：1.5.6

本软件以 MIT 许可证（MIT License）开源发布。

MIT 许可证授予任何人免费获得本软件副本及相关文档文件（以下简称""软件""）的权利，
可以不受限制地处理本软件，包括但不限于使用、复制、修改、合并、发布、分发、
再授权及销售本软件副本的权利，前提是在本软件的所有副本或重要部分中包含
上述版权声明和本许可声明。

本软件按""现状""提供，不附带任何形式的明示或暗示担保，包括但不限于对适销性、
特定用途适用性和不侵权的担保。在任何情况下，作者或版权持有人均不对任何索赔、
损害或其他责任负责，无论这些追责来自合同、侵权或其它行为中，还是产生于本软件
本身及对本软件的使用或其它处置。

完整许可证文本见随软件分发的 LICENSE 文件。

Copyright (c) 2026 MC5lan";
    }
}
