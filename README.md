# dsh-screenshot-native

给 DeepSeek Harness 加一个**微信式截图**按钮：点一下，整个桌面变成可框选的画面，选中的区域直接进输入框。

Windows 专用。原生浮层（C# WinForms），**不是**在网页里画遮罩 —— 只有这样才能截到 DSH 窗口之外的任何程序。

> 本包是 [`dsh-plugin-screenshot`](https://github.com/Durandalw/dsh-plugin-screenshot)（MIT，作者 baiyinshiyuan）的 **fork**，
> 保留原作者版权与许可。改动清单见文末「相对上游的改动」。

---

## 用起来是什么样

1. 点输入框左侧的 **相机按钮**
2. DSH 窗口**自动最小化**，整屏被冻结成一张图，约 0.3–0.4 秒
3. 在冻结画面上操作：
   - **单击**：截取鼠标下的整个窗口（悬停时该窗口被青绿边框高亮）
   - **拖动**：自由框选；拖动时鼠标旁有一个 `宽 × 高` 胶囊跟着走
   - **松手后**：选区右下角出现一个**圆形对号**，点它（或 `Enter`）完成
   - **右键 / Esc**：取消（右键在松开时才生效，不会漏给下层窗口）
   - **Shift**：把 DSH 窗口收回来 / 再藏起来（画面瞬时切换，不闪）
   - **双击选区**：完成
4. 完成后截图**自动插入输入框**（等同于粘贴一张图）

一次性使用：截完即删，`bin\.shots\` 里不会留文件。

### 视觉风格

浮层刻意只用**一个**动作控件（圆形对号），其余靠手势与键盘，避免按钮堆叠：

| 元素 | 样式 |
| --- | --- |
| 蒙版 | 51% 黑，选区抠亮 |
| 选框 | 青绿 2px + 1px 内白线 + 四角浅色方块 |
| 尺寸 | 拖动时跟手胶囊；静止时写进顶部提示条末尾 |
| 提示条 | 深底 + 淡青描边 + 浅色字 |
| 对号 | 青绿实心圆 + 深色勾，悬停变亮、指针变手型 |

配色对齐当前 DSH 深青皮肤（`#33C8B4` 强调、`#0A0F12`/`#0F1518`/`#182025` 层次、`#E7F3F6` 文字、`rgba(130,230,215,.16)` 描边）。浮层不是网页 DOM，拿不到 CSS 变量，所以这些值是**引擎里的常量**；换皮肤时需要同步这几个值。

---

## 给 AI 的三个工具

插件同时把能力暴露给模型，所以不用人点也能截图：

| 工具 | 参数 | 用途 |
| --- | --- | --- |
| `screenshot_capture` | `mode`: `screen` \| `region` \| `window`；`region` 用 `x,y,width,height`；`window` 用 `hwnd` | 非交互截图，返回 PNG 路径 |
| `list_windows` | 无 | 列出可见顶层窗口（句柄、进程、矩形、标题），用来给 `window` 模式挑句柄 |
| `annotate_image` | `source`、`marks[]`、`out?` | 在图上画矩形/椭圆/箭头/文字/马赛克，让"圈出来"替代坐标描述 |

`annotate_image` 的 mark：`{ type: rect|ellipse|arrow|label|mosaic, x, y, width, height, text?, color?, thickness?, fontSize? }`

---

## 行为细节（值得知道的）

- **打开即最小化**：按窗口属主的 **exe 镜像路径**识别 Harness，而不是 pid —— 桌面端会在窗口属主和 node 宿主之间插一层包装进程，按 pid 会找错人。
- **隐藏前先验证**：最小化后检查 `IsIconic`，藏不住就**中止并说明原因**（写 `<out>.error`，宿主把中文原因弹给用户），而不是拍一张"自己的聊天界面"当截图。
- **Shift 是真切换**：真的 `SC_MINIMIZE` / `SW_RESTORE`，画面用打开瞬间预抓的两帧切换，所以不闪；代价是这之后窗口内容若变化，冻结画面不会跟着更新（想截最新就重新点一次）。
- **悬停跳过系统外壳**：任务栏、桌面、`TextInputHost.exe` 那块透明全屏 IME 面、`Windows.UI.Core.CoreWindow` 等都排除。鼠标在桌面/任务栏上时高亮**整个屏幕**，单击可得全屏图。
- **拍不到自己**：取景与保存时浮层自身透明 90ms 并 `DwmFlush`，遮罩不会进画面。
- **只重绘变化的部分**：拖动时只失效"旧选区 ∪ 新选区 ∪ 尺寸胶囊的新旧位置"，所以又快又不留残影。
- **临时文件**：`bin\.shots\`（包内，读完即删）。不能放 `%TEMP%` 或 `DSH_HOME` —— 引擎由宿主直接 spawn，普通用户令牌在那两处会被沙箱拒绝。
- **日志**：`bin\wechatshot.log`（`boot:` / `open:` / `hover:` / `toggle:` / `[cli]` / `[host]`），排查问题时先看它；宿主也会追加 `[host] overlay closed code=… file=…`，两边能对账。

---

## 架构

| 层 | 文件 | 职责 |
| --- | --- | --- |
| 浏览器半身 | `lib/client.js`（~321 行） | 只画一个相机按钮，点它 `POST /dsh-screenshot-native/native`，拿到 PNG 后塞进输入框 |
| 宿主半身 | `lib/index.js`（~579 行） | 注册 `POST /native`，spawn 引擎，注册上面三个工具，读写暂存目录 |
| 原生引擎 | `bin/WeChatShot.cs`（~2266 行） | 全屏浮层、冻结画面、窗口命中、框选、最小化/恢复、DPI、PNG 保存 |
| 辅助脚本 | `scripts/*.ps1` | `shot.ps1` 抓屏、`win.ps1` 列窗口、`annotate.ps1` 画标注 |

无运行时依赖：不 `import '@deepseek-ai/dsh-tools'`（linked 包解析不到 peer），工具用 `ctx.tools.register({...})` 的普通对象定义。

---

## 安装

桌面端：**插件页 → 添加插件**，包名 `dsh-screenshot-native`（本地开发时是 `link:` 到源码目录）。

命令行（非桌面 profile）：

```bash
npx @deepseek-ai/dsh plugin --profile web add <包路径或名称>
```

⚠️ **改完宿主代码要重启应用**才会生效；改引擎（`.cs`/`.exe`）或 `.ps1` 则不用 —— 每次点击都会重新 spawn。热重载（禁用再启用）会撞 `duplicate exact route`，不可用。

---

## 引擎命令行

引擎只有插件实际使用的那几个开关（2026-10-24 清掉了全部调试入口：`--selftest` / `--shot` / `--windowat` / `--snapshot*` / `--hide` / `--inset` / `--bench` / `--dumpframes`，以及 perf 探针和阶段计时日志）：

```powershell
WeChatShot.exe --trigger --hide-pid <pid> --hide-exe <exe路径> --out <png路径>   # 插件正常路径：交互浮层
WeChatShot.exe --help                                                           # 打印开关
```


---

## 安装

```powershell
# GitHub（推荐，跟随 tag 版本）
dsh plugin --profile desktop add github:zhangzeyufr-commits/dsh-screenshot-native#v1.0.0

# npm（发布后可用）
dsh plugin --profile desktop add dsh-screenshot-native
```

装完重启 DSH（安装器会重写 `dsh.profile.bundles`，重启后生效）。卸载：

```powershell
dsh plugin --profile desktop remove dsh-screenshot-native
```

> 引擎是**预编译好的 `bin/WeChatShot.exe`**，不需要本地 SDK；源码在同一目录，想改就重新编译（见下）。

## 构建

用系统自带的 `csc.exe`（.NET Framework 4.x，C# 5），不需要 SDK：

```powershell
cd bin
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /codepage:65001 `
  /win32manifest:app.manifest /out:WeChatShot.exe `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll WeChatShot.cs
```

`bin\build.ps1` 是同一件事的脚本版。**exe 未签名**，首次运行可能触发 SmartScreen 提示；插件用 `spawn` 直接启动，不经过 SmartScreen。

---

## 配置

宿主接受 `shellPath`（可选，指定 PowerShell 解释器路径），其余键（`snapThreshold`、`ignoreWindowPatterns`、`longShot*`、`defaultAction`）是上游遗留，**当前代码不再使用**。

---

## 拖动性能（2026-10-24 实测）

原生浮层在 2560×1440 / 150% 缩放下，拖动选区的每帧成本（同机实测）：

| 选区大小 | 每帧平均 | 每帧最大 |
| --- | --- | --- |
| 小框（约 450×350） | **0.77–0.83 ms** | ~10 ms |
| 大框（1500×1100） | **1.56–1.62 ms** | ~9 ms |
| 打开浮层（含抓屏） | **54–150 ms** | — |

关键结论（都是实测换来的，别再踩）：

- **不要节流**。丢鼠标事件会让采样变不均，比多画更卡；正确做法是把单帧成本压到 1 ms 级。
- **不要用 `DwmFlush` 做节流**。阻塞在 UI 线程里会推迟下一帧，延迟反而变大。
- 两处贴图（蒙版图、选区回拷）走 gdi32 `BitBlt`（DDB 镜像 + `GetHbitmap`），比 `Graphics.DrawImage` 快数倍。
- DDB 缓存在窗口打开时预热（`OnLoad`），否则第一次拖动的首帧会多出 40–70 ms 的 `GetHbitmap` 开销。
- 提示条 / 尺寸标签 / 对号按脏区相交判断跳过，不在脏区就完全不测量、不绘制。
- 每帧耗时基本线性于**脏区像素数**：拖大框比拖小框贵一倍多，这属于必要开销。

---

## 已知限制

- 仅 Windows。
- **没有交互式标注**：框选后不能画圈。需要标注时走 `annotate_image`（先截、再画、再发）。
- 多显示器 + 混合 DPI 未验证（单屏 150% 缩放已实测）。
- 双帧缓存：Shift 之后的窗口内容变化不会反映到冻结画面里（换取"不闪"）。
- 边缘吸附：上游用 PowerShell 实现过，当前引擎**没有**实现。
- 配色是引擎常量，跟随皮肤需要手动同步（见上面「视觉风格」）。

---

## 相对上游的改动

- **引擎换血**：页面内浮层 → 原生 WinForms 全屏浮层（能截 DSH 之外的窗口），新增悬停选窗、自动最小化、Shift 切换、DPI 感知。
- **按 exe 路径识别宿主窗口**（pid 不可靠）；排除 IME/桌面/任务栏等系统外壳。
- **最小化改用** `WM_SYSCOMMAND / SC_MINIMIZE`（`ShowWindow` 的首次调用会被 STARTUPINFO 覆盖）。
- **隐藏前验证**：`IsIconic` 不成立就中止并说明原因；保存失败也不再假装成功（退出码 3 + `<out>.error`）。
- **保存降级**：GDI+ 直接写失败时改走内存流（TEMP 不可写时也能保存）。
- **三个模型工具**（截图 / 列窗口 / 标注）与统一 `{ status, data }` 输出信封。
- **浮层 UI 重做**：单一圆形对号（手绘，非控件）、右键取消且不漏事件、拖动时尺寸胶囊跟手、只重绘变化区域（无残影）、配色对齐 DSH 深青皮肤。
- **清理**：删掉托盘 App、单实例互斥锁、`config.ini`、7 条死路由、3 个失效脚本、`HostPids`/Toolhelp32/`--clip` 等死代码；客户端从 1013 行降到 ~320 行。
- **性能**：预合成"图+蒙版"后直接用位块拷贝，选区只重画变化区域 —— 拖动跟手。

---

## 许可证

MIT。原项目版权归 baiyinshiyuan；本 fork 的改动同样以 MIT 发布。
