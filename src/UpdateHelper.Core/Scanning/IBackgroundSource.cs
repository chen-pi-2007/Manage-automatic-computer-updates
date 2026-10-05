namespace UpdateHelper.Core.Scanning;

public interface IBackgroundSource
{
    IReadOnlyList<BackgroundItem> Read();
}
