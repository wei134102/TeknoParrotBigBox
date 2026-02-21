#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
从 LaunchBox 风格的 Box - 3D 封面目录中，
根据 Teknoparrot.xml 和 bat 脚本，复制并重命名封面到:

    Media/Covers/{profileId}.png

不修改原来的 covers 目录内容，只是复制到 Media/Covers。

使用方式（在 TeknoParrotBigBox 目录下运行）:

    python rename_covers_from_box3d.py

默认假设:
1. Teknoparrot.xml       在当前目录下
2. bat 脚本目录          为 ./bat
3. Box - 3D 封面目录     为 ./covers/Box - 3D
4. 目标封面目录          为 ./Media/Covers

匹配规则:
- LaunchBox <Title> 对应 Box - 3D 里的文件名开头，比如:
    Title: "化解危机 5"
    文件: "化解危机 5-01.png" / "化解危机 5-02.png"
  会优先选 -01，找不到再选任意同名前缀的文件。
- profileId 从 bat 第一行的 --profile=XXXX.xml 解析为 XXXX。
- 若没有 bat 目录，则从 UserProfiles/UserProfiles_by_genre 的 XML 与（可选）launchbox_descriptions.json
  按「标题/游戏名」匹配 profileId。
"""

from __future__ import annotations

import io
import json
import os
import re
import shutil
import sys
import unicodedata
import xml.etree.ElementTree as ET
from typing import Dict, Optional, List, Tuple


BASE_DIR = os.path.dirname(os.path.abspath(__file__))
LAUNCHBOX_XML = os.path.join(BASE_DIR, "Teknoparrot.xml")
BAT_DIR = os.path.join(BASE_DIR, "bat")
USER_PROFILES_DIRS = [
    os.path.join(BASE_DIR, "UserProfiles"),
    os.path.join(BASE_DIR, "UserProfiles_by_genre"),
]
LAUNCHBOX_DESCRIPTIONS_JSON = os.path.join(BASE_DIR, "launchbox_descriptions.json")
BOX3D_DIR = os.path.join(BASE_DIR, "covers", "Box - 3D")
ARCADE_DIR = os.path.join(BASE_DIR, "covers", "Arcade - Cabinet")
DEST_COVERS_DIR = os.path.join(BASE_DIR, "Media", "Covers")


def normalize_title(name: str) -> str:
    """
    规范化标题/文件名前缀:
    - 去掉扩展名
    - 去掉类似 "-01" "-02" 的后缀
    - 去掉首尾空白
    """
    base = os.path.splitext(name)[0]
    # 去掉末尾的 -数字 例如 "-01" "-02"
    m = re.match(r"^(.*?)-\d{2}$", base)
    if m:
        base = m.group(1)
    return base.strip()


def normalize_for_match(s: str) -> str:
    """规范化字符串用于匹配：去空格、转小写、去标点、统一 Unicode"""
    if not s:
        return ""
    s = unicodedata.normalize("NFKC", s)
    s = re.sub(r"[\s\-_.]+", "", s.lower())
    s = re.sub(r"[^\w\u4e00-\u9fff]", "", s)
    return s


def _game_name_from_path(game_path: str) -> Optional[str]:
    """从 GamePath 提取游戏文件夹名。"""
    if not game_path:
        return None
    path = game_path.replace("/", "\\")
    parts = path.split("\\")
    for i in range(len(parts) - 1, -1, -1):
        p = parts[i].strip()
        if p and not p.lower().endswith((".exe", ".bat")):
            return p
    return None


def load_title_to_profile_without_bat() -> Dict[str, str]:
    """
    在无 bat 时使用：从 UserProfiles 与 launchbox_descriptions.json 构建
    normalized_title -> profileId，用于按 LaunchBox 标题匹配 profileId。
    """
    mapping: Dict[str, str] = {}

    # 1) UserProfiles：profileId + GamePath 文件夹名 -> profileId
    for profiles_dir in USER_PROFILES_DIRS:
        if not os.path.isdir(profiles_dir):
            continue
        for root, _dirs, files in os.walk(profiles_dir):
            for f in files:
                if not f.lower().endswith(".xml"):
                    continue
                profile_id = os.path.splitext(f)[0]
                if not profile_id:
                    continue
                mapping[normalize_for_match(profile_id)] = profile_id
                xml_path = os.path.join(root, f)
                try:
                    tree = ET.parse(xml_path)
                    for elem in tree.getroot().iter():
                        if elem.tag == "GamePath" and elem.text:
                            name = _game_name_from_path(elem.text.strip())
                            if name:
                                mapping[normalize_for_match(name)] = profile_id
                            break
                except Exception:
                    pass

    # 2) launchbox_descriptions.json：title -> profileId
    if os.path.isfile(LAUNCHBOX_DESCRIPTIONS_JSON):
        try:
            with io.open(LAUNCHBOX_DESCRIPTIONS_JSON, "r", encoding="utf-8") as fp:
                data = json.load(fp)
            for pid, desc in (data or {}).items():
                if not isinstance(desc, dict):
                    continue
                title = (desc.get("title") or "").strip()
                if title:
                    mapping[normalize_for_match(title)] = pid
        except Exception:
            pass

    return mapping


def load_image_dir(root_dir: str) -> Dict[str, List[str]]:
    """
    扫描指定封面目录，按“标准化标题”索引所有图片路径。
    key = 标题前缀（去掉 -01 等）
    """
    mapping: Dict[str, List[str]] = {}
    if not os.path.isdir(root_dir):
        return mapping

    for fname in os.listdir(root_dir):
        if not fname.lower().endswith((".png", ".jpg", ".jpeg")):
            continue
        key = normalize_title(fname)
        full_path = os.path.join(root_dir, fname)
        mapping.setdefault(key, []).append(full_path)

    return mapping


def load_launchbox_games() -> Dict[str, Dict[str, str]]:
    """
    从 Teknoparrot.xml 读取:
        Title, ApplicationPath
    返回字典:
        key = Title (原样)
        value = {"title": Title, "app_path": ApplicationPath}
    """
    if not os.path.isfile(LAUNCHBOX_XML):
        print("未找到 Teknoparrot.xml:", LAUNCHBOX_XML)
        return {}

    with io.open(LAUNCHBOX_XML, "r", encoding="utf-8") as f:
        data = f.read()

    root = ET.fromstring(data)
    result: Dict[str, Dict[str, str]] = {}

    for game_elem in root.findall("Game"):
        title = (game_elem.findtext("Title") or "").strip()
        app_path = (game_elem.findtext("ApplicationPath") or "").strip()
        if not title or not app_path:
            continue
        result[title] = {
            "title": title,
            "app_path": app_path,
        }

    print("LaunchBox 中读取到游戏条目数:", len(result))
    return result


def extract_profile_id_from_bat(bat_path: str) -> Optional[str]:
    """
    从 bat 第一行解析 TeknoParrot profileId:
        START ..\\TeknoParrotUi.exe --profile=WMMT6RR.xml
    返回 "WMMT6RR" 或 None。
    """
    try:
        with io.open(bat_path, "r", encoding="utf-8", errors="ignore") as f:
            first_line = f.readline()
    except IOError:
        return None

    marker = "--profile="
    idx = first_line.lower().find(marker)
    if idx < 0:
        return None

    start = idx + len(marker)
    end = first_line.lower().find(".xml", start)
    if end <= start:
        return None
    return first_line[start:end].strip()


def choose_best_image(paths: List[str]) -> str:
    """
    多个候选封面时，优先选择文件名中包含 "-01" 的那一个，否则返回第一个。
    """
    for p in paths:
        if re.search(r"-01\.(png|jpg|jpeg)$", p, re.IGNORECASE):
            return p
    return paths[0]


def main() -> int:
    lb_games = load_launchbox_games()
    if not lb_games:
        return 1

    use_bat = os.path.isdir(BAT_DIR)
    title_to_profile: Dict[str, str] = {}
    if not use_bat:
        print("未找到 bat 目录，改用 UserProfiles / launchbox_descriptions 按标题匹配 profileId")
        title_to_profile = load_title_to_profile_without_bat()
        if not title_to_profile:
            print("也未找到 UserProfiles 或 launchbox_descriptions.json，无法解析 profileId")
            return 1
        print("已加载标题->profileId 映射数量:", len(set(title_to_profile.values())))

    # 先加载 Box - 3D 封面
    mapping: Dict[str, List[str]] = {}
    box3d_mapping = load_image_dir(BOX3D_DIR)
    if box3d_mapping:
        print("Box - 3D 中发现封面条目数(按标准化标题):", len(box3d_mapping))
        mapping.update(box3d_mapping)

    # 再加载 Arcade - Cabinet 作为补充（如果 Box - 3D 没有对应 key，就用这里的）
    arcade_mapping = load_image_dir(ARCADE_DIR)
    if arcade_mapping:
        print("Arcade - Cabinet 中发现封面条目数(按标准化标题):", len(arcade_mapping))
        for k, v in arcade_mapping.items():
            if k in mapping:
                # 已有 Box - 3D，追加候选
                mapping[k].extend(v)
            else:
                mapping[k] = v

    if not mapping:
        print("Box - 3D / Arcade - Cabinet 目录中未发现任何图片")
        return 1

    if not os.path.isdir(DEST_COVERS_DIR):
        os.makedirs(DEST_COVERS_DIR)

    copied = 0
    skipped_no_image = 0
    skipped_no_bat = 0
    skipped_no_profile = 0

    for title, info in lb_games.items():
        norm_title = normalize_title(title)

        # 1) 找到对应的封面图片（先 Box - 3D，再 Arcade - Cabinet）
        candidates = mapping.get(norm_title)
        if not candidates:
            skipped_no_image += 1
            continue

        src_image = choose_best_image(candidates)

        # 2) 解析 profileId：优先 bat，否则按标题从 UserProfiles/launchbox_descriptions 匹配
        profile_id = None
        if use_bat:
            app_path = info["app_path"]
            bat_name = os.path.basename(app_path)
            local_bat = os.path.join(BAT_DIR, bat_name)
            if not os.path.isfile(local_bat):
                skipped_no_bat += 1
                continue
            profile_id = extract_profile_id_from_bat(local_bat)
            if not profile_id:
                skipped_no_profile += 1
                continue
        else:
            norm_key = normalize_for_match(norm_title)
            profile_id = title_to_profile.get(norm_key)
            if not profile_id:
                skipped_no_profile += 1
                continue

        # 3) 复制为 Media/Covers/{profileId}.png
        dest_ext = os.path.splitext(src_image)[1].lower()
        if dest_ext not in [".png", ".jpg", ".jpeg"]:
            dest_ext = ".png"

        dest_path = os.path.join(DEST_COVERS_DIR, profile_id + dest_ext)
        try:
            shutil.copy2(src_image, dest_path)
            copied += 1
        except Exception as exc:
            print("复制封面失败:", src_image, "->", dest_path, "错误:", exc)

    print("处理完成。")
    print("  成功复制封面数量:", copied)
    print("  跳过（找不到对应图片）的条目:", skipped_no_image)
    print("  跳过（找不到对应 bat 文件）的条目:", skipped_no_bat)
    print("  跳过（bat 中未解析出 profileId）的条目:", skipped_no_profile)

    return 0


if __name__ == "__main__":
    sys.exit(main())

