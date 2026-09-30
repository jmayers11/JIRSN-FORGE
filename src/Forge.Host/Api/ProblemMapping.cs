using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Microsoft.AspNetCore.Diagnostics;

namespace Forge.Host.Api;

/// <summary>Maps kernel exceptions to RFC 9457 problem responses that explain what happened.</summary>
public sealed class ProblemMapping : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        IResult? result = exception switch
        {
            ForgeAuthorizationException denied => Results.Problem(
                title: "Not authorized",
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?> { ["reasons"] = denied.Decision.Reasons }),
            CommandRejectedException rejected => Results.Problem(
                title: "Command rejected",
                detail: rejected.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?> { ["code"] = rejected.Code }),
            IdempotencyConflictException conflict => Results.Problem(
                title: "Idempotency key conflict",
                detail: conflict.Message,
                statusCode: StatusCodes.Status409Conflict),
            _ => null,
        };

        if (result is null)
        {
            return false;
        }

        await result.ExecuteAsync(httpContext);
        return true;
    }
}
