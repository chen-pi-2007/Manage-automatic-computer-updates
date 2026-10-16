# 设计：规则订阅（客户端在线刷新官方规则）

状态：已与用户确认（2026-10-13）。本文件是本功能的权威设计，覆盖主设计文档
`2026-09-30-update-helper-design.md` §8 中"订阅"部分的细化。

## 1. 目标与范围

### 要解决的问题
官方规则现在随 app 发版打包进程序（`rules/` 目录），只有发新版 app 才能更新。
但软件出新版（WPS、迅雷等的 `update.latest` 版本号）比我们发 app 的频率高得多。
**规则订阅让规则不等 app 发版就能更新**：改一下规则包里的 `latest`，用户当天同步就能认出"有更新"。

### 本轮范围（只做客户端 A）
- 客户端能从官方源下载**签名规则包** → 验签 → 本地缓存 → 和内置规则按版本择新加载。
- 新增「规则」页：显示官方源状态、上次同步时间、规则包版本、规则数；一个「立即同步」按钮；每天自动同步一次。
- 一个最小的本地签名工具，维护者本机打包签名、手动传到 GitHub Release。

### 本轮不做（明确推迟）
- 第三方源的添加/管理（页面结构预留位置，逻辑不做）。
- 完整 CI（GitHub Actions 每日检查最新版本并自动签名发布）。
- 国内镜像（客户端把官方源 URL 做成可配置，默认 GitHub Release，镜像以后再加）。

### 不变的底线
- **客户端从不执行任何代码、不访问软件官网**；只下载数据、验签、解析 YAML。
- 内置规则永远在，离线或验签失败都用它兜底，联网问题绝不影响程序稳定。

## 2. 分发位置

官方签名规则包作为资产传到**现有仓库的专用 Release**（固定 tag `rules-latest`）：
- `rules-bundle.zip`：规则包
- `rules-bundle.zip.sig`：对 zip 字节的分离签名

客户端默认源 URL 指向该 Release 的两个固定下载地址。URL 在代码里是常量，
但做成"可配置的官方源地址"，为以后的镜像留口子。

## 3. 签名与信任（核心）

**威胁模型**：GitHub 或镜像上的规则包被人替换。要让客户端能发现并拒绝。

**方案**：非对称签名 + 公钥内置。
- 维护者本机持**私钥**（RSA-3072 PEM，存在仓库外，由用户保管，**绝不进仓库**）。
- 客户端内置**公钥**（写死在 Core 的常量）。
- 发布时对 `rules-bundle.zip` 的字节算**分离签名**（RSA-3072 + SHA-256，`RSASignaturePadding.Pkcs1`）。
- 客户端下载后用内置公钥 `VerifyData` 验签；不过就整包丢弃，继续用内置/旧缓存。
- 用 .NET 自带 `System.Security.Cryptography.RSA`（`ImportFromPem` / `SignData` / `VerifyData`），
  **不引入任何第三方加密库**。

**为什么不用别的**：Authenticode 要代码签名证书（连 app 都没签），太重；GPG 要拖 BouncyCastle；
只校验 hash 不签名挡不住"镜像被替换"——而这正是要防的。

## 4. 规则包格式

`rules-bundle.zip` 内容：
- 全部 `*.yaml` 规则文件（与仓库 `rules/` 目录一致的平铺结构）。
- `manifest.json`：
  ```json
  {
    "bundleVersion": 3,
    "generatedAt": "2026-10-13T00:00:00Z",
    "ruleCount": 12,
    "files": [ { "name": "kingsoft.wps.yaml", "sha256": "…" }, … ]
  }
  ```
  - `bundleVersion`：单调递增整数，用于择新与防降级。
  - 每个文件一条 sha256。

客户端验证顺序（任一步失败即整包丢弃）：
1. 用公钥验 `rules-bundle.zip.sig` 对 zip 字节的签名。
2. 解压（带上限，见下）。
3. 读 `manifest.json`，逐个校验文件 sha256 与 manifest 一致，且不多不少。
4. 全过 → 原子落盘到缓存目录。

**防滥用上限**（防 zip 炸弹 / 异常包）：单文件解压上限（如 1 MB）、文件总数上限（如 500）、
解压总大小上限（如 20 MB）、只接受 `manifest.json` 和 `*.yaml`（忽略其他条目），
拒绝带路径穿越（`..`、绝对路径、目录分隔）的条目。

## 5. 缓存与优先级

