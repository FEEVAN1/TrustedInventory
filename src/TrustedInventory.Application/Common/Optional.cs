namespace TrustedInventory.Application.Common;

/// <summary>
/// A merge-patch field (DEC-019): absent from the request, or present with a value that may be null.
/// Lets PATCH distinguish "leave unchanged" from "set to null".
/// </summary>
public readonly record struct Optional<T>
{
    private readonly T _value;

    public Optional(T value)
    {
        _value = value;
        IsSet = true;
    }

    public bool IsSet { get; }

    public T Value => IsSet ? _value : throw new InvalidOperationException("Optional value is not set.");

    public T GetValueOrDefault(T fallback) => IsSet ? _value : fallback;

    public static implicit operator Optional<T>(T value) => new(value);
}
