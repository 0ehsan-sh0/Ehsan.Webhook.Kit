using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WebhookKit.Testing;

/// <summary>Minimal host abstraction used by the WebhookKit test harness.</summary>
public interface IWebhookTestHost
{
    /// <summary>Creates a client owned by the host and tracked by the harness.</summary>
    /// <returns>A client for the started test host.</returns>
    HttpClient CreateClient();

    /// <summary>Starts the host when it supports an explicit lifecycle.</summary>
    /// <param name="cancellationToken">Token used to cancel startup.</param>
    /// <returns>A task representing startup.</returns>
    Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Stops the host when it supports an explicit lifecycle.</summary>
    /// <param name="cancellationToken">Token used to cancel shutdown.</param>
    /// <returns>A task representing shutdown.</returns>
    Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Lifecycle-managed test host that owns clients created from a host or client factory.</summary>
public sealed class WebhookTestHarness : IDisposable, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IWebhookTestHost>> _hostFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly HashSet<HttpClient> _clients = [];
    private IWebhookTestHost? _host;
    private int _disposed;

    /// <summary>Creates a harness from a synchronous host factory.</summary>
    /// <param name="hostFactory">Factory invoked once when the harness starts.</param>
    public WebhookTestHarness(Func<IWebhookTestHost> hostFactory)
        : this(CreateAsyncHostFactory(hostFactory))
    {
    }

    /// <summary>Creates a harness from an asynchronous host factory.</summary>
    /// <param name="hostFactory">Factory invoked once when the harness starts.</param>
    public WebhookTestHarness(Func<CancellationToken, Task<IWebhookTestHost>> hostFactory)
    {
        ArgumentNullException.ThrowIfNull(hostFactory);
        _hostFactory = hostFactory;
    }

    /// <summary>Creates a harness around an existing host instance.</summary>
    /// <param name="host">Host owned and disposed by the harness.</param>
    public WebhookTestHarness(IWebhookTestHost host)
        : this(() => host)
    {
    }

    /// <summary>Creates a harness from a synchronous client factory.</summary>
    /// <param name="clientFactory">Factory invoked once when the harness starts.</param>
    public WebhookTestHarness(Func<HttpClient> clientFactory)
        : this(CreateAsyncClientHostFactory(clientFactory))
    {
    }

    /// <summary>Creates a harness from an asynchronous client factory.</summary>
    /// <param name="clientFactory">Factory invoked once when the harness starts.</param>
    public WebhookTestHarness(Func<Task<HttpClient>> clientFactory)
        : this(CreateAsyncClientHostFactory(clientFactory))
    {
    }

    /// <summary>Creates a harness from a cancellation-aware client factory.</summary>
    /// <param name="clientFactory">Factory invoked once when the harness starts.</param>
    public WebhookTestHarness(Func<CancellationToken, Task<HttpClient>> clientFactory)
        : this(CreateAsyncClientHostFactory(clientFactory))
    {
    }

    /// <summary>Whether the host has been started and remains available.</summary>
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

    /// <summary>Starts the host once; repeated calls while started are no-ops.</summary>
    /// <param name="cancellationToken">Token used to cancel startup.</param>
    /// <returns>A task that completes when the host is ready or already started.</returns>
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

    /// <summary>Creates and tracks a client owned by the active host.</summary>
    /// <returns>A client that the harness disposes during shutdown.</returns>
    /// <exception cref="InvalidOperationException">The harness has not been started.</exception>
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

    /// <summary>Sends a request using the default response-content completion option.</summary>
    /// <param name="request">Request to send; the harness does not take ownership of it.</param>
    /// <param name="cancellationToken">Token used to cancel the send.</param>
    /// <returns>The HTTP response.</returns>
    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    /// <summary>Sends a request with an explicit response completion option.</summary>
    /// <param name="request">Request to send; the harness does not take ownership of it.</param>
    /// <param name="completionOption">Controls when the response task completes.</param>
    /// <param name="cancellationToken">Token used to cancel the send.</param>
    /// <returns>The HTTP response.</returns>
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

    /// <summary>Stops the host and disposes clients created by the harness.</summary>
    /// <param name="cancellationToken">Token used to cancel shutdown.</param>
    /// <returns>A task that completes after host and client cleanup.</returns>
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

    /// <summary>Synchronously stops and disposes the harness.</summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Asynchronously stops and disposes the harness.</summary>
    /// <returns>A task that completes after host and client cleanup.</returns>
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

    /// <summary>Returns the harness type name without exposing host details.</summary>
    /// <returns>The type name.</returns>
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

/// <summary>Typed <see cref="WebApplicationFactory{TEntryPoint}"/>-backed test harness.</summary>
/// <typeparam name="TApplication">Application entry-point type used by the test server.</typeparam>
public sealed class WebhookTestHarness<TApplication> : IDisposable, IAsyncDisposable
    where TApplication : class
{
    private readonly WebhookTestHarness _inner;

    /// <summary>Creates a harness with a default application factory.</summary>
    public WebhookTestHarness()
        : this(new WebApplicationFactory<TApplication>())
    {
    }

    /// <summary>Creates a harness around an existing application factory.</summary>
    /// <param name="factory">The test-server factory owned by the harness.</param>
    public WebhookTestHarness(WebApplicationFactory<TApplication> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _inner = new WebhookTestHarness(new WebApplicationFactoryHost(factory));
    }

    /// <summary>Creates and configures an application factory before starting it.</summary>
    /// <param name="configureFactory">Callback that customizes the factory.</param>
    public WebhookTestHarness(Action<WebApplicationFactory<TApplication>> configureFactory)
        : this(CreateFactory(configureFactory))
    {
    }

    /// <summary>Whether the underlying test server is started.</summary>
    public bool IsStarted => _inner.IsStarted;

    /// <summary>Starts the underlying test server.</summary>
    /// <param name="cancellationToken">Token used to cancel startup.</param>
    /// <returns>A task representing startup.</returns>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return _inner.StartAsync(cancellationToken);
    }

    /// <summary>Creates a client from the underlying test server.</summary>
    /// <returns>A client owned by the harness.</returns>
    public HttpClient CreateClient()
    {
        return _inner.CreateClient();
    }

    /// <summary>Sends a request through the underlying test server.</summary>
    /// <param name="request">Request to send.</param>
    /// <param name="cancellationToken">Token used to cancel the send.</param>
    /// <returns>The HTTP response.</returns>
    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        return _inner.SendAsync(request, cancellationToken);
    }

    /// <summary>Sends a request with an explicit response completion option.</summary>
    /// <param name="request">Request to send.</param>
    /// <param name="completionOption">Controls when the response task completes.</param>
    /// <param name="cancellationToken">Token used to cancel the send.</param>
    /// <returns>The HTTP response.</returns>
    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken = default)
    {
        return _inner.SendAsync(request, completionOption, cancellationToken);
    }

    /// <summary>Stops the underlying test server and disposes its clients.</summary>
    /// <param name="cancellationToken">Token used to cancel shutdown.</param>
    /// <returns>A task representing shutdown.</returns>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return _inner.StopAsync(cancellationToken);
    }

    /// <summary>Synchronously disposes the underlying harness.</summary>
    public void Dispose()
    {
        _inner.Dispose();
    }

    /// <summary>Asynchronously disposes the underlying harness.</summary>
    /// <returns>A task representing disposal.</returns>
    public ValueTask DisposeAsync()
    {
        return _inner.DisposeAsync();
    }

    /// <summary>Returns the harness type name without exposing application details.</summary>
    /// <returns>The type name.</returns>
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
