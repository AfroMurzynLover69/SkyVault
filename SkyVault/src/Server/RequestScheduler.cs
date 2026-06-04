using System.Collections.Concurrent;
using System.Threading;

public enum RequestWorkload
{
    Ui,
    Upload,
    Download,
    FileOperation
}

public sealed class RequestScheduler
{
    private readonly ServerOptions options;
    private readonly SemaphoreSlim activeRequests;
    private readonly SemaphoreSlim uiRequests;
    private readonly SemaphoreSlim uploads;
    private readonly SemaphoreSlim downloads;
    private readonly SemaphoreSlim fileOperations;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> userUploads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> userDownloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> userFileOperations = new(StringComparer.OrdinalIgnoreCase);
    private long rejectedByLimit;
    private long queueTimeouts;
    private int activeRequestCount;
    private int activeUploadCount;
    private int activeDownloadCount;

    public RequestScheduler(ServerOptions options)
    {
        this.options = options;
        activeRequests = new SemaphoreSlim(options.MaxActiveRequests, options.MaxActiveRequests);
        uiRequests = new SemaphoreSlim(options.MaxUiRequests, options.MaxUiRequests);
        uploads = new SemaphoreSlim(options.MaxUploads, options.MaxUploads);
        downloads = new SemaphoreSlim(options.MaxDownloads, options.MaxDownloads);
        fileOperations = new SemaphoreSlim(options.MaxFileOperations, options.MaxFileOperations);
    }

    public long RejectedByLimit => Interlocked.Read(ref rejectedByLimit);
    public long QueueTimeouts => Interlocked.Read(ref queueTimeouts);
    public int ActiveRequests => Volatile.Read(ref activeRequestCount);
    public int ActiveUploads => Volatile.Read(ref activeUploadCount);
    public int ActiveDownloads => Volatile.Read(ref activeDownloadCount);

    public async Task<RequestLease?> TryAcquireAsync(RequestWorkload workload, string? username, CancellationToken cancellationToken)
    {
        TimeSpan queueTimeout = TimeSpan.FromSeconds(GetQueueTimeoutSeconds(workload));
        var acquired = new List<SemaphoreSlim>();
        RequestLease? lease = null;

        try
        {
            if (!await TryWaitAsync(activeRequests, queueTimeout, cancellationToken))
            {
                Interlocked.Increment(ref queueTimeouts);
                return null;
            }

            acquired.Add(activeRequests);
            Interlocked.Increment(ref activeRequestCount);

            SemaphoreSlim workloadSemaphore = workload switch
            {
                RequestWorkload.Upload => uploads,
                RequestWorkload.Download => downloads,
                RequestWorkload.FileOperation => fileOperations,
                _ => uiRequests
            };

            if (!await TryWaitAsync(workloadSemaphore, queueTimeout, cancellationToken))
            {
                Interlocked.Increment(ref queueTimeouts);
                return null;
            }

            acquired.Add(workloadSemaphore);

            if (workload == RequestWorkload.Upload)
            {
                Interlocked.Increment(ref activeUploadCount);
                SemaphoreSlim userSemaphore = userUploads.GetOrAdd(username ?? "anonymous", _ => new SemaphoreSlim(options.MaxUploadsPerUser, options.MaxUploadsPerUser));

                if (!await TryWaitAsync(userSemaphore, queueTimeout, cancellationToken))
                {
                    Interlocked.Increment(ref queueTimeouts);
                    return null;
                }

                acquired.Add(userSemaphore);
            }
            else if (workload == RequestWorkload.Download)
            {
                Interlocked.Increment(ref activeDownloadCount);
                SemaphoreSlim userSemaphore = userDownloads.GetOrAdd(username ?? "anonymous", _ => new SemaphoreSlim(options.MaxDownloadsPerUser, options.MaxDownloadsPerUser));

                if (!await TryWaitAsync(userSemaphore, queueTimeout, cancellationToken))
                {
                    Interlocked.Increment(ref queueTimeouts);
                    return null;
                }

                acquired.Add(userSemaphore);
            }
            else if (workload == RequestWorkload.FileOperation)
            {
                SemaphoreSlim userSemaphore = userFileOperations.GetOrAdd(username ?? "anonymous", _ => new SemaphoreSlim(options.MaxFileOperationsPerUser, options.MaxFileOperationsPerUser));

                if (!await TryWaitAsync(userSemaphore, queueTimeout, cancellationToken))
                {
                    Interlocked.Increment(ref queueTimeouts);
                    return null;
                }

                acquired.Add(userSemaphore);
            }

            lease = new RequestLease(this, workload, acquired.ToList());
            return lease;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref queueTimeouts);
            return null;
        }
        finally
        {
            if (lease is null && acquired.Count > 0)
            {
                Release(workload, acquired);
            }
        }
    }

    private int GetQueueTimeoutSeconds(RequestWorkload workload)
    {
        return workload switch
        {
            RequestWorkload.Upload => options.UploadQueueTimeoutSeconds,
            RequestWorkload.Download => options.DownloadQueueTimeoutSeconds,
            RequestWorkload.FileOperation => options.FileOperationQueueTimeoutSeconds,
            _ => options.UiQueueTimeoutSeconds
        };
    }

    private static async Task<bool> TryWaitAsync(SemaphoreSlim semaphore, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await semaphore.WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private void Release(RequestWorkload workload, IReadOnlyList<SemaphoreSlim> acquired)
    {
        foreach (SemaphoreSlim semaphore in acquired.AsEnumerable().Reverse())
        {
            semaphore.Release();
        }

        Interlocked.Decrement(ref activeRequestCount);

        if (workload == RequestWorkload.Upload && acquired.Contains(uploads))
        {
            Interlocked.Decrement(ref activeUploadCount);
        }
        else if (workload == RequestWorkload.Download && acquired.Contains(downloads))
        {
            Interlocked.Decrement(ref activeDownloadCount);
        }
    }

    public void MarkRejected()
    {
        Interlocked.Increment(ref rejectedByLimit);
    }

    public sealed class RequestLease : IAsyncDisposable
    {
        private readonly RequestScheduler owner;
        private readonly RequestWorkload workload;
        private readonly IReadOnlyList<SemaphoreSlim> acquired;
        private bool disposed;

        public RequestLease(RequestScheduler owner, RequestWorkload workload, IReadOnlyList<SemaphoreSlim> acquired)
        {
            this.owner = owner;
            this.workload = workload;
            this.acquired = acquired;
        }

        public ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                owner.Release(workload, acquired);
                disposed = true;
            }

            return ValueTask.CompletedTask;
        }
    }
}
