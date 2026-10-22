using HyFib.HealthCare360.Platform.Realtime;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// A group join that fails (with several servers, Redis is away) is undone before the failure is passed on, so that joining
/// again joins: SignalR would otherwise count the connection in the group and take every later join as done. CI has no
/// Redis server; RedisBackplaneTests shows the whole of it with one.
/// </summary>
public sealed class RealtimeGroupsTests
{
    private readonly IGroupManager _groups = Substitute.For<IGroupManager>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_join_that_fails_is_undone_and_the_failure_passed_on()
    {
        var failure = new InvalidOperationException("No connection is active/available to service this operation: SUBSCRIBE");
        _groups.AddToGroupAsync("connection", "ward:1", Arg.Any<CancellationToken>()).ThrowsAsync(failure);

        (await Should.ThrowAsync<InvalidOperationException>(() => _groups.JoinAsync("connection", "ward:1", Ct))).ShouldBeSameAs(failure);

        await _groups.Received(1).RemoveFromGroupAsync("connection", "ward:1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_join_failure_is_passed_on_when_undoing_it_fails_too()
    {
        var failure = new InvalidOperationException("Redis is away");
        _groups.AddToGroupAsync("connection", "ward:1", Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        _groups.RemoveFromGroupAsync("connection", "ward:1", Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());

        (await Should.ThrowAsync<InvalidOperationException>(() => _groups.JoinAsync("connection", "ward:1", Ct))).ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task A_join_that_succeeds_is_kept()
    {
        await _groups.JoinAsync("connection", "ward:1", Ct);

        await _groups.Received(1).AddToGroupAsync("connection", "ward:1", Ct);
        await _groups.DidNotReceive().RemoveFromGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
