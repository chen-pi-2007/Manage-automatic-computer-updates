namespace UpdateHelper.Winget;

/// <summary>winget 不可用（没装、服务坏了、连不上源、查询失败）。Message 是给用户看的中文说明。</summary>
public sealed class WingetUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
