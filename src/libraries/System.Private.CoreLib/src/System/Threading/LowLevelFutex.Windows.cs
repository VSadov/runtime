// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;

internal static unsafe class LowLevelFutex
{
    internal static void WaitOnAddress(int* address, int comparand)
    {
        Interop.Kernel32.WaitOnAddress(address, &comparand, sizeof(int), -1);
    }

    internal static bool WaitOnAddressTimeout(int* address, int comparand, int milliseconds)
    {
        return Interop.Kernel32.WaitOnAddress(address, &comparand, sizeof(int), milliseconds) == Interop.BOOL.TRUE;
    }

    internal static void WakeByAddressSingle(int* address)
    {
        Interop.Kernel32.WakeByAddressSingle(address);
    }
}
