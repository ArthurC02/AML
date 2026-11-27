using Aml.Core.Pages;

namespace Aml.Core.Ports;

/// <summary>Pages received by URL, one record per page id (external-sources.md#received-url-source-2).</summary>
public interface IPageStore
{
    Task<PageRecord?> GetAsync(string pageId, CancellationToken ct);

    /// <summary>
    /// Queues the page's fetch job and marks the page PENDING with that job, in one transaction (inserting the page when it is
    /// new). When the job's idempotency key is already queued nothing changes, and the stored page is returned.
    /// </summary>
    Task<PageRecord> QueueAsync(PageRecord page, NewJob job, CancellationToken ct);

    /// <summary>Stores a fetch outcome; false (nothing changed) when the page has since been queued under another job.</summary>
    Task<bool> SaveAsync(PageRecord page, CancellationToken ct);
}
