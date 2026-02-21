#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
根据 Metadata 目录下的游戏配置文件（*.json）中的 game_name，
扫描「脚本所在目录及子目录」下的图片，将最接近 game_name 的图片就地重命名为：
  配置文件名称.扩展名

使用方式：
  1. 把本脚本复制到你的图片目录
  2. 在该目录下运行（需指定 Metadata 路径）:
     python rename_covers_from_metadata.py --metadata "D:\\path\\to\\Metadata"
  3. 图片会在原位置被重命名，不复制、不移动

  python rename_covers_from_metadata.py --metadata "D:\\path\\to\\Metadata" --dry-run   # 预览
"""

from __future__ import annotations

import argparse
import difflib
import io
import json
import os
import re
import sys
import unicodedata
from typing import Dict, List, Optional, Tuple

# 支持的图片扩展名
IMAGE_EXTENSIONS = (".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif")


def normalize_for_match(s: str) -> str:
    """规范化字符串用于相似度比较：去空格/标点、转小写、统一 Unicode"""
    if not s:
        return ""
    s = unicodedata.normalize("NFKC", s)
    s = re.sub(r"[\s\-_.,;:()\[\]']+", " ", s)
    s = s.strip().lower()
    s = re.sub(r"[^\w\u4e00-\u9fff]", "", s)
    return s


def load_metadata(metadata_dir: str) -> Tuple[Dict[str, str], List[str]]:
    """
    扫描 Metadata 目录（含子目录）下所有 *.json，读取 game_name。
    返回: ( { 配置文件名称: game_name }, 扫描过的目录列表 )
    """
    result: Dict[str, str] = {}
    dirs_scanned: List[str] = []
    if not os.path.isdir(metadata_dir):
        return result, dirs_scanned
    for root, _dirs, files in os.walk(metadata_dir):
        dirs_scanned.append(os.path.abspath(root))
        for f in files:
            if not f.lower().endswith(".json"):
                continue
            config_name = os.path.splitext(f)[0]
            if not config_name:
                continue
            path = os.path.join(root, f)
            try:
                with io.open(path, "r", encoding="utf-8") as fp:
                    data = json.load(fp)
                if isinstance(data, dict):
                    name = (data.get("game_name") or "").strip()
                    if name:
                        result[config_name] = name
            except Exception:
                pass
    return result, dirs_scanned


def collect_images(root_dir: str) -> Tuple[List[Tuple[str, str]], List[str]]:
    """
    递归扫描 root_dir 下所有图片。
    返回: ( [ (完整路径, 文件名无扩展名), ... ], 扫描过的目录列表 )
    """
    out: List[Tuple[str, str]] = []
    dirs_scanned: List[str] = []
    if not os.path.isdir(root_dir):
        return out, dirs_scanned
    root_dir = os.path.abspath(root_dir)
    for dirpath, _dirnames, filenames in os.walk(root_dir):
        dirs_scanned.append(os.path.abspath(dirpath))
        for f in filenames:
            ext = os.path.splitext(f)[1].lower()
            if ext not in IMAGE_EXTENSIONS:
                continue
            full = os.path.join(dirpath, f)
            base = os.path.splitext(f)[0]
            out.append((full, base))
    return out, dirs_scanned


def similarity(a: str, b: str) -> float:
    """返回 0~1 的相似度。使用规范化后的字符串。"""
    an = normalize_for_match(a)
    bn = normalize_for_match(b)
    if not an or not bn:
        return 0.0
    if an == bn:
        return 1.0
    return difflib.SequenceMatcher(None, an, bn).ratio()


def best_matching_image(
    game_name: str,
    images: List[Tuple[str, str]],
    used_paths: set,
    min_ratio: float = 0.25,
) -> Optional[Tuple[str, str]]:
    """
    在未使用的图片中，找出与 game_name 相似度最高的那张。
    返回 (完整路径, 文件名无扩展名) 或 None。
    """
    best_path: Optional[str] = None
    best_base: Optional[str] = None
    best_score = min_ratio
    for path, base in images:
        if path in used_paths:
            continue
        score = similarity(game_name, base)
        if score > best_score:
            best_score = score
            best_path = path
            best_base = base
    if best_path is None:
        return None
    return (best_path, best_base)


def main() -> int:
    # 脚本所在目录 = 图片目录（用户把脚本复制到这里运行）
    script_dir = os.path.dirname(os.path.abspath(__file__))

    parser = argparse.ArgumentParser(
        description="按 Metadata 中 game_name 匹配图片，就地重命名为配置文件名称"
    )
    parser.add_argument(
        "--metadata",
        default=r"D:\code\TeknoParrotBigBox\Metadata",
        help="Metadata 目录路径（内含 *.json，可含子目录）",
    )
    parser.add_argument(
        "--images-dir",
        default=script_dir,
        help="扫描图片的根目录（会递归子目录），默认=脚本所在目录",
    )
    parser.add_argument(
        "--min-ratio",
        type=float,
        default=0.25,
        help="最低相似度 0~1，低于此不匹配（默认 0.25）",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="仅打印将要执行的操作，不实际重命名",
    )
    args = parser.parse_args()

    # ---------- 加载 Metadata ----------
    metadata, meta_dirs = load_metadata(args.metadata)
    if not metadata:
        print("未在 Metadata 目录中找到任何带 game_name 的 JSON:", args.metadata)
        return 1

    print("======== 扫描 Metadata ========")
    print("Metadata 目录:", os.path.abspath(args.metadata))
    print("扫描过的子目录 (共 %d 个):" % len(meta_dirs))
    for d in sorted(meta_dirs):
        print("  -", d)
    print("找到的配置文件 (共 %d 个):" % len(metadata))
    for config_name, game_name in sorted(metadata.items()):
        short_gn = game_name[:50] + "..." if len(game_name) > 50 else game_name
        print("  - %s => game_name: %s" % (config_name, short_gn))
    print()

    # ---------- 扫描图片 ----------
    images, img_dirs = collect_images(args.images_dir)
    if not images:
        print("未在图片目录中发现任何图片:", os.path.abspath(args.images_dir))
        return 1

    print("======== 扫描图片 ========")
    print("图片根目录:", os.path.abspath(args.images_dir))
    print("扫描过的子目录 (共 %d 个):" % len(img_dirs))
    for d in sorted(img_dirs):
        count = sum(1 for p, _ in images if os.path.abspath(os.path.dirname(p)) == d)
        print("  - %s  (%d 张)" % (d, count))
    print("找到的图片 (共 %d 张):" % len(images))
    for path, base in sorted(images, key=lambda x: x[0].lower()):
        print("  -", path)
    print()

    used_paths: set = set()
    done = 0
    skipped = 0
    renamed_list: List[Tuple[str, str]] = []  # (原路径, 新路径)

    # 按 game_name 长度降序处理，优先把长名（更具体）的游戏先匹配
    for config_name, game_name in sorted(metadata.items(), key=lambda x: -len(x[1])):
        best = best_matching_image(game_name, images, used_paths, args.min_ratio)
        if not best:
            skipped += 1
            continue
        src_path, _ = best
        used_paths.add(src_path)
        ext = os.path.splitext(src_path)[1].lower()
        dest_name = config_name + ext
        dest_path = os.path.join(os.path.dirname(src_path), dest_name)

        if args.dry_run:
            print("[预览] 重命名:", src_path, "->", dest_name, "  (game_name:", game_name[:40] + "..." if len(game_name) > 40 else game_name, ")")
            done += 1
            renamed_list.append((src_path, dest_path))
            continue

        if os.path.normpath(src_path) == os.path.normpath(dest_path):
            continue
        if os.path.exists(dest_path) and os.path.abspath(dest_path) != os.path.abspath(src_path):
            print("跳过（目标已存在）:", dest_path)
            continue
        try:
            os.rename(src_path, dest_path)
            done += 1
            renamed_list.append((src_path, dest_path))
        except Exception as e:
            print("失败:", src_path, "->", dest_path, e)

    # ---------- 重命名明细与汇总 ----------
    print()
    print("======== 重命名明细 ========")
    if renamed_list:
        for src, dest in renamed_list:
            print("  %s  ->  %s" % (src, dest))
    else:
        print("  (无)")

    print()
    print("======== 处理完成 ========")
    print("  成功重命名: %d" % done)
    print("  未匹配（相似度不足或无可用图片）: %d" % skipped)
    return 0


if __name__ == "__main__":
    sys.exit(main())