- 验证通过的规则包解压到 `%LOCALAPPDATA%\UpdateHelper\rules-cache\`（含 `manifest.json`）。
- 落盘用"先写临时目录再整体替换"，避免中途失败留下半套。
- **内置规则也带一个 `manifest.json`**（同格式，有 `bundleVersion`，随 app 打包）。
- 加载时**比较内置与缓存的 `bundleVersion`，谁高用谁**作为官方源目录：
  - 缓存比内置新 → 用缓存。
  - 刚更新了 app、内置反而更新 → 用内置（缓存视为过时）。
- **防降级**：同步时若下到的 `bundleVersion` ≤ 当前缓存版本，保留旧缓存、不覆盖。
- 缓存损坏/读不出 manifest → 当作没有缓存，用内置兜底。

## 6. 同步时机

- **手动**：「规则」页「立即同步」按钮。
- **自动**：每天一次（沿用现有定时检查的思路，默认开启）。
- 走 `HttpClient`（自动使用系统代理）。
- 失败（网络、验签、降级）**不弹错误框**，只在页面显示上次同步时间 + 状态文案。

## 7. 模块划分（Core，全部可脱离网络单测）

新增 `src/UpdateHelper.Core/Rules/Subscription/`：

- `BundleManifest`（record）：`bundleVersion`、`generatedAt`、`ruleCount`、`files[]`。
  配 `ManifestParser`（读写 JSON，坏格式返回失败不抛）。
- `BundleVerifier`：输入公钥、zip 路径、sig 路径 → 验签；再校验解压内容的 sha256 与 manifest 一致。
  纯函数式，返回 `(Ok, 错误)`。公钥作为参数注入，测试用测试密钥对。
- `RuleCacheStore`：缓存目录读写；`TrySelectOfficialDirectory(内置目录, 缓存目录)` 比较两份
  manifest 版本返回该用哪个目录 + 版本信息；原子替换写入；防降级判断。
- `RuleSyncService`：注入 `IDownloader`（复用现有 `Install/IDownloader`）；
  下 zip + sig 到临时目录 → `BundleVerifier` 验 → `RuleCacheStore` 存（含防降级）→
  返回 `SyncOutcome`（成功/已是最新/验签失败/网络失败/被降级拒绝 + 版本号 + 时间）。

改动：
- `RuleSetLoader`：官方源目录改为由 `RuleCacheStore.TrySelectOfficialDirectory` 选出的那个
  （现在已是多目录加载，改动小）。第三方目录维持不变（本轮仍只传官方）。
- 签名公钥常量 + 默认源 URL 常量放在 Core 的一个 `RuleSubscriptionConfig` 里。

## 8. 界面「规则」页

- 新增顶层导航项「规则」（Nav 顺序：首页 / 更新 / 我的软件 / 后台项目 / 安全 / **规则** / 历史）。
  `--page rules` 可直达。
- `RuleSubscriptionViewModel`（Presentation）：
  - 显示：官方源地址、上次同步时间、当前规则包版本、规则数、状态文案
    （已是最新 / 有更新已应用 / 同步失败（原因）/ 离线用内置）。
  - 「立即同步」命令 → 后台线程调 `RuleSyncService` → 刷新显示。
  - 页面预留第三方源列表区域（本轮不接逻辑）。
- App：接线 `HttpDownloader` + `RuleSyncService`；把每日自动同步挂到现有定时逻辑。

## 9. 签名工具（最小版顶 CI）

新增控制台项目 `src/UpdateHelper.RuleSign`（或归入 ScanCli 的子命令，实现时定）：
- 输入：`rules/` 目录、私钥 PEM 路径、输出目录、`bundleVersion`。
- 输出：`rules-bundle.zip`（含 `manifest.json`）+ `rules-bundle.zip.sig`。
- 同一工具也能只生成**内置规则的 `manifest.json`**（随 app 打包进 `rules/`），
  保证 §5 的版本比较有数据。
- 中文用法说明。私钥只从命令行参数指定的仓库外路径读，工具本身不碰仓库里的密钥（仓库里没有）。

密钥准备（实现前一次性）：生成一对 RSA-3072，私钥 PEM 交用户存仓库外，公钥写进 Core 常量。

## 10. 测试

- `ManifestParser`：正常、坏 JSON、缺字段、版本非法。
- `BundleVerifier`：用测试密钥对——正确签名通过；篡改 zip 一个字节 → 验签失败；
  sha256 对不上 → 失败；多/少文件 → 失败；路径穿越条目 → 拒绝；超限 → 拒绝。
- `RuleCacheStore`：缓存版本高/低/相等于内置时的择新结果；防降级；缓存损坏当无缓存；原子替换。
- `RuleSyncService`：用假 `IDownloader`（给预置的 zip+sig）——成功落盘；验签失败不动缓存；
  网络异常不抛、返回失败结果；下到旧版本被拒。
- `RuleSubscriptionViewModel`：各 `SyncOutcome` → 对应状态文案；同步在后台不卡。
- 签名工具：产出的包能被 `BundleVerifier` 用对应公钥验过（往返测试）。
- 真实网络不进单测；端到端由用户在真机点一次「立即同步」确认。

## 11. 对现有设计的落点

- 主 spec §8"订阅"：本文件把"签名""每天同步""坏规则跳过"落成具体机制。
  "第三方源""CI""镜像"按本轮范围推迟，不改主 spec 的方向。
- 复用：`Install/IDownloader`、`HttpDownloader`、现有多目录 `RuleSetLoader`、YAML 解析。
- 不涉及提权，与搁置的后台助手（计划 7）无关。
