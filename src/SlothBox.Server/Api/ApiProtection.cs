namespace SlothBox.Server.Api;

internal sealed class PhoneEndpoint;

internal static class ApiProtection
{
    internal static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var phoneRoute = context.GetEndpoint()?.Metadata.GetMetadata<PhoneEndpoint>() is not null;
        if (context.Connection.LocalPort == 5081)
        {
            if (!phoneRoute)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(context);
            return;
        }

        var host = context.Request.Host.Value;
        if (!string.Equals(host, "127.0.0.1:5080", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(host, "localhost:5080", StringComparison.OrdinalIgnoreCase))
        {
            await Results.Problem(statusCode: 400, title: "Invalid Host").ExecuteAsync(context);
            return;
        }
        if (phoneRoute)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var header = context.Request.Headers["X-SlothBox"];
        if (!HttpMethods.IsGet(context.Request.Method) && (header.Count != 1 || header[0] != "1"))
        {
            await Results.Problem(statusCode: 403, title: "Required header missing or invalid").ExecuteAsync(context);
            return;
        }
        await next(context);
    }
}
