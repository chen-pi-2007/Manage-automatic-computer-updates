using System.Text.RegularExpressions;
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Scanning;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace UpdateHelper.Core.Rules;

public sealed record ParseResult(Rule? Rule, IReadOnlyList<string> Errors)
{
    public bool Ok => Rule is not null;
}

/// <summary>把一个 YAML 规则文件的文本解析并校验成 Rule。永不抛异常。</summary>
public static partial class RuleParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static ParseResult Parse(string yaml, string sourceFile)
    {
        var errors = new List<string>();
        void Error(string message) => errors.Add($"{sourceFile}: {message}");

        RuleDto? dto;
        try
        {
            dto = Deserializer.Deserialize<RuleDto?>(yaml);
        }
        catch (YamlException ex)
        {
            Error($"YAML 格式错误（第 {ex.Start.Line} 行）：{ex.InnerException?.Message ?? ex.Message}");
            return new ParseResult(null, errors);
        }

        if (dto is null)
        {
            Error("文件是空的");
            return new ParseResult(null, errors);
        }

        if (string.IsNullOrWhiteSpace(dto.Id)) Error("缺少 id");
        else if (!IdPattern().IsMatch(dto.Id)) Error($"id \"{dto.Id}\" 格式不对：只能用小写字母、数字、. _ -，并以字母或数字开头");

        if (string.IsNullOrWhiteSpace(dto.Name)) Error("缺少 name");
        if (string.IsNullOrWhiteSpace(dto.Match?.DisplayName)) Error("缺少 match.displayName");

        SoftwareCategory? category = null;
        if (dto.Category is not null)
        {
            category = dto.Category switch
            {
                "application" => SoftwareCategory.Application,
                "devtool" => SoftwareCategory.DevTool,
                "game" => SoftwareCategory.Game,
                "runtime" => SoftwareCategory.Runtime,
                "driver" => SoftwareCategory.Driver,
                _ => null,
            };
            if (category is null) Error($"category \"{dto.Category}\" 无效，可选 application / devtool / game / runtime / driver");
        }

        var update = ParseUpdate(dto.Update, Error);
        var background = ParseBackground(dto.Background, Error);
        var leftovers = ParseLeftovers(dto.Leftovers, Error);

        if (errors.Count > 0) return new ParseResult(null, errors);

        var rule = new Rule(dto.Id!, dto.Name!, new RuleMatch(dto.Match!.DisplayName!, NullIfBlank(dto.Match.Publisher)),
            category, update, background, leftovers) { SourceFile = sourceFile };
        return new ParseResult(rule, errors);
    }

    private static UpdateRule? ParseUpdate(UpdateDto? u, Action<string> error)
    {
        if (u is null) return null;

        var risk = (u.Risk ?? "normal") switch
        {
            "normal" => RiskLevel.Normal,
            "careful" => RiskLevel.Careful,
            "never-auto" => RiskLevel.NeverAuto,
            _ => (RiskLevel?)null,
        };
        if (risk is null) error($"update.risk \"{u.Risk}\" 无效，可选 normal / careful / never-auto");

        UpdateCheck? check = null;
        if (u.Check is not null)
        {
            if (!IsHttpUrl(u.Check.Url) || string.IsNullOrWhiteSpace(u.Check.Extract))
                error("update.check 需要同时写 url（http/https 开头）和 extract");
            else check = new UpdateCheck(u.Check.Url!, u.Check.Extract!);
        }

        UpdateLatest? latest = null;
        if (u.Latest is not null)
        {
            if (string.IsNullOrWhiteSpace(u.Latest.Version) || !IsHttpUrl(u.Latest.Url))
                error("update.latest 需要同时写 version 和 url（http/https 开头）");
            else latest = new UpdateLatest(u.Latest.Version!, u.Latest.Url!);
        }

        return new UpdateRule(check, latest, NullIfBlank(u.Installer?.Signer), NullIfBlank(u.Installer?.SilentArgs),
            risk ?? RiskLevel.Normal);
    }

    private static List<BackgroundRule> ParseBackground(List<BackgroundDto>? list, Action<string> error)
    {
        var result = new List<BackgroundRule>();
        for (var i = 0; i < (list?.Count ?? 0); i++)
        {
            var b = list![i];
            var targets = new (BackgroundKind Kind, string? Pattern)[]
            {
                (BackgroundKind.ScheduledTask, b.Task),
                (BackgroundKind.Service, b.Service),
                (BackgroundKind.RunKey, b.Run),
                (BackgroundKind.StartupFolder, b.Startup),
            }.Where(t => !string.IsNullOrWhiteSpace(t.Pattern)).ToList();

            if (targets.Count != 1)
                error($"background[{i}] 需要且只能写 task / service / run / startup 中的一个");
            else if (string.IsNullOrWhiteSpace(b.Explain))
                error($"background[{i}] 缺少 explain");
            else
                result.Add(new BackgroundRule(targets[0].Kind, targets[0].Pattern!, b.Explain!));
        }
        return result;
    }

    private static List<LeftoverRule> ParseLeftovers(List<LeftoverDto>? list, Action<string> error)
    {
        var result = new List<LeftoverRule>();
        for (var i = 0; i < (list?.Count ?? 0); i++)
        {
            var l = list![i];
            LeftoverKind? kind = l.Kind switch
            {
                "owned" => LeftoverKind.Owned,
                "personal" => LeftoverKind.Personal,
                "shared" => LeftoverKind.Shared,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(l.Path)) error($"leftovers[{i}] 缺少 path");
            else if (kind is null) error($"leftovers[{i}] 的 kind \"{l.Kind}\" 无效，可选 owned / personal / shared");
            else result.Add(new LeftoverRule(l.Path!, kind.Value));
        }
        return result;
    }

    private static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex IdPattern();

    // —— YAML 中间对象：字段全部可空，校验在上面做 ——
    private sealed class RuleDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public MatchDto? Match { get; set; }
        public UpdateDto? Update { get; set; }
        public List<BackgroundDto>? Background { get; set; }
        public List<LeftoverDto>? Leftovers { get; set; }
    }

    private sealed class MatchDto
    {
        public string? DisplayName { get; set; }
        public string? Publisher { get; set; }
    }

    private sealed class UpdateDto
    {
        public CheckDto? Check { get; set; }
        public LatestDto? Latest { get; set; }
        public InstallerDto? Installer { get; set; }
        public string? Risk { get; set; }
    }

    private sealed class CheckDto
    {
        public string? Url { get; set; }
        public string? Extract { get; set; }
    }

    private sealed class LatestDto
    {
        public string? Version { get; set; }
        public string? Url { get; set; }
    }

    private sealed class InstallerDto
    {
        public string? Signer { get; set; }
        public string? SilentArgs { get; set; }
    }

    private sealed class BackgroundDto
    {
        public string? Task { get; set; }
        public string? Service { get; set; }
        public string? Run { get; set; }
        public string? Startup { get; set; }
        public string? Explain { get; set; }
    }

    private sealed class LeftoverDto
    {
        public string? Path { get; set; }
        public string? Kind { get; set; }
    }
}
