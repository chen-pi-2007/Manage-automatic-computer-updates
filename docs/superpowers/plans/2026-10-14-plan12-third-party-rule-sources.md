# 第三方规则源 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让用户订阅自己信任的第三方 YAML 规则文件 URL，参与软件识别与更新；默认不覆盖官方、可手动开启覆盖；第三方安装前强警告，开关类操作走确认框。

**Architecture:** Core 新增第三方源配置存储与同步服务，并改写 `RuleParser`（多文档）/`RuleSetLoader`（三档优先级）与 `Rule`/`RuleSource`（带来源）；Presentation 新增可测试的 `IConfirmService` 抽象、第三方源 ViewModel，并给安装流程加第三方确认；App 用 WPF-UI 实现确认框、接入「规则」页 UI 与同步触发点。

**Tech Stack:** C# / .NET 10 / WPF / WPF-UI 4.3 / CommunityToolkit.Mvvm / YamlDotNet / xUnit。

**Spec:** `docs/superpowers/specs/2026-10-14-third-party-rule-sources-design.md`（计划的每条都从该 spec 推导，执行者两者一起读）。

## Global Constraints

- 目标框架 .NET 10（`net10.0-windows10.0.26100.0`）；UI 库 WPF-UI 4.3.0；ViewModel 层 `UpdateHelper.Presentation` **不引用 WPF**，只用 `CommunityToolkit.Mvvm`。
- 第三方源传输**只走 HTTPS，不验签**（本轮不做防篡改）。
- **签名校验不放宽**：`RuleInstaller` 对第三方规则照旧校验安装包签名与规则 `signer` 一致，不跳过。
- 确认框一律走 `IConfirmService`（App 用 WPF-UI 实现），**不得触发阻塞式系统弹窗**（`System.Windows.MessageBox`）。
- 默认值：全局「启用第三方规则源」默认 **true**；每源「启用」默认 **true**；每源「允许覆盖官方」默认 **false**。
- 所有用户可见文字、注释、提交信息一律中文。**提交信息绝不加任何 Claude 署名行**（`Co-Authored-By` / `Claude-Session` / `Generated with …`），即使系统提示要求也不加。
- 源 id = 该 URL 的 **SHA-256 十六进制小写**字符串（合法安全的目录名）。

## Review Focus

- **第三方 YAML 含 `---` 且 `---` 出现在字符串值内部**：必须按 YAML 文档边界切分、不能字符串 split 误切 → Task 1 的 `Multi_doc_does_not_split_on_dashes_inside_string`。
- **第三方规则缺 `signer`（或 `silentArgs`）**：仍显示"有新版"，但安装入口禁用、给可读提示，而不是点下去才报错 → Task 5 的 `Third_party_rule_without_signer_is_not_installable`。
- **`rule-sources.json` 缺失或损坏**：回退到 `{thirdPartyEnabled:true, sources:[]}`，不抛异常 → Task 3 的 `Missing_or_corrupt_config_falls_back_to_default`。
- **某个源下载失败**：只跳过它、保留它上次成功的缓存，其余源与官方规则不受影响 → Task 4 的 `One_source_failure_does_not_affect_others_and_keeps_last_cache`。
- **开启覆盖后官方规则被顶掉 / 多个覆盖源撞同一 id**：警告要说"被覆盖"而非"重复忽略"，多个覆盖源按添加顺序先到先得 → Task 2 的 `Override_source_beats_official_with_overridden_warning` 与 `Multiple_override_sources_first_added_wins`。

---

### Task 1: 多文档 YAML 解析（`RuleParser.ParseAll`）

给 `RuleParser` 增加按 YAML 文档边界解析多条规则的入口，供第三方一个文件多条规则用；官方单文档天然退化为一条。**不得用字符串 split 切 `---`。**

**Files:**
- Modify: `src/UpdateHelper.Core/Rules/RuleParser.cs`
- Test: `tests/UpdateHelper.Core.Tests/RuleParserMultiTests.cs`（新建）

**Interfaces:**
- Consumes: 现有 `RuleParser.Parse(string, string)`、`ParseResult`、`Rule`。
- Produces: `public static IReadOnlyList<ParseResult> RuleParser.ParseAll(string yaml, string sourceFile)` —— 每个 YAML 文档一个 `ParseResult`（校验失败的文档 `Ok==false` 并带错误，其余照常）；遇到 YAML **语法**错误则记一条错误并停止该文件后续文档（解析器状态已不可续）；整段无文档时返回一条"文件是空的"错误。

- [ ] **Step 1: 写失败测试**

新建 `tests/UpdateHelper.Core.Tests/RuleParserMultiTests.cs`：

```csharp
using UpdateHelper.Core.Rules;

namespace UpdateHelper.Core.Tests;

public class RuleParserMultiTests
{
    [Fact]
    public void Single_document_yields_one_rule()
    {
        var yaml = "id: a\nname: A\nmatch:\n  displayName: \"A\"\n";
        var results = RuleParser.ParseAll(yaml, "a.yaml");
        var r = Assert.Single(results);
        Assert.True(r.Ok);
        Assert.Equal("a", r.Rule!.Id);
    }

    [Fact]
    public void Multiple_documents_split_on_separator()
    {
        var yaml = """
            id: a
            name: A
            match:
              displayName: "A"
            ---
            id: b
            name: B
            match:
              displayName: "B"
            """;
        var results = RuleParser.ParseAll(yaml, "multi.yaml");
        Assert.Equal(new[] { "a", "b" }, results.Where(x => x.Ok).Select(x => x.Rule!.Id));
    }

    [Fact]
    public void One_invalid_document_is_skipped_others_load()
    {
        var yaml = """
            id: a
            name: A
            match:
              displayName: "A"
            ---
            name: 缺了id
            match:
              displayName: "B"
            ---
            id: c
            name: C
            match:
              displayName: "C"
            """;
        var results = RuleParser.ParseAll(yaml, "multi.yaml");
        Assert.Equal(new[] { "a", "c" }, results.Where(x => x.Ok).Select(x => x.Rule!.Id));
        Assert.Contains(results, x => !x.Ok && x.Errors.Any(e => e.Contains("缺少 id")));
    }

    [Fact]
    public void Multi_doc_does_not_split_on_dashes_inside_string()   // Review Focus 1
    {
        // explain 值里含有 "---"，绝不能被当成文档分隔符切开
        var yaml = """
            id: a
            name: A
            match:
              displayName: "A"
            background:
              - service: "svc"
                explain: "分隔线 --- 不是文档边界"
            """;
        var results = RuleParser.ParseAll(yaml, "a.yaml");
        var r = Assert.Single(results);
        Assert.True(r.Ok);
        Assert.Contains("---", r.Rule!.Background[0].Explain);
    }

    [Fact]
    public void Empty_text_reports_empty_error()
    {
        var results = RuleParser.ParseAll("   \n", "empty.yaml");
        Assert.All(results, x => Assert.False(x.Ok));
        Assert.Contains(results, x => x.Errors.Any(e => e.Contains("空")));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter RuleParserMultiTests`
Expected: 编译失败 / `ParseAll` 不存在。

- [ ] **Step 3: 实现 `ParseAll`，重构出 `Validate`**

编辑 `src/UpdateHelper.Core/Rules/RuleParser.cs`：顶部补 `using`（`Parser`/事件在 `YamlDotNet.Core`）：

```csharp
using System.IO;
using YamlDotNet.Core;
```

把现有 `Parse` 改为"反序列化 + 调用 `Validate`"，并新增 `ParseAll` 与 `Validate`、`SyntaxError`（把原 `Parse` 里从 `if (dto is null)` 开始到结尾的校验逻辑整体搬进 `Validate`，签名改为吃 `RuleDto?`）：

```csharp
public static ParseResult Parse(string yaml, string sourceFile)
{
    RuleDto? dto;
    try { dto = Deserializer.Deserialize<RuleDto?>(yaml); }
    catch (YamlException ex) { return SyntaxError(sourceFile, ex); }
    return Validate(dto, sourceFile);
}

public static IReadOnlyList<ParseResult> ParseAll(string yaml, string sourceFile)
{
    var results = new List<ParseResult>();
    var parser = new Parser(new StringReader(yaml));
    try
    {
        parser.Consume<YamlDotNet.Core.Events.StreamStart>();
        while (parser.Accept<YamlDotNet.Core.Events.DocumentStart>(out _))
        {
            RuleDto? dto;
            try { dto = Deserializer.Deserialize<RuleDto?>(parser); }
            catch (YamlException ex)
            {
                results.Add(SyntaxError(sourceFile, ex));
                break;   // 解析器状态已乱，无法安全续读后续文档
            }
            results.Add(Validate(dto, sourceFile));
        }
    }
    catch (YamlException ex)
    {
        results.Add(SyntaxError(sourceFile, ex));
    }

    if (results.Count == 0)
        results.Add(new ParseResult(null, [$"{sourceFile}: 文件是空的，没有规则"]));
    return results;
}

private static ParseResult SyntaxError(string sourceFile, YamlException ex) =>
    new(null, [$"{sourceFile}: YAML 格式错误（第 {ex.Start.Line} 行）：{ex.InnerException?.Message ?? ex.Message}"]);

private static ParseResult Validate(RuleDto? dto, string sourceFile)
{
    var errors = new List<string>();
    void Error(string message) => errors.Add($"{sourceFile}: {message}");

    if (dto is null)
    {
        Error("文件是空的");
        return new ParseResult(null, errors);
    }
    // …… 原 Parse 中从 `if (string.IsNullOrWhiteSpace(dto.Id))` 到构造 Rule、return 的全部逻辑原样搬到这里 ……
}
```

