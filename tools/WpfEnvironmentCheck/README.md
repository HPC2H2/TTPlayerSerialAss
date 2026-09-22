# C# / WPF 环境验证示例

这是独立的环境验证程序，尚未包含串口助手业务。

用 Visual Studio 打开 `WpfEnvironmentCheck.sln`，按 F5 运行；按钮演示 C# 事件与 WPF 数据绑定。项目目标为 `net10.0-windows`，无第三方 NuGet 依赖。

也可从终端运行：

```powershell
dotnet run --project E:\TTPlayerSerialAss\tools\WpfEnvironmentCheck\WpfEnvironmentCheck.csproj
```

自动检查（不弹出交互窗口）：

```powershell
& E:\TTPlayerSerialAss\tools\WpfEnvironmentCheck\Verify-Wpf.ps1
```

检查会编译 Release 配置、加载编译后的 XAML、执行按钮处理程序、核对绑定更新，并使用 WPF 渲染 PNG。结果写入 `E:\TTPlayerSerialAss\docs\environment\verification`。
