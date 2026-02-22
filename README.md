# TeknoParrot BigBox 前端（自用版）

一个独立的 WPF 启动前端，用于为 TeknoParrot 提供类似 LaunchBox / BigBox 风格的全屏封面 + 视频浏览界面。

> 运行环境：Windows + .NET Framework 4.6.2  
> 目录位置建议：将 `TeknoParrotBigBox.exe` 与 `TeknoParrotUi.exe` 放在**同一目录**

---

## 功能概览

- 左侧：按 **类型/Genre 中文分类** 的游戏列表（含「★ 收藏」顶栏）
- 中间：当前分类下的 **游戏封面墙**
- 右侧：
  - 当前选中游戏的标题
  - 大封面图
  - 游戏预览视频（基于 LibVLCSharp，兼容多种格式；可静音/开声）
  - 可滚动的游戏介绍（来自 LaunchBox `Teknoparrot.xml`）
  - 操作按钮：
    - **开始游戏**：启动当前游戏（调用同目录 `TeknoParrotUi.exe --profile=ID.xml`；无 UserProfiles 时回退为执行 bat）
    - **收藏游戏 / 取消收藏**：加入/移出「★ 收藏」分类
    - **返回鹦鹉**：退出 BigBox，并启动同目录下 `TeknoParrotUi.exe`
    - **设置 / 关于本程序**：语言、Media 路径、调试日志开关等

---

## 目录结构约定

项目根目录（即 BigBox 程序所在目录）下常用目录：

- **`UserProfiles\`**（推荐）  
  官方 TeknoParrot 客户端添加游戏后生成的配置目录，拷贝到 BigBox 程序目录即可。  
  - 内含 `*.xml`，文件名即 **profileId**（如 `WMMT6RR.xml`）。  
  - BigBox **优先**扫描此目录作为游戏列表来源，比 bat 更可靠。  
  - 开始游戏时执行：**`TeknoParrotUi.exe --profile=ID.xml`**（鹦鹉 UI 与 BigBox 同目录）。

- `bat\`（回退）  
  当 `UserProfiles` 不存在或为空时，才使用 bat 目录：
  - `XXX.bat`，首行包含 `--profile=XXXX.xml`，用于推导 profileId 并作为启动脚本。

- `Metadata\`  
  原 TeknoParrot 元数据（`*.json`），不再修改，只做读取。

- `launchbox_descriptions.json`  
  由 `extract_launchbox_descriptions.py` 从 `Teknoparrot.xml` 生成，按 profileId 保存 LaunchBox 的标题、说明等。

- `Media\Covers\{profileId}.png` / `.jpg`  
  BigBox 使用的封面（profileId 命名）。

- `Media\Videos\{profileId}.mp4`  
  BigBox 使用的预览视频（profileId 命名）。可在设置中指定自定义 Media 根目录。

- `favorites.json`  
  BigBox 运行时生成/更新，保存收藏列表。

---

## 编译与运行

### 编译

1. 安装 [Visual Studio](https://visualstudio.microsoft.com/)（含 .NET 桌面开发工作负载）或带 .NET Framework 4.6.2 的 MSBuild。
2. 还原 NuGet 包（Visual Studio 会自动还原，或命令行）：
   ```bash
   dotnet restore TeknoParrotBigBox.csproj
   ```
3. 在 Visual Studio 中打开 `TeknoParrotBigBox.csproj`，选择 **生成 → 重新生成解决方案**；或使用 MSBuild：
   ```bash
   msbuild TeknoParrotBigBox.csproj /p:Configuration=Release
   ```
4. 输出在 `bin\Release\` 或 `bin\Debug\`，需将 **VideoLAN.LibVLC.Windows** 随程序一起发布（NuGet 会复制原生库到输出目录）。

### 运行

1. 将 `TeknoParrotBigBox.exe` 与 `TeknoParrotUi.exe` 放在同一目录。
2. 准备 `UserProfiles\`、`Metadata\`、封面与视频（见上方目录约定）。
3. 运行 `TeknoParrotBigBox.exe`。

---

## 快捷操作说明

- **键盘**  
  - ↑↓←→：切换分类（左/右）、切换游戏（上/下）  
  - Enter：开始游戏  
  - Esc：退出（带确认）
- **手柄（XInput / DINPUT）**  
  - 左/右：切换分类；上/下：切换游戏  
  - A / Start：开始游戏；B / Back：退出
- **鼠标**  
  - 左侧点击切换分类；中间点击/滚轮切换游戏；右侧介绍区可滚轮浏览。
- **预览视频**  
  - 右下角喇叭切换静音；启动游戏时自动停止预览。

---

## 注意事项

- 本仓库通过 `.gitignore` 忽略了 `bin/`、`obj/`、`Media/` 等，建议只将源码和脚本纳入版本控制。
- 预览视频使用 **LibVLCSharp**，需保证运行目录或 NuGet 输出中包含 VLC 原生库（VideoLAN.LibVLC.Windows 会复制到输出目录）。

---

## 开源协议（License）

**本项目代码采用 [MIT 许可证](LICENSE)。**  
使用、修改与再分发本仓库中的**原创代码**时，请保留版权声明与许可证文件。  
本项目中使用的**第三方库**各有其自身许可证，见下方「引用与致谢」，分发时须同时满足其许可要求。

---

## 引用与致谢（Attributions）

本项目中引用或依赖的第三方代码与库如下，在此致谢并标明许可与来源。

### 第三方库（NuGet）

| 项目 | 版本 | 用途 | 许可证 | 链接 |
|------|------|------|--------|------|
| **LibVLCSharp** | 3.8.5 | 预览视频播放（.NET 绑定） | LGPL-2.1-or-later | https://github.com/videolan/libvlcsharp |
| **LibVLCSharp.WPF** | 3.8.5 | WPF 视频视图控件 | LGPL-2.1-or-later | https://github.com/videolan/libvlcsharp |
| **VideoLAN.LibVLC.Windows** | 3.0.20 | VLC 原生库（Windows） | GPL-2.0-or-later | https://github.com/videolan/vlc |
| **Newtonsoft.Json** | 13.0.3 | JSON 解析与配置 | MIT | https://www.newtonsoft.com/json |

### 设计/思路参考（无直接代码引用）

- **Pegasus Frontend**（主题 pegasus-theme-grid）  
  - 预览视频「停留一段时间再加载、切换时先停止再延时加载」的交互思路，在本项目中以类似方式实现（延迟计时、后台加载）。  
  - Pegasus 为 GPL-3.0，本项目未复制其源码，仅参考了主题的 QML 逻辑思路。  
  - 项目地址：https://github.com/mmatyas/pegasus-frontend  

### 运行依赖

- **TeknoParrot / TeknoParrotUi**  
  - 本前端为 TeknoParrot 的独立启动器界面，通过调用同目录下的 `TeknoParrotUi.exe` 启动游戏。  
  - TeknoParrot 项目与许可证请以官方仓库为准。

---

## 许可证全文（MIT）

本仓库中的原创代码按 MIT 许可证发布。许可证全文见 [LICENSE](LICENSE) 文件。