> 注意：`Validate` 里构造 `Rule` 的那段、`ParseUpdate`/`ParseBackground`/`ParseLeftovers` 等私有方法保持不动。`Parse` 里原来的 `try/catch(YamlException)` 与 `dto is null` 判断已移出，别重复。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter RuleParserMultiTests`
Expected: PASS（5 个）。再跑 `--filter RuleParserTests` 确认原解析测试仍绿。

- [ ] **Step 5: 提交**

```bash
git add src/UpdateHelper.Core/Rules/RuleParser.cs tests/UpdateHelper.Core.Tests/RuleParserMultiTests.cs
git commit -m "feat(core): RuleParser 支持多文档 YAML（按文档边界解析，非字符串切分）"
```

---

### Task 2: 规则记录带来源 + `RuleSetLoader` 三档优先级与多文档加载

给 `RuleSource` 加覆盖开关与来源标识、给 `Rule` 加 `SourceUrl/SourceName`，并重写 `RuleSetLoader`：三档排序、三类准确去重警告、用 `ParseAll` 支持一个文件多条规则、给规则填来源。

**Files:**
- Modify: `src/UpdateHelper.Core/Rules/RuleSet.cs`（`RuleSource` 加字段）
- Modify: `src/UpdateHelper.Core/Rules/Rule.cs`（`Rule` 加 `SourceUrl`/`SourceName`）
- Modify: `src/UpdateHelper.Core/Rules/RuleSetLoader.cs`（重写 Load）
- Modify: `tests/UpdateHelper.Core.Tests/RuleSetLoaderTests.cs`（更新旧断言 + 新增用例）

**Interfaces:**
- Consumes: `RuleParser.ParseAll`（Task 1）。
- Produces:
  - `record RuleSource(string Directory, RuleTrust Trust, bool AllowOverrideOfficial = false, string SourceUrl = "", string SourceName = "")`
  - `Rule` 新增 `string SourceUrl { get; init; } = ""`、`string SourceName { get; init; } = ""`
  - `RuleSetLoader.Load` 的排序键：开覆盖第三方=0 > 官方=1 > 普通第三方=2；稳定排序。

- [ ] **Step 1: 写失败测试**

编辑 `tests/UpdateHelper.Core.Tests/RuleSetLoaderTests.cs`：

(a) **更新**现有 `Third_party_cannot_override_official_even_if_listed_first` 的最后一行断言（文案已变）：

```csharp
        Assert.Contains(set.Warnings, w => w.Contains("未开启覆盖"));
```

(b) 末尾新增用例：

```csharp
    private static RuleSource Third(string dir, bool allowOverride, string name) =>
        new(dir, RuleTrust.ThirdParty, allowOverride, SourceUrl: "https://src/" + name, SourceName: name);

    [Fact]
    public void Override_source_beats_official_with_overridden_warning()   // Review Focus 5
    {
        using var official = new TempDir();
        using var third = new TempDir();
        official.Write("wps.yaml", RuleYaml("kingsoft.wps", "官方"));
        third.Write("wps.yaml", RuleYaml("kingsoft.wps", "第三方覆盖"));

        var set = RuleSetLoader.Load([
            new RuleSource(official.Path, RuleTrust.Official),
            Third(third.Path, allowOverride: true, "社区源")]);

        Assert.Equal("第三方覆盖", set.Rules.Single(r => r.Id == "kingsoft.wps").Name);
        Assert.Contains(set.Warnings, w => w.Contains("覆盖") && w.Contains("社区源"));
    }

    [Fact]
    public void Multiple_override_sources_first_added_wins()   // Review Focus 5
    {
        using var official = new TempDir();
        using var a = new TempDir();
        using var b = new TempDir();
        official.Write("x.yaml", RuleYaml("soft.x", "官方"));
        a.Write("x.yaml", RuleYaml("soft.x", "源A"));
        b.Write("x.yaml", RuleYaml("soft.x", "源B"));

        var set = RuleSetLoader.Load([
            new RuleSource(official.Path, RuleTrust.Official),
            Third(a.Path, true, "A"),    // 先添加
            Third(b.Path, true, "B")]);  // 后添加

        Assert.Equal("源A", set.Rules.Single(r => r.Id == "soft.x").Name);
    }

    [Fact]
    public void Loaded_rule_carries_source_url_and_name()
    {
        using var third = new TempDir();
        third.Write("x.yaml", RuleYaml("soft.x", "X"));
        var set = RuleSetLoader.Load([Third(third.Path, false, "社区源")]);
        var rule = Assert.Single(set.Rules);
        Assert.Equal("https://src/社区源", rule.SourceUrl);
        Assert.Equal("社区源", rule.SourceName);
    }

    [Fact]
    public void Multi_rule_file_loads_all_rules()
    {
        using var dir = new TempDir();
        dir.Write("many.yaml", """
            id: soft.a
            name: A
            match:
              displayName: "A"
            ---
            id: soft.b
            name: B
            match:
              displayName: "B"
            """);
        var set = RuleSetLoader.Load([new RuleSource(dir.Path, RuleTrust.ThirdParty)]);
        Assert.Equal(new[] { "soft.a", "soft.b" }, set.Rules.Select(r => r.Id).Order());
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter RuleSetLoaderTests`
Expected: 编译失败（`RuleSource` 没有第 3/4/5 参数，`Rule` 没有 `SourceUrl`）。

- [ ] **Step 3: 改数据模型**

`src/UpdateHelper.Core/Rules/RuleSet.cs`：

```csharp
/// <summary>一个存放规则文件的本地目录，以及它的可信度、是否允许覆盖官方、来源标识。</summary>
public sealed record RuleSource(
    string Directory,
    RuleTrust Trust,
    bool AllowOverrideOfficial = false,
    string SourceUrl = "",
    string SourceName = "");
```

`src/UpdateHelper.Core/Rules/Rule.cs`，在 `Rule` 的 `SourceFile`/`Trust` 旁加：

```csharp
    /// <summary>第三方源规则的来源 URL（官方为空）。</summary>
    public string SourceUrl { get; init; } = "";

    /// <summary>第三方源的显示名（官方为空）。</summary>
    public string SourceName { get; init; } = "";
```

- [ ] **Step 4: 重写 `RuleSetLoader.Load`**

`src/UpdateHelper.Core/Rules/RuleSetLoader.cs` 整体替换为：

```csharp
namespace UpdateHelper.Core.Rules;

/// <summary>从多个本地目录加载规则。三档优先级（开覆盖第三方 &gt; 官方 &gt; 普通第三方），坏文件/坏文档跳过，去重带来源。只读。</summary>
public static class RuleSetLoader
{
    public static RuleSet Load(IEnumerable<RuleSource> sources)
    {
        var rules = new List<Rule>();
        var byId = new Dictionary<string, Rule>(StringComparer.Ordinal);
        var errors = new List<string>();
        var warnings = new List<string>();

        // 稳定排序：开覆盖第三方(0) → 官方(1) → 普通第三方(2)；同档保持传入顺序（即 rule-sources.json 的顺序）
        foreach (var source in sources.OrderBy(SortKey))
        {
            foreach (var file in FindRuleFiles(source.Directory, errors))
            {
                string text;
                try { text = File.ReadAllText(file); }   // 自动去 UTF-8 BOM
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{file}: 读取失败：{ex.Message}");
                    continue;
                }

                foreach (var parsed in RuleParser.ParseAll(text, file))
                {
                    if (!parsed.Ok) { errors.AddRange(parsed.Errors); continue; }

                    var rule = parsed.Rule! with
                    {
                        Trust = source.Trust,
                        SourceUrl = source.SourceUrl,
                        SourceName = source.SourceName,
                    };
                    if (byId.TryGetValue(rule.Id, out var existing))
                    {
                        warnings.Add(DuplicateWarning(existing, rule, file));
                        continue;
                    }
                    byId[rule.Id] = rule;
                    rules.Add(rule);
                }
            }
        }

        return new RuleSet(rules, errors, warnings);
    }

    private static int SortKey(RuleSource s) =>
        s.Trust == RuleTrust.ThirdParty && s.AllowOverrideOfficial ? 0
        : s.Trust == RuleTrust.Official ? 1
        : 2;

    private static string DuplicateWarning(Rule existing, Rule incoming, string file)
    {
        // 排序保证：官方被顶掉时，existing 必是"开了覆盖的第三方"
        if (existing.Trust == RuleTrust.ThirdParty && incoming.Trust == RuleTrust.Official)
            return $"官方规则 {incoming.Id} 已被你允许覆盖的第三方源「{existing.SourceName}」覆盖";
        if (existing.Trust == RuleTrust.Official && incoming.Trust == RuleTrust.ThirdParty)
            return $"第三方规则 {incoming.Id}（来自「{incoming.SourceName}」）与官方规则同名，未开启覆盖，已忽略";
        return $"{file}: id {incoming.Id} 与 {existing.SourceFile} 重复，已忽略";
    }

    private static IEnumerable<string> FindRuleFiles(string directory, List<string> errors)
    {
        if (!Directory.Exists(directory)) return [];
        try
        {
            return Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{directory}: 无法列出规则文件：{ex.Message}");
            return [];
        }
    }
}
```

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter RuleSetLoaderTests`
Expected: PASS（含更新后的旧用例与 4 个新用例）。

- [ ] **Step 6: 提交**

```bash
git add src/UpdateHelper.Core/Rules/RuleSet.cs src/UpdateHelper.Core/Rules/Rule.cs src/UpdateHelper.Core/Rules/RuleSetLoader.cs tests/UpdateHelper.Core.Tests/RuleSetLoaderTests.cs
git commit -m "feat(core): RuleSetLoader 三档优先级与来源标注，支持第三方可选覆盖官方"
```

---

### Task 3: 第三方源配置存储（`RuleSourceStore` + 模型 + id 派生 + 缓存清理）

新增第三方源的配置模型、`rule-sources.json` 读写（缺失/损坏回退默认）、源 id 派生、缓存目录辅助与清理、以及"配置 → RuleSource 列表"的映射。

**Files:**
- Create: `src/UpdateHelper.Core/Rules/ThirdParty/ThirdPartyModels.cs`
- Create: `src/UpdateHelper.Core/Rules/ThirdParty/RuleSourceStore.cs`
- Test: `tests/UpdateHelper.Core.Tests/ThirdParty/RuleSourceStoreTests.cs`（新建）

