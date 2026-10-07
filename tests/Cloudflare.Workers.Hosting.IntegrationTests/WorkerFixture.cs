using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cloudflare.Workers.Hosting.IntegrationTests;
using Cysharp.Diagnostics;
using Xunit;

[assembly: AssemblyFixture(typeof(WorkerFixture))]

namespace Cloudflare.Workers.Hosting.IntegrationTests;

/// <summary>
/// Starts the C# worker (plus the JS upstream worker) with <c>wrangler dev</c> once per test run
/// and exposes an <see cref="HttpClient"/> pointing at it.
/// </summary>
public sealed class WorkerFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);

    private readonly StringBuilder _log = new();
    private readonly string _persistDirectory = Path.Combine(Path.GetTempPath(), "cf-e2e-" + Guid.NewGuid().ToString("N"));
    private Process? _wrangler;
    private Task? _wranglerOutputTask;
    private bool _stopping;
    private UpstreamServer? _upstream;

    /// <summary>Client for the worker under test. Redirects are not followed.</summary>
    public HttpClient Client { get; private set; } = null!;

    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>Base URL of the local HTTP server the worker calls out to.</summary>
    public string UpstreamUrl => _upstream?.BaseUrl ?? throw new InvalidOperationException("The upstream server has not been initialized.");

    public UpstreamServer.WaitUntilGate CreateWaitUntilGate(string key)
        => (_upstream ?? throw new InvalidOperationException("The upstream server has not been initialized.")).CreateWaitUntilGate(key);

    public string WranglerLog
    {
        get
        {
            lock (_log)
            {
                return _log.ToString();
            }
        }
    }

    public async ValueTask InitializeAsync()
    {
        string workerDirectory = Path.Combine(FindRepositoryRoot(), "tests", "Cloudflare.Workers.Hosting.IntegrationTests.App");
        string wrangler = Path.Combine(workerDirectory, "node_modules", "wrangler", "bin", "wrangler.js");
        string publishedWorker = Path.Combine(workerDirectory, "bin", "Release", "net10.0", "wasi-wasm", "publish", "worker", "index.js");

        if (!File.Exists(wrangler))
        {
            throw new InvalidOperationException($"'{wrangler}' was not found. Run 'npm ci' in {workerDirectory}.");
        }

        if (!File.Exists(publishedWorker))
        {
            throw new InvalidOperationException($"'{publishedWorker}' was not found. Run 'dotnet publish -c Release' in {workerDirectory}.");
        }

        _upstream = await UpstreamServer.StartAsync();

        int port = GetFreePort();
        int inspectorPort = GetFreePort();
        BaseAddress = new Uri($"http://127.0.0.1:{port}/");

        string command = $"node \"{wrangler}\" dev -c wrangler.jsonc -c upstream/wrangler.jsonc"
            + $" --test-scheduled --ip 127.0.0.1 --port {port} --inspector-port {inspectorPort}"
            + $" --persist-to \"{_persistDirectory}\" --var \"UPSTREAM_URL:{_upstream.BaseUrl}\"";

        // ProcessX adds environment variables rather than overwriting them. Keep inherited
        // values (including CI on GitHub Actions) and supply defaults only when absent.
        var environment = new Dictionary<string, string>
        {
            ["WRANGLER_SEND_METRICS"] = "false",
            ["NO_COLOR"] = "1",
            ["CI"] = "1",
        }
            .Where(entry => Environment.GetEnvironmentVariable(entry.Key) is null)
            .ToDictionary(entry => entry.Key, entry => entry.Value);

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (process, stdout, stderr) = ProcessX.GetDualAsyncEnumerable(
            command, workingDirectory: workerDirectory, environmentVariable: environment, encoding: Encoding.UTF8);
        _wrangler = process;
        _wranglerOutputTask = ObserveWranglerAsync(stdout, stderr, ready);

        using var timeout = new CancellationTokenSource(StartupTimeout);
        try
        {
            await ready.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("wrangler dev did not become ready in time." + Environment.NewLine + WranglerLog);
        }

        Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = BaseAddress,
            Timeout = TimeSpan.FromSeconds(60),
        };

        // The first request instantiates the wasm module; wait for it so tests do not pay for it.
        using var warmup = new CancellationTokenSource(StartupTimeout);
        while (true)
        {
            try
            {
                using var response = await Client.GetAsync("ping", warmup.Token);
                if (response.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(500, warmup.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();

        try
        {
            await StopWranglerAsync();
        }
        finally
        {
            if (_upstream is not null)
            {
                await _upstream.DisposeAsync();
            }

            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "wrangler-e2e.log"), WranglerLog);
                if (Directory.Exists(_persistDirectory))
                {
                    Directory.Delete(_persistDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task StopWranglerAsync()
    {
        _stopping = true;
        if (_wrangler is not null)
        {
            try
            {
                if (!_wrangler.HasExited)
                {
                    _wrangler.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (_wranglerOutputTask is not null)
        {
            // ProcessX disposes the process after stdout is drained. Wait for both streams
            // before writing the log; cancellation would discard buffered output.
            await _wranglerOutputTask;
        }
    }

    private async Task ObserveWranglerAsync(IAsyncEnumerable<string> stdout, IAsyncEnumerable<string> stderr, TaskCompletionSource ready)
    {
        try
        {
            await Task.WhenAll(ReadOutputAsync(stdout, ready), ReadOutputAsync(stderr, ready));
            ready.TrySetException(new InvalidOperationException(
                "wrangler dev exited before it was ready." + Environment.NewLine + WranglerLog));
        }
        catch (ProcessErrorException) when (_stopping)
        {
            // Killing the process tree during teardown produces a nonzero exit code.
        }
        catch (Exception ex)
        {
            ready.TrySetException(new InvalidOperationException(
                "wrangler dev failed." + Environment.NewLine + WranglerLog, ex));
            throw;
        }
    }

    private async Task ReadOutputAsync(IAsyncEnumerable<string> output, TaskCompletionSource ready)
    {
        await foreach (string line in output)
        {
            lock (_log)
            {
                _log.AppendLine(line);
            }

            if (line.Contains("Ready on", StringComparison.Ordinal))
            {
                ready.TrySetResult();
            }
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cloudflare.Workers.Hosting.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root (Cloudflare.Workers.Hosting.slnx).");
    }
}
