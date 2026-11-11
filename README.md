# 灌注桩钢筋笼建模插件

- `source/` 包含 Revit 2025 插件源码、构建脚本及所需的钢筋类型和定位板族资源。
- `models/Pile-Comparison.rvt` 包含三根已配筋试验桩和一根无钢筋对照桩。
- `models/Empty-Project.rvt` 是空白公制项目，可用于从头建模。

## 使用方法

1. 在安装了 Revit 2025 和 .NET 8 SDK 的 Windows 电脑上，从仓库根目录运行以下 PowerShell 命令，将两个目录换成实际路径。

   ```powershell
   .\source\Build.ps1 -RevitRoot '<Revit 2025 安装目录>' -FamilyTemplateRoot '<中文公制族样板目录>'
   ```

2. 将生成的 `source/bin/R26/PileCage.Reproduction.addin` 复制到 `%APPDATA%\Autodesk\Revit\Addins\2025`。保留构建生成的 DLL 及其相对于 `source/pilecage.workspace` 的目录结构，然后启动 Revit 2025。
3. 打开空白项目，从“附加模块 → 外部工具 → 钢筋笼参数建模”打开参数窗口，输入尺寸及配筋参数，点击“生成模型”并保存项目。通过“保存参数”和“载入参数”可复用 JSON 输入。对照项目可直接打开查看各试验桩。
