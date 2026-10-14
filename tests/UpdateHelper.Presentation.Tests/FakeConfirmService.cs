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
