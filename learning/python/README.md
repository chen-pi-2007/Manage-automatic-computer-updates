# Python 学习版

每个脚本对照 C# 核心库的一个模块，用最少的代码讲清楚原理。脚本开头写明了对应的 C# 文件。
都只读取信息，不会修改电脑上的任何东西。

| 脚本 | 内容 | 对应 C# |
|---|---|---|
| 01_扫描已装软件.py | 读注册表、区分隐藏条目、把组件归到主软件下 | Scanning/RegistryUninstallSource.cs、Grouping/SoftwareGrouper.cs |

运行：`python learning/python/01_扫描已装软件.py`（需要 Python 3.9+，Windows）
