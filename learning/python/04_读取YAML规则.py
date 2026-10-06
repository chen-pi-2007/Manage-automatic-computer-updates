"""
学习版 04：读取 YAML 规则
对应 C# 代码：
  - 通配符匹配      → src/UpdateHelper.Core/Rules/GlobPattern.cs
  - 解析和校验      → src/UpdateHelper.Core/Rules/RuleParser.cs
  - 从目录加载      → src/UpdateHelper.Core/Rules/RuleSetLoader.cs

运行：python learning/python/04_读取YAML规则.py
只读取 rules/ 目录里的文件，不会修改任何东西。
"""
import re
from pathlib import Path

import yaml  # PyYAML：Anaconda 自带，否则 pip install pyyaml

RULES_DIR = Path(__file__).resolve().parents[2] / "rules"
ID_PATTERN = re.compile(r"^[a-z0-9][a-z0-9._-]*$")


def glob_match(pattern, text):
    """只认 * 和 ? 的通配符匹配，忽略大小写（对应 GlobPattern.IsMatch）。"""
    if text is None:
        return False
    # re.escape 先把所有特殊字符变成字面量，再把 \* \? 换回通配符
    regex = re.escape(pattern).replace(r"\*", ".*").replace(r"\?", ".")
    return re.fullmatch(regex, text, flags=re.IGNORECASE | re.DOTALL) is not None


def validate(data, file_name):
    """最基本的校验，返回错误列表（C# 版检查得更全）。"""
    if not isinstance(data, dict):
        return [f"{file_name}: 根节点必须是键值对"]
    errors = []
    rule_id = data.get("id")
    if not rule_id:
        errors.append(f"{file_name}: 缺少 id")
    elif not ID_PATTERN.match(str(rule_id)):
        errors.append(f"{file_name}: id 格式不对")
    if not data.get("name"):
        errors.append(f"{file_name}: 缺少 name")
    if not (data.get("match") or {}).get("displayName"):
        errors.append(f"{file_name}: 缺少 match.displayName")
    for i, b in enumerate(data.get("background") or []):
        targets = [k for k in ("task", "service", "run", "startup") if b.get(k)]
        if len(targets) != 1:
            errors.append(f"{file_name}: background[{i}] 需要且只能写一个目标")
        if not b.get("explain"):
            errors.append(f"{file_name}: background[{i}] 缺少 explain")
    return errors


def load_rules(directory):
    """读取目录下所有 .yaml/.yml，坏文件跳过（对应 RuleSetLoader.Load）。"""
    rules, errors, seen = [], [], set()
    if not directory.exists():
        return rules, errors
    files = sorted(list(directory.rglob("*.yaml")) + list(directory.rglob("*.yml")))
    for path in files:
        try:
            # utf-8-sig：自动去掉文件开头的 BOM
            data = yaml.safe_load(path.read_text(encoding="utf-8-sig"))
        except yaml.YAMLError as ex:
            errors.append(f"{path.name}: YAML 格式错误：{ex}")
            continue
        problems = validate(data, path.name)
        if problems:
            errors.extend(problems)
            continue
        if data["id"] in seen:
            errors.append(f"{path.name}: id {data['id']} 重复，已忽略")
            continue
        seen.add(data["id"])
        rules.append(data)
    return rules, errors


def main():
    rules, errors = load_rules(RULES_DIR)
    print(f"规则目录：{RULES_DIR}")
    print(f"加载成功 {len(rules)} 条，错误 {len(errors)} 条\n")
    for e in errors:
        print("  错误：", e)

    for rule in rules:
        print(f"[{rule['id']}] {rule['name']}  匹配：{rule['match']['displayName']}")
        for b in rule.get("background") or []:
            target = next(k for k in ("task", "service", "run", "startup") if b.get(k))
            print(f"    {target:8} {b[target]:32} —— {b['explain']}")

    # 试一试通配符：哪条规则能认出这些软件名
    print()
    for name in ["WPS Office (12.1.0.23125)", "Java 8 Update 331 (64-bit)", "QQ音乐"]:
        hits = [r["id"] for r in rules if glob_match(r["match"]["displayName"], name)]
        print(f"{name!r} 命中规则：{hits or '无'}")


if __name__ == "__main__":
    main()
