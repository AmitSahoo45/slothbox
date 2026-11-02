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

        // Spec 8.4: no page may frame the dashboard. Framed, its requests are same-origin and pass every check below,
        // so another page could disguise its buttons (clickjacking). Added as the response starts, so an error answer
        // that clears the headers still carries them.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
            return Task.CompletedTask;
        });
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
        // What the browser marks as sent by another site's page, or by a page on another port of this laptop, even a
        // followed link or a pop-up: the dashboard opens from the tray, a bookmark or the address bar.
        var site = context.Request.Headers["Sec-Fetch-Site"];
        if (site == "cross-site" || site == "same-site")
        {
            await Results.Problem(statusCode: 403, title: "Requests from other sites are refused").ExecuteAsync(context);
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
