# coverdata 目录 - 鹦鹉游戏封面图片

将你收集的 TeknoParrot 游戏封面图片放入此目录，然后运行：

```bash
python rename_covers_from_coverdata.py
```

脚本会自动将图片按 **profileId** 重命名并复制到 `Media/Covers/`，供 TeknoParrot BigBox 使用。

---

## 图片命名建议

### 推荐方式（优先匹配）

1. **直接使用 profileId 命名**（最可靠）
   - 例如：`WMMT6RR.png`、`TC5.jpg`、`VF5FSOffline.png`
   - profileId 即 UserProfiles 下的 XML 文件名（不含 .xml）

2. **使用游戏英文名**
   - 例如：`Time Crisis 5.png`、`Wangan Midnight Maximum Tune 6RR.jpg`
   - 脚本会从 GamePath 提取文件夹名进行匹配

3. **使用游戏中文名**
   - 例如：`化解危机5.png`、`头文字D6RR.jpg`
   - 支持中英文混合、空格、连字符会被忽略

### 多格式支持

- 支持 `.png`、`.jpg`、`.jpeg`、`.webp`
- 多个同名时优先选择 `.png`

---

## 使用流程

1. 创建 `coverdata` 目录（若不存在）
2. 将收集的封面图片放入其中
3. 在 TeknoParrotBigBox 目录下运行：
   ```bash
   python rename_covers_from_coverdata.py
   ```
4. 检查 `Media/Covers/` 确认生成结果
5. 若需预览不实际复制，可加 `--dry-run`：
   ```bash
   python rename_covers_from_coverdata.py --dry-run
   ```

---

## 图片使用建议

### 图片规格

- **推荐尺寸**：400×600 或 600×800（竖版封面）
- **比例**：约 2:3 或 3:4，与 BigBox 封面墙一致
- **格式**：PNG 或 JPG，文件大小建议 < 500KB

### 图片来源

- LaunchBox 的 Box - 3D 或 Arcade - Cabinet 封面
- 游戏官网、宣传图
- 街机框体照片（Arcade Cabinet 风格）
- 自制的统一风格封面

### 批量处理建议

- 若图片来自不同来源，建议先统一裁剪为相同比例
- 可使用 `rename_covers_from_box3d.py` 处理 LaunchBox 风格命名（游戏名-01.png）
- 本脚本 `rename_covers_from_coverdata.py` 专门处理「自由命名」的图片

### 未匹配的图片

运行后若有未匹配的图片，脚本会列出。可手动：
1. 将图片重命名为对应的 profileId（如 `WMMT6RR.png`）
2. 或参考 UserProfiles 下的 XML 文件名
3. 放入 coverdata 后重新运行脚本
