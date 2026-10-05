using System.Xml;
using System.Xml.Linq;

namespace UpdateHelper.Core.Scanning;

/// <summary>通过任务计划程序 COM 读取非微软的计划任务（含隐藏任务）。只读。</summary>
public sealed class ScheduledTaskSource : IBackgroundSource
{
    private const int TaskEnumHidden = 1;
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public IReadOnlyList<BackgroundItem> Read()
    {
        var result = new List<BackgroundItem>();
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return result;
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            Walk(service.GetFolder("\\"), result);
        }
        catch (Exception)
        {
            // COM 不可用：整体跳过（Review Focus 5）
        }
        return result;
    }

    private static void Walk(dynamic folder, List<BackgroundItem> into)
    {
        string path = folder.Path;
        if (path.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            foreach (dynamic task in folder.GetTasks(TaskEnumHidden))
            {
                try
                {
                    var item = ParseTaskXml((string)task.Path, (string)task.Xml);
                    if (item is not null) into.Add(item);
                }
                catch (Exception) { /* 单个任务读不了就跳过 */ }
            }
            foreach (dynamic sub in folder.GetFolders(0))
                Walk(sub, into);
        }
        catch (Exception) { /* 某个文件夹没权限就跳过 */ }
    }

    /// <summary>从任务 XML 中取第一个"启动程序"动作；没有或 XML 损坏都返回 null。</summary>
    public static BackgroundItem? ParseTaskXml(string taskPath, string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var exec = doc.Descendants(Ns + "Exec").FirstOrDefault();
            var command = exec?.Element(Ns + "Command")?.Value.Trim();
            if (string.IsNullOrEmpty(command)) return null;

            var args = exec!.Element(Ns + "Arguments")?.Value.Trim();
            var full = string.IsNullOrEmpty(args) ? command : $"{command} {args}";
            var name = taskPath[(taskPath.LastIndexOf('\\') + 1)..];

            return new BackgroundItem(BackgroundKind.ScheduledTask, name, null, full,
                CommandLineParser.ExtractExecutable(command), taskPath);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
