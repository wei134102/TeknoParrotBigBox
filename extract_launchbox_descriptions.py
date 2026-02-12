#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
从 LaunchBox 导出的 Teknoparrot.xml 中提取游戏说明，
并生成一个新的 JSON 文件（不修改 Metadata 目录下的任何文件）。

输出文件: launchbox_descriptions.json
结构示例:
{
  "WMMT6RR": {
    "profile_id": "WMMT6RR",
    "bat_name": "Wangan Midnight Maximum Tune 6RR   競速-灣岸午夜極速6RR",
    "title": "太鼓達人 紅版",
    "notes": "......",
    "genre": "Music",
    "developer": "...",
    "publisher": "...",
    "release_date": "2016-07-14T08:00:00+08:00"
  },
  ...
}

使用方式（在 TeknoParrotBigBox 目录下运行）:

    python extract_launchbox_descriptions.py

前提约定:
1. Teknoparrot.xml 位于本脚本同级目录下。
2. bat 目录下的 .bat 文件名与 LaunchBox 中 ApplicationPath 的 bat 文件名一致
   例如:
     LaunchBox ApplicationPath: Emulators\\Teknoparrot\\bat\\WMMT6RR   競速-灣岸午夜極速6RR.bat
     本地 bat 目录: .\\bat\\WMMT6RR   競速-灣岸午夜極速6RR.bat
3. profileId 通过 bat 第一行里的 --profile=XXXX.xml 提取（例如 WMMT6RR）。
"""

from __future__ import annotations

import io
import json
import os
import sys
import xml.etree.ElementTree as ET
from typing import Dict, Optional


BASE_DIR = os.path.dirname(os.path.abspath(__file__))
LAUNCHBOX_XML = os.path.join(BASE_DIR, "Teknoparrot.xml")
BAT_DIR = os.path.join(BASE_DIR, "bat")
OUTPUT_JSON = os.path.join(BASE_DIR, "launchbox_descriptions.json")


class LaunchBoxGame(object):
    def __init__(
        self,
        title: str,
        notes: str,
        genre: str,
        developer: str,
        publisher: str,
        release_date: str,
    ):
        self.title = title
        self.notes = notes
        self.genre = genre
        self.developer = developer
        self.publisher = publisher
        self.release_date = release_date


def load_launchbox_games() -> Dict[str, LaunchBoxGame]:
    """
    解析 Teknoparrot.xml，按 bat 文件名（不含扩展名）建立索引:
        key = "WMMT6RR   競速-灣岸午夜極速6RR"
    """
    if not os.path.isfile(LAUNCHBOX_XML):
        print("未找到 Teknoparrot.xml:", LAUNCHBOX_XML)
        return {}

    print("读取 LaunchBox XML:", LAUNCHBOX_XML)
    # 明确使用 UTF-8 读取，避免中文乱码
    with io.open(LAUNCHBOX_XML, "r", encoding="utf-8") as f:
        data = f.read()

    # ElementTree 直接从字符串解析
    root = ET.fromstring(data)
    games_by_batname: Dict[str, LaunchBoxGame] = {}

    for game_elem in root.findall("Game"):
        app_path = (game_elem.findtext("ApplicationPath") or "").strip()
        if not app_path:
            continue

        bat_name = os.path.splitext(os.path.basename(app_path))[0]
        title = (game_elem.findtext("Title") or "").strip()
        notes = game_elem.findtext("Notes") or ""
        genre = (game_elem.findtext("Genre") or "").strip()
        developer = (game_elem.findtext("Developer") or "").strip()
        publisher = (game_elem.findtext("Publisher") or "").strip()
        release_date = (game_elem.findtext("ReleaseDate") or "").strip()

        games_by_batname[bat_name] = LaunchBoxGame(
            title=title,
            notes=notes,
            genre=genre,
            developer=developer,
            publisher=publisher,
            release_date=release_date,
        )

    print("从 LaunchBox 读取到游戏条目数:", len(games_by_batname))
    return games_by_batname


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


def main() -> int:
    lb_games = load_launchbox_games()
    if not lb_games:
        return 1

    if not os.path.isdir(BAT_DIR):
        print("未找到 bat 目录:", BAT_DIR)
        return 1

    # 结果字典: key = profileId
    result: Dict[str, Dict] = {}

    skipped_no_match = 0
    skipped_no_profile = 0

    for bat_file in sorted(os.listdir(BAT_DIR)):
        if not bat_file.lower().endswith(".bat"):
            continue

        bat_path = os.path.join(BAT_DIR, bat_file)
        bat_name = os.path.splitext(bat_file)[0]

        lb_game = lb_games.get(bat_name)
        if lb_game is None:
            skipped_no_match += 1
            continue

        profile_id = extract_profile_id_from_bat(bat_path)
        if not profile_id:
            skipped_no_profile += 1
            continue

        result[profile_id] = {
            "profile_id": profile_id,
            "bat_name": bat_name,
            "title": lb_game.title,
            "notes": lb_game.notes,
            "genre": lb_game.genre,
            "developer": lb_game.developer,
            "publisher": lb_game.publisher,
            "release_date": lb_game.release_date,
        }

    # 写出总 JSON 文件
    with io.open(OUTPUT_JSON, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=2)

    print("处理完成。")
    print("  已写出描述文件:", OUTPUT_JSON)
    print("  跳过（未在 LaunchBox 中找到对应 bat 名）的数量:", skipped_no_match)
    print("  跳过（bat 中未解析出 profileId）的数量:", skipped_no_profile)
    return 0


if __name__ == "__main__":
    sys.exit(main())

