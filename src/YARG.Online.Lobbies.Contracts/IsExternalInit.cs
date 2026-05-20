// Shim required so record `init` accessors compile against netstandard2.1.
// The BCL type was added in .NET 5; declaring it here lets the compiler emit
// init-only setters on older target frameworks.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}
