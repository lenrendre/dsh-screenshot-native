# dsh-screenshot-native

给 DeepSeek Harness 加一个**微信式截图按钮**：点一下，桌面冻结成可框选的画面，选中的区域直接进输入框。

Windows 专用。原生 C# 全屏浮层（不是网页遮罩），所以能截到 DSH 之外的任何窗口；引擎是预编译的 `bin/WeChatShot.exe`，无运行时依赖。

> fork 自 [Durandalw/dsh-plugin-screenshot](https://github.com/Durandalw/dsh-plugin-screenshot)（MIT，作者 baiyinshiyuan），本仓库维护 fork 版本。

## 安装

```powershell
dsh plugin --profile desktop add github:lenrendre/dsh-screenshot-native#v1.0.1
```

装完重启 DSH，输入框左侧出现相机按钮。

## 使用

| 操作 | 效果 |
| --- | --- |
| 单击窗口 | 截整个窗口（悬停即高亮，被遮挡也能截） |
| 拖动 | 自由框选 |
| 单击桌面 | 截桌面，**不含任务栏**；在任务栏上单击则截整屏 |
| 对号 / Enter / 双击 | 完成，图片进输入框 |
| 右键 / Esc | 取消 |
| Shift | 隐藏或保留 DSH 画面 |

截图只进内存与剪贴板，`bin\.shots\` 不留文件。

## 性能（2560×1440 / 150% 实测）

| 场景 | 每帧 |
| --- | --- |
| 小框拖动 | 0.77–0.83 ms |
| 大框拖动 | 1.56–1.62 ms |
| 打开浮层（含抓屏） | 54–150 ms |

实现要点：蒙版与选区回拷走 gdi32 `BitBlt`（DDB 缓存），只重画脏区；不做帧率节流、不用 `DwmFlush` 卡帧。

## 构建

系统自带 `csc.exe` 即可，不需要 SDK：

```powershell
cd bin
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /codepage:65001 `
  /win32manifest:app.manifest /out:WeChatShot.exe `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll WeChatShot.cs
```

`bin\build.ps1` 是同一件事的脚本版。**exe 未签名**，双击可能触发 SmartScreen；插件用 `spawn` 启动，不经过它。

## 已知限制

- 仅 Windows 10/11。
- 没有交互式标注（需要标注时用 `annotate_image`）。
- 多显示器 + 混合 DPI 未验证（单屏 150% 已实测）。
- Shift 之后窗口内容变化不反映到冻结画面（换取切换不闪）。
- 配色是引擎常量，跟随皮肤需手动同步。

## 许可

MIT。上游版权归 baiyinshiyuan，fork 改动同样以 MIT 发布。
