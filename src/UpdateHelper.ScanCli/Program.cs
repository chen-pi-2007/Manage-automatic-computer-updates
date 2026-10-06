using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Scanning;

// 用法：
//   dotnet run --project src/UpdateHelper.ScanCli                     只列普通软件和开发工具
//   dotnet run --project src/UpdateHelper.ScanCli -- --all            列出全部分类
//   dotnet run --project src/UpdateHelper.ScanCli -- --rules <目录>   指定规则目录（默认找仓库里的 rules/）
//   dotnet run --project src/UpdateHelper.ScanCli -- --json scan.json 另存完整结果
Console.OutputEncoding = Encoding.UTF8;

var showAll = args.Contains("--all");
var jsonPath = ArgValue("--json");
var rulesDir = ArgValue("--rules") ?? FindRepoRules();

var scan = SystemScanner.CreateDefault().Scan();
var rules = rulesDir is null ? RuleSet.Empty : RuleSetLoader.Load([new RuleSource(rulesDir, RuleTrust.Official)]);
var applied = RuleApplier.Apply(scan.Result, rules);
var r = applied.Result;
var groups = r.Groups;

Console.WriteLine($"注册表登记总数：{r.TotalEntries}");
Console.WriteLine($"整理后的软件组：{groups.Count}");
foreach (var byCat in groups.GroupBy(g => g.Category))
    Console.WriteLine($"  {byCat.Key,-16}{byCat.Count()}");
Console.WriteLine($"归入软件的组件：{groups.Sum(g => g.Components.Count)}");
Console.WriteLine($"后台项目：已归属 {groups.Sum(g => g.Background.Count)}，未归属 {r.UnassignedBackground.Count}");
Console.WriteLine($"规则：{rules.Rules.Count} 条（{rulesDir ?? "未找到规则目录"}），命中 {groups.Count(g => g.RuleId is not null)} 个软件，" +
                  $"说明了 {applied.Explanations.Count} 个后台项目");
foreach (var w in scan.Warnings) Console.WriteLine($"警告：{w}");
foreach (var e in rules.Errors) Console.WriteLine($"规则错误：{e}");
foreach (var w in rules.Warnings) Console.WriteLine($"规则警告：{w}");
Console.WriteLine();

var visible = showAll
    ? groups
    : groups.Where(g => g.Category is SoftwareCategory.Application or SoftwareCategory.DevTool).ToList();

foreach (var g in visible)
{
    var flag = g.IsIncomplete ? " [信息不完整]" : "";
    var rule = g.RuleId is null ? "" : $"  [规则 {g.RuleId}]";
    Console.WriteLine($"{g.Name}{flag}  {g.Version}  · {g.Publisher}  · {g.Category}{rule}");
    if (g.Components.Count > 0) Console.WriteLine($"    组件 {g.Components.Count} 个");
    foreach (var b in g.Background) Console.WriteLine($"    后台 [{b.Kind}] {b.Name}{Explain(b)}");
}

Console.WriteLine();
Console.WriteLine("未归属的后台项目：");
foreach (var b in r.UnassignedBackground) Console.WriteLine($"  [{b.Kind}] {b.Name}  {b.ExecutablePath}{Explain(b)}");

if (jsonPath is not null)
{
    var options = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文不转义
        Converters = { new JsonStringEnumConverter() },
    };
    var output = new
    {
        Result = r,
        Explanations = applied.Explanations.Select(kv => new { Item = kv.Key, Explain = kv.Value }),
        scan.Warnings,
        RuleErrors = rules.Errors,
        RuleWarnings = rules.Warnings,
    };
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(output, options), Encoding.UTF8);
    Console.WriteLine($"完整结果已保存到 {Path.GetFullPath(jsonPath)}");
}

string Explain(BackgroundItem b) => applied.Explanations.TryGetValue(b, out var text) ? $"  —— {text}" : "";

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// 从当前目录往上找 UpdateHelper.slnx，返回它旁边的 rules 目录
static string? FindRepoRules()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "UpdateHelper.slnx")))
        {
            var rules = Path.Combine(dir.FullName, "rules");
            return Directory.Exists(rules) ? rules : null;
        }
    }
    return null;
}
