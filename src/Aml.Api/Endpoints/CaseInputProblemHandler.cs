using Aml.Infra.Txt;
using Microsoft.AspNetCore.Diagnostics;

namespace Aml.Api.Endpoints;

/// <summary>
/// Turns a <see cref="CaseInputException"/> from the files a POST sends into its problem; on any other request the input was
/// read from the database, so it stays a 500.
/// </summary>
internal sealed class CaseInputProblemHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not CaseInputException e || !HttpMethods.IsPost(httpContext.Request.Method)) return false;
        await Problems.Input(e).ExecuteAsync(httpContext);
        return true;
    }
}
