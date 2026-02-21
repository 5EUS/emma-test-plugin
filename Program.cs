using EMMA.TestPlugin.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace EMMA.TestPlugin;

public static class Program
{
    public static void Main(string[] args)
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var port = GetPort(args, 5005);

        var builder = WebApplication.CreateBuilder(args);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
            });
        });

        builder.Services.AddGrpc();

        var app = builder.Build();

        app.MapGrpcService<TestPluginControlService>();
        app.MapGrpcService<TestSearchProviderService>();
        app.MapGrpcService<TestPageProviderService>();
        app.MapGrpcService<TestVideoProviderService>();
        app.MapGet("/", () => "EMMA test plugin is running.");

        app.Run();
    }

    private static int GetPort(string[] args, int defaultPort)
    {
        var envPort = Environment.GetEnvironmentVariable("EMMA_TEST_PLUGIN_PORT");
        if (!string.IsNullOrWhiteSpace(envPort) && int.TryParse(envPort, out var parsedEnv))
        {
            return parsedEnv;
        }

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var parsedArg))
            {
                return parsedArg;
            }
        }

        return defaultPort;
    }
}
