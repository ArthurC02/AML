using Aml.Core.Cases;
using Aml.Core.Codes;
using Aml.Infra.Txt;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aml.Api.Endpoints;

/// <summary>RFC 9457 problem responses; every problem has a detail, errors[] (what is wrong where) and fix (what to send instead).</summary>
internal static class Problems
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string UnsupportedFile = "UNSUPPORTED_FILE";
    public const string InputIncomplete = "INPUT_INCOMPLETE";
    public const string InputUnreadable = "INPUT_UNREADABLE";
    public const string NotFound = "NOT_FOUND";
    public const string HttpError = "HTTP_ERROR";
    public const string InternalError = "INTERNAL_ERROR";

    public static string Type(string code) => $"/api/codes/{code}";

    public static string CodeOf(int status) => status switch
    {
        StatusCodes.Status400BadRequest => InvalidRequest,
        StatusCodes.Status404NotFound => NotFound,
        >= 500 => InternalError,
        _ => HttpError,
    };

    public static string Title(string code) => CodeCatalog.All[code].Name;

    public static string Fix(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Send the parameters and body the operation declares in /openapi/v1.json, with values of the declared types.",
        StatusCodes.Status404NotFound => "Check the route in /openapi/v1.json and the id, taken unchanged from the response that created or listed the resource.",
        StatusCodes.Status408RequestTimeout => "Send the whole request body without pausing; the server stops waiting for a body that arrives too slowly.",
        StatusCodes.Status429TooManyRequests => "Retry after the seconds in Retry-After: the server builds only a few workbooks at a time (Api:WorkbookConcurrency, Api:WorkbookQueue).",
        StatusCodes.Status413PayloadTooLarge => "Send fewer or smaller files per request: the request body is over the server's limit (Api:MaxRequestBytes).",
        >= 500 => "Retry later; if it persists, report the traceId of this response.",
        _ => "Use the method and content type the operation declares in /openapi/v1.json.",
    };

    public static ProblemHttpResult Of(string code, string detail, string fix, params object[] errors) =>
        TypedResults.Problem(detail, type: Type(code), title: Title(code), statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["errors"] = errors, ["fix"] = fix });

    public static ProblemHttpResult? Top(int? top) => top is < 1 or > ProfilesEndpoints.MaxTop
        ? Of(InvalidRequest, $"top must be 1 to {ProfilesEndpoints.MaxTop}.", $"Send a top from 1 to {ProfilesEndpoints.MaxTop}, or leave it out for {ProfilesEndpoints.DefaultTop}.", new { parameter = "top" })
        : null;

    public static ProblemHttpResult? Paging(int? pageSize, string? pageToken, out int size, out (DateTimeOffset, string)? after)
    {
        size = pageSize ?? PageToken.DefaultSize;
        after = null;
        if (size is < 1 or > PageToken.MaxSize)
            return Of(InvalidRequest, $"pageSize must be 1 to {PageToken.MaxSize}.", $"Send a pageSize from 1 to {PageToken.MaxSize}, or leave it out for {PageToken.DefaultSize}.", new { parameter = "pageSize" });
        if (string.IsNullOrEmpty(pageToken)) return null;
        if (!PageToken.TryDecode(pageToken, out var key))
            return Of(InvalidRequest, "pageToken was not issued by this API.", "Send the nextPageToken of the previous page unchanged, or leave it out for the first page.", new { parameter = "pageToken" });
        after = key;
        return null;
    }

    public static ProblemHttpResult Input(CaseInputException e) => e.Problem switch
    {
        CaseInputException.Missing => Of(InputIncomplete, e.Message,
            $"Upload {e.File}: every Case needs exactly one 案件資訊*.txt and the subject's own file (客戶-背景資料.txt or 客戶-關聯人背景資料.txt).", Error(e)),
        CaseInputException.Duplicate => Of(InputIncomplete, e.Message,
            $"Send exactly one file that reads as {e.File}; remove the others.", Error(e)),
        _ => Of(InputUnreadable, e.Message, $"Send {e.File} as exported, in the .txt layout or as the HTML page.", Error(e)),
    };

    private static object Error(CaseInputException e) => new { file = e.File, table = e.Table, problem = e.Problem };
}
