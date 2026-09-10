using DiagramMaker.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace DiagramMaker.Tests;

public sealed class CodeBlockGuardTests
{
    [Theory]
    [InlineData("/health", "https://untrusted.invalid", false, false, 200, true, false)]
    [InlineData("/api/v1/code-block-workspaces", "", false, false, 200, true, true)]
    [InlineData("/api/v1/code-block-runs", "https://untrusted.invalid", false, false, 403, false, false)]
    [InlineData("/api/v1/code-block-runs", "http://localhost:5173", true, false, 200, true, true)]
    [InlineData("/api/v1/code-block-runs", "http://localhost:5173", false, false, 403, false, false)]
    [InlineData("/api/v1/code-block-runs", "http://localhost:5080", false, false, 200, true, true)]
    [InlineData("/api/v1/code-block-runs", "", false, true, 200, true, false)]
    public async Task GuardPreservesAllAccessAndBodyLimitPaths(string path, string origin,
        bool development, bool readOnly, int status, bool passes, bool changesLimit)
    {
        using var services = new ServiceCollection().AddOptions().Configure<DiagramMaker.Configuration.CodeBlockOptions>(o => o.MaximumRequestBytes = 8 * 1024 * 1024).AddSingleton<IWebHostEnvironment>(new TestEnvironment
            { EnvironmentName = development ? "Development" : "Production" }).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path; context.Request.Scheme = "http"; context.Request.Host = new HostString("localhost:5080");
        context.Request.Headers.Origin = origin;
        context.Response.Body = new MemoryStream();
        var limit = new BodyLimit { IsReadOnly = readOnly, MaxRequestBodySize = 100 };
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(limit);
        var passed = false;
        await CodeBlockEndpoints.GuardAsync(context, _ => { passed = true; return Task.CompletedTask; });
        Assert.Equal(status, context.Response.StatusCode); Assert.Equal(passes, passed);
        Assert.Equal(changesLimit ? 8 * 1024 * 1024 : 100, limit.MaxRequestBodySize);
    }

    private sealed class BodyLimit : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; init; }
        public long? MaxRequestBodySize { get; set; }
    }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "test";
        public string EnvironmentName { get; set; } = "Production";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
