// This is required to quiet down a compilation error, for some reason
// https://stackoverflow.com/a/64749403

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace System.Runtime.CompilerServices
#pragma warning restore IDE0130 // Namespace does not match folder structure
{
    internal static class IsExternalInit { }
}