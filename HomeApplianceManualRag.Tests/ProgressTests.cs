using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Documents;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeApplianceManualRag.Tests;

public sealed class ProgressTests
{
    [Fact]
    public async Task Notifier_publishes_only_to_matching_owner_or_admin()
    {
        var notifier = new DocumentProgressNotifier(NullLogger<DocumentProgressNotifier>.Instance);
        var ownerUpdates = 0;
        var otherUpdates = 0;
        var adminUpdates = 0;
        using var owner = notifier.Subscribe("user-1", false, _ =>
        {
            ownerUpdates++;
            return Task.CompletedTask;
        });
        using var other = notifier.Subscribe("user-2", false, _ =>
        {
            otherUpdates++;
            return Task.CompletedTask;
        });
        using var admin = notifier.Subscribe("admin", true, _ =>
        {
            adminUpdates++;
            return Task.CompletedTask;
        });

        await notifier.PublishAsync(new(Guid.NewGuid(), "user-1", DocumentStatus.Processing, null));

        Assert.Equal(1, ownerUpdates);
        Assert.Equal(0, otherUpdates);
        Assert.Equal(1, adminUpdates);
    }

    [Fact]
    public async Task Disposed_subscription_no_longer_receives_updates()
    {
        var notifier = new DocumentProgressNotifier(NullLogger<DocumentProgressNotifier>.Instance);
        var updates = 0;
        var subscription = notifier.Subscribe("user-1", false, _ =>
        {
            updates++;
            return Task.CompletedTask;
        });

        subscription.Dispose();
        await notifier.PublishAsync(new(Guid.NewGuid(), "user-1", DocumentStatus.Ready, null));

        Assert.Equal(0, updates);
    }
}
