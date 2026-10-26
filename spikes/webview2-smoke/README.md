# WebView2 冒烟验证工程（spike）

用途：在正式开工前验证"**只用 Windows 自带件**"这一技术路线是否成立。M1 的宿主工程可以直接从这里起步。

## 结论（本机实测，Windows 11 26200）

| 验证项 | 结果 |
| --- | --- |
| `dotnet build`（net48 + WinForms + WebView2，离线） | ✅ 10s 成功（从 NuGet 缓存还原，无需联网） |
| 产物体积 | ✅ exe 9KB + Core.dll 590KB + WinForms.dll 38KB + Loader.dll 162KB ≈ **1.0MB** |
| `CoreWebView2Environment.GetAvailableBrowserVersionString()` | ✅ 返回 `155.0.4283.62` → 可作首启依赖检测手段 |
| 实际创建 WebView2 控件 | ⚠️ 在 DSH 受限沙箱内失败 `0x8000FFFF (E_UNEXPECTED)`：WebView2 与浏览器进程走命名管道，沙箱禁止。**真实桌面会话可正常运行** |

## 用法

```powershell
# 依赖自检（不建窗口，任何环境都能跑）
.\bin\Release\net48\af-spike.exe

# 创建 WebView2 控件 → 导航内联 HTML → JS 断言（需真实桌面会话）
.\bin\Release\net48\af-spike.exe --gui
```

## 文件

- `af-spike.csproj`：`net48` + `UseWindowsForms` + `Microsoft.Web.WebView2`（唯一第三方包）
- `Program.cs`：依赖检测 + 最小 WebView2 导航/断言冒烟

> 说明：该目录只是可行性验证，不参与正式构建；M1 会新建 `AppForge.Host` 工程。
