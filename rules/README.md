# 官方规则

每个软件一个 YAML 文件，文件名等于规则的 `id`。格式见设计文档第 8 节。

提交规则前请确认：
- `dotnet test --filter OfficialRulesTests` 通过
- 后台项目的 `explain` 只写事实（它是什么、什么时候运行），不写评价
- 暂不写 `update`（等 CI 搭好）；`leftovers` 只写已核实的路径

## leftovers（卸载残留）

卸载时用来找残留的路径列表，每条一个 `path` 加一个 `kind`：

```yaml
leftovers:
  - path: "%APPDATA%\\Tencent\\QQ"
    kind: shared          # Tencent 目录可能被多个腾讯软件共用
  - path: "HKCU\\Software\\Tencent\\QQ"
    kind: owned
```

- `kind` 可选值：
  - `owned`：确定属于该软件，默认勾选
  - `personal`：个人数据，默认不勾选，红色警告
  - `shared`：多软件共用，默认不勾选，黄色警告
- `path` 支持 `%APPDATA%`、`%LOCALAPPDATA%`、`%PROGRAMDATA%` 等环境变量。
- `HKCU\`、`HKLM\`、`HKCR\` 开头的是注册表项，其余是文件或文件夹。
- 聊天记录、存档、文档一律用 `personal`；被同厂商多个软件共用的目录用 `shared`。
