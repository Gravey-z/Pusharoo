namespace Pusharoo.EventRelay.Models;

public enum SubscriptionManagementFailure
{
    Invalid,
    NotFound,
    Conflict,
    Gone,
}

public sealed record SubscriptionManagementResult<T>(
    T? Value,
    SubscriptionManagementFailure? Failure = null,
    string? Error = null
)
{
    public bool IsSuccess => Failure is null;

    public static SubscriptionManagementResult<T> Success(T? value) => new(value);

    public static SubscriptionManagementResult<T> Invalid(string error) =>
        new(default, SubscriptionManagementFailure.Invalid, error);

    public static SubscriptionManagementResult<T> NotFound() =>
        new(default, SubscriptionManagementFailure.NotFound);

    public static SubscriptionManagementResult<T> Conflict(string error) =>
        new(default, SubscriptionManagementFailure.Conflict, error);

    public static SubscriptionManagementResult<T> Gone(string error) =>
        new(default, SubscriptionManagementFailure.Gone, error);
}
