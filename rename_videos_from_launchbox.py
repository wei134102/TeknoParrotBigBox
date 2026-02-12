#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
从 LaunchBox 风格的 videos 目录中，
根据 Teknoparrot.xml 和 bat 脚本，重命名并“移动”视频到:

    Media/Videos/{profileId}.mp4

注意：是移动（move），不是复制，以减少磁盘占用。

使用方式（在 TeknoParrotBigBox 目录下运行）:

    python rename_videos_from_launchbox.py

默认假设:
1. Teknoparrot.xml 在当前目录下
2. bat 脚本目录为 ./bat
3. 源视频目录为 ./videos
4. 目标视频目录为 ./Media/Videos

匹配规则:
- LaunchBox <Title> 对应 videos 里的文件名前缀，例如:
    Title: "化解危机 5"
    文件: "化解危机 5-01.mp4"
  会优先选 -01 结尾的视频，找不到再选任意同名前缀的视频。
- profileId 从 bat 第一行的 --profile=XXXX.xml 解析为 XXXX。
"""

from __future__ import annotations

import io
import os
import re
import shutil
import sys
import xml.etree.ElementTree as ET
from typing import Dict, Optional, List


BASE_DIR = os.path.dirname(os.path.abspath(__file__))
LAUNCHBOX_XML = os.path.join(BASE_DIR, "Teknoparrot.xml")
BAT_DIR = os.path.join(BASE_DIR, "bat")
VIDEOS_DIR = os.path.join(BASE_DIR, "videos")
DEST_VIDEOS_DIR = os.path.join(BASE_DIR, "Media", "Videos")


def normalize_title(name: str) -> str:
    """
    规范化标题/文件名前缀:
    - 去掉扩展名
    - 去掉类似 "-01" "-02" 的后缀
    - 去掉首尾空白
    """
    base = os.path.splitext(name)[0]
    m = re.match(r"^(.*?)-\d{2}$", base)
    if m:
        base = m.group(1)
    return base.strip()


def load_video_files() -> Dict[str, List[str]]:
    """
    扫描 videos 目录，按“标准化标题”索引所有视频路径。
    key = 标准化标题（去掉 -01 等）
    """
    mapping: Dict[str, List[str]] = {}
    if not os.path.isdir(VIDEOS_DIR):
        print("videos 目录不存在:", VIDEOS_DIR)
        return mapping

    for fname in os.listdir(VIDEOS_DIR):
        if not fname.lower().endswith((".mp4", ".m4v", ".mov", ".avi", ".mkv")):
            continue
        key = normalize_title(fname)
        full_path = os.path.join(VIDEOS_DIR, fname)
        mapping.setdefault(key, []).append(full_path)

    print("videos 中发现视频条目数(按标准化标题):", len(mapping))
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


def choose_best_video(paths: List[str]) -> str:
    """
    多个候选视频时，优先选择文件名中包含 "-01" 的那一个，否则返回第一个。
    """
    for p in paths:
        if re.search(r"-01\.(mp4|m4v|mov|avi|mkv)$", p, re.IGNORECASE):
            return p
    return paths[0]


def main() -> int:
    lb_games = load_launchbox_games()
    if not lb_games:
        return 1

    if not os.path.isdir(BAT_DIR):
        print("未找到 bat 目录:", BAT_DIR)
        return 1

    video_mapping = load_video_files()
    if not video_mapping:
        print("videos 目录中未发现任何视频:", VIDEOS_DIR)
        return 1

    if not os.path.isdir(DEST_VIDEOS_DIR):
        os.makedirs(DEST_VIDEOS_DIR)

    moved = 0
    skipped_no_video = 0
    skipped_no_bat = 0
    skipped_no_profile = 0

    for title, info in lb_games.items():
        norm_title = normalize_title(title)

        # 1) 找到对应视频
        candidates = video_mapping.get(norm_title)
        if not candidates:
            skipped_no_video += 1
            continue

        src_video = choose_best_video(candidates)

        # 2) 根据 ApplicationPath 找到对应 bat 文件
        app_path = info["app_path"]
        bat_name = os.path.basename(app_path)
        local_bat = os.path.join(BAT_DIR, bat_name)
        if not os.path.isfile(local_bat):
            skipped_no_bat += 1
            continue

        # 3) 从 bat 解析 profileId
        profile_id = extract_profile_id_from_bat(local_bat)
        if not profile_id:
            skipped_no_profile += 1
            continue

        # 4) 移动为 Media/Videos/{profileId}.mp4
        dest_ext = ".mp4"  # 统一输出为 .mp4
        dest_path = os.path.join(DEST_VIDEOS_DIR, profile_id + dest_ext)

        try:
            shutil.move(src_video, dest_path)
            moved += 1
        except Exception as exc:
            print("移动视频失败:", src_video, "->", dest_path, "错误:", exc)

    print("处理完成。")
    print("  成功移动视频数量:", moved)
    print("  跳过（找不到对应视频）的条目:", skipped_no_video)
    print("  跳过（找不到对应 bat 文件）的条目:", skipped_no_bat)
    print("  跳过（bat 中未解析出 profileId）的条目:", skipped_no_profile)

    return 0


if __name__ == "__main__":
    sys.exit(main())

