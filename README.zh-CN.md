# PerfOverlay

PerfOverlay 是一款适用于 Windows 的性能悬浮窗，会自动监视当前前台应用。悬浮窗透明且可点击穿透，不依赖 NVIDIA API。

## 运行

- 支持 Windows 10/11 x64。
- 从源代码运行：安装 .NET 10 SDK，然后执行 `dotnet run --project src\PerfOverlay\PerfOverlay.csproj`。
- 自包含发布包：`artifacts\PerfOverlay-win-x64.zip`。

程序启动后会自动监视前台应用。左键单击通知区域图标打开设置；右键单击可选择 **Open Settings** 或 **Exit**。托盘菜单中的 Exit 会立即退出；设置窗口中的 **Exit App** 会先请求一次确认。`Ctrl+Alt+F4` 用于打开或隐藏设置窗口，`Ctrl+Shift+F4` 用于显示或隐藏悬浮窗；两组快捷键可分别修改。隐藏设置窗口或悬浮窗不会停止监视。

设置项包括显示器、位置、布局、文字颜色和独立快捷键。**Overlay Data** 页面可以分别显示或隐藏 FPS、基础帧率、1% Low、GPU、CPU、内存和延迟；隐藏指标仍会继续采集。该页面也简要说明 FPS 数值并列出检测到的 GPU 型号。像素风格设置窗口主页面较短，子页面会按需增高。

## 指标说明

- **FPS**：PresentMon `MsBetweenDisplayChange` 提供的近期显示帧间隔；无数据时回退到 `MsBetweenPresents`。
- **BASE FPS**：PresentMon `MsBetweenSimulationStart` 提供的近期应用模拟帧率；游戏不提供时显示为不可用。
- **1% LOW**：30 秒滚动窗口内，最慢 1% 有效帧样本的 FPS 算术平均值。至少积累 100 个样本后才显示数值。
- **GPU**：分别显示各 GPU 的利用率和显存占用估算。集成 GPU 行始终显示，其他检测到的 GPU 也会列出。
- **CPU**：系统总 CPU 利用率。
- **RAM**：系统物理内存使用率。
- **LAT**：近期 1 秒窗口内 PresentMon `MsPCLatency` 的平均值。这是 PC 延迟，不是渲染延迟；数据不受支持或不可用时显示为不可用。

## 采集故障排查

PresentMon 位于 `tools\PresentMon.exe`，其许可证和第三方声明位于 `licenses` 目录。如果帧采集无法启动，请确认当前 Windows 账户拥有所需的 Performance Log Users 权限。采集失败和自动恢复信息记录在 `%LOCALAPPDATA%\PerfOverlay\capture.log`，并通过通知区域气泡提示。程序只清理自身遗留的采集器和 ETW 会话。检测到已知 Intel `DCA_PMON` 或 NVIDIA FrameView 会话故障时，程序会先询问，再请求管理员权限重启相关服务。

## 构建与发布

```powershell
dotnet restore PerfOverlay.sln
dotnet build PerfOverlay.sln --configuration Release
dotnet publish src\PerfOverlay\PerfOverlay.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts\publish\PerfOverlay-win-x64
```

程序在 WPF 创建窗口前，通过模块初始化器设置 `PerMonitorV2` DPI 感知。应用清单仅保留执行级别声明，避免重复配置 DPI。当前 Release 构建无警告。
