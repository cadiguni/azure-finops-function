namespace Personal.FinOpsApi.AzureFunctions.Services;

public interface ICostExportBlobSource
{
    IAsyncEnumerable<string> ListSubscriptionIdsAsync(
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> ListManifestNamesAsync(
        IReadOnlyCollection<string> subscriptionIds,
        int maximumResults,
        CancellationToken cancellationToken = default);

    Task<string> ReadTextAsync(
        string blobName,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(
        string blobName,
        CancellationToken cancellationToken = default);
}
