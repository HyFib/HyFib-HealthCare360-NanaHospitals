using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class IdempotencyTests
{
    [Fact]
    public async Task An_answer_that_must_not_be_kept_never_reaches_the_idempotency_store()
    {
        var executed = false;
        var middleware = new IdempotencyMiddleware(_ =>
        {
            executed = true;
            return Task.CompletedTask;
        }, NullLogger<IdempotencyMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/ai/clinical/structure-note";
        context.Request.Headers[IdempotencyMiddleware.HeaderName] = "4b1f0c2e-retry-key";
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(NoStoredResponseMetadata.Instance), "structure note"));
        var store = Substitute.For<IDbContextFactory<HospitalDbContext>>();

        await middleware.InvokeAsync(context, store, Substitute.For<ICurrentUser>(), Substitute.For<IFieldProtector>(), TimeProvider.System);

        executed.ShouldBeTrue();
        await store.DidNotReceiveWithAnyArgs().CreateDbContextAsync(TestContext.Current.CancellationToken);
    }
}
