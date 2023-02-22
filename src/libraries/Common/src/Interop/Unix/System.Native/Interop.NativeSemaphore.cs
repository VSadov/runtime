// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;

internal static partial class Interop
{
    internal static partial class Sys
    {
        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_NativeSemaphore_Create")]
        internal static partial IntPtr NativeSemaphore_Create(int initialCount, int maxCount);

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_NativeSemaphore_Destroy")]
        internal static partial void NativeSemaphore_Destroy(IntPtr semaphore);

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_NativeSemaphore_Wait")]
        internal static partial int NativeSemaphore_Wait(IntPtr semaphore);

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_NativeSemaphore_TimedWait")]
        internal static partial int NativeSemaphore_TimedWait(IntPtr semaphore, int timeoutMilliseconds);

        [LibraryImport(Libraries.SystemNative, EntryPoint = "SystemNative_NativeSemaphore_Release")]
        internal static partial int NativeSemaphore_Release(IntPtr semaphore);
    }
}
