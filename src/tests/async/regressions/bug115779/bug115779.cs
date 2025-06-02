// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xunit;

public class Program
{
    static string s_null;

    public static int Main()
    {
        try
        {
            M0().GetAwaiter().GetResult();
        }
        catch
        {
            return 100;
        }

        return 12345;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task M0()
    {
	var e = new System.Exception();
        await M1();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async static Task M1()
    {
        await M2();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async static Task<int> M2()
    {
        return s_null.Length;
    }
}
