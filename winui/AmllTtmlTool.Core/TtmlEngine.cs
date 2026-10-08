namespace AmllTtmlTool.Core;

public sealed record TtmlError(
    string Kind,
    string? Message = null,
    long? ByteOffset = null,
    string? LineId = null,
    List<string>? TagStack = null,
    string? CurrentAttribute = null,
    string? OffendingString = null);

public sealed class Result<T>
{
    public bool Success { get; }
    public T? Data { get; }
    public TtmlError? Error { get; }

    private Result(bool success, T? data, TtmlError? error)
    {
        Success = success;
        Data = data;
        Error = error;
    }

    public static Result<T> Ok(T data) => new(true, data, null);
    public static Result<T> Fail(TtmlError error) => new(false, default, error);
}

public sealed record TtmlParseOptions(
    string? TranslationLanguage = null,
    string? RomanizationLanguage = null);

public sealed record TtmlGenerateOptions(
    string? TranslationLanguage = null,
    string? RomanizationLanguage = null);

public sealed record GeneratorConfig(
    bool UseAppleFormatRules = false,
    bool Format = false,
    bool LineTiming = false);
