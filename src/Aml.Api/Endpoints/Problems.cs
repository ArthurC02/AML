using Aml.Core.Codes;
using Aml.Infra.Txt;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aml.Api.Endpoints;

/// <summary>
/// RFC 9457 problem responses. The type is the problem's entry in the code catalog (/api/codes/{code}); every problem carries
/// errors[] (what is wrong where) and fix (what to send instead).
/// </summary>
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

    /// <summary>The problem code of a status that no handler described (unmatched route, binding failure, unhandled exception).</summary>
    public static string CodeOf(int status) => status switch
    {
        StatusCodes.Status400BadRequest => InvalidRequest,
        StatusCodes.Status404NotFound => NotFound,
        >= 500 => InternalError,
        _ => HttpError,
    };

    public static string Title(string code) => CodeCatalog.All[code].Name;

    public static ProblemHttpResult Of(string code, string detail, string fix, params object[] errors) =>
        TypedResults.Problem(detail, type: Type(code), title: Title(code), statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["errors"] = errors, ["fix"] = fix });

    /// <summary>A required file missing or sent twice is INPUT_INCOMPLETE; a file not in the export layout is INPUT_UNREADABLE.</summary>
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
