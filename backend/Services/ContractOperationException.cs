namespace backend.Services;

public sealed class ContractOperationException(int statusCode, string code, string message)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;
}
