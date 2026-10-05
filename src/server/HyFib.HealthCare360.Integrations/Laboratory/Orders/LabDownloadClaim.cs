namespace HyFib.HealthCare360.Integrations.Laboratory.Orders;

/// <summary>Keeps a session's claim on the orders it sends for as long as the send takes.</summary>
internal static class LabDownloadClaim
{
    /// <summary>
    /// Runs <paramref name="send"/>, extending the claim on <paramref name="downloads"/> every <paramref name="every"/>
    /// until it ends.
    /// </summary>
    public static async Task<T> HoldWhileAsync<T>(
        ILabMessageProcessor processor,
        LabInstrumentSettings instrument,
        IReadOnlyList<LabDownload> downloads,
        TimeSpan every,
        TimeProvider time,
        Func<Task<T>> send,
        CancellationToken cancellationToken)
    {
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewing = RenewAsync(sending.Token);
        try
        {
            return await send();
        }
        finally
        {
            await sending.CancelAsync();
            await renewing;
        }

        async Task RenewAsync(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(every, time, token);
                    await processor.ExtendDownloadClaimAsync(instrument, downloads, token);
                }
            }
            catch (OperationCanceledException)
            {
                // The send is over.
            }
        }
    }
}
