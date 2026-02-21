using Grpc.Core;

namespace EMMA.TestPlugin.Services;

internal static class TestPluginRpcGuard
{
    private const string CorrelationIdHeader = "x-correlation-id";

    public static string GetCorrelationId(ServerCallContext context)
    {
        var header = context.RequestHeaders?.Get(CorrelationIdHeader);
        return string.IsNullOrWhiteSpace(header?.Value) ? "unknown" : header.Value;
    }

    public static void EnsureActive(ServerCallContext context)
    {
        if (context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Request was cancelled."));
        }

        if (context.Deadline != DateTime.MaxValue
            && context.Deadline.ToUniversalTime() <= DateTime.UtcNow)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Request deadline exceeded."));
        }
    }
}
