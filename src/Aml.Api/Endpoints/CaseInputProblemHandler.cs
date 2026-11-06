using Aml.Infra.Txt;
using Microsoft.AspNetCore.Diagnostics;

namespace Aml.Api.Endpoints;

internal sealed class CaseInputProblemHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var inputCameFromDatabaseNotRequest = !HttpMethods.IsPost(httpContext.Request.Method);
        if (exception is not CaseInputException e || inputCameFromDatabaseNotRequest) return false;
        await Problems.Input(e).ExecuteAsync(httpContext);
        return true;
    }
}
