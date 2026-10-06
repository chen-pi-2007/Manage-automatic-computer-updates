# 官方规则

每个软件一个 YAML 文件，文件名等于规则的 `id`。格式见设计文档第 8 节。

提交规则前请确认：
- `dotnet test --filter OfficialRulesTests` 通过
- 后台项目的 `explain` 只写事实（它是什么、什么时候运行），不写评价
- 暂不写 `update`（等 CI 搭好）和 `leftovers`（等卸载功能逐个核实路径）
