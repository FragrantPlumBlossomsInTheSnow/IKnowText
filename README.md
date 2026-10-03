# IKnowText

一个 Windows 划词工具栏：选中文本即出现，提供复制、翻译、文本转换、编码/解码、网页搜索等动作。
本仓库是**独立项目**（非 GitHub fork），基于 [SnapActions](https://github.com/roko-tech/SnapActions) v2.4.5 改造。

A Windows selection toolbar that appears when you select text — an **independent project** (not a GitHub fork) based on SnapActions v2.4.5.

仓库 Repository：<https://github.com/XuejiMeixiangli/IKnowText>

[中文](#中文) · [English](#english)

***

## 中文

### 简介

选中文本时弹出工具栏，提供复制、翻译、文本转换、编码/解码、网页搜索等动作，结果与预览都留在选区附近。

本项目是**独立仓库**（非 GitHub fork），基于 [roko-tech/SnapActions](https://github.com/roko-tech/SnapActions) **v2.4.5** 改造，相对上游主要做了三件事：界面与文案中文化并重绘外观、翻译改用百度翻译 API（自绘弹层，**不依赖 WebView2**）、新增自定义 JS 脚本动作。

> 上游自 v2.5.0 起采用 Google 翻译 + WebView2 方案；本项目不跟进该线路，走百度翻译。

### 主要特性

- **划词工具栏**：选中文本即出现；悬停动作可预览结果；结果弹层提供「复制结果」，选区可编辑时提供「替换选区」

- **可自定义**：拖动动作固定到工具栏或调整顺序，右键「取消固定」，齿轮进入编辑模式（左键显示/隐藏动作）；子菜单为 **4 列自适应网格**，编辑模式下不会自动收起

- **动作分组开关**：粘贴 / 翻译 / 文本转换 / 编码解码 / 网页搜索，可在设置窗口或托盘菜单里开关

- **粘贴模式**：长按文本输入框（或双击，按设置）弹出「粘贴为」菜单，把剪贴板文本按动作转换后粘贴回原处；也可关闭

- **翻译**：百度翻译开放平台 API（`nmt` 模型）；AppID / 密钥经 Windows DPAPI（当前用户）加密后落盘；结果在自绘弹层内显示

- **字典**：选中 1–3 个英文单词可查释义（dictionaryapi.dev）

- **自定义 JS 脚本动作**：在设置里编写 `JSAction(text)`，选区文本入参、返回文本即结果（沙箱限制见下）

- **自定义翻译引擎**：用 JS 脚本实现翻译；选中后内置「翻译」动作与脚本里的 `Translation(...)` 都走它，未选则回退百度翻译

- **系统托盘**：5 个动作组开关、开机自启、更多设置、退出；单击托盘图标打开设置，右键为菜单

- **可选触发方式**：「划词即复制」、按 `Ctrl+C` 时弹出工具栏、排除应用清单

### 系统要求

- 64 位 Windows 10 / Windows 11

- 发布包为**单文件自包含**（无需安装 .NET 运行时），无安装器，解压即用；同目录另有 `licenses\` 存放第三方许可

- **不需要 WebView2**（本项目已移除该依赖）

- 在线功能（翻译 / 字典 / 货币等）默认**关闭**，需在设置中开启「允许在线查询」

### 下载与运行

获取源码后自行打包（完整打包与校验：`SnapActions\build.bat`，产物在 `artifacts\`；快速发布单文件：`SnapActions\publish.bat`）：

```bat
git clone https://github.com/XuejiMeixiangli/IKnowText.git
```

1. 取发布目录中的 `IKnowText.exe`（单文件）与 `licenses\`，放在固定位置
2. 直接运行：托盘出现图标，单击打开设置
3. 选中任意文本 → 工具栏出现 → 选择动作

### 使用要点

| 操作         | 说明                                     |
| ---------- | -------------------------------------- |
| 选中文本       | 工具栏出现（延迟、排除应用等可在设置中调整）                 |
| 单击动作       | 执行，结果进入弹层；可「复制结果」，可编辑时也可「替换选区」         |
| 悬停动作       | 预览结果（纯动作才会预览）                          |
| 右键动作       | 固定 / 取消固定（拖到工具栏也可固定）                   |
| 拖动动作       | 固定到工具栏或在固定区排序                          |
| 齿轮         | 编辑模式：左键显示 / 隐藏动作；**编辑期间面板不自动收起**       |
| 长按 / 双击输入框 | 打开「粘贴为」菜单（按「粘贴模式触发」设置，可关闭）             |
| 托盘图标       | 单击打开设置；右键为菜单（动作组开关 / 开机自启 / 更多设置 / 退出） |
| `Esc`      | 关闭工具栏与弹层                               |

### 翻译与在线查询

- **翻译**：在设置中填入百度翻译的 AppID 与密钥（改动后自动保存）；凭据经 DPAPI 加密，仅当前 Windows 用户可解密

- 单次翻译上限 **2000 UTF-8 字节**（按字节而非字符计）

- **字典**：使用 dictionaryapi.dev，仅支持英文

- **自定义翻译引擎**：用 JS 脚本实现翻译，与「自定义 JS 脚本动作」同构——定义 `JSAction(text)`，返回值即译文。在「设置 → 自定义翻译」里添加引擎并选为默认后，内置「翻译」动作与 JS 脚本里的 `await Translation(text, from, to)` 都走它；未选中则回退百度翻译

  - 引擎脚本以联网沙箱运行（引擎必然联网，开关固定开启）：可用 `await http.get/post(url, options)`；当前语言由全局变量 `SNAP_SOURCE_LANGUAGE` / `SNAP_TARGET_LANGUAGE` 提供

  - 引擎自身的沙箱**不**注入 `Translation`（否则引擎调用它会自递归），也不套百度的字节上限与凭据要求

- 在线能力总开关：「允许在线查询」（默认关闭）

### 自定义 JS 脚本动作

- 脚本需定义全局函数 `JSAction(text)`：选区文本作为入参，返回值作为结果文本

- 默认运行在纯 Jint 沙箱中：**无法访问文件 / 网络 / 剪贴板**；单次执行限时 **2 秒**，最多 5 万语句、16MB 内存，输出上限 128K 字符

- 勾选「允许此脚本访问网络」（每个脚本独立、默认关闭，首次需通过「允许在线查询」同意门）后，沙箱额外注入两个宿主函数：

  - `await Translation(text, from, to)` → 走设置里选中的**自定义翻译引擎**（语言码原样透传）
  - `await http.get(url, options)` / `await http.post(url, body, options)` → 返回 `{status, ok, headers, body}`；仅允许 http/https、拒绝回环与内网地址、响应 ≤256KB、单请求 ≤8 秒、单次运行 ≤5 个请求

- 脚本以 `{动作Id}.js` 存放在数据目录的 `scripts\` 下，可直接用编辑器修改，下次执行即生效

- 支持「上下文触发」正则：命中选区时，该动作会直接出现在工具栏的上下文区

联网脚本示例（需在编辑器里勾选「允许此脚本访问网络」）：

```js
async function JSAction(text) {
  // 走「设置 → 自定义翻译」里选中的引擎
  return await Translation(text, 'en', 'zh');
}
```

自定义翻译引擎示例（在「设置 → 自定义翻译」里添加并选为默认；引擎必然联网）：

```js
async function JSAction(text) {
  // 调用任意翻译接口：语言码从全局变量取（由弹层当前选择写入），返回值为译文
  var resp = await http.post(
    'https://api.example.com/translate',
    JSON.stringify({ q: text, from: SNAP_SOURCE_LANGUAGE, to: SNAP_TARGET_LANGUAGE }),
    { headers: { 'Content-Type': 'application/json' } });
  if (!resp.ok) throw new Error('HTTP ' + resp.status);
  return JSON.parse(resp.body).result;
}
```

### 设置与数据目录

数据目录：`%APPDATA%\IKnowText`（改名前的 `%APPDATA%\SnapActions` 会在首次启动时把 `settings.json` 与 `scripts\` 自动搬过来，旧目录保留不删）

- `settings.json` — 全部设置（含加密后的翻译凭据）

- `logs\` — 按天日志，保留最近 7 天

- `scripts\` — 自定义 JS 脚本源码

用环境变量 `SNAPACTIONS_DATA_DIR` 可指定其它目录（用于多实例或隔离测试；UI 自检必须使用隔离目录）。

### 从源码构建

需要 **.NET SDK 10.0.400**（`global.json` 中 `rollForward: disable` 锁定）。

```bat
:: 构建
dotnet build SnapActions\SnapActions.csproj -c Release

:: 单元测试
dotnet test SnapActions.Tests\SnapActions.Tests.csproj -c Release

:: 快速发布单文件 → SnapActions\bin\publish\IKnowText.exe
SnapActions\publish.bat

:: 完整打包与校验（调用 tools\package.py，产物在 artifacts\，含 ZIP 与校验和）
SnapActions\build.bat
```

UI 自检（会渲染各界面留档、校验交互链路）：

```bat
set SNAPACTIONS_DATA_DIR=%TEMP%\sa-selftest
IKnowText.exe --self-test
```

### 与上游的差异

| 方面    | 上游 SnapActions               | IKnowText                       |
| ----- | ---------------------------- | ------------------------------- |
| 翻译    | Google 翻译 + WebView2         | 百度翻译 API + 自绘弹层（无需 WebView2）    |
| 界面    | 英文                           | 中文界面，自绘 Fluent 外观（主题 / 图标 / 动效） |
| 动作    | 内置动作                         | 另有自定义 JS 脚本动作、上下文触发             |
| 浏览器扩展 | 提供 browser-extension 读取浏览器选区 | 已移除                             |
| 版本    | 已发布 v2.5.0                   | 保持 2.4.5，不跟进上游翻译线路              |

### 隐私

- 识别选区优先使用 Windows UI Automation；当 UI Automation 读不到选区时，会注入一次 `Ctrl+Insert` 读取选区，并在读回后**恢复原剪贴板**（快照 → 注入 → 读回 → 还原）

- 文本转换、编码、格式化、本地计算全部在本机完成

- 无遥测、无自动更新

- 在线能力需显式开启：翻译会把选中文本与语言发送到百度翻译；字典把单词发送到 dictionaryapi.dev；搜索 / IP / URL 动作会在浏览器打开对应地址

- 勾选「允许此脚本访问网络」的自定义脚本动作、以及选中的自定义翻译引擎，会按脚本逻辑发起 HTTP 请求（首次需同意），请求目标与内容由脚本决定；不勾选则脚本仍然完全离线

### 许可

MIT License，见 [LICENSE](LICENSE)；版权归上游作者 roko-tech 所有。

***

## English

### What it is

A Windows toolbar that appears when you select text, offering copy, translate, transforms, encode/decode and web search. Results and previews stay close to your selection.

IKnowText is an **independent project** (not a GitHub fork) based on [SnapActions](https://github.com/roko-tech/SnapActions) v2.4.5. Compared with upstream it localizes the UI into Chinese and redraws the look, replaces the Google/WebView2 translation with the Baidu Translate API and a self-drawn popup (**no WebView2 required**), and adds user-defined JavaScript actions.

### Highlights

- Selection toolbar with hover previews, copy-result and replace-selection

- Pin, reorder and hide actions; 4-column adaptive submenu; edit mode does not auto-collapse

- Paste mode (long-press or double-click on a text input) applies actions to clipboard text

- Baidu Translate with credentials encrypted by Windows DPAPI; dictionary lookup via dictionaryapi.dev

- Custom JavaScript actions in a Jint sandbox (no file/clipboard access; 2 s, 50k statements, 16 MB, 128K output). Opting a script into network access adds `await Translation(...)` (your own translation provider) and `await http.get/post(...)`, still bounded by http/https-only, loopback/private-address blocking, size and request limits

- Tray menu: five action-group toggles, auto-start, settings, exit

### Requirements

- 64-bit Windows 10 / Windows 11

- Release builds are self-contained single files — no .NET install, and **no WebView2**

- Online features are off by default ("Allow online lookups")

### Build and run

- Source: `git clone https://github.com/XuejiMeixiangli/IKnowText.git`

- .NET SDK 10.0.400 (pinned by `global.json`)

- `dotnet build SnapActions\SnapActions.csproj -c Release`

- `dotnet test SnapActions.Tests\SnapActions.Tests.csproj -c Release`

- Quick single-file publish: `SnapActions\publish.bat`; full package verification: `SnapActions\build.bat`

- UI self-test: `IKnowText.exe --self-test` with `SNAPACTIONS_DATA_DIR` pointing at an isolated folder

### Data and privacy

Settings, logs and scripts live in `%APPDATA%\IKnowText` (the pre-rename `%APPDATA%\SnapActions` is migrated on first launch; override with `SNAPACTIONS_DATA_DIR`). Text processing is local; there is no telemetry and no auto-updater. Online lookups are opt-in and send only the documented payloads — the selected text to Baidu Translate and a single word to dictionaryapi.dev.

### Licence

MIT — see [LICENSE](LICENSE). Copyright (c) 2026 roko-tech.

***

## Acknowledgements

This project is based on [roko-tech/SnapActions](https://github.com/roko-tech/SnapActions), originally developed by [M. AL-hejji (rokogan)](https://github.com/rokogan).
