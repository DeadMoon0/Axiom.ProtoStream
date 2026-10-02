namespace ProtoStream.Http;

/// <summary>HTTP status codes (RFC 9110 section 15) as named constants.</summary>
public static class HttpStatus
{
    /// <summary>Smallest valid status code.</summary>
    public const int MinValue = 100;

    /// <summary>Largest valid status code: status codes have three digits.</summary>
    public const int MaxValue = 999;

    /// <summary>100.</summary>
    public const int Continue = 100;

    /// <summary>101.</summary>
    public const int SwitchingProtocols = 101;

    /// <summary>103.</summary>
    public const int EarlyHints = 103;

    /// <summary>200.</summary>
    public const int Ok = 200;

    /// <summary>201.</summary>
    public const int Created = 201;

    /// <summary>202.</summary>
    public const int Accepted = 202;

    /// <summary>204.</summary>
    public const int NoContent = 204;

    /// <summary>206.</summary>
    public const int PartialContent = 206;

    /// <summary>300.</summary>
    public const int MultipleChoices = 300;

    /// <summary>301.</summary>
    public const int MovedPermanently = 301;

    /// <summary>302.</summary>
    public const int Found = 302;

    /// <summary>303.</summary>
    public const int SeeOther = 303;

    /// <summary>304.</summary>
    public const int NotModified = 304;

    /// <summary>307.</summary>
    public const int TemporaryRedirect = 307;

    /// <summary>308.</summary>
    public const int PermanentRedirect = 308;

    /// <summary>400.</summary>
    public const int BadRequest = 400;

    /// <summary>401.</summary>
    public const int Unauthorized = 401;

    /// <summary>403.</summary>
    public const int Forbidden = 403;

    /// <summary>404.</summary>
    public const int NotFound = 404;

    /// <summary>405.</summary>
    public const int MethodNotAllowed = 405;

    /// <summary>407.</summary>
    public const int ProxyAuthenticationRequired = 407;

    /// <summary>408.</summary>
    public const int RequestTimeout = 408;

    /// <summary>409.</summary>
    public const int Conflict = 409;

    /// <summary>411.</summary>
    public const int LengthRequired = 411;

    /// <summary>413.</summary>
    public const int ContentTooLarge = 413;

    /// <summary>414.</summary>
    public const int UriTooLong = 414;

    /// <summary>415.</summary>
    public const int UnsupportedMediaType = 415;

    /// <summary>417.</summary>
    public const int ExpectationFailed = 417;

    /// <summary>426.</summary>
    public const int UpgradeRequired = 426;

    /// <summary>429.</summary>
    public const int TooManyRequests = 429;

    /// <summary>431.</summary>
    public const int RequestHeaderFieldsTooLarge = 431;

    /// <summary>500.</summary>
    public const int InternalServerError = 500;

    /// <summary>501.</summary>
    public const int NotImplemented = 501;

    /// <summary>502.</summary>
    public const int BadGateway = 502;

    /// <summary>503.</summary>
    public const int ServiceUnavailable = 503;

    /// <summary>504.</summary>
    public const int GatewayTimeout = 504;

    /// <summary>505.</summary>
    public const int HttpVersionNotSupported = 505;

    /// <summary>True for 1xx codes.</summary>
    public static bool IsInformational(int status) => status is >= Continue and < Ok;

    /// <summary>True for 2xx codes.</summary>
    public static bool IsSuccess(int status) => status is >= Ok and < MultipleChoices;

    /// <summary>True for codes whose responses never carry content (1xx, 204, 304).</summary>
    public static bool IsBodiless(int status) => IsInformational(status) || status is NoContent or NotModified;

    /// <summary>The standard reason phrase, or an empty string for an unknown code.</summary>
    public static string ReasonPhrase(int status) => status switch
    {
        Continue => "Continue",
        SwitchingProtocols => "Switching Protocols",
        EarlyHints => "Early Hints",
        Ok => "OK",
        Created => "Created",
        Accepted => "Accepted",
        NoContent => "No Content",
        PartialContent => "Partial Content",
        MultipleChoices => "Multiple Choices",
        MovedPermanently => "Moved Permanently",
        Found => "Found",
        SeeOther => "See Other",
        NotModified => "Not Modified",
        TemporaryRedirect => "Temporary Redirect",
        PermanentRedirect => "Permanent Redirect",
        BadRequest => "Bad Request",
        Unauthorized => "Unauthorized",
        Forbidden => "Forbidden",
        NotFound => "Not Found",
        MethodNotAllowed => "Method Not Allowed",
        ProxyAuthenticationRequired => "Proxy Authentication Required",
        RequestTimeout => "Request Timeout",
        Conflict => "Conflict",
        LengthRequired => "Length Required",
        ContentTooLarge => "Content Too Large",
        UriTooLong => "URI Too Long",
        UnsupportedMediaType => "Unsupported Media Type",
        ExpectationFailed => "Expectation Failed",
        UpgradeRequired => "Upgrade Required",
        TooManyRequests => "Too Many Requests",
        RequestHeaderFieldsTooLarge => "Request Header Fields Too Large",
        InternalServerError => "Internal Server Error",
        NotImplemented => "Not Implemented",
        BadGateway => "Bad Gateway",
        ServiceUnavailable => "Service Unavailable",
        GatewayTimeout => "Gateway Timeout",
        HttpVersionNotSupported => "HTTP Version Not Supported",
        _ => "",
    };
}
