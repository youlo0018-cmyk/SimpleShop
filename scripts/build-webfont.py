"""生成自托管 webfont（后台与小程序共用同一份，保证所有用户看到一致的排版）。

为什么必须自带字体而不是靠系统字体栈：
spec 里的字体栈在**不同电脑上解析结果不同**。实测 Windows 上
-apple-system / SF Pro Text / PingFang SC 全部不存在，所有字符落到
Microsoft YaHei —— 它是中文黑体，数字又宽又方（8 位数字 188px，
Segoe UI 只要 173px），满屏数字的后台因此「一眼方正」。
换台 Mac 字体又完全不一样。对**网站**来说这不可接受：
同一份后台，在不同人电脑上应该是同一张脸。

用法：
    python scripts/build-webfont.py

依赖：pip install fonttools brotli
源字体放 deploy/fonts/ 下（Noto Sans SC 可变字体，OFL 授权可再分发）。
"""

import os
import sys

from fontTools import subset
from fontTools.ttLib import TTFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC_DIR = os.path.join(ROOT, "deploy", "fonts")
OUT_DIR = os.path.join(ROOT, "apps", "admin-vue", "public", "fonts")


def gb2312_charset() -> set:
    """枚举 GB2312 全部汉字与符号。

    不用「常用 3500 字」的人工清单：那份清单换个词就可能漏字，
    漏掉的字会静默回退到系统字体 —— 正是这次要消灭的问题。
    GB2312 由编码标准定义，完整且可复现。
    """
    chars = set()
    for high in range(0xA1, 0xFF):
        for low in range(0xA1, 0xFF):
            try:
                chars.add(bytes([high, low]).decode("gb2312"))
            except UnicodeDecodeError:
                continue
    return chars


def build_charset() -> set:
    """ASCII + 拉丁补充 + 标点 + 全角 + GB2312 汉字。"""
    chars = set()
    # 可打印 ASCII：数字、字母、常用符号
    chars.update(chr(c) for c in range(0x20, 0x7F))
    # 拉丁补充 / 常用符号 / 箭头 / 几何
    chars.update(chr(c) for c in range(0xA0, 0x100))
    chars.update(chr(c) for c in range(0x2000, 0x206F))
    chars.update(chr(c) for c in range(0x20A0, 0x20BF))
    chars.update(chr(c) for c in range(0x2190, 0x21FF))
    chars.update(chr(c) for c in range(0x2200, 0x22FF))
    chars.update(chr(c) for c in range(0x2460, 0x24FF))  # 带圈数字
    # CJK 符号与标点、全角字符
    chars.update(chr(c) for c in range(0x3000, 0x3040))
    chars.update(chr(c) for c in range(0xFF00, 0xFFF0))
    # 常用符号（℃、㎡、§、№ 等 UI 里偶尔出现）
    chars.update("℃㎡§№×÷±≈≤≥≠∞°′″‰€£¥¢©®™§¶")
    chars.update(gb2312_charset())
    return chars


def main() -> int:
    src = os.path.join(SRC_DIR, "NotoSansSC-VF.ttf")
    if not os.path.exists(src):
        print(f"找不到源字体: {src}")
        print("请把 Noto Sans SC 可变字体（OFL 授权）放到 deploy/fonts/ 下。")
        return 1

    os.makedirs(OUT_DIR, exist_ok=True)
    out = os.path.join(OUT_DIR, "NotoSansSC-subset.woff2")

    text = "".join(sorted(build_charset()))
    print(f"字符集: {len(text)} 个字符")

    font = TTFont(src)
    options = subset.Options()
    options.flavor = "woff2"
    options.desubroutinize = True
    options.layout_features = ["kern", "liga", "locl", "ccmp", "mark", "mkmk"]
    options.name_IDs = ["*"]
    options.notdef_outline = True
    options.recalc_bounds = True
    options.drop_tables = ["DSIG"]
    # 保留可变字体的 wght 轴：一个文件覆盖 400~900，
    # 比按字重各切一份省一大半体积
    options.retain_axes = ["wght"]

    subsetter = subset.Subsetter(options=options)
    subsetter.populate(text=text)
    subsetter.subset(font)
    font.flavor = "woff2"
    font.save(out)

    size_mb = os.path.getsize(out) / 1024 / 1024
    src_mb = os.path.getsize(src) / 1024 / 1024
    print(f"源字体 {src_mb:.2f} MB -> 子集 {size_mb:.2f} MB  ({out})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
