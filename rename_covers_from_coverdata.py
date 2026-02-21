#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
从 coverdata 目录中的图片，根据 TeknoParrot 游戏配置文件（UserProfiles/*.xml），
重命名并复制到 Media/Covers/{profileId}.png

适用于：你收集了一批鹦鹉游戏封面图片，文件名可能是游戏名、profileId 或其它格式，
希望批量转换为 BigBox 可识别的 profileId 命名。

使用方式（在 TeknoParrotBigBox 目录下运行）:

    python rename_covers_from_coverdata.py
    python rename_covers_from_coverdata.py --coverdata ./my_images
    python rename_covers_from_coverdata.py --dry-run

默认假设:
1. 源图片目录: ./coverdata
2. 游戏配置目录: ./UserProfiles 或 ./UserProfiles_by_genre（递归）
3. 目标封面目录: ./Media/Covers

匹配规则（按优先级）:
- 精确匹配: 图片文件名（去扩展名）= profileId，如 WMMT6RR.png -> WMMT6RR
- 路径匹配: 从 XML 的 GamePath 提取游戏文件夹名，如 "Time Crisis 5" 与图片名模糊匹配
- 中文/英文: 支持游戏名与图片名的多种变体
"""

from __future__ import annotations

import argparse
import io
import os
import re
import shutil
import sys
import unicodedata
import xml.etree.ElementTree as ET
from typing import Dict, List, Optional, Tuple


def normalize_for_match(s: str) -> str:
    """规范化字符串用于模糊匹配：去空格、转小写、去标点、统一 Unicode"""
    if not s:
        return ""
    s = unicodedata.normalize("NFKC", s)
    s = re.sub(r"[\s\-_.]+", "", s.lower())
    s = re.sub(r"[^\w\u4e00-\u9fff]", "", s)
    return s


def extract_game_name_from_path(game_path: str) -> Optional[str]:
    r"""
    从 GamePath 提取游戏文件夹名，例如:
    ..\..\games\TeknoParrot\shooter\Time Crisis 5\RSLauncher.exe -> Time Crisis 5
    """
    if not game_path:
        return None
    path = game_path.replace("/", "\\")
    parts = path.split("\\")
    for i in range(len(parts) - 1, -1, -1):
        p = parts[i].strip()
        if p and not p.lower().endswith((".exe", ".bat")):
            return p
    return None


def load_profiles(profiles_dirs: List[str]) -> Dict[str, Dict[str, str]]:
    """
    扫描 UserProfiles 目录，加载所有 profile XML。
    返回: { profileId: { "game_name": "从 GamePath 提取", "path": "..." } }
    """
    result: Dict[str, Dict[str, str]] = {}
    for base_dir in profiles_dirs:
        if not os.path.isdir(base_dir):
            continue
        for root, _dirs, files in os.walk(base_dir):
            for f in files:
                if not f.lower().endswith(".xml"):
                    continue
                xml_path = os.path.join(root, f)
                profile_id = os.path.splitext(f)[0]
                if not profile_id:
                    continue
                try:
                    tree = ET.parse(xml_path)
                    game_path = None
                    for elem in tree.getroot().iter():
                        if elem.tag == "GamePath" and elem.text:
                            game_path = elem.text.strip()
                            break
                    game_name = extract_game_name_from_path(game_path) if game_path else None
                    result[profile_id] = {
                        "game_name": game_name or "",
                        "path": xml_path,
                    }
                except Exception:
                    pass
    return result


def load_images(coverdata_dir: str) -> Dict[str, List[str]]:
    """
    扫描 coverdata 目录，按「规范化文件名」索引图片路径。
    key = normalize_for_match(文件名去扩展名)
    value = [ 完整路径列表 ]
    """
    mapping: Dict[str, List[str]] = {}
    if not os.path.isdir(coverdata_dir):
        return mapping
    for fname in os.listdir(coverdata_dir):
        if not fname.lower().endswith((".png", ".jpg", ".jpeg", ".webp")):
            continue
        base = os.path.splitext(fname)[0]
        key = normalize_for_match(base)
        full_path = os.path.join(coverdata_dir, fname)
        mapping.setdefault(key, []).append(full_path)
    return mapping


def choose_best_image(paths: List[str]) -> str:
    """多个候选时，优先 .png，其次 .jpg"""
    for ext in (".png", ".jpg", ".jpeg", ".webp"):
        for p in paths:
            if p.lower().endswith(ext):
                return p
    return paths[0]


def find_matching_image(
    profile_id: str,
    game_name: str,
    image_mapping: Dict[str, List[str]],
) -> Optional[str]:
    """
    为 profileId 找到匹配的图片。
    优先级: 1) 精确 profileId  2) 游戏名  3) 模糊匹配
    """
    # 1) 精确匹配 profileId
    key_id = normalize_for_match(profile_id)
    if key_id in image_mapping:
        return choose_best_image(image_mapping[key_id])

    # 2) 游戏名匹配
    if game_name:
        key_name = normalize_for_match(game_name)
        if key_name in image_mapping:
            return choose_best_image(image_mapping[key_name])

    # 3) 模糊：图片 key 包含 profileId 或 profileId 包含图片 key
    for img_key, paths in image_mapping.items():
        if key_id in img_key or img_key in key_id:
            return choose_best_image(paths)
        if game_name and key_name:
            if key_name in img_key or img_key in key_name:
                return choose_best_image(paths)

    return None


def main() -> int:
    parser = argparse.ArgumentParser(
        description="将 coverdata 中的图片按 profileId 重命名并复制到 Media/Covers"
    )
    parser.add_argument(
        "--coverdata",
        default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "coverdata"),
        help="源图片目录（默认: ./coverdata）",
    )
    parser.add_argument(
        "--profiles",
        nargs="+",
        default=None,
        help="游戏配置目录，默认: ./UserProfiles ./UserProfiles_by_genre",
    )
    parser.add_argument(
        "--dest",
        default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "Media", "Covers"),
        help="目标封面目录（默认: ./Media/Covers）",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="仅预览，不实际复制",
    )
    parser.add_argument(
        "--move",
        action="store_true",
        help="移动而非复制（减少磁盘占用）",
    )
    args = parser.parse_args()

    base_dir = os.path.dirname(os.path.abspath(__file__))
    if args.profiles is None:
        args.profiles = [
            os.path.join(base_dir, "UserProfiles"),
            os.path.join(base_dir, "UserProfiles_by_genre"),
        ]

    profiles = load_profiles(args.profiles)
    if not profiles:
        print("未找到任何游戏配置文件（UserProfiles/*.xml）")
        print("请确保 UserProfiles 或 UserProfiles_by_genre 目录存在且包含 .xml 文件")
        return 1

    print("已加载 profile 数量:", len(profiles))

    image_mapping = load_images(args.coverdata)
    if not image_mapping:
        print("coverdata 目录中未发现任何图片:", args.coverdata)
        print("请将收集的封面图片放入 coverdata 目录后重试")
        return 1

    print("coverdata 中发现图片条目数:", len(image_mapping))

    if not os.path.isdir(args.dest) and not args.dry_run:
        os.makedirs(args.dest)

    copied = 0
    matched_by_id = 0
    matched_by_name = 0
    unmatched_images: List[str] = []

    for profile_id, info in profiles.items():
        game_name = info.get("game_name", "")
        src_image = find_matching_image(profile_id, game_name, image_mapping)
        if not src_image:
            continue

        ext = os.path.splitext(src_image)[1].lower()
        if ext not in (".png", ".jpg", ".jpeg", ".webp"):
            ext = ".png"
        dest_path = os.path.join(args.dest, profile_id + ext)

        if args.dry_run:
            print("[预览] {} -> {}".format(os.path.basename(src_image), os.path.basename(dest_path)))
            copied += 1
            continue

        if args.move and not os.path.exists(src_image):
            continue  # 已被前一个 profile 移动，跳过

        try:
            if args.move:
                shutil.move(src_image, dest_path)
            else:
                shutil.copy2(src_image, dest_path)
            copied += 1
            key_id = normalize_for_match(profile_id)
            key_name = normalize_for_match(game_name)
            img_key = normalize_for_match(os.path.splitext(os.path.basename(src_image))[0])
            if key_id == img_key:
                matched_by_id += 1
            else:
                matched_by_name += 1
        except Exception as exc:
            print("处理失败:", src_image, "->", dest_path, "错误:", exc)

    # 检查未匹配的图片
    used_paths = set()
    for profile_id, info in profiles.items():
        src = find_matching_image(profile_id, info.get("game_name", ""), image_mapping)
        if src:
            used_paths.add(os.path.normpath(src))
    for img_key, paths in image_mapping.items():
        for p in paths:
            if os.path.normpath(p) not in used_paths:
                unmatched_images.append(p)

    print("\n处理完成。")
    print("  成功处理数量:", copied)
    print("  - 按 profileId 精确匹配:", matched_by_id)
    print("  - 按游戏名/模糊匹配:", matched_by_name)
    if unmatched_images:
        print("  未匹配的图片（可手动重命名为 profileId 后放入 coverdata 再运行）:")
        for p in unmatched_images[:20]:
            print("    -", os.path.basename(p))
        if len(unmatched_images) > 20:
            print("    ... 共", len(unmatched_images), "个")

    return 0


if __name__ == "__main__":
    sys.exit(main())
