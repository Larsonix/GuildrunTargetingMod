using System;

namespace System.Runtime.CompilerServices;

// The project compiles against the installed net6 runtime rather than an SDK reference pack, so
// these two marker types are not on the reference path. Roslyn still needs them to emit unmanaged
// constraints and init-only properties.
[AttributeUsage(AttributeTargets.All, Inherited = false)]
internal sealed class IsUnmanagedAttribute : Attribute { }

internal static class IsExternalInit { }
