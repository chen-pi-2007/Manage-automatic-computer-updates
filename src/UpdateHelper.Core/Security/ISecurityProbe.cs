namespace UpdateHelper.Core.Security;

/// <summary>只读查询安全状态。两个方法失败都返回空/null，不抛异常。</summary>
public interface ISecurityProbe
{
    /// <summary>SecurityCenter2 里注册的杀毒软件；读不到返回空列表。</summary>
    IReadOnlyList<RegisteredAntivirus> QueryRegistered();

    /// <summary>Windows Defender 的真实状态；Defender 不可用或读不到返回 null。</summary>
    DefenderStatus? QueryDefender();
}
