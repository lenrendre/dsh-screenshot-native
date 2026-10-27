# AGENTS.md · 给 AI 的入口

> 给任何 AI / Agent 看的事实卡片。人看的说明在 [README.md](README.md)。

## 这是什么

DSH（DeepSeek Harness）的**微信式截图插件**：点一下冻结桌面 → 拖框或单击窗口 → 图片直接进输入框。
**仅 Windows 10/11**；原生 C# 全屏浮层（不是网页遮罩），所以能截 DSH 之外的任何窗口。

## 安装

```powershell
dsh plugin --profile desktop add dsh-screenshot-native
# 或钉版本：github:lenrendre/dsh-screenshot-native#v1.0.0
```

装完**重启 DSH**，输入框左侧出现相机按钮。

## 前置条件

- DSH `>= 0.2.0-rc.1`（`package.json` 的 `dsh.compatibility.dsh`）
- **运行时零依赖**：引擎是仓库内预编译的 `bin/WeChatShot.exe`
- 首次截图若走源码编译，用系统自带 `csc.exe`（不需要 SDK）

## 能力

| 操作 | 效果 |
| --- | --- |
| 单击窗口 | 截整个窗口（悬停高亮，被遮挡也能截） |
| 拖动 | 自由框选 |
| 单击桌面 | 截桌面，不含任务栏；在任务栏上单击截整屏 |
| 对号 / Enter / 双击 | 完成，图片进输入框 |
| 右键 / Esc | 取消 |
| Shift | 隐藏或保留 DSH 画面 |

截图只进内存与剪贴板，不落盘。

## 不提供

- 交互式标注（矩形/箭头/马赛克/文字）—— 需要时用内置 `annotate_image` 工具
- OCR / 翻译 / 截图入库
- 任何联网行为

## ⚠️ 冲突

本插件 fork 自 [`Durandalw/dsh-plugin-screenshot`](https://github.com/Durandalw/dsh-plugin-screenshot)。
**不要与原版同时安装**：两者注册同源路由与按钮，会重复。

## 已知限制

- 多显示器 + 混合 DPI 未验证（单屏 150% 已实测）
- Shift 之后窗口内容变化不反映到冻结画面（换取切换不闪）
- exe 未签名，双击可能触发 SmartScreen；插件用 `spawn` 启动，不经过它

## 文件地图

```
bin/WeChatShot.cs        浮层引擎（C#，单文件）
bin/WeChatShot.exe       预编译引擎
bin/build.ps1            csc 编译脚本
lib/index.js             Host 侧：启动引擎、注册路由/工具
lib/client.js            客户端：输入框按钮与交互
cordis.patch.yml         bundle 配置
```

## 验证

```powershell
# 引擎自检（不开界面）
bin\WeChatShot.exe --help

# 执行一次截图到指定文件
bin\WeChatShot.exe --trigger --out _smoke.png
```

许可证 MIT；上游版权归 baiyinshiyuan。
