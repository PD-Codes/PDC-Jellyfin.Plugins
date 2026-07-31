using System;
using System.Net;

namespace Jellyfin.Plugin.PdCodesApi.Api;

/// <summary>
/// Raised for any v5 response the caller must distinguish by status code.
/// </summary>
/// <remarks>
/// This exists so a provider can tell 404 (no such work) from 409 (ambiguous, or
/// episodes fetched but not merged) from 400 (retired source / meaningless filter)
/// from 503 (pipeline degraded). Collapsing them into "null" was the tempting
/// design and it is wrong: 404 means stop, 409 means stop AND tell the operator
/// something specific, and 503 means try again later rather than record an absence.
/// </remarks>
public class PdCodesApiException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PdCodesApiException"/> class.
    /// </summary>
    /// <param name="statusCode">HTTP status returned by the API.</param>
    /// <param name="requestUrl">The URL that produced it, for the log line.</param>
    /// <param name="body">The response body, truncated by the caller. v5 error bodies
    /// explain what to do, so they are worth surfacing verbatim.</param>
    public PdCodesApiException(HttpStatusCode statusCode, string requestUrl, string? body)
        : base($"PD-Codes API v5 returned {(int)statusCode} for {requestUrl}: {body}")
    {
        StatusCode = statusCode;
        RequestUrl = requestUrl;
        Body = body;
    }

    /// <summary>Gets the HTTP status code.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Gets the request URL.</summary>
    public string RequestUrl { get; }

    /// <summary>Gets the raw response body, if one was readable.</summary>
    public string? Body { get; }
}
