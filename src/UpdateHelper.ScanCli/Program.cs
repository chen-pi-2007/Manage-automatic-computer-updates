using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Scanning;

// 用法：
//   dotnet run --project src/UpdateHelper.ScanCli            只列普通软件和开发工具
//   dotnet run --project src/UpdateHelper.ScanCli -- --all   列出全部分类
//   dotnet run --project src/UpdateHelper.ScanCli -- --json scan.json   另存完整结果
Console.OutputEncoding = Encoding.UTF8;

var showAll = args.Contains("--all");
var jsonIndex = Array.IndexOf(args, "--json");
var jsonPath = jsonIndex >= 0 && jsonIndex + 1 < args.Length ? args[jsonIndex + 1] : null;

var report = SystemScanner.CreateDefault().Scan();
var r = report.Result;
var groups = r.Groups;

Console.WriteLine($"注册表登记总数：{r.TotalEntries}");
Console.WriteLine($"整理后的软件组：{groups.Count}");
foreach (var byCat in groups.GroupBy(g => g.Category))
    Console.WriteLine($"  {byCat.Key,-16}{byCat.Count()}");
Console.WriteLine($"归入软件的组件：{groups.Sum(g => g.Components.Count)}");
Console.WriteLine($"后台项目：已归属 {groups.Sum(g => g.Background.Count)}，未归属 {r.UnassignedBackground.Count}");
foreach (var w in report.Warnings) Console.WriteLine($"警告：{w}");
Console.WriteLine();

var visible = showAll
    ? groups
    : groups.Where(g => g.Category is SoftwareCategory.Application or SoftwareCategory.DevTool).ToList();

foreach (var g in visible)
{
    var flag = g.IsIncomplete ? " [信息不完整]" : "";
    Console.WriteLine($"{g.Name}{flag}  {g.Version}  · {g.Publisher}  · {g.Category}");
    if (g.Components.Count > 0) Console.WriteLine($"    组件 {g.Components.Count} 个");
    foreach (var b in g.Background) Console.WriteLine($"    后台 [{b.Kind}] {b.Name}");
}

Console.WriteLine();
Console.WriteLine("未归属的后台项目：");
foreach (var b in r.UnassignedBackground) Console.WriteLine($"  [{b.Kind}] {b.Name}  {b.ExecutablePath}");

if (jsonPath is not null)
{
    var options = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文不转义
        Converters = { new JsonStringEnumConverter() },
    };
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, options), Encoding.UTF8);
    Console.WriteLine($"完整结果已保存到 {Path.GetFullPath(jsonPath)}");
}
