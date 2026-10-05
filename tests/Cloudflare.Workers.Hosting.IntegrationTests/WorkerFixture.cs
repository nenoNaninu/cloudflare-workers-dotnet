using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cloudflare.Workers.Hosting.IntegrationTests;
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
    private UpstreamServer? _upstream;

    public static bool Enabled => Environment.GetEnvironmentVariable("CLOUDFLARE_E2E") == "1";

    /// <summary>Client for the worker under test. Redirects are not followed.</summary>
    public HttpClient Client { get; private set; } = null!;

    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>Base URL of the local HTTP server the worker calls out to.</summary>
    public string UpstreamUrl => _upstream?.BaseUrl ?? throw new InvalidOperationException("E2E tests are disabled.");

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
        if (!Enabled)
        {
            return;
        }

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

        var startInfo = new ProcessStartInfo("node")
        {
            WorkingDirectory = workerDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(wrangler);
        startInfo.ArgumentList.Add("dev");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("wrangler.jsonc");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("upstream/wrangler.jsonc");
        startInfo.ArgumentList.Add("--test-scheduled");
        startInfo.ArgumentList.Add("--ip");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--inspector-port");
        startInfo.ArgumentList.Add(inspectorPort.ToString());
        startInfo.ArgumentList.Add("--persist-to");
        startInfo.ArgumentList.Add(_persistDirectory);
        startInfo.ArgumentList.Add("--var");
        startInfo.ArgumentList.Add($"UPSTREAM_URL:{_upstream.BaseUrl}");
        startInfo.Environment["WRANGLER_SEND_METRICS"] = "false";
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["CI"] = "1";

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wrangler = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _wrangler.OutputDataReceived += (_, e) => OnLine(e.Data, ready);
        _wrangler.ErrorDataReceived += (_, e) => OnLine(e.Data, ready);
        _wrangler.Exited += (_, _) => ready.TrySetException(
            new InvalidOperationException("wrangler dev exited before it was ready." + Environment.NewLine + WranglerLog));
        _wrangler.Start();
        _wrangler.BeginOutputReadLine();
        _wrangler.BeginErrorReadLine();

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

        if (_wrangler is not null)
        {
            try
            {
                if (!_wrangler.HasExited)
                {
                    _wrangler.Kill(entireProcessTree: true);
                    await _wrangler.WaitForExitAsync();
                }
            }
            catch (InvalidOperationException)
            {
            }

            _wrangler.Dispose();
        }

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

    private void OnLine(string? line, TaskCompletionSource ready)
    {
        if (line is null)
        {
            return;
        }

        lock (_log)
        {
            _log.AppendLine(line);
        }

        if (line.Contains("Ready on", StringComparison.Ordinal))
        {
            ready.TrySetResult();
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
