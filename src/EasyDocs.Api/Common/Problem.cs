namespace EasyDocs.Api.Common;

// Thin RFC-7807 wrappers over the stdlib Results.Problem (already emits application/problem+json).
public static class Problem
{
    public static IResult Of(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}
