# TeknoParrot BigBox 前端（自用版）

一个独立的 WPF 启动前端，用于为 TeknoParrot 提供类似 LaunchBox / BigBox 风格的全屏封面 + 视频浏览界面。

> 运行环境：Windows + .NET Framework 4.6.2  
> 目录位置建议：与 `TeknoParrotUi.exe` 放在同一目录下（例如 `D:\code\TeknoParrotUI_MYSELF` 旁边）

---

## 功能概览

- 左侧：按 **类型/Genre 中文分类** 的游戏列表（含「★ 收藏」顶栏）
- 中间：当前分类下的 **游戏封面墙**
- 右侧：
  - 当前选中游戏的标题
  - 大封面图
  - 游戏预览视频（可静音/开声）
  - 可滚动的游戏介绍（来自 LaunchBox `Teknoparrot.xml`）
  - 操作按钮：
    - **开始游戏**：启动当前游戏（调用 bat / TeknoParrot）
    - **收藏游戏 / 取消收藏**：加入/移出「★ 收藏」分类
    - **返回鹦鹉**：退出 BigBox，并启动同目录下 `TeknoParrotUi.exe`
    - **设置 / 关于本程序**：预留入口 + 显示当前 BigBox 程序版本

---

## 目录结构约定

项目根目录（`D:\code\TeknoParrotBigBox`）下常用目录：

- `bat\`  
  启动脚本，通常为：
  - `XXX.bat`，首行包含 `--profile=XXXX.xml`  
  由 BigBox 用来推导 **profileId**。

- `Metadata\`  
  原 TeknoParrot 元数据（`*.json`），不再修改，只做读取：
  - `game_name` / `game_genre` / `icon_name` / `platform` / `release_year` 等。

- `launchbox_descriptions.json`  
  由 `extract_launchbox_descriptions.py` 从 `Teknoparrot.xml` 生成：  
  按 profileId 保存 LaunchBox 的标题、说明、开发商等，用于右侧介绍区。

- `covers\Box - 3D\`、`covers\Arcade - Cabinet\`  
  从 LaunchBox 复制来的原始封面图片，文件名为「游戏名-01.png」之类，  
  由 `rename_covers_from_box3d.py` 扫描并复制为：

- `Media\Covers\{profileId}.png` / `.jpg`  
  BigBox 真正使用的封面（profileId 命名）。

- `videos\`  
  从 LaunchBox 复制来的原始预览视频（`XXX-01.mp4` 之类），  
  由 `rename_videos_from_launchbox.py` 移动为：

- `Media\Videos\{profileId}.mp4`  
  BigBox 真正使用的预览视频（profileId 命名）。

- `favorites.json`  
  BigBox 运行时生成/更新，保存收藏列表：
  ```json
  {
    "favorites": [ "WMMT6RR", "VF5FSOffline", ... ]
  }
  ```

---

## 运行方式

1. 使用 Visual Studio 或 MSBuild 编译 `TeknoParrotBigBox.csproj`（.NET 4.6.2）。
2. 将生成的 `TeknoParrotBigBox.exe` 放到与你的 `TeknoParrotUi.exe` 同一目录（或保证 `bat/`、`Metadata/` 等路径正确）。
3. 准备数据：
   - `bat\`：TeknoParrot 的启动脚本（含 `--profile=XXXX.xml`）。
   - `Metadata\`：原 TeknoParrot JSON 配置。
   - `Teknoparrot.xml`：从 LaunchBox 导出的游戏数据。
   - 原始封面/视频复制到：
     - `covers\Box - 3D\` / `covers\Arcade - Cabinet\`
     - `videos\`
4. 在 BigBox 目录下执行一次：
   ```bash
   python extract_launchbox_descriptions.py
   python rename_covers_from_box3d.py
   python rename_videos_from_launchbox.py
   ```
5. 直接运行 `TeknoParrotBigBox.exe` 进入前端。

> 也可以从 TeknoParrot UI 右上角新增的 **BB 按钮** 一键切换到 BigBox（`TeknoParrotUi` 会自动退出，BigBox 会启动）。

---

## 快捷操作说明

- 键盘：
  - 方向键：切换分类 / 切换游戏
  - Enter：等同于点击「开始游戏」
  - Esc：退出 BigBox 程序
- 鼠标：
  - 左侧分类：点击切换分类
  - 中间封面：点击/滚轮上下，切换游戏
  - 右侧介绍区域：
    - 自动缓慢向下滚动
    - 鼠标移入时暂停自动滚动，可用滚轮自由浏览
- 预览视频：
  - 右下角小喇叭：切换有声/静音（只影响预览，不影响实际游戏）
  - 启动游戏时：立即停止预览视频（不在后台播放）

---

## 注意事项

- 本仓库通过 `.gitignore` 忽略了大部分大体积/生成目录：
  - `bin/`、`obj/`、`bat/`、`covers/`、`videos/`、`Media/`、`Icons/`、`Metadata/`、`Models/`
  - 建议只将源码和脚本纳入版本控制，封面与视频资源在本地维护。
- 如需扩展：
  - 可以在 `GameEntry` / `GameCategory` 中加入更多字段（平台、年份、评分等）。
  - 可在右侧按钮区挂接真正的设置窗口或更复杂的关于界面。

