using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using V12.MCP;

// The MCP client launches this process and speaks JSON-RPC over stdin/stdout.
// Everything else (logs, diagnostics) must go to stderr or it corrupts the
// protocol stream.

try
{
    ProbeClient.Configure(ProbeClient.FromEnvironment(args));
}
catch (Exception ex)
{
    Console.Error.WriteLine("[V12.MCP] " + ex.Message);
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;
