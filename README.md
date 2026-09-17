# Cloudflare Workers for .NET

[![NuGet](https://img.shields.io/nuget/v/Cloudflare.Workers.Hosting.svg)](https://www.nuget.org/packages/Cloudflare.Workers.Hosting)

Build and deploy Cloudflare Workers applications written in C#.

You can write applications using the Minimal API style similar to ASP.NET Core.

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

- [Examples](#examples)
- [Installation \& Setup](#installation--setup)
  - [Add the Packages](#add-the-packages)
  - [Install and Configure the WASI SDK](#install-and-configure-the-wasi-sdk)
  - [Configure the Project File](#configure-the-project-file)
- [Usage](#usage)
  - [Write C# Code](#write-c-code)
  - [Development \& Deployment](#development--deployment)
  - [Fetch](#fetch)
  - [KV](#kv)
  - [R2](#r2)
  - [D1](#d1)
    - [Migrations](#migrations)
- [Limitations](#limitations)
- [Supported Cloudflare Workers Features](#supported-cloudflare-workers-features)


## Examples

For the simplest example, see the [cloudflare-workers-dotnet-examples](https://github.com/nenoNaninu/cloudflare-workers-dotnet-examples).


## Installation & Setup

To deploy an application to Cloudflare Workers, you need to compile C# project to WebAssembly (WASM) using NativeAOT.
The .NET SDK doesn't currently support NativeAOT compilation to WASM out of the box, so you'll need some additional setup.


### Add the Packages

NativeAOT compilation to WASM requires [`Microsoft.DotNet.ILCompiler.LLVM`](https://github.com/dotnet/runtimelab/blob/feature/NativeAOT-LLVM/docs/using-nativeaot/compiling.md), an experimental package that is not published on [nuget.org](https://www.nuget.org/).

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

Add references to the following three packages to your application project (`.csproj`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <!-- ... -->

    <ItemGroup>
        <PackageReference Include="Cloudflare.Workers.Hosting" Version="0.2.0" />
        <PackageReference Include="Microsoft.DotNet.ILCompiler.LLVM" Version="10.0.0-rc.1.26357.1" />
        <PackageReference Include="runtime.$(NETCoreSdkPortableRuntimeIdentifier).Microsoft.DotNet.ILCompiler.LLVM" Version="10.0.0-rc.1.26357.1" />
    </ItemGroup>

    <!-- ... -->
</Project>
```

If you use Central Package Management (CPM), adjust the configuration accordingly. See [`Directory.Packages.props`](./Directory.Packages.props) in this repository for an example.

Verify that `dotnet restore` completes successfully before continuing.

### Install and Configure the WASI SDK

NativeAOT compilation to WASM requires the [WASI SDK](https://github.com/WebAssembly/wasi-sdk).
For `Microsoft.DotNet.ILCompiler.LLVM` version `10.0.0-rc.1.26357.1`, [WASI SDK v29](https://github.com/WebAssembly/wasi-sdk/releases#release-wasi-sdk-29) is [strongly recommended](https://github.com/dotnet/runtimelab/blob/feature/NativeAOT-LLVM/docs/using-nativeaot/prerequisites.md#wasi-sdk).

After downloading and extracting the WASI SDK, set the `WASI_SDK_PATH` environment variable to the extracted directory:

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

    <!-- ... -->
</Project>
```

## Usage

### Write C# Code

Use `WorkerApplication.CreateBuilder()` as the starting point for your application, and define routes in the Minimal API style.
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
  "main": "bin/Release/net10.0/wasi-wasm/publish/worker/index.js",
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
Using `Cloudflare.Workers.Hosting` does not change how Wrangler is configured, so refer to the official Cloudflare documentation for configuration details.


To use KV, R2, or D1, configure the corresponding [binding in `wrangler.jsonc`](https://developers.cloudflare.com/workers/wrangler/configuration/#bindings). The name passed to the appropriate `context.Env` method must match the binding's `binding` value.

For KV, R2, and D1, configure the corresponding [bindings in `wrangler.jsonc`](https://developers.cloudflare.com/workers/wrangler/configuration/#bindings). The names passed to `context.Env` must match the `binding` values in your configuration.

### Fetch

Use `Fetch.FetchAsync` to make an outbound HTTP request.

```cs
builder.MapGet("/fetch", static async _ =>
{
    using var response = await Fetch.FetchAsync("https://example.com/");
    string body = await response.ReadAsStringAsync();
    return HttpResponse.Text(body, response.StatusCode);
});
```

To specify a method, headers, or a body, pass a `FetchRequestMessage` instead of a URL string.

```cs
builder.MapPost("/fetch", static async _ =>
{
    var message = new FetchRequestMessage("https://api.example.com/messages")
    {
        Method = "POST",
        Body = """{"message":"Hello from C#!"}""",
        Headers =
        {
            { "Content-Type", "application/json" },
            { "Accept", "application/json" }
        }
    };

    using var response = await Fetch.FetchAsync(message);
    string body = await response.ReadAsStringAsync();
    return HttpResponse.Json(body, response.StatusCode);
});
```

### KV

In `wrangler.jsonc`, configure a KV namespace binding with a name of your choice. This example uses `MY_KV`; replace it with the name of your binding.

```cs
builder.MapPut("/kv/:key", static async context =>
{
    using var kv = context.Env.Kv("MY_KV");
    var value = await context.Request.ReadAsStringAsync();
    await kv.PutAsync(context.Parameters["key"], value);
    return HttpResponse.Empty();
});

builder.MapGet("/kv/:key", static async context =>
{
    using var kv = context.Env.Kv("MY_KV");
    var value = await kv.GetTextAsync(context.Parameters["key"]);
    return value is null ? HttpResponse.NotFound() : HttpResponse.Text(value);
});
```

Use `GetBytesAsync` and the `byte[]` overload of `PutAsync` for binary data. `DeleteAsync` removes a key, and `ListAsync` lists keys.

### R2

In `wrangler.jsonc`, configure an R2 bucket binding with a name of your choice. This example uses `MY_BUCKET`; replace it with the name of your binding.

```cs
builder.MapPut("/r2/:key", static async context =>
{
    using var bucket = context.Env.R2("MY_BUCKET");
    var body = await context.Request.ReadAsBytesAsync();
    using var uploaded = await bucket.PutAsync(context.Parameters["key"], body);
    return HttpResponse.Ok();
});

builder.MapGet("/r2/:key", static async context =>
{
    using var bucket = context.Env.R2("MY_BUCKET");
    using var obj = await bucket.GetAsync(context.Parameters["key"]);

    return obj is null
        ? HttpResponse.NotFound()
        : HttpResponse.Binary(await obj.BodyBytesAsync());
});
```

Use `HeadAsync` to retrieve object metadata without reading the body, and use `DeleteAsync` to remove an object.

### D1

In `wrangler.jsonc`, configure a D1 database binding with a name of your choice. This example uses `MY_DB`; replace it with the name of your binding.

Use `Prepare` and `Bind` to bind values to SQL parameters. Call `RunAsync` for write operations or `AllJsonAsync` to retrieve rows as JSON.

```cs
builder.MapPost("/d1/messages", static async context =>
{
    using var db = context.Env.D1("MY_DB");
    string text = await context.Request.ReadAsStringAsync();
    using var statement = db
        .Prepare("insert into messages (text) values (?1)")
        .Bind(JsArg.From(text));

    var result = await statement.RunAsync();

    return result.Success
        ? HttpResponse.Created()
        : HttpResponse.Error("Failed to insert the message.");
});

builder.MapGet("/d1/messages", static async context =>
{
    using var db = context.Env.D1("MY_DB");
    using var statement = db.Prepare(
        "select id, text from messages order by id desc limit 100");
    return HttpResponse.Json(await statement.AllJsonAsync());
});
```

Use `FirstJsonAsync` to retrieve a single row as JSON, or use `AllAsync` and `FirstAsync` with a source-generated `JsonTypeInfo<T>` to deserialize query results into .NET types.

#### Migrations

Before using the D1 APIs, apply the required migrations to the target database.
You can specify the directory containing the migration files by setting `migrations_dir` in the D1 binding configuration.

Add the following configuration to the `d1_databases` section of `wrangler.jsonc`:

```jsonc
{
    "d1_databases": [
        {
            "binding": "MY_DB",
            "database_name": "my-db",
            "migrations_dir": "my_migrations"
        }
    ]
}
```

Create a migration file named `0001-my-migration.sql` in the `my_migrations` directory:

```sql
CREATE TABLE messages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    text TEXT NOT NULL
);
```

Add the following scripts to `package.json`:

```json
{
  "scripts": {
    "d1:migrate:local": "wrangler d1 migrations apply MY_DB --local",
    "d1:migrate:remote": "wrangler d1 migrations apply MY_DB --remote"
  }
}
```

Run the appropriate script to apply the migrations to the local or remote database:

```
$ npm run d1:migrate:local
$ npm run d1:migrate:remote
```



## Limitations

Running WASM on Cloudflare Workers comes with several limitations:

- The `Main` method must be synchronous and return `void`.
  - You cannot use `async` / `await` in top-level statements.
- The WASM environment is currently single-threaded.
  - Multithreaded code is not supported.
  - For example, `Task.Run` is not available.
- Standard I/O APIs are not available.
  - Use the APIs provided by `Cloudflare.Workers.Hosting`.
  - For example, use `Fetch.FetchAsync` instead of `HttpClient`. Support for `HttpClient` is planned.
- Use `WorkerTimer.Delay` when your code needs to wait for a specified duration.
  - `Task.Delay` and `Thread.Sleep` are not available.

## Supported Cloudflare Workers Features

- [Handlers](https://developers.cloudflare.com/workers/runtime-apis/handlers/)
  - [Fetch Handler](https://developers.cloudflare.com/workers/runtime-apis/handlers/fetch/)
  - [Scheduled Handler](https://developers.cloudflare.com/workers/runtime-apis/handlers/scheduled/)
- [Fetch](https://developers.cloudflare.com/workers/runtime-apis/fetch/)
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