**Interfaces:**
- Consumes: `RuleSource`/`RuleTrust`（Task 2）。
- Produces:
  - `record ThirdPartySource(string Id, string Url, string Note, bool Enabled, bool AllowOverrideOfficial)`
  - `record ThirdPartyConfig(bool ThirdPartyEnabled, IReadOnlyList<ThirdPartySource> Sources)`，`ThirdPartyConfig.Default`
  - `static string SourceId.FromUrl(string url)`（SHA-256 hex 小写）
  - `class RuleSourceStore(string configPath, string cacheRoot)`：`Read()`、`Write(config)`、`CacheDirFor(id)`、`CleanOrphanCaches(config)`、`BuildRuleSources(config)`、静态 `DefaultConfigPath`/`DefaultCacheRoot`。

- [ ] **Step 1: 写失败测试**

新建 `tests/UpdateHelper.Core.Tests/ThirdParty/RuleSourceStoreTests.cs`：

```csharp
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Rules.ThirdParty;

namespace UpdateHelper.Core.Tests.ThirdParty;

public class RuleSourceStoreTests
{
    private static RuleSourceStore New(TempDir dir) =>
        new(System.IO.Path.Combine(dir.Path, "rule-sources.json"), System.IO.Path.Combine(dir.Path, "cache"));

    [Fact]
    public void Source_id_is_64_hex_lowercase_and_stable()
    {
        var a = SourceId.FromUrl("https://x/rules.yaml");
        var b = SourceId.FromUrl("https://x/rules.yaml");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Fact]
    public void Missing_or_corrupt_config_falls_back_to_default()   // Review Focus 3
    {
        using var dir = new TempDir();
        var store = New(dir);
        var missing = store.Read();
        Assert.True(missing.ThirdPartyEnabled);
        Assert.Empty(missing.Sources);

        dir.Write("rule-sources.json", "{ 这不是合法 json");
        var corrupt = store.Read();
        Assert.True(corrupt.ThirdPartyEnabled);
        Assert.Empty(corrupt.Sources);
    }

    [Fact]
    public void Write_then_read_roundtrips()
    {
        using var dir = new TempDir();
        var store = New(dir);
        var config = new ThirdPartyConfig(false, [
            new ThirdPartySource(SourceId.FromUrl("https://x/a.yaml"), "https://x/a.yaml", "备注A", true, false),
            new ThirdPartySource(SourceId.FromUrl("https://x/b.yaml"), "https://x/b.yaml", "", false, true)]);
        store.Write(config);

        var read = store.Read();
        Assert.False(read.ThirdPartyEnabled);
        Assert.Equal(2, read.Sources.Count);
        Assert.Equal("备注A", read.Sources[0].Note);
        Assert.True(read.Sources[1].AllowOverrideOfficial);
        Assert.False(read.Sources[1].Enabled);
    }

    [Fact]
    public void Clean_orphan_caches_removes_dirs_not_in_config()
    {
        using var dir = new TempDir();
        var store = New(dir);
        var keep = SourceId.FromUrl("https://x/keep.yaml");
        var orphan = SourceId.FromUrl("https://x/gone.yaml");
        System.IO.Directory.CreateDirectory(store.CacheDirFor(keep));
        System.IO.Directory.CreateDirectory(store.CacheDirFor(orphan));

        store.CleanOrphanCaches(new ThirdPartyConfig(true, [
            new ThirdPartySource(keep, "https://x/keep.yaml", "", true, false)]));

        Assert.True(System.IO.Directory.Exists(store.CacheDirFor(keep)));
        Assert.False(System.IO.Directory.Exists(store.CacheDirFor(orphan)));
    }

    [Fact]
    public void Build_rule_sources_includes_only_enabled_and_maps_fields()
    {
        using var dir = new TempDir();
        var store = New(dir);
        var id = SourceId.FromUrl("https://x/a.yaml");
        var config = new ThirdPartyConfig(true, [
            new ThirdPartySource(id, "https://x/a.yaml", "社区源", true, true),
            new ThirdPartySource(SourceId.FromUrl("https://x/b.yaml"), "https://x/b.yaml", "", false, false)]);

        var built = store.BuildRuleSources(config);
        var s = Assert.Single(built);
        Assert.Equal(store.CacheDirFor(id), s.Directory);
        Assert.Equal(RuleTrust.ThirdParty, s.Trust);
        Assert.True(s.AllowOverrideOfficial);
        Assert.Equal("https://x/a.yaml", s.SourceUrl);
        Assert.Equal("社区源", s.SourceName);
    }

    [Fact]
    public void Build_rule_sources_empty_when_global_disabled()
    {
        using var dir = new TempDir();
        var store = New(dir);
        var config = new ThirdPartyConfig(false, [
            new ThirdPartySource(SourceId.FromUrl("https://x/a.yaml"), "https://x/a.yaml", "", true, false)]);
        Assert.Empty(store.BuildRuleSources(config));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter RuleSourceStoreTests`
Expected: 编译失败（类型不存在）。

- [ ] **Step 3: 实现模型**

新建 `src/UpdateHelper.Core/Rules/ThirdParty/ThirdPartyModels.cs`：

```csharp
using System.Security.Cryptography;
using System.Text;

namespace UpdateHelper.Core.Rules.ThirdParty;

/// <summary>一个第三方规则源。Id 由 Url 派生（SHA-256 hex），同时用作缓存目录名。</summary>
public sealed record ThirdPartySource(string Id, string Url, string Note, bool Enabled, bool AllowOverrideOfficial);

/// <summary>第三方源总配置：全局开关 + 源数组（数组顺序即加载优先顺序）。</summary>
public sealed record ThirdPartyConfig(bool ThirdPartyEnabled, IReadOnlyList<ThirdPartySource> Sources)
{
    public static ThirdPartyConfig Default { get; } = new(true, []);
}

public static class SourceId
{
    /// <summary>URL → SHA-256 十六进制小写，作为稳定、安全的目录名。</summary>
    public static string FromUrl(string url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim())));
}
```

- [ ] **Step 4: 实现 `RuleSourceStore`**

新建 `src/UpdateHelper.Core/Rules/ThirdParty/RuleSourceStore.cs`：

```csharp
using System.Text.Json;

namespace UpdateHelper.Core.Rules.ThirdParty;

/// <summary>第三方源配置（rule-sources.json）读写 + 缓存目录管理。读坏数据不抛，回退默认。</summary>
public sealed class RuleSourceStore(string configPath, string cacheRoot)
{
    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UpdateHelper", "rule-sources.json");

    public static string DefaultCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateHelper", "thirdparty-cache");

    public string CacheRoot => cacheRoot;
    public string CacheDirFor(string id) => Path.Combine(cacheRoot, id);

    public ThirdPartyConfig Read()
    {
        try
        {
            if (!File.Exists(configPath)) return ThirdPartyConfig.Default;
            var dto = JsonSerializer.Deserialize<ConfigDto>(File.ReadAllText(configPath), JsonOpts);
            if (dto is null) return ThirdPartyConfig.Default;
            var sources = (dto.Sources ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s.Url))
                .Select(s => new ThirdPartySource(
                    string.IsNullOrWhiteSpace(s.Id) ? SourceId.FromUrl(s.Url!) : s.Id!,
                    s.Url!.Trim(), s.Note ?? "", s.Enabled, s.AllowOverrideOfficial))
                .ToList();
            return new ThirdPartyConfig(dto.ThirdPartyEnabled, sources);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ThirdPartyConfig.Default;
        }
    }

    public void Write(ThirdPartyConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(configPath))!);
        var dto = new ConfigDto
        {
            ThirdPartyEnabled = config.ThirdPartyEnabled,
            Sources = config.Sources.Select(s => new SourceDto
            {
                Id = s.Id, Url = s.Url, Note = s.Note, Enabled = s.Enabled, AllowOverrideOfficial = s.AllowOverrideOfficial,
            }).ToList(),
        };
        File.WriteAllText(configPath, JsonSerializer.Serialize(dto, JsonOpts));
    }

    /// <summary>删掉缓存里不在当前配置中的源目录（删源 / 改 URL 后的残留清理）。</summary>
    public void CleanOrphanCaches(ThirdPartyConfig config)
    {
        try
        {
            if (!Directory.Exists(cacheRoot)) return;
            var keep = config.Sources.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var d in Directory.GetDirectories(cacheRoot))
            {
                if (keep.Contains(Path.GetFileName(d))) continue;
                try { Directory.Delete(d, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>把"已启用的第三方源"映射成加载用的 RuleSource；全局关 → 空。</summary>
    public IReadOnlyList<RuleSource> BuildRuleSources(ThirdPartyConfig config)
    {
        if (!config.ThirdPartyEnabled) return [];
        return config.Sources
            .Where(s => s.Enabled)
            .Select(s => new RuleSource(CacheDirFor(s.Id), RuleTrust.ThirdParty,
                s.AllowOverrideOfficial, s.Url, string.IsNullOrWhiteSpace(s.Note) ? s.Url : s.Note))
            .ToList();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private sealed class ConfigDto
    {
        public bool ThirdPartyEnabled { get; set; } = true;   // 缺省键 → 默认开
        public List<SourceDto>? Sources { get; set; }
    }

    private sealed class SourceDto
    {
        public string? Id { get; set; }
        public string? Url { get; set; }
        public string? Note { get; set; }
        public bool Enabled { get; set; } = true;
        public bool AllowOverrideOfficial { get; set; }
    }
}
```

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter RuleSourceStoreTests`
Expected: PASS（7 个）。

- [ ] **Step 6: 提交**

```bash
git add src/UpdateHelper.Core/Rules/ThirdParty/ tests/UpdateHelper.Core.Tests/ThirdParty/
git commit -m "feat(core): 第三方源配置存储（SHA-256 id、缺失/坏数据回退、缓存清理、RuleSource 映射）"
```

---

### Task 4: 第三方同步服务（`ThirdPartySyncService`）

对每个启用的源下载 YAML 到缓存、写每源同步状态；失败只跳过该源并保留上次缓存。**不验签。**

**Files:**
- Create: `src/UpdateHelper.Core/Rules/ThirdParty/ThirdPartySyncService.cs`
- Test: `tests/UpdateHelper.Core.Tests/ThirdParty/ThirdPartySyncServiceTests.cs`（新建）

**Interfaces:**
- Consumes: `IDownloader`（`UpdateHelper.Core.Install`，签名 `DownloadAsync(string url, string dest, IProgress<double>?, CancellationToken)`）、`RuleSourceStore`（Task 3）、`RuleParser.ParseAll`（Task 1）。
- Produces:
  - `enum ThirdPartySyncStatus { Updated, NetworkFailed, Empty }`
  - `record ThirdPartySourceState(DateTimeOffset When, ThirdPartySyncStatus Status, int RuleCount, string? Detail)`
  - `class ThirdPartySyncService(IDownloader downloader, RuleSourceStore store, Func<DateTimeOffset>? clock = null)`：`Task SyncAsync(IEnumerable<ThirdPartySource> sources, CancellationToken ct = default)`；`static ThirdPartySourceState? ReadState(RuleSourceStore store, string id)`；常量 `RulesFileName = "rules.yaml"`。

- [ ] **Step 1: 写失败测试**

新建 `tests/UpdateHelper.Core.Tests/ThirdParty/ThirdPartySyncServiceTests.cs`：

```csharp
using System.Text;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Rules.ThirdParty;

