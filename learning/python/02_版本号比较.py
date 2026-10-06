"""
学习版 02：版本号比较
对应 C# 代码：src/UpdateHelper.Core/Updates/AppVersion.cs

为什么不能直接比较字符串："1.10" 和 "1.9" 按字符串比，"1.10" 更小（因为 '1' < '9'），
但按版本号它更大。所以要拆成一段一段的数字来比。
运行：python learning/python/02_版本号比较.py
"""
import re

SEPARATORS = re.compile(r"[.\-_+]")


def parse(text):
    """把版本号拆成 [(数字, 后缀), ...]；拆不了返回 None。返回 (段列表, 是否是范围写法)。"""
    if not text or not text.strip():
        return None
    s = text.strip()
    approximate = s[0] in "<>"          # winget 的 "< 3.10.8" 表示只知道大概范围
    if approximate:
        s = s[1:].strip()
    if s[:1] in ("v", "V"):
        s = s[1:]

    parts = []
    for segment in filter(None, SEPARATORS.split(s)):
        m = re.match(r"(\d*)(.*)", segment)
        digits, suffix = m.group(1), m.group(2)
        if not digits:
            if not parts:
                return None             # 第一段必须以数字开头
            parts.append((0, segment))
        else:
            parts.append((int(digits), suffix))
    return (parts, approximate) if parts else None


def compare(a, b):
    """a < b 返回 -1，相等 0，a > b 返回 1（只比较段，不管是否范围写法）。"""
    pa, pb = a[0], b[0]
    for i in range(max(len(pa), len(pb))):
        x = pa[i] if i < len(pa) else (0, "")
        y = pb[i] if i < len(pb) else (0, "")
        if x[0] != y[0]:
            return -1 if x[0] < y[0] else 1
        if x[1] != y[1]:
            if x[1] == "":              # 没有后缀的是正式版，更新
                return 1
            if y[1] == "":
                return -1
            return -1 if x[1].lower() < y[1].lower() else 1
    return 0


def main():
    cases = [
        ("1.10", "1.9"),                       # 按数字比
        ("3.1.12", "3.1.41"),                  # 网易云
        ("2022.10", "2026.07-1"),              # Anaconda：大版本变了
        ("1.0.0", "1.0.0-beta"),               # 正式版比预览版新
        ("2021.3.45f2c1", "2021.3.45f1"),      # Unity 的后缀
        ("< 3.10.8", "3.14.7"),                # 范围写法
        ("unknown", "2.0"),                    # 拆不了
    ]
    symbol = {-1: "<", 0: "=", 1: ">"}
    for a, b in cases:
        pa, pb = parse(a), parse(b)
        if pa is None or pb is None:
            print(f"{a!r:>18} ? {b!r:<14} 无法解析")
            continue
        note = "（已装版本是范围写法，判断层会归到“不自动”）" if pa[1] else ""
        print(f"{a!r:>18} {symbol[compare(pa, pb)]} {b!r:<14}{note}")


if __name__ == "__main__":
    main()
