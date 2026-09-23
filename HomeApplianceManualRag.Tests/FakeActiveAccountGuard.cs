using HomeApplianceManualRag.Web.Data;

namespace HomeApplianceManualRag.Tests;

internal sealed class FakeActiveAccountGuard : IActiveAccountGuard
{
    public Task EnsureEnabledAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