namespace UpdateHelper.Core.Tests.ThirdParty;

public class ThirdPartySyncServiceTests
{
    private sealed class FakeDownloader(Dictionary<string, string?> map) : IDownloader
    {
        // map: url → 内容；值为 null 表示该 url 抛异常（模拟断网）
        public Task DownloadAsync(string url, string dest, IProgress<double>? p, CancellationToken ct)
        {
            if (!map.TryGetValue(url, out var content) || content is null) throw new HttpRequestException("模拟失败");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
            File.WriteAllText(dest, content, new UTF8Encoding(false));
            return Task.CompletedTask;
        }
    }

    private const string RuleYaml = "id: soft.x\nname: X\nmatch:\n  displayName: \"X\"\n";

    private static (ThirdPartySyncService svc, RuleSourceStore store) New(Dictionary<string, string?> map, TempDir dir)
    {
        var store = new RuleSourceStore(Path.Combine(dir.Path, "c.json"), Path.Combine(dir.Path, "cache"));
        var svc = new ThirdPartySyncService(new FakeDownloader(map), store,
            () => new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero));
        return (svc, store);
    }

    private static ThirdPartySource Src(string url, bool enabled = true) =>
        new(SourceId.FromUrl(url), url, "", enabled, false);

    [Fact]
    public async Task Downloads_yaml_and_records_rule_count()
    {
        using var dir = new TempDir();
        var url = "https://x/a.yaml";
        var (svc, store) = New(new() { [url] = RuleYaml }, dir);
        var src = Src(url);

        await svc.SyncAsync([src]);

        Assert.Equal(RuleYaml, File.ReadAllText(Path.Combine(store.CacheDirFor(src.Id), "rules.yaml")));
        var state = ThirdPartySyncService.ReadState(store, src.Id);
        Assert.Equal(ThirdPartySyncStatus.Updated, state!.Status);
        Assert.Equal(1, state.RuleCount);
    }

    [Fact]
    public async Task One_source_failure_does_not_affect_others_and_keeps_last_cache()   // Review Focus 4
    {
        using var dir = new TempDir();
        var good = "https://x/good.yaml";
        var bad = "https://x/bad.yaml";
        var (svc, store) = New(new() { [good] = RuleYaml, [bad] = null }, dir);
        var goodSrc = Src(good);
        var badSrc = Src(bad);

        // 先给 bad 一份"上次成功"的缓存
        Directory.CreateDirectory(store.CacheDirFor(badSrc.Id));
        File.WriteAllText(Path.Combine(store.CacheDirFor(badSrc.Id), "rules.yaml"), "旧缓存");

        await svc.SyncAsync([goodSrc, badSrc]);

        // good 正常
        Assert.Equal(ThirdPartySyncStatus.Updated, ThirdPartySyncService.ReadState(store, goodSrc.Id)!.Status);
        // bad 失败但旧缓存保留
        Assert.Equal(ThirdPartySyncStatus.NetworkFailed, ThirdPartySyncService.ReadState(store, badSrc.Id)!.Status);
        Assert.Equal("旧缓存", File.ReadAllText(Path.Combine(store.CacheDirFor(badSrc.Id), "rules.yaml")));
    }

    [Fact]
    public async Task Empty_download_is_marked_empty_and_does_not_overwrite()
    {
        using var dir = new TempDir();
        var url = "https://x/e.yaml";
        var (svc, store) = New(new() { [url] = "   " }, dir);
        var src = Src(url);

        await svc.SyncAsync([src]);

        Assert.Equal(ThirdPartySyncStatus.Empty, ThirdPartySyncService.ReadState(store, src.Id)!.Status);
        Assert.False(File.Exists(Path.Combine(store.CacheDirFor(src.Id), "rules.yaml")));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter ThirdPartySyncServiceTests`
Expected: 编译失败（类型不存在）。

- [ ] **Step 3: 实现同步服务**

新建 `src/UpdateHelper.Core/Rules/ThirdParty/ThirdPartySyncService.cs`：

```csharp
using System.Text;
using System.Text.Json;
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Rules.ThirdParty;

public enum ThirdPartySyncStatus { Updated, NetworkFailed, Empty }

public sealed record ThirdPartySourceState(DateTimeOffset When, ThirdPartySyncStatus Status, int RuleCount, string? Detail);

/// <summary>按源下载 YAML 到各自缓存目录（不验签），写每源同步状态；某源失败只跳过它并保留旧缓存。</summary>
public sealed class ThirdPartySyncService(IDownloader downloader, RuleSourceStore store, Func<DateTimeOffset>? clock = null)
{
    public const string RulesFileName = "rules.yaml";
    private const string StateFileName = "source-state.json";
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public async Task SyncAsync(IEnumerable<ThirdPartySource> sources, CancellationToken ct = default)
    {
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            await SyncOneAsync(source, ct);
        }
    }

    private async Task SyncOneAsync(ThirdPartySource source, CancellationToken ct)
    {
        var dir = store.CacheDirFor(source.Id);
        var temp = Path.Combine(Path.GetTempPath(), "uh-tp-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            await downloader.DownloadAsync(source.Url, temp, null, ct);
            var text = await File.ReadAllTextAsync(temp, ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                SaveState(dir, new ThirdPartySourceState(_clock(), ThirdPartySyncStatus.Empty,
                    ReadStateInDir(dir)?.RuleCount ?? 0, "下载到的内容为空"));
                return;
            }
            var count = RuleParser.ParseAll(text, source.Url).Count(p => p.Ok);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, RulesFileName), text, new UTF8Encoding(false));   // 原样存
            SaveState(dir, new ThirdPartySourceState(_clock(), ThirdPartySyncStatus.Updated, count, null));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)   // 网络/IO 失败：保留旧缓存，只记状态
        {
            SaveState(dir, new ThirdPartySourceState(_clock(), ThirdPartySyncStatus.NetworkFailed,
                ReadStateInDir(dir)?.RuleCount ?? 0, ex.Message));
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static ThirdPartySourceState? ReadState(RuleSourceStore store, string id) =>
        ReadStateInDir(store.CacheDirFor(id));

    private static ThirdPartySourceState? ReadStateInDir(string dir)
    {
        try
        {
            var path = Path.Combine(dir, StateFileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ThirdPartySourceState>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void SaveState(string dir, ThirdPartySourceState state)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, StateFileName), JsonSerializer.Serialize(state));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter ThirdPartySyncServiceTests`
Expected: PASS（3 个）。

- [ ] **Step 5: 提交**

```bash
git add src/UpdateHelper.Core/Rules/ThirdParty/ThirdPartySyncService.cs tests/UpdateHelper.Core.Tests/ThirdParty/ThirdPartySyncServiceTests.cs
git commit -m "feat(core): 第三方源同步服务（按源下载 YAML、每源状态、失败隔离保留旧缓存）"
```

---

### Task 5: 确认服务抽象 + 安装前第三方警告 + 无 signer 禁装

新增 `IConfirmService`（Presentation 层可测试抽象），给 `UpdatesViewModel` 的安装流程加"第三方更新先确认再装"，并把"第三方缺 signer/silentArgs"的更新标为不可安装。

**Files:**
- Create: `src/UpdateHelper.Presentation/IConfirmService.cs`
- Modify: `src/UpdateHelper.Presentation/ViewModels/UpdatesViewModel.cs`
- Modify: `tests/UpdateHelper.Presentation.Tests/UpdatesViewModelTests.cs`
- Create: `tests/UpdateHelper.Presentation.Tests/FakeConfirmService.cs`

**Interfaces:**
- Consumes: `AppState.Snapshot`（已有 `ScanSnapshot.Rules`）、`Rule`/`RuleTrust`、`RuleUpdateSource.PackagePrefix`（= `"rule:"`）。
- Produces:
  - `interface IConfirmService { Task<bool> ConfirmAsync(string title, string message); Task InfoAsync(string title, string message); }`
  - `UpdatesViewModel` 构造函数签名变为 `UpdatesViewModel(AppState state, IConfirmService confirm)`。
  - `UpdateRow` 新增只读属性 `bool IsThirdParty`、`string SourceUrl`、`string? Signer`、`bool Installable`、`string? BlockReason`。

- [ ] **Step 1: 写失败测试**

新建 `tests/UpdateHelper.Presentation.Tests/FakeConfirmService.cs`：

```csharp
namespace UpdateHelper.Presentation.Tests;

public sealed class FakeConfirmService(bool answer = true) : IConfirmService
{
    public List<string> ConfirmMessages { get; } = [];
    public List<string> InfoMessages { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message)
    {
        ConfirmMessages.Add(message);
        return Task.FromResult(answer);
    }

    public Task InfoAsync(string title, string message)
    {
        InfoMessages.Add(message);
        return Task.CompletedTask;
    }
}
```

编辑 `tests/UpdateHelper.Presentation.Tests/UpdatesViewModelTests.cs`：把 `Make` 改为注入确认服务，并新增第三方相关用例。先改 `Make`（和它的返回元组）：

```csharp
    private static async Task<(UpdatesViewModel Vm, FakeBackend Backend, AppState State, FakeConfirmService Confirm)> Make(
        bool confirmAnswer = true, params JudgedUpdate[] updates)
    {
        var backend = new FakeBackend { Report = new UpdateReport(updates, null) };
        var state = new AppState(backend);
        await state.RefreshAsync();
        var confirm = new FakeConfirmService(confirmAnswer);
        return (new UpdatesViewModel(state, confirm), backend, state, confirm);
    }
```

> 现有调用处形如 `var (vm, _, _) = await Make(Update(...), ...)`。需要把这些调用改成具名/四元组，并把 updates 作为后续参数传入：`var (vm, _, _, _) = await Make(updates: [Update("QQ", UpdateTier.Low), ...]);`（逐个用例改）。保持断言不变。

末尾新增第三方用例（含一个辅助，用带第三方规则的快照）：

```csharp
    private static async Task<(UpdatesViewModel Vm, FakeBackend Backend, FakeConfirmService Confirm)> MakeThirdParty(
        string? signer, bool confirmAnswer = true)
    {
        var rule = new UpdateHelper.Core.Rules.Rule(
            "community.foo", "Foo",
            new UpdateHelper.Core.Rules.RuleMatch("Foo*", null), null,
            new UpdateHelper.Core.Rules.UpdateRule(null,
                new UpdateHelper.Core.Rules.UpdateLatest("2.0", "https://x/setup.exe"),
                signer, signer is null ? null : "/S", UpdateHelper.Core.Rules.RiskLevel.Normal),
            [], [])
        {
            Trust = UpdateHelper.Core.Rules.RuleTrust.ThirdParty,
            SourceUrl = "https://src/rules.yaml",
            SourceName = "社区源",
        };
        var rules = new UpdateHelper.Core.Rules.RuleSet([rule], [], []);
        var snapshot = new ScanSnapshot(
            UpdateHelper.Core.Grouping.SoftwareGrouper.Group([], []), rules,
            new Dictionary<UpdateHelper.Core.Scanning.BackgroundItem, string>(), []);
        var candidate = new UpdateCandidate("rule:community.foo", "Foo", null, "1.0", "2.0", ["Foo"]);
        var update = new JudgedUpdate(candidate, null, UpdateTier.Low, "新版本");
        var backend = new FakeBackend { Snapshot = snapshot, Report = new UpdateReport([update], null) };
        var state = new AppState(backend);
        await state.RefreshAsync();
        var confirm = new FakeConfirmService(confirmAnswer);
        return (new UpdatesViewModel(state, confirm), backend, confirm);
    }

    [Fact]
    public async Task Third_party_update_shows_source_and_signer_and_confirms_before_install()
    {
        var (vm, backend, confirm) = await MakeThirdParty(signer: "Some Publisher");
        vm.Rows[0].IsSelected = true;
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Single(confirm.ConfirmMessages);
        Assert.Contains("未经审核", confirm.ConfirmMessages[0]);
        Assert.Contains("https://src/rules.yaml", confirm.ConfirmMessages[0]);
        Assert.Contains("Some Publisher", confirm.ConfirmMessages[0]);
        Assert.Contains("rule:community.foo", backend.Installed);   // 确认后才装
    }

    [Fact]
    public async Task Third_party_update_cancelled_is_not_installed()
    {
        var (vm, backend, _) = await MakeThirdParty(signer: "Some Publisher", confirmAnswer: false);
        vm.Rows[0].IsSelected = true;
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.DoesNotContain("rule:community.foo", backend.Installed);
        Assert.Contains("取消", vm.Rows[0].Status);
    }

    [Fact]
    public async Task Third_party_rule_without_signer_is_not_installable()   // Review Focus 2
    {
        var (vm, _, _) = await MakeThirdParty(signer: null);
        Assert.False(vm.Rows[0].Installable);
        Assert.False(vm.Rows[0].CanSelect);
        Assert.Contains("签名者", vm.Rows[0].BlockReason);
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests --filter UpdatesViewModelTests`
Expected: 编译失败（`IConfirmService` / 新构造签名 / 新属性不存在）。

- [ ] **Step 3: 新增 `IConfirmService`**

新建 `src/UpdateHelper.Presentation/IConfirmService.cs`：

```csharp
namespace UpdateHelper.Presentation;

/// <summary>向用户确认 / 提示的抽象。App 用 WPF-UI 对话框实现，绝不触发阻塞式系统弹窗。</summary>
public interface IConfirmService
{
    /// <summary>显示带"确定/取消"的确认框；用户点确定返回 true。</summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>显示只读提示框（单个"知道了"按钮）。</summary>
    Task InfoAsync(string title, string message);
}
```

- [ ] **Step 4: 改 `UpdatesViewModel`**

编辑 `src/UpdateHelper.Presentation/ViewModels/UpdatesViewModel.cs`：

(a) `using` 补 `using UpdateHelper.Core.Rules;`。

(b) `UpdateRow` 构造改为接收匹配到的规则，并计算第三方相关属性：

```csharp
public sealed partial class UpdateRow : ObservableObject
{
    public UpdateRow(JudgedUpdate update, Rule? rule)
    {
        Update = update;
        IsThirdParty = rule?.Trust == RuleTrust.ThirdParty;
        SourceUrl = rule?.SourceUrl ?? "";
        Signer = rule?.Update?.Signer;

        // 第三方规则缺 signer / silentArgs → 能识别、能显示"有新版"，但不能一键安装
        if (IsThirdParty && (string.IsNullOrWhiteSpace(rule!.Update?.Signer) || string.IsNullOrWhiteSpace(rule.Update?.SilentArgs)))
            BlockReason = "该规则未声明签名者，无法一键安装";

        Installable = BlockReason is null;
        CanSelect = Installable && update.Tier is UpdateTier.Low or UpdateTier.Careful;
        IsSelected = CanSelect && update.Tier == UpdateTier.Low;   // 默认只勾可装的低风险
    }

    public JudgedUpdate Update { get; }
    public bool IsThirdParty { get; }
    public string SourceUrl { get; }
    public string? Signer { get; }
    public bool Installable { get; }
    public string? BlockReason { get; }

    public string Name => Update.Group?.Name ?? Update.Candidate.Name;
    public string Versions => $"{Update.Candidate.InstalledVersion} → {Update.Candidate.AvailableVersion}";
    public string TierName => Display.TierName(Update.Tier);
    public string Reason => Update.Reason;

    public bool CanSelect { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _status = "";

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !CanSelect) IsSelected = false;
    }
}
```

(c) `UpdatesViewModel`：加 `IConfirmService` 字段、改构造、`Rebuild` 里查规则、`InstallSelectedAsync` 里对第三方行先确认：

```csharp
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly IConfirmService _confirm;

    public UpdatesViewModel(AppState state, IConfirmService confirm)
    {
        _state = state;
        _confirm = confirm;
        _state.PropertyChanged += OnStateChanged;
        Rebuild();
    }
```

`InstallSelectedAsync` 的 `foreach (var row in selected)` 循环体最前面加（在 `row.Status = "正在更新……";` 之前）：

```csharp
                if (row.IsThirdParty)
                {
                    var ok = await _confirm.ConfirmAsync("第三方规则更新",
                        $"此更新来自未经审核的第三方规则源，内容与安装包都由对方负责，风险自负。\n\n" +
                        $"来源：{row.SourceUrl}\n发布者（签名校验目标）：{row.Signer ?? "（未声明）"}\n\n确定要安装 {row.Name} 吗？");
                    if (!ok) { row.Status = "已取消"; results.Add($"{row.Name}：已取消"); continue; }
                }
```

`Rebuild` 里构造 `UpdateRow` 处改为查规则：

```csharp
    private void Rebuild()
    {
        foreach (var row in Rows) row.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        foreach (var update in _state.Updates.Where(u => u.Tier != UpdateTier.Ignored))
        {
            var row = new UpdateRow(update, FindRule(update));
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
        SelectionChanged();
    }

    private Rule? FindRule(JudgedUpdate update)
    {
        var id = update.Candidate.PackageId;
        if (!id.StartsWith(UpdateHelper.Core.Updates.RuleUpdateSource.PackagePrefix, StringComparison.Ordinal))
            return null;
        var ruleId = id[UpdateHelper.Core.Updates.RuleUpdateSource.PackagePrefix.Length..];
        return _state.Snapshot?.Rules.Rules.FirstOrDefault(r => r.Id == ruleId);
    }
```

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests --filter UpdatesViewModelTests`
Expected: PASS（原有 + 3 个新用例）。

- [ ] **Step 6: 提交**

```bash
git add src/UpdateHelper.Presentation/IConfirmService.cs src/UpdateHelper.Presentation/ViewModels/UpdatesViewModel.cs tests/UpdateHelper.Presentation.Tests/FakeConfirmService.cs tests/UpdateHelper.Presentation.Tests/UpdatesViewModelTests.cs
git commit -m "feat(presentation): 确认服务抽象 + 第三方更新安装前强警告 + 无签名者禁装"
```

---

### Task 6: 第三方源管理 ViewModel（`RuleSourcesViewModel`）

「规则」页第三方区的界面逻辑：列表、添加（校验 https）、启用/覆盖/删除/全局开关及各自的确认提示。

**Files:**
- Modify: `src/UpdateHelper.Presentation/IAppBackend.cs`（加第三方相关方法 + `ThirdPartySourceStatus` 记录）
- Create: `src/UpdateHelper.Presentation/ViewModels/RuleSourcesViewModel.cs`
- Modify: `tests/UpdateHelper.Presentation.Tests/FakeBackend.cs`（实现新方法）
- Create: `tests/UpdateHelper.Presentation.Tests/RuleSourcesViewModelTests.cs`

**Interfaces:**
- Consumes: `IConfirmService`（Task 5）、`ThirdPartyConfig`/`ThirdPartySource`/`SourceId`（Task 3）。
- Produces（加到 `IAppBackend`）：
  - `record ThirdPartySourceStatus(string Id, string Url, string Note, bool Enabled, bool AllowOverrideOfficial, int RuleCount, string StatusText)`（放在 `IAppBackend.cs` 内，命名空间 `UpdateHelper.Presentation`）
  - `ThirdPartyConfig GetThirdPartyConfig();`
  - `void SaveThirdPartyConfig(ThirdPartyConfig config);`
  - `IReadOnlyList<ThirdPartySourceStatus> GetThirdPartySourceStatuses();`
  - `Task SyncThirdPartyAsync(CancellationToken ct);`
  - `RuleSourcesViewModel(IAppBackend backend, IConfirmService confirm)`，含 `ObservableCollection<ThirdPartySourceRow> Sources`、`bool GlobalEnabled`、`string NewSourceUrl`、`string NewSourceNote`、`Task AddSourceAsync()`、`Task RefreshAsync()`、`Task SyncAsync()`，以及行操作 `Task SetEnabledAsync(row,bool)`/`Task SetOverrideAsync(row,bool)`/`Task DeleteAsync(row)`、`Task SetGlobalEnabledAsync(bool)`。

- [ ] **Step 1: 写失败测试**

先在 `IAppBackend.cs`、`FakeBackend.cs` 加方法（见 Step 3/4），否则测试无法编译。本步只写测试文件 `tests/UpdateHelper.Presentation.Tests/RuleSourcesViewModelTests.cs`：

```csharp
using UpdateHelper.Core.Rules.ThirdParty;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.Presentation.Tests;

public class RuleSourcesViewModelTests
{
    private static (RuleSourcesViewModel Vm, FakeBackend Backend, FakeConfirmService Confirm) Make(
        ThirdPartyConfig? config = null, bool confirmAnswer = true)
    {
        var backend = new FakeBackend { ThirdPartyConfig = config ?? ThirdPartyConfig.Default };
        var confirm = new FakeConfirmService(confirmAnswer);
        return (new RuleSourcesViewModel(backend, confirm), backend, confirm);
    }

    [Fact]
    public async Task Add_source_rejects_non_https()
    {
        var (vm, backend, _) = Make();
        vm.NewSourceUrl = "http://x/rules.yaml";   // 非 https
        await vm.AddSourceAsync();
        Assert.Empty(backend.ThirdPartyConfig.Sources);
        Assert.Contains("https", vm.AddError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Add_source_accepts_https_and_saves()
    {
        var (vm, backend, _) = Make();
        vm.NewSourceUrl = "https://x/rules.yaml";
        vm.NewSourceNote = "社区源";
        await vm.AddSourceAsync();
        var s = Assert.Single(backend.ThirdPartyConfig.Sources);
        Assert.Equal("https://x/rules.yaml", s.Url);
        Assert.True(s.Enabled);
        Assert.False(s.AllowOverrideOfficial);
        Assert.True(backend.SyncThirdPartyCalled);
    }

    private static ThirdPartyConfig OneSource(bool enabled = true, bool allowOverride = false) =>
        new(true, [new ThirdPartySource(SourceId.FromUrl("https://x/a.yaml"), "https://x/a.yaml", "A", enabled, allowOverride)]);

    [Fact]
    public async Task Disable_source_asks_confirmation_and_reverts_on_cancel()
    {
        var (vm, backend, confirm) = Make(OneSource(), confirmAnswer: false);
        await vm.RefreshAsync();
        await vm.SetEnabledAsync(vm.Sources[0], false);
        Assert.Single(confirm.ConfirmMessages);                     // 弹了确认
        Assert.True(backend.ThirdPartyConfig.Sources[0].Enabled);   // 取消 → 未改
    }

    [Fact]
    public async Task Enable_override_asks_confirmation_before_applying()
    {
        var (vm, backend, confirm) = Make(OneSource(), confirmAnswer: true);
        await vm.RefreshAsync();
        await vm.SetOverrideAsync(vm.Sources[0], true);
        Assert.Contains(confirm.ConfirmMessages, m => m.Contains("覆盖"));
        Assert.True(backend.ThirdPartyConfig.Sources[0].AllowOverrideOfficial);
    }

    [Fact]
    public async Task Delete_source_confirms_then_saves_without_it()
    {
        var (vm, backend, confirm) = Make(OneSource(), confirmAnswer: true);
        await vm.RefreshAsync();
        await vm.DeleteAsync(vm.Sources[0]);
        Assert.Single(confirm.ConfirmMessages);
        Assert.Empty(backend.ThirdPartyConfig.Sources);
    }

    [Fact]
    public async Task Turning_global_off_with_sources_asks_confirmation()
    {
        var (vm, backend, confirm) = Make(OneSource(), confirmAnswer: false);
        await vm.RefreshAsync();
        await vm.SetGlobalEnabledAsync(false);
        Assert.Single(confirm.ConfirmMessages);
        Assert.True(backend.ThirdPartyConfig.ThirdPartyEnabled);   // 取消 → 仍开
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests --filter RuleSourcesViewModelTests`
Expected: 编译失败。

- [ ] **Step 3: 扩展 `IAppBackend`**

编辑 `src/UpdateHelper.Presentation/IAppBackend.cs`：顶部补 `using UpdateHelper.Core.Rules.ThirdParty;`；在文件内（`ScanSnapshot` 记录旁）加：

```csharp
/// <summary>第三方源在界面上要显示的状态。</summary>
public sealed record ThirdPartySourceStatus(
    string Id, string Url, string Note, bool Enabled, bool AllowOverrideOfficial, int RuleCount, string StatusText);
```

在 `interface IAppBackend` 末尾加：

```csharp
    /// <summary>读取第三方源配置（缺失/损坏回退默认）。</summary>
    ThirdPartyConfig GetThirdPartyConfig();

    /// <summary>保存第三方源配置，并清理不再引用的缓存目录。</summary>
    void SaveThirdPartyConfig(ThirdPartyConfig config);

    /// <summary>读取各第三方源的展示状态（规则数、上次同步结果）。</summary>
    IReadOnlyList<ThirdPartySourceStatus> GetThirdPartySourceStatuses();

    /// <summary>同步所有启用的第三方源；失败按源隔离，不抛。</summary>
    Task SyncThirdPartyAsync(CancellationToken ct);
```

- [ ] **Step 4: `FakeBackend` 实现新方法**

编辑 `tests/UpdateHelper.Presentation.Tests/FakeBackend.cs`：顶部补 `using UpdateHelper.Core.Rules.ThirdParty;`；类内加：

```csharp
    public ThirdPartyConfig ThirdPartyConfig { get; set; } = ThirdPartyConfig.Default;
    public bool SyncThirdPartyCalled { get; private set; }
    public List<ThirdPartySourceStatus> ThirdPartyStatuses { get; } = [];

    public ThirdPartyConfig GetThirdPartyConfig() => ThirdPartyConfig;
    public void SaveThirdPartyConfig(ThirdPartyConfig config) => ThirdPartyConfig = config;
    public IReadOnlyList<ThirdPartySourceStatus> GetThirdPartySourceStatuses() => ThirdPartyStatuses;
    public Task SyncThirdPartyAsync(CancellationToken ct) { SyncThirdPartyCalled = true; return Task.CompletedTask; }
```

- [ ] **Step 5: 实现 `RuleSourcesViewModel`**

新建 `src/UpdateHelper.Presentation/ViewModels/RuleSourcesViewModel.cs`：

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpdateHelper.Core.Rules.ThirdParty;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>第三方源列表里的一行（只读展示 + 当前开关值）。</summary>
public sealed partial class ThirdPartySourceRow(ThirdPartySourceStatus status) : ObservableObject
{
    public string Id => status.Id;
    public string Url => status.Url;
    public string Note => string.IsNullOrWhiteSpace(status.Note) ? status.Url : status.Note;
    public bool Enabled => status.Enabled;
    public bool AllowOverrideOfficial => status.AllowOverrideOfficial;
    public string StateText => $"{status.RuleCount} 条规则 · {status.StatusText}";
}

/// <summary>「规则」页第三方源管理：增删、启用/覆盖/全局开关（消费性操作走确认框）。</summary>
public sealed partial class RuleSourcesViewModel(IAppBackend backend, IConfirmService confirm) : ObservableObject
{
    [ObservableProperty] private bool _globalEnabled = true;
    [ObservableProperty] private string _newSourceUrl = "";
    [ObservableProperty] private string _newSourceNote = "";
    [ObservableProperty] private string _addError = "";

    public ObservableCollection<ThirdPartySourceRow> Sources { get; } = [];

    public string RiskNotice => "第三方源未经审核，内容和更新包都由对方负责，风险自负。";

    public async Task RefreshAsync()
    {
        var config = await Task.Run(backend.GetThirdPartyConfig);
        GlobalEnabled = config.ThirdPartyEnabled;
        var statuses = await Task.Run(backend.GetThirdPartySourceStatuses);
        Sources.Clear();
        foreach (var s in statuses) Sources.Add(new ThirdPartySourceRow(s));
    }

    public async Task SyncAsync()
    {
        await backend.SyncThirdPartyAsync(CancellationToken.None);
        await RefreshAsync();
    }

    [RelayCommand]
    public async Task AddSourceAsync()
    {
        AddError = "";
        var url = (NewSourceUrl ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
        {
            AddError = "请填写 https:// 开头的规则文件地址";
            return;
        }
        var config = backend.GetThirdPartyConfig();
        if (config.Sources.Any(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            AddError = "这个源已经添加过了";
            return;
        }
        var added = new ThirdPartySource(SourceId.FromUrl(url), url, (NewSourceNote ?? "").Trim(), true, false);
        backend.SaveThirdPartyConfig(config with { Sources = [.. config.Sources, added] });
        NewSourceUrl = "";
        NewSourceNote = "";
        await backend.SyncThirdPartyAsync(CancellationToken.None);
        await RefreshAsync();
    }

    public async Task SetGlobalEnabledAsync(bool value)
    {
        var config = backend.GetThirdPartyConfig();
        if (!value && config.Sources.Count > 0)   // 关闭且有源 → 确认
        {
            var ok = await confirm.ConfirmAsync("关闭第三方规则源",
                "关闭后，你添加的第三方规则将全部停用，相关软件可能变回认不出 / 不再提示更新。确定关闭吗？");
            if (!ok) { GlobalEnabled = true; return; }
        }
        backend.SaveThirdPartyConfig(config with { ThirdPartyEnabled = value });
        await RefreshAsync();
    }

    public async Task SetEnabledAsync(ThirdPartySourceRow row, bool value)
    {
        if (!value)
        {
            var ok = await confirm.ConfirmAsync("停用这个源",
                $"停用「{row.Note}」后，它的规则将不再生效，相关软件可能变回认不出 / 不再提示更新。确定停用吗？");
            if (!ok) { await RefreshAsync(); return; }
        }
        Mutate(row.Id, s => s with { Enabled = value });
        await RefreshAsync();
    }

    public async Task SetOverrideAsync(ThirdPartySourceRow row, bool value)
    {
        if (value)
        {
            var ok = await confirm.ConfirmAsync("允许覆盖官方规则",
                $"打开后，「{row.Note}」的规则会覆盖同名的官方规则（含安装包的发布者校验目标）。请确认你信任这个源。确定打开吗？");
            if (!ok) { await RefreshAsync(); return; }
        }
        Mutate(row.Id, s => s with { AllowOverrideOfficial = value });
        await RefreshAsync();
    }

    public async Task DeleteAsync(ThirdPartySourceRow row)
    {
        var ok = await confirm.ConfirmAsync("删除这个源",
            $"确定删除「{row.Note}」吗？它的本地缓存也会一起清理。");
        if (!ok) return;
        var config = backend.GetThirdPartyConfig();
        backend.SaveThirdPartyConfig(config with { Sources = config.Sources.Where(s => s.Id != row.Id).ToList() });
        await RefreshAsync();
    }

    private void Mutate(string id, Func<ThirdPartySource, ThirdPartySource> change)
    {
        var config = backend.GetThirdPartyConfig();
        backend.SaveThirdPartyConfig(config with
        {
            Sources = config.Sources.Select(s => s.Id == id ? change(s) : s).ToList(),
        });
    }
}
```

> 说明：`SetGlobalEnabledAsync`/`SetEnabledAsync`/`SetOverrideAsync` 不做成 `partial void OnXxxChanged`，因为取消时要把开关视觉状态拨回去——由 App 侧把 `ToggleSwitch` 的事件接到这些方法、在取消分支用 `RefreshAsync()` 重建行来回弹（见 Task 8）。

- [ ] **Step 6: 跑测试确认通过**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests --filter RuleSourcesViewModelTests`
Expected: PASS（7 个）。

- [ ] **Step 7: 提交**

```bash
git add src/UpdateHelper.Presentation/IAppBackend.cs src/UpdateHelper.Presentation/ViewModels/RuleSourcesViewModel.cs tests/UpdateHelper.Presentation.Tests/FakeBackend.cs tests/UpdateHelper.Presentation.Tests/RuleSourcesViewModelTests.cs
git commit -m "feat(presentation): 第三方源管理 ViewModel（增删/启用/覆盖/全局开关，消费性操作走确认框）"
```

---

### Task 7: `RealBackend` 接入第三方（统一加载 + 配置/状态/同步实现）

让 `RealBackend` 的两处规则加载都含第三方源，并实现 `IAppBackend` 的四个第三方方法。

**Files:**
- Modify: `src/UpdateHelper.App/RealBackend.cs`
- Test: 无新单测（第三方映射/优先级/同步的逻辑已分别由 Task 2/3/4 覆盖）；本任务靠编译 + 全量测试 + Task 8 冒烟验证。

**Interfaces:**
- Consumes: `RuleSourceStore`/`ThirdPartyConfig`（Task 3）、`ThirdPartySyncService`/`ThirdPartySourceState`（Task 4）、`ThirdPartySourceStatus`（Task 6）、`RuleSetLoader`/`RuleSource`（Task 2）。
- Produces: `RealBackend` 实现全部新 `IAppBackend` 方法；内部统一的 `BuildSources()`。

- [ ] **Step 1: 改 `RealBackend`**

编辑 `src/UpdateHelper.App/RealBackend.cs`：

(a) 顶部补 `using UpdateHelper.Core.Rules.ThirdParty;`。

(b) 构造函数加第三方存储（给默认路径，便于测试可注入）：

```csharp
public sealed class RealBackend(string rulesDirectory, RuleSourceStore? sourceStore = null) : IAppBackend
{
    private readonly RuleSourceStore _sourceStore =
        sourceStore ?? new RuleSourceStore(RuleSourceStore.DefaultConfigPath, RuleSourceStore.DefaultCacheRoot);
    private volatile ScanSnapshot? _lastSnapshot;
```

(c) 把 `LoadRules()` 改成统一组装（官方 + 已启用第三方）：

```csharp
    /// <summary>官方规则（内置/缓存择新）+ 所有已启用的第三方源。扫描和安装共用，避免"能检测不能装"。</summary>
    private RuleSet LoadRules() => RuleSetLoader.Load(BuildSources());

    private IReadOnlyList<RuleSource> BuildSources()
    {
        var officialDir = OfficialRules.SelectDirectory(rulesDirectory, RuleSubscriptionConfig.DefaultCacheDirectory);
        var sources = new List<RuleSource> { new(officialDir, RuleTrust.Official) };
        sources.AddRange(_sourceStore.BuildRuleSources(_sourceStore.Read()));
        return sources;
    }
```

> `InstallAsync` 里现有的 `var rules = LoadRules();` 不用改——它现在自动含第三方。`GetRuleSubscriptionInfo` 里那次 `RuleSetLoader.Load([new RuleSource(officialDir, …)])` 仅用于**统计官方规则数**，保持只数官方，不要动。

(d) 文件末尾（类内）实现第三方四方法：

```csharp
    public ThirdPartyConfig GetThirdPartyConfig() => _sourceStore.Read();

    public void SaveThirdPartyConfig(ThirdPartyConfig config)
    {
        _sourceStore.Write(config);
        _sourceStore.CleanOrphanCaches(config);   // 删源 / 改 URL 的残留一并清理
    }

    public IReadOnlyList<ThirdPartySourceStatus> GetThirdPartySourceStatuses()
    {
        var config = _sourceStore.Read();
        var list = new List<ThirdPartySourceStatus>();
        foreach (var s in config.Sources)
        {
            var state = ThirdPartySyncService.ReadState(_sourceStore, s.Id);
            var statusText = state?.Status switch
            {
                ThirdPartySyncStatus.Updated => $"上次同步：{state.When.LocalDateTime:yyyy-MM-dd HH:mm}",
                ThirdPartySyncStatus.NetworkFailed => "上次同步失败（连不上），仍用上次缓存",
                ThirdPartySyncStatus.Empty => "源内容为空",
                _ => "尚未同步",
            };
            list.Add(new ThirdPartySourceStatus(s.Id, s.Url, s.Note, s.Enabled, s.AllowOverrideOfficial,
                state?.RuleCount ?? 0, statusText));
        }
        return list;
    }

    public Task SyncThirdPartyAsync(CancellationToken ct)
    {
        var config = _sourceStore.Read();
        if (!config.ThirdPartyEnabled) return Task.CompletedTask;
        var enabled = config.Sources.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0) return Task.CompletedTask;
        var svc = new ThirdPartySyncService(new HttpDownloader(), _sourceStore);
        return Task.Run(() => svc.SyncAsync(enabled, ct), ct);
    }
```

- [ ] **Step 2: 跑测试（Core + Presentation）**

Run: `dotnet test tests/UpdateHelper.Core.Tests` 与 `dotnet test tests/UpdateHelper.Presentation.Tests`
Expected: 两个测试项目全绿（含前面各任务新增）。

> 说明：App 项目此时**还不能整体编译**——`AppHost` 仍以旧签名 `new UpdatesViewModel(State)` 调用（Task 5 已改签名），要等 Task 8 改 `AppHost` 才恢复。测试项目只依赖 Core/Presentation、不依赖 App，所以用上面两个分项目测试验证本任务；`RealBackend` 的整体编译在 Task 8 Step 6 的 `dotnet build` 一并确认。本任务只需保证 `RealBackend.cs` 自身对接口的实现正确（靠评审看 diff）。

- [ ] **Step 3: 提交**

```bash
git add src/UpdateHelper.App/RealBackend.cs
git commit -m "feat(app): RealBackend 统一加载官方+第三方规则，实现第三方配置/状态/同步"
```

---

### Task 8: App 接线（WPF-UI 确认框 + 「规则」页第三方 UI + 同步触发 + 收敛遗留 MessageBox）

把前面的抽象接到真实界面：WPF-UI 实现 `IConfirmService`、组合根注入、规则页第三方区、启动/每日/手动三处触发第三方同步、替换遗留阻塞式 `MessageBox`。此任务以**编译 + 真机冒烟**验证为主。

**Files:**
- Create: `src/UpdateHelper.App/WpfUiConfirmService.cs`
- Modify: `src/UpdateHelper.App/AppHost.cs`
- Modify: `src/UpdateHelper.App/App.xaml.cs`
- Modify: `src/UpdateHelper.App/Pages/RulesPage.xaml` 与 `RulesPage.xaml.cs`
- Modify: `src/UpdateHelper.App/Pages/SoftwarePage.xaml.cs`

**Interfaces:**
- Consumes: `IConfirmService`（Task 5）、`RuleSourcesViewModel`（Task 6）、`IAppBackend.SyncThirdPartyAsync`（Task 7）。

- [ ] **Step 1: 实现 `WpfUiConfirmService`（WPF-UI，非系统弹窗）**

新建 `src/UpdateHelper.App/WpfUiConfirmService.cs`：

```csharp
using UpdateHelper.Presentation;
using Wpf.Ui.Controls;

namespace UpdateHelper.App;

/// <summary>用 WPF-UI 的 MessageBox（主题化、非系统阻塞弹窗）实现确认/提示。始终在 UI 线程显示。</summary>
public sealed class WpfUiConfirmService : IConfirmService
{
    public Task<bool> ConfirmAsync(string title, string message) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
            };
            var result = await box.ShowDialogAsync();
            return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }).Task.Unwrap();

    public Task InfoAsync(string title, string message) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                CloseButtonText = "知道了",
            };
            await box.ShowDialogAsync();
        }).Task.Unwrap();
}
```

> 若 WPF-UI 4.3 的 `MessageBox`/`MessageBoxResult` 命名空间或按钮 API 与此处不符，按编译器提示调整为该版本的等价 API（仍必须是 WPF-UI 的对话框，不得用 `System.Windows.MessageBox`）。

- [ ] **Step 2: 组合根注入**

编辑 `src/UpdateHelper.App/AppHost.cs`：加静态属性并在 `Initialize` 里创建：

```csharp
    public static RuleSourcesViewModel RuleSources { get; private set; } = null!;
    internal static IConfirmService Confirm { get; private set; } = null!;
```

`Initialize` 里（`Updates = …` 那行前后）：

```csharp
        Confirm = new WpfUiConfirmService();
        Updates = new UpdatesViewModel(State, Confirm);
        ...
        RuleSources = new RuleSourcesViewModel(backend, Confirm);
```

（`using UpdateHelper.Presentation;` 已在文件顶部。）

- [ ] **Step 3: 规则页第三方 UI**

编辑 `src/UpdateHelper.App/Pages/RulesPage.xaml`，在官方源 `ui:Card` 与"立即同步"之间插入第三方区（DataContext 仍是 `AppHost.RuleSubscription`，第三方区显式绑到 `AppHost.RuleSources`）：

```xml
            <TextBlock Text="第三方规则源" FontSize="20" FontWeight="SemiBold" Margin="0,24,0,8" />
            <ui:Card DataContext="{x:Static app:AppHost.RuleSources}">
                <StackPanel>
                    <StackPanel Orientation="Horizontal">
                        <ui:ToggleSwitch IsChecked="{Binding GlobalEnabled, Mode=OneWay}" Click="OnGlobalToggle"
                                         Content="启用第三方规则源" />
                    </StackPanel>
                    <TextBlock Text="{Binding RiskNotice}" Margin="0,8,0,0" Opacity="0.7" TextWrapping="Wrap" />

                    <ItemsControl ItemsSource="{Binding Sources}" Margin="0,12,0,0">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <Border Margin="0,0,0,8" Padding="12" CornerRadius="6"
                                        Background="{DynamicResource ControlFillColorDefaultBrush}">
                                    <StackPanel>
                                        <TextBlock Text="{Binding Note}" FontWeight="SemiBold" TextWrapping="Wrap" />
                                        <TextBlock Text="{Binding Url}" Opacity="0.6" FontSize="12" TextWrapping="Wrap" />
                                        <TextBlock Text="{Binding StateText}" Opacity="0.7" FontSize="12" Margin="0,4,0,0" />
                                        <StackPanel Orientation="Horizontal" Margin="0,6,0,0">
                                            <ui:ToggleSwitch IsChecked="{Binding Enabled, Mode=OneWay}" Click="OnSourceToggle" Content="启用" />
                                            <ui:ToggleSwitch IsChecked="{Binding AllowOverrideOfficial, Mode=OneWay}" Click="OnOverrideToggle"
                                                             Content="允许覆盖官方" Margin="16,0,0,0" />
                                            <ui:Button Content="删除" Click="OnDeleteSource" Margin="16,0,0,0" />
                                        </StackPanel>
                                    </StackPanel>
                                </Border>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>

                    <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                        <ui:TextBox x:Name="NewUrl" PlaceholderText="https:// 规则文件地址" Width="360"
                                    Text="{Binding NewSourceUrl, UpdateSourceTrigger=PropertyChanged}" />
                        <ui:TextBox x:Name="NewNote" PlaceholderText="备注（可选）" Width="160" Margin="8,0,0,0"
                                    Text="{Binding NewSourceNote, UpdateSourceTrigger=PropertyChanged}" />
                        <ui:Button Content="添加源" Margin="8,0,0,0" Click="OnAddSource" />
                    </StackPanel>
                    <TextBlock Text="{Binding AddError}" Foreground="#E53935" Margin="0,6,0,0"
                               Visibility="{Binding AddError, Converter={StaticResource StringToVisibility}}" TextWrapping="Wrap" />
                </StackPanel>
            </ui:Card>
```

> `StringToVisibility` 若项目里没有现成转换器，去掉该 `Visibility` 绑定即可（`AddError` 为空时显示空文本，无碍）。优先不引入新转换器。

编辑 `src/UpdateHelper.App/Pages/RulesPage.xaml.cs`，`OnLoaded` 里补加载第三方、并加事件处理（ToggleSwitch 的 `Click` 发生在 `IsChecked` 已翻转之后，用其新值调用 VM，取消分支 VM 会 `RefreshAsync` 把视觉拨回）：

```csharp
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = AppHost.RuleSubscription.RefreshAsync();
        await AppHost.RuleSources.RefreshAsync();
    }

    private async void OnSync(object sender, RoutedEventArgs e)
    {
        _ = AppHost.RuleSubscription.SyncAsync();
        await AppHost.RuleSources.SyncAsync();
    }

    private async void OnAddSource(object sender, RoutedEventArgs e) => await AppHost.RuleSources.AddSourceAsync();

    private async void OnGlobalToggle(object sender, RoutedEventArgs e)
        => await AppHost.RuleSources.SetGlobalEnabledAsync(((Wpf.Ui.Controls.ToggleSwitch)sender).IsChecked == true);

    private async void OnSourceToggle(object sender, RoutedEventArgs e)
    {
        var sw = (Wpf.Ui.Controls.ToggleSwitch)sender;
        if (sw.DataContext is UpdateHelper.Presentation.ViewModels.ThirdPartySourceRow row)
            await AppHost.RuleSources.SetEnabledAsync(row, sw.IsChecked == true);
    }

    private async void OnOverrideToggle(object sender, RoutedEventArgs e)
    {
        var sw = (Wpf.Ui.Controls.ToggleSwitch)sender;
        if (sw.DataContext is UpdateHelper.Presentation.ViewModels.ThirdPartySourceRow row)
            await AppHost.RuleSources.SetOverrideAsync(row, sw.IsChecked == true);
    }

    private async void OnDeleteSource(object sender, RoutedEventArgs e)
    {
        if (((System.Windows.FrameworkElement)sender).DataContext is UpdateHelper.Presentation.ViewModels.ThirdPartySourceRow row)
            await AppHost.RuleSources.DeleteAsync(row);
    }
```

- [ ] **Step 4: 三处触发第三方同步**

编辑 `src/UpdateHelper.App/App.xaml.cs`：

启动同步块（`if (!_startedAtLogin) { … SyncAsync() }`）里追加一行：

```csharp
            _ = Task.Run(() => AppHost.Backend.SyncThirdPartyAsync(CancellationToken.None));
```

每分钟 tick 的规则同步后台块里，在 `await AppHost.RuleSubscription.SyncAsync();` 之后追加：

```csharp
                        await AppHost.Backend.SyncThirdPartyAsync(CancellationToken.None);
```

（手动「立即同步」已在 Step 3 的 `OnSync` 里接上。）需要 `using System.Threading;` 若尚未引入（`CancellationToken`）。

- [ ] **Step 5: 收敛遗留 MessageBox**

编辑 `src/UpdateHelper.App/Pages/SoftwarePage.xaml.cs`，把阻塞式系统弹窗换成 WPF-UI 提示：

```csharp
    private async void ViewLeftovers_Click(object sender, RoutedEventArgs e)
    {
        if (SoftwareGrid.SelectedItem is not SoftwareRow row)
        {
            await AppHost.Confirm.InfoAsync("查看残留", "请先在列表里选一个软件。");
            return;
        }
        new LeftoversWindow(row.Group) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
```

> `AppHost.Confirm` 是 `internal`，`SoftwarePage` 同程序集可访问。

- [ ] **Step 6: 编译 + 全量测试 + 冒烟**

Run: `dotnet build` 与 `dotnet test`
Expected: 全绿。

发布版冒烟：
```powershell
dotnet publish src/UpdateHelper.App -c Release -r win-x64 --self-contained true -o publish
```
启动 `publish/UpdateHelper.exe`，到「规则」页手动验：
1. 添加一个 https 源（可先在本地或 Gist 放一个含 1~2 条规则的 yaml），看它出现在列表、规则数>0；填 http 开头应被拒并提示。
2. 关「启用第三方规则源」总开关 → 弹 WPF-UI 确认框；取消则开关弹回。
3. 对某源打开「允许覆盖官方」→ 弹确认框说明后果。
4. 删除一个源 → 弹确认框；确认后列表移除、`%LOCALAPPDATA%\UpdateHelper\thirdparty-cache\<id>` 被清。
5. 让某第三方规则命中一个已装软件并显示"有新版"，点安装 → 弹"未经审核的第三方规则源"警告（含来源 URL + signer）；缺 signer 的第三方更新不可勾选并有提示。
6. 「我的软件」不选软件点"查看残留" → 是 WPF-UI 提示框，不是系统弹窗。

- [ ] **Step 7: 提交**

```bash
git add src/UpdateHelper.App/
git commit -m "feat(app): WPF-UI 确认框、规则页第三方源管理 UI、三处触发第三方同步、收敛遗留弹窗"
```

---

## 完成全部任务后（整体审查 + 发版 0.6.0）

1. 整个分支交给一个全新的审查子代理（最强模型）独立审查，重点：三档优先级与去重警告、多文档解析不误切、第三方安装前警告的数据链、无 signer 禁装、配置缺失/坏数据回退、缓存清理、确认框不触发系统弹窗、签名校验未被放宽。
2. 修完审查意见、全部测试通过后合并 main、推送。
3. 发版 0.6.0：改 `src/UpdateHelper.App/UpdateHelper.App.csproj` 的 `<Version>`、更新 CHANGELOG.md 与 README（路线图把"第三方规则源"从"📋 之后"挪为"✅ 0.6.0"、功能表「规则」行补第三方）、发布前用 Release 版冒烟、打 tag `v0.6.0`、`gh release create` 上传便携版 + 小体积版两个 zip。
4. 每次提交后用 `git log -1 --pretty=%B | grep -ci -E 'co-authored|claude-session|generated with'` 校验署名行数为 0。
