# Codex 网络与时区环境适配

把 **ChatGPT / Codex 桌面端** 的运行环境，对齐到当前代理节点：

1. 网络出口走海外节点（例如圣何塞 VPS）
2. 进程时区使用该节点的 IANA 时区（例如 `America/Los_Angeles`）
3. **不修改** Windows 系统时区

社区有一种观察：本地系统时区（如 `China Standard Time`）和出口 IP 所在地区不一致时，ChatGPT / Codex 桌面端可能出现质量异常。浏览器可以用插件改时区；桌面端不行，所以本项目只在启动 `ChatGPT.exe` 时注入 `TZ`。

这不是 OpenAI 官方机制说明，当作环境对齐工具使用即可。

## 它做什么

| 项目 | 行为 |
|------|------|
| 出口探测 | 读取当前公网 IP、城市、国家、IANA 时区 |
| 代理检查 | 显示用户代理 / WinHTTP / 进程代理变量，不擅自改代理 |
| 时区注入 | 只给 ChatGPT 进程设置 `TZ` |
| 系统时区 | 保持原样，例如继续用中国标准时间 |

不下载、不运行第三方「时区启动器」exe。脚本都在 `scripts/` 里，可直接阅读。

## 快速开始

在 PowerShell 中：

```powershell
cd D:\myprojects\codex-env-adapter

# 1. 先看当前出口 IP、节点时区、代理和 ChatGPT 安装情况
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Show-CodexEnvironment.ps1

# 2. 按节点时区启动 ChatGPT（若已在运行会先退出再拉起）
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Launch-ChatGPT.ps1

# 3. 可选：安装桌面快捷方式
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-DesktopShortcut.ps1
```

也可以双击 `scripts\Launch-ChatGPT.cmd`。

换节点后必须重新探测并重新启动 ChatGPT。`TZ` 只在进程启动时生效。

## 推荐使用方式

1. 打开 Clash / 系统代理，确认 GPT 相关流量走目标节点。
2. 运行 `Show-CodexEnvironment.ps1`，确认出口城市和时区符合预期。
3. 用本仓库启动器打开 ChatGPT，不要从开始菜单直接开。
4. 浏览器访问 chatgpt.com 时，另外用时区插件把浏览器时区改成同一 IANA 名称。

手动指定时区：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Launch-ChatGPT.ps1 -Timezone America/Los_Angeles
```

## 目录

```text
config/settings.example.json     配置示例
docs/原理.md                     为什么要对齐网络和时区
docs/使用指南.md                 日常操作与排错
scripts/CodexEnv.psm1            探测 / 检查 / 启动的公共模块
scripts/Show-CodexEnvironment.ps1  只检查，不启动
scripts/Launch-ChatGPT.ps1       探测节点并启动 ChatGPT
scripts/Launch-ChatGPT.cmd       双击入口
scripts/Install-DesktopShortcut.ps1
```

运行后的最近一次探测结果写在：

`%LOCALAPPDATA%\CodexEnvAdapter\settings.json`

## 和代理仓库的关系

VPS / Clash 节点本身在 `D:\myprojects\vps-racknerd`。本仓库不管怎么搭代理，只负责：

- 确认 **现在** 的出口是哪
- 把 ChatGPT 桌面端的 `TZ` 对齐到这个出口

当前这台机器上实测过的一组环境：

- 出口：美国加州圣何塞，时区 `America/Los_Angeles`
- Windows 时区：`China Standard Time`（未改）
- ChatGPT 包：`OpenAI.Codex`，启动文件为 `app\ChatGPT.exe`
