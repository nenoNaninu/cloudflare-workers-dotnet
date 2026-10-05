using System.Runtime.CompilerServices;
using Xunit;

namespace Cloudflare.Workers.Hosting.IntegrationTests;

/// <summary>
/// A test that needs a running <c>wrangler dev</c>. It is skipped unless <c>CLOUDFLARE_E2E=1</c>;
/// see tests/README.md.
/// </summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!WorkerFixture.Enabled)
        {
            Skip = "Set CLOUDFLARE_E2E=1 (after building the worker and running npm ci) to run the wrangler dev tests.";
        }
    }
}
