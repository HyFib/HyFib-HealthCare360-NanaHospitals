namespace HyFib.HealthCare360.Modules.Quality.Services;

/// <summary>
/// Runs work so that nothing in it can see the signed-in user. Anonymous incident reporting promises the reporter
/// that no identity is kept, but the platform's unit of work stamps <c>CreatedBy</c> and writes a data-audit event
/// with the user, session, device and IP of whoever saves a row. Running the insert in a fresh DI scope on a flow
/// that does not carry the HTTP context (execution-context flow suppressed) makes the current user anonymous for
/// the database context, the audit interceptor and the number series alike, while the incident keeps its normal
/// audit trail for all later changes.
/// The scope fails closed: if the current user is still visible inside it (for example because user resolution
/// changes in the platform), the work is not run and a failure is returned instead of leaking an identity.
/// </summary>
public sealed class AnonymousReportingScope(IServiceScopeFactory scopeFactory)
{
    public async Task<Result<T>> RunAsync<T>(Func<IServiceProvider, Task<Result<T>>> work)
    {
        Task<Result<T>> task;
        using (ExecutionContext.SuppressFlow())
        {
            task = Task.Run(async () =>
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
                if (user.IsAuthenticated || user.UserId is not null || user.SessionId is not null)
                {
                    return Result<T>.Fail(Error.Rule("incident.anonymity_unavailable",
                        "Anonymous reporting is unavailable right now. Report with your name, or contact the quality team."));
                }

                return await work(scope.ServiceProvider);
            });
        }

        return await task;
    }
}
