// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

internal static partial class Interop
{
    internal static partial class Sys
    {
        [SuppressGCTransition]
        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_SuppressCurrentThreadWakePreemption")]
        internal static partial int SuppressCurrentThreadWakePreemption([MarshalAs(UnmanagedType.Bool)] bool suppress);
    }
}
