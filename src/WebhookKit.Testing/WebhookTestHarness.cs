using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WebhookKit.Testing;

public interface IWebhookTestHost
{
    HttpClient CreateClient();
    Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class WebhookTestHarness : IDisposable, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IWebhookTestHost>> _hostFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly HashSet<HttpClient> _clients = [];
    private IWebhookTestHost? _host;
    private int _disposed;

    public WebhookTestHarness(Func<IWebhookTestHost> hostFactory)
        : this(CreateAsyncHostFactory(hostFactory))
    {
    }

    public WebhookTestHarness(Func<CancellationToken, Task<IWebhookTestHost>> hostFactory)
    {
        ArgumentNullException.ThrowIfNull(hostFactory);
        _hostFactory = hostFactory;
    }

    public WebhookTestHarness(IWebhookTestHost host)
        : this(() => host)
    {
    }

    public WebhookTestHarness(Func<HttpClient> clientFactory)
        : this(CreateAsyncClientHostFactory(clientFactory))
    {
    }

    public WebhookTestHarness(Func<Task<HttpClient>> clientFactory)
        : this(CreateAsyncClientHostFactory(clientFactory))
    {
    }

    public WebhookTestHarness(Func<CancellationToken, Task<HttpClient>> clientFactory)
        : this(CreateAsyncClientHostFactory(clientFactory))
    {
    }

    public bool IsStarted
    {
        get
        {
            lock (_clients)
            {
                return _host is not null;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_clients)
            {
                if (_host is not null)
                {
                    return;
                }
            }

            var host = await _hostFactory(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The test-host factory returned no host.");
            try
            {
                await host.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception startException)
            {
                try
                {
                    await DisposeAfterFailedStartAsync(host).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(startException, cleanupException);
                }

                throw;
            }

            lock (_clients)
            {
                _host = host;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public HttpClient CreateClient()
    {
        ThrowIfDisposed();
        IWebhookTestHost host;
        lock (_clients)
        {
            if (_host is null)
            {
                throw new InvalidOperationException("The harness must be started before creating a client.");
            }

            host = _host;
        }

        var client = host.CreateClient()
            ?? throw new InvalidOperationException("The test host returned no client.");
        lock (_clients)
        {
            _clients.Add(client);
        }

        return client;
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var client = CreateClient();
        return await client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    public override string ToString()
    {
        return nameof(WebhookTestHarness);
    }

    private static Func<CancellationToken, Task<IWebhookTestHost>> CreateAsyncHostFactory(Func<IWebhookTestHost> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return _ => Task.FromResult(factory());
    }

    private static Func<CancellationToken, Task<IWebhookTestHost>> CreateAsyncClientHostFactory(Func<HttpClient> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return _ => Task.FromResult<IWebhookTestHost>(new ClientHost(factory));
    }

    private static Func<CancellationToken, Task<IWebhookTestHost>> CreateAsyncClientHostFactory(Func<Task<HttpClient>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return _ => Task.FromResult<IWebhookTestHost>(new ClientHost(factory));
    }

    private static Func<CancellationToken, Task<IWebhookTestHost>> CreateAsyncClientHostFactory(Func<CancellationToken, Task<HttpClient>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return cancellationToken => Task.FromResult<IWebhookTestHost>(new ClientHost(factory));
    }

    private static async Task DisposeAfterFailedStartAsync(IWebhookTestHost host)
    {
        if (host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (host is IDisposable disposable)
        {
            disposable.Dispose();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        IWebhookTestHost? host;
        lock (_clients)
        {
            host = _host;
            _host = null;
        }

        if (host is null)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await host.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        List<HttpClient> clients;
        lock (_clients)
        {
            clients = _clients.ToList();
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            try
            {
                client.Dispose();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        try
        {
            if (host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (host is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed class ClientHost : IWebhookTestHost, IDisposable
    {
        private readonly Func<CancellationToken, Task<HttpClient>> _clientFactory;
        private HttpClient? _client;

        public ClientHost(Func<HttpClient> clientFactory)
            : this(CreateFactory(clientFactory))
        {
        }

        public ClientHost(Func<Task<HttpClient>> clientFactory)
            : this(CreateFactory(clientFactory))
        {
        }

        public ClientHost(Func<CancellationToken, Task<HttpClient>> clientFactory)
        {
            ArgumentNullException.ThrowIfNull(clientFactory);
            _clientFactory = clientFactory;
        }

        public HttpClient CreateClient()
        {
            return _client ?? throw new InvalidOperationException("The client factory has not been started.");
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _client = await _clientFactory(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The client factory returned no client.");
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private static Func<CancellationToken, Task<HttpClient>> CreateFactory(Func<HttpClient> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            return _ => Task.FromResult(factory());
        }

        private static Func<CancellationToken, Task<HttpClient>> CreateFactory(Func<Task<HttpClient>> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            return _ => factory();
        }

        public void Dispose()
        {
            _client?.Dispose();
            _client = null;
        }
    }
}

public sealed class WebhookTestHarness<TApplication> : IDisposable, IAsyncDisposable
    where TApplication : class
{
    private readonly WebhookTestHarness _inner;

    public WebhookTestHarness()
        : this(new WebApplicationFactory<TApplication>())
    {
    }

    public WebhookTestHarness(WebApplicationFactory<TApplication> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _inner = new WebhookTestHarness(new WebApplicationFactoryHost(factory));
    }

    public WebhookTestHarness(Action<WebApplicationFactory<TApplication>> configureFactory)
        : this(CreateFactory(configureFactory))
    {
    }

    public bool IsStarted => _inner.IsStarted;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return _inner.StartAsync(cancellationToken);
    }

    public HttpClient CreateClient()
    {
        return _inner.CreateClient();
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        return _inner.SendAsync(request, cancellationToken);
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken = default)
    {
        return _inner.SendAsync(request, completionOption, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return _inner.StopAsync(cancellationToken);
    }

    public void Dispose()
    {
        _inner.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        return _inner.DisposeAsync();
    }

    public override string ToString()
    {
        return nameof(WebhookTestHarness<TApplication>);
    }

    private static WebApplicationFactory<TApplication> CreateFactory(
        Action<WebApplicationFactory<TApplication>> configureFactory)
    {
        ArgumentNullException.ThrowIfNull(configureFactory);
        var factory = new WebApplicationFactory<TApplication>();
        configureFactory(factory);
        return factory;
    }

    private sealed class WebApplicationFactoryHost : IWebhookTestHost, IDisposable
    {
        private readonly WebApplicationFactory<TApplication> _factory;

        public WebApplicationFactoryHost(WebApplicationFactory<TApplication> factory)
        {
            _factory = factory;
        }

        public HttpClient CreateClient()
        {
            return _factory.CreateClient();
        }

        public void Dispose()
        {
            _factory.Dispose();
        }
    }
}
