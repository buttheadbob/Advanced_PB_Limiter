// The compiler needs this type for records and init accessors. .NET has it, .NET Framework does not.
#if !NETCOREAPP
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
#endif
