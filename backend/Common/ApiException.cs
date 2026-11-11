namespace UserManagement.Api.Common;

// Carry only safe client-facing domain failures; never database or credential details.
public sealed class ApiException(int status, string code, string detail) : Exception(detail)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
