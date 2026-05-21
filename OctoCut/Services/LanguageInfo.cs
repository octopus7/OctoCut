namespace OctoCut.Services;

public sealed class LanguageInfo
{
    public required string Code { get; init; }

    public required string NativeName { get; init; }

    public required string FilePath { get; init; }

    public override string ToString() => NativeName;
}
