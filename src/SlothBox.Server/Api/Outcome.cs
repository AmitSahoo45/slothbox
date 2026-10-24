namespace SlothBox.Server.Api;

// How a page's request ended: done, refused (400), unknown id (404) or not possible now (409).
public abstract record Outcome<T>
{
    public sealed record Done(T Value) : Outcome<T>;
    public sealed record Refused(string Reason) : Outcome<T>;
    public sealed record NotFound : Outcome<T>;
    public sealed record Conflict(string Reason) : Outcome<T>;

    // The API's answer: the value, or a problem whose title is the sentence the page shows (spec 8.1).
    public IResult ToResult(string notFound) => this switch
    {
        Done done => Results.Ok(done.Value),
        Refused refused => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: refused.Reason),
        Conflict conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: conflict.Reason),
        _ => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: notFound)
    };
}
