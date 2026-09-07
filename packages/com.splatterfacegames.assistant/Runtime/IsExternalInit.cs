// Polyfill for record/init-only support on the Unity (C# 9 / .NET Standard 2.1)
// compiler, which does not ship System.Runtime.CompilerServices.IsExternalInit.
// Guarded so it is a no-op on .NET 5+ (the service already provides this type).
// NOTE: intentionally lives in Runtime/ root, NOT Runtime/Contracts/, so the
// service's Compile glob (Runtime/Contracts/*.cs) does not also include it.
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
