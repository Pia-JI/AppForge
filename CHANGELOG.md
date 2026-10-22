# 变更记录（CHANGELOG）

格式：`版本 · 日期 · 摘要`。里程碑含义见 [docs/03-任务规划与里程碑.md](docs/03-任务规划与里程碑.md)。

---

## v1.0.2 · 修复"关闭后第二次双击没反应"

- **根因**：关闭窗口是**最小化到托盘**（进程仍在运行，设计如此）。但第二次启动的激活逻辑用的是 `Process.MainWindowHandle` —— 窗口被 `Hide()` 后该值变成 0，激活逻辑直接跳过，第二个进程静默退出 ⇒ 用户看到「双击没反应」；任务管理器杀掉进程后自然恢复正常
- **修复**：
  - 用 `EnumWindows` + `GetWindowThreadProcessId` 找**已运行实例的窗口（含隐藏窗口）**，再 `ShowWindow`/`SetForegroundWindow` 唤回
  - 通过 `WM_COPYDATA` 把指令传给已运行实例（`__activate__` = 只唤出；`appId` = 唤出并打开该应用），桌面快捷方式直达应用也走这条链路
  - 保留文件交接兜底 + 启动日志（`检测到已有实例 activated=?`、`收到激活请求`）
- **实测**：启动 → 模拟点击标题栏 X（`reason=UserClosing` → `已隐藏到托盘（进程继续运行）`）→ 再次双击 → 旧实例记录 `收到激活请求：__activate__`、`activated=True`，窗口唤回且**进程数始终为 1**
- 顺带修复：`pack.ps1` 不再清空整个 `dist`（之前会把便携包一起删掉）；新增关闭行为诊断日志

## v1.0.1 · 交付方式修复（用户反馈：启动器写目录被拒 / SmartScreen）

- **修复**：单文件启动器解压时依赖 `%TEMP%` 写临时 zip，在受限环境（沙箱、受控文件夹访问、策略限制）会 `UnauthorizedAccessException` 直接启动失败
  - 改为**内存中解压**（不落临时 zip），彻底去掉 TEMP 依赖
  - 解压目录改为**多级自动回退**：`%LOCALAPPDATA%` → `%TEMP%` → exe 同目录；全部失败才报错并给出 `APPFORGE_PAYLOAD_DIR` 指引
  - 宿主数据目录同样三级回退：默认 → exe 同目录 `data` → `%TEMP%\AppForge\data`
- **新增**：启动器日志（exe 同目录 `logs\launcher.log` / TEMP / LocalAppData 三处择一）+ CLI 模式不再弹错误对话框（避免卡住脚本）
- **新增**：应用图标（多尺寸 ICO，代码生成，无第三方资源）
- **新增**：`scripts\pack-portable.ps1` → `dist\AppForge-portable.zip`（绿色便携包，**推荐分发方式**：不追加数据、杀软误判概率最低）
- **文档**：用户手册补充 SmartScreen 处理、受限环境说明、两种交付方式；已知问题更新
- 实测：单文件版 `--selftest` 59/59、`--ui-diag` 全绿；便携包解压后自检 59/59

## v1.0.0 · M5 交付形态

**单文件交付 + 文档 + 验收数据**

- 新增 `AppForge.Launcher`（自解压启动器）：exe + payload.zip + 尾标记 → **产出单文件 `dist\AppForge.exe`（0.42 MB）**
  - 首次运行解压到 `%LOCALAPPDATA%\AppForge\payload\<版本>\`，之后复用；升级不撞文件锁
  - 支持 `APPFORGE_PAYLOAD_DIR` 覆盖解压目录（便携/测试）
- 新增 `scripts/pack.ps1` 一键打包
- 新增 `APPFORGE_SIMULATE_NO_WEBVIEW2=1` 演练开关，可验证"缺少 WebView2"的检测与引导分支
- 新增文档：[06 用户手册](docs/06-用户手册.md)、[07 已知问题与限制](docs/07-已知问题与限制.md)、本 CHANGELOG
- 实测：单文件版 `--selftest` **59/59**、单文件版 `--ui-diag` 全绿（4 卡片 / 8 面板 / 修改模式 / 版本 [2,1] / 应用窗口正常）
- 体积：单文件 **0.42 MB**（目标 ≤5MB）；内存：宿主 ~54MB，WebView2 子进程在高 DPI 大窗口下偏高（已列入已知问题 #7）

## v0.4.0 · M4 桌面与系统集成

- 导出 zip（含 `如何运行.md`）、桌面快捷方式（`--open-app` 单实例交接，双击直达应用）
- 版本对比：逐文件 + LCS 行级 diff（超大文件降级为行数统计）
- 回收站：列出 / 恢复 / 7 天自动清理
- 系统集成：全局快捷键 `Ctrl+Alt+Space`、开机自启（HKCU Run，可开关）
- 诊断：`diag.info` / `diag.export` 诊断包（不含密钥明文）
- `--selftest` 51 → **59 条断言**

## v0.3.0 · M3 增量修改与版本

- AI 增量修改：对已有应用"说一句就改"，只改相关文件，自测通过后存为新版本
- 版本服务：列表 / 回滚 / 保留策略（当前版本永不清理）/ `version.json` 元数据
- UI：卡片菜单「用 AI 修改」「版本历史」；Builder 修改模式
- CLI：`--gen` 配合 `--app` 做无界面修改
- `--selftest` 44 → **51 条断言**

## v0.2.0 · M2 生成闭环

- 生成引擎：`plan → code → selftest → repair(≤3) → finalize`，function calling 逐文件写代码
- 自测器：隐藏 WebView2 真跑草稿，采集 JS 报错 / 白屏 / `data-app-root` 缺失
- Builder UI：需求输入、阶段状态、实时日志、成功/失败卡片（生成后自动打开）
- CLI：`--gen="<需求>"` 无界面闭环；manifest 落盘改为 camelCase 契约
- 首次真实端到端：待办清单需求 → 3 轮工具调用 → 自测通过 → 保存 v0001

## v0.1.2 · 修复模型下拉框

- `<input list>` + `<datalist>` 在 Chromium 中聚焦不展开 → 改为原生 `<select>` + 可手填
- 模型列表缓存到 localStorage、首次进设置页自动拉取、支持自定义模型名

## v0.1.1 · 修复界面三问题 + 高 DPI

- 修复 WebView2 消息解析错误（`TryGetWebMessageAsString` 对对象消息抛异常被吞）导致的**全站 RPC 无响应**
  → 设置页空白、应用卡片空白、应用窗口打不开
- 修复 200% 高 DPI 下窗口未缩放导致内容放大 2 倍（补 App.config + `DpiHelper` 逻辑/物理换算）
- 启动脚本构建前自动关闭旧实例（避免 dll 被占用导致构建失败）
- 新增原生启动画面（实测 108ms 可见）、`--ui-diag` 界面自检（截图 + JS 错误采集）

## v0.1.0 · M1 骨架

- C#/.NET Framework 4.8 + WinForms + WebView2（运行时全部来自 Windows 自带）
- 免安装绿色包、依赖自检与首启 WebView2 静默安装引导
- JSON 应用库（原子写 / 每日快照 / 索引自愈 / 回收站）、DPAPI 密钥存储
- 虚拟主机隔离（每应用独立 origin、应用窗口无宿主通道）
- 设置页（API 地址 / 密钥 / 模型 / 参数 / 依赖状态）、启动桌面、应用运行
- CLI：`--check` / `--selftest` / `--bench` / `--check-model` / `--list-models`
- 双击一键启动脚本（`启动AppForge.cmd`）与桌面快捷方式脚本
