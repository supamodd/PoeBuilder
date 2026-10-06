namespace PoeBuilder.Core.Filters;

/// <summary>Thrown when a filter document violates the game's syntax rules.</summary>
public sealed class FilterFormatException : Exception
{
    public FilterFormatException() { }
    public FilterFormatException(string message) : base(message) { }
    public FilterFormatException(string message, Exception innerException) : base(message, innerException) { }
}