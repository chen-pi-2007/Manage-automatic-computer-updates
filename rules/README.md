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

## 维护者：发布规则更新（在线订阅）

程序通过「规则订阅」功能从 GitHub 的 `rules-latest` Release 自动拉取签名规则包。改完规则后这样发布：

1. 用签名工具打包并签名（私钥在仓库外，由维护者保管，**切勿提交**）：
   ```powershell
   dotnet run --project src/UpdateHelper.RuleSign -- build rules <递增的版本号> <私钥.pem 路径> <输出目录>
   ```
   版本号每次要比上一次大（客户端据此择新、且拒绝更旧的包）。产出 `rules-bundle.zip` 和 `rules-bundle.zip.sig`。
2. 把这两个文件上传/替换到 `rules-latest` Release：
   ```powershell
   gh release upload rules-latest <输出目录>\rules-bundle.zip <输出目录>\rules-bundle.zip.sig --clobber
   ```
3. 客户端会在当天自动同步，或由用户在「规则」页点「立即同步」。

> 公钥内置在客户端（`RuleSubscriptionConfig.PublicKeyPem`）。换私钥必须同步换客户端里的公钥并重新发版，否则老客户端会验签失败并回退到内置规则。
