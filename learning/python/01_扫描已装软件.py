"""
学习版 01：扫描已装软件
对应 C# 代码：
  - 读注册表        → src/UpdateHelper.Core/Scanning/RegistryUninstallSource.cs
  - 安全读值        → src/UpdateHelper.Core/Scanning/RegistryValues.cs
  - 隐藏组件归组    → src/UpdateHelper.Core/Grouping/SoftwareGrouper.cs（规则 c 的简化版）

运行：python learning/python/01_扫描已装软件.py
本脚本只读注册表，不会修改任何东西。
"""
import winreg

UNINSTALL = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"

# 三处登记位置：(根键, 访问标志, 说明)
# KEY_WOW64_64KEY / 32KEY 决定看 64 位还是 32 位那一份注册表
LOCATIONS = [
    (winreg.HKEY_LOCAL_MACHINE, winreg.KEY_WOW64_64KEY, "HKLM 64 位"),
    (winreg.HKEY_LOCAL_MACHINE, winreg.KEY_WOW64_32KEY, "HKLM 32 位"),
    (winreg.HKEY_CURRENT_USER, 0, "HKCU 当前用户"),
]


def read_value(key, name):
    """安全读值：值不存在或类型奇怪时返回 None（对应 C# 的 RegistryValues）。"""
    try:
        value, _type = winreg.QueryValueEx(key, name)
    except OSError:
        return None
    if isinstance(value, str):
        value = value.strip()
        return value or None
    if isinstance(value, int):
        return value
    return None  # 二进制等其他类型当作缺失


def read_entries():
    """读出三处注册表里所有带 DisplayName 的登记。"""
    entries = []
    for root, flag, label in LOCATIONS:
        try:
            uninstall = winreg.OpenKey(root, UNINSTALL, 0, winreg.KEY_READ | flag)
        except OSError:
            continue  # 整个位置打不开就跳过
        index = 0
        while True:
            try:
                sub_name = winreg.EnumKey(uninstall, index)
            except OSError:
                break  # 枚举完了
            index += 1
            try:
                with winreg.OpenKey(uninstall, sub_name) as key:
                    name = read_value(key, "DisplayName")
                    if not isinstance(name, str):
                        continue  # 没有显示名的键不算软件
                    entries.append({
                        "name": name,
                        "version": read_value(key, "DisplayVersion"),
                        "publisher": read_value(key, "Publisher"),
                        "hidden": read_value(key, "SystemComponent") == 1,
                        "is_patch": read_value(key, "ParentKeyName") is not None,
                        "where": label,
                    })
            except OSError:
                continue  # 单个键没权限就跳过
        winreg.CloseKey(uninstall)
    return entries


def words(text):
    """把名称拆成词，和 C# 版的 Words() 一样按空格、括号、横线、逗号拆。"""
    for ch in "()-,":
        text = text.replace(ch, " ")
    return text.split()


def shared_leading_words(a, b, ignored):
    """两个名称从开头起连续相同的词数；发布者名里出现的词不计分。"""
    score = 0
    for x, y in zip(words(a), words(b)):
        if x.lower() != y.lower():
            break
        if x.lower() not in ignored:
            score += 1
    return score


def group(entries):
    """可见的登记各成一组；隐藏组件按"同发布者 + 名称开头共同词"找主人。"""
    primaries = [e for e in entries if not e["hidden"] and not e["is_patch"]]
    groups = {id(p): {"main": p, "parts": []} for p in primaries}
    orphans = []

    for e in entries:
        if not (e["hidden"] or e["is_patch"]):
            continue
        ignored = {w.lower() for w in words(e["publisher"] or "")}
        best, best_score = None, 0
        for p in primaries:
            if p["publisher"] != e["publisher"]:
                continue
            score = shared_leading_words(e["name"], p["name"], ignored)
            if score > best_score:
                best, best_score = p, score
        if best is None:
            orphans.append(e)
        else:
            groups[id(best)]["parts"].append(e)
    return list(groups.values()), orphans


def main():
    entries = read_entries()
    hidden = [e for e in entries if e["hidden"]]
    print(f"登记总数：{len(entries)}，其中隐藏的：{len(hidden)}")

    groups, orphans = group(entries)
    print(f"整理后：{len(groups)} 个软件，{len(orphans)} 个找不到主人的组件\n")

    for g in sorted(groups, key=lambda g: g["main"]["name"].lower()):
        main_entry = g["main"]
        line = f"{main_entry['name']}  {main_entry['version'] or ''}"
        if g["parts"]:
            line += f"   （含 {len(g['parts'])} 个组件）"
        print(line)


if __name__ == "__main__":
    main()
