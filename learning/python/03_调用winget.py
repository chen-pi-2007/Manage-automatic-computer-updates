"""
学习版 03：调用 winget
对应 C# 代码：src/UpdateHelper.Winget/WingetUpdateSource.cs

C# 版用的是 winget 的官方编程接口（COM），拿到的是结构化数据。
Python 这里用最直观的办法：运行命令行 `winget upgrade`，再从打印出来的表格里拆字段。
跑一跑就能看出这种办法的问题——表格是给人看的：名字太长会被截断成"…"，
版本号里可能有空格（"< 3.10.8"），格式一变就拆错。这正是 C# 版不用它的原因。

运行：python learning/python/03_调用winget.py
只查询，不会安装任何东西。第一次运行可能要等一会儿（winget 要下载软件源索引）。
"""
import subprocess


def run_winget_upgrade():
    result = subprocess.run(
        ["winget", "upgrade", "--source", "winget", "--disable-interactivity"],
        capture_output=True,
    )
    # winget 输出 UTF-8；用 replace 防止个别字符解码失败
    return result.stdout.decode("utf-8", errors="replace")


def parse_table(text):
    """从表格里拆出 (名称, Id, 已装版本, 可用版本)。从右往左拆，因为名称里可能有空格。"""
    rows = []
    started = False
    for line in text.splitlines():
        if set(line.strip()) == {"-"}:      # 表头下面那条横线之后才是数据
            started = True
            continue
        if not started or not line.strip():
            continue
        tokens = line.split()
        # 只查一个源（--source winget）时，winget 会省掉最后的"源"列，
        # 所以每行最后三列依次是：Id、已装版本、可用版本
        if len(tokens) < 4:
            continue                         # 末尾的"N 升级可用"统计行等
        available, installed, package_id = tokens[-1], tokens[-2], tokens[-3]
        name_end = -3
        if package_id in ("<", ">"):         # 已装版本是 "< 3.10.8" 这种带空格的写法
            installed = f"{package_id} {installed}"
            package_id = tokens[-4]
            name_end = -4
        rows.append((" ".join(tokens[:name_end]), package_id, installed, available))
    return rows


def main():
    try:
        text = run_winget_upgrade()
    except FileNotFoundError:
        print("没有找到 winget。可以在微软商店安装或更新“应用安装程序”后再试。")
        return

    rows = parse_table(text)
    print(f"winget 报告 {len(rows)} 个软件有更新：\n")
    for name, package_id, installed, available in rows:
        print(f"  {name[:24]:<24} {installed:>16} → {available:<16} {package_id}")


if __name__ == "__main__":
    main()
