# Cloudflare Workers for .NET

Build and deploy Cloudflare Workers applications written in C#.

Similar to ASP.NET Core, you can write applications using the Minimal API style.

```cs
using Cloudflare.Workers.Hosting;

var builder = WorkerApplication.CreateBuilder();

builder.MapGet("/", static _ =>
    Task.FromResult(HttpResponse.Text("Hello from C# on Cloudflare Workers!")));

builder.MapGet("/delay/:ms", static async context =>
{
    double ms = Math.Min(double.Parse(context.Parameters["ms"]), 5000);
    await WorkerTimer.Delay(ms);
    return HttpResponse.Text($"Slept {ms} ms without blocking the isolate.");
});

builder.MapPost("/echo", static async context =>
    HttpResponse.Text(await context.Request.ReadAsStringAsync()));

builder.Build().Run();
```

## Table of Contents

- [Installation \& Setup](#installation--setup)
  - [Add the Packages](#add-the-packages)
  - [Install and Configure the WASI SDK](#install-and-configure-the-wasi-sdk)
  - [Configure the Project File](#configure-the-project-file)
- [Usage](#usage)
  - [Write C# Code](#write-c-code)
  - [Development \& Deployment](#development--deployment)
- [Limitations](#limitations)
- [Simple Examples](#simple-examples)
- [Supported Cloudflare Workers Features](#supported-cloudflare-workers-features)

## Installation & Setup

To deploy an application to Cloudflare Workers, you need to compile C# project to WebAssembly (WASM) using NativeAOT.
The .NET SDK doesn't currently support NativeAOT compilation to WASM out of the box, so you'll need some additional setup.


### Add the Packages

NativeAOT compilation to WASM requires `Microsoft.DotNet.ILCompiler.LLVM`, an experimental package that is not published on [nuget.org](https://www.nuget.org/).

Create a `nuget.config` file from the template:

```
$ dotnet new nugetconfig
```

Replace its contents with the following to make `Microsoft.DotNet.ILCompiler.LLVM` available:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
    <packageSources>
        <clear />
        <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
        <add key="dotnet-experimental" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-experimental/nuget/v3/index.json" />
    </packageSources>
    <packageSourceMapping>
        <packageSource key="nuget.org">
            <package pattern="*" />
        </packageSource>
        <packageSource key="dotnet-experimental">
            <package pattern="Microsoft.DotNet.ILCompiler.LLVM" />
            <package pattern="runtime.win-x64.Microsoft.DotNet.ILCompiler.LLVM" />
            <package pattern="runtime.linux-x64.Microsoft.DotNet.ILCompiler.LLVM" />
            <package pattern="runtime.linux-arm64.Microsoft.DotNet.ILCompiler.LLVM" />
            <package pattern="runtime.osx-x64.Microsoft.DotNet.ILCompiler.LLVM" />
            <package pattern="runtime.osx-arm64.Microsoft.DotNet.ILCompiler.LLVM" />
            <package pattern="runtime.wasi-wasm.Microsoft.DotNet.ILCompiler.LLVM" />
        </packageSource>
    </packageSourceMapping>
</configuration>
```

Add these three packages to your application project (`.csproj`) using `PackageReference`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <ItemGroup>
        <PackageReference Include="Cloudflare.Workers.Hosting" Version="0.1.0" />
        <PackageReference Include="Microsoft.DotNet.ILCompiler.LLVM" Version="10.0.0-rc.1.26357.1" />
        <PackageReference Include="runtime.$(NETCoreSdkPortableRuntimeIdentifier).Microsoft.DotNet.ILCompiler.LLVM" Version="10.0.0-rc.1.26357.1" />
    </ItemGroup>
</Project>
```

If you use Central Package Management (CPM), adjust the configuration accordingly. See `Directory.Packages.props` in this repository for an example.

At this point, check that `dotnet restore` completes successfully.

### Install and Configure the WASI SDK

NativeAOT compilation to WASM requires the [WASI SDK](https://github.com/WebAssembly/wasi-sdk).
For `Microsoft.DotNet.ILCompiler.LLVM` version `10.0.0-rc.1.26357.1`, [WASI SDK v29](https://github.com/WebAssembly/wasi-sdk/releases#release-wasi-sdk-29) is [strongly recommended](https://github.com/dotnet/runtimelab/blob/feature/NativeAOT-LLVM/docs/using-nativeaot/prerequisites.md#wasi-sdk).

After downloading and extracting the WASI SDK, set the `WASI_SDK_PATH` environment variable to its directory:

```powershell
$Env:WASI_SDK_PATH="C:\your\path\wasi-sdk-29.0-x86_64-windows"  
```

### Configure the Project File

Set `OutputType`, `RuntimeIdentifier`, `SelfContained`, `UseAppHost`, `PublishTrimmed`, and `MSBuildEnableWorkloadResolver` in your application's `.csproj` file as follows:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>

        <!-- NativeAOT-LLVM / wasi-wasm -->
        <RuntimeIdentifier>wasi-wasm</RuntimeIdentifier>
        <SelfContained>true</SelfContained>
        <UseAppHost>false</UseAppHost>
        <PublishTrimmed>true</PublishTrimmed>
        <MSBuildEnableWorkloadResolver>false</MSBuildEnableWorkloadResolver>
    </PropertyGroup>

</Project>
```

## Usage

### Write C# Code

Use `WorkerApplication.CreateBuilder()` as the starting point for your application and define routes in the Minimal API style.
Delegates passed to `MapGet` and `MapPost` can use `async` / `await`.

```cs
using Cloudflare.Workers.Hosting;

var builder = WorkerApplication.CreateBuilder();

builder.MapGet("/", static _ =>
    Task.FromResult(HttpResponse.Text("Hello from C# on Cloudflare Workers!")));

builder.MapGet("/delay/:ms", static async context =>
{
    double ms = Math.Min(double.Parse(context.Parameters["ms"]), 5000);
    await WorkerTimer.Delay(ms);
    return HttpResponse.Text($"Slept {ms} ms without blocking the isolate.");
});

builder.MapPost("/echo", static async context =>
    HttpResponse.Text(await context.Request.ReadAsStringAsync()));

builder.Build().Run();
```

### Development & Deployment

There are several ways to set up development and deployment. This example uses npm.

First, create a `package.json` file with the following contents:

```json
{
  "devDependencies": {
    "wrangler": "^4.131.0"
  },
  "scripts": {
    "dev": "wrangler dev",
    "deploy": "wrangler deploy"
  }
}
```

Next, create a `wrangler.jsonc` file as shown below.
The two key settings for `Cloudflare.Workers.Hosting` are `main` and `build.command`.

```jsonc
{
  "name": "[your-app-name]",
  "main": "bin/Release/net10.0/wasi-wasm/publish/worker/index.mjs",
  "compatibility_date": "2026-09-10",
  "build": {
    "command": "dotnet publish [YourProject].csproj -c Release",
  }
}
```

Install the dependencies, then use the following commands to run the application locally or deploy it to Cloudflare:

```
$ npm install
$ npm run dev
$ npm run deploy
```

Extend `wrangler.jsonc` as needed for the Cloudflare features you want to use.
Using `Cloudflare.Workers.Hosting` does not change how you configure Wrangler, so refer to the official Cloudflare documentation for configuration details.

## Limitations

Running WASM on Cloudflare Workers comes with several limitations:

- The `Main` method must be synchronous and return `void`.
  - You cannot use `async` / `await` in top-level statements.
- The WASM environment is currently single-threaded.
  - Multithreaded code is not supported.
  - For example, `Task.Run` is not available.
- Standard I/O APIs are not available.
  - Use the APIs provided by `Cloudflare.Workers.Hosting`.
  - For example, use `Fetch.FetchAsync` instead of `HttpClient`.
    - Support for `HttpClient` is planned.
- Use `WorkerTimer.Delay` to wait for a specified duration.
  - `Task.Delay` and `Thread.Sleep` are not available.

## Simple Examples

See [cloudflare-workers-dotnet-examples](https://github.com/nenoNaninu/cloudflare-workers-dotnet-examples).

## Supported Cloudflare Workers Features

- [Handlers](https://developers.cloudflare.com/workers/runtime-apis/handlers/)
  - [Fetch Handler](https://developers.cloudflare.com/workers/runtime-apis/handlers/fetch/)
  - [Scheduled Handler](https://developers.cloudflare.com/workers/runtime-apis/handlers/scheduled/)
- [KV](https://developers.cloudflare.com/kv/)
  - get
  - put
  - delete
  - list
- [R2](https://developers.cloudflare.com/r2/api/workers/workers-api-reference/)
  - get
  - head
  - put
  - delete
- [D1](https://developers.cloudflare.com/d1/worker-api/d1-database/)
  - [prepare](https://developers.cloudflare.com/d1/worker-api/d1-database/#prepare)
  - [bind](https://developers.cloudflare.com/d1/worker-api/prepared-statements/#bind)
  - [run](https://developers.cloudflare.com/d1/worker-api/prepared-statements/#run)
  - all
  - [first](https://developers.cloudflare.com/d1/worker-api/prepared-statements/#first)
- [Service Binding](https://developers.cloudflare.com/workers/runtime-apis/bindings/service-bindings/)
- [Context](https://developers.cloudflare.com/workers/runtime-apis/context/)
  - [waitUntil](https://developers.cloudflare.com/workers/runtime-apis/context/#waituntil)
