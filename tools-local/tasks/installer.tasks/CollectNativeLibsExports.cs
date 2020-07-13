// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

public class CollectNativeLibsExports : Task
{
    [Required]
    public ITaskItem[] Assemblies { get; set; }

    [Required]
    public string OutputPath { get; set; }

    public override bool Execute()
    {
        CollectExports(Assemblies!.Select(item => item.ItemSpec).ToArray());
        return true;
    }

    public void CollectExports(string[] assemblies)
    {
        var pinvokes = new List<string>();

        var resolver = new System.Reflection.PathAssemblyResolver(assemblies);
        var mlc = new MetadataLoadContext(resolver, "System.Private.CoreLib");

        foreach (var aName in assemblies)
        {
            var a = mlc.LoadFromAssemblyPath(aName);

            foreach (var type in a.GetTypes())
            {
                CollectPInvokes(pinvokes, type);
            }
        }

        Log.LogMessage(MessageImportance.Normal, $"Generating pinvoke table to '{OutputPath}'.");

        System.Console.WriteLine($"Generating pinvoke table to '{OutputPath}'.");

        using (var w = File.CreateText(OutputPath))
        {
            w.WriteLine("; the following two exports are needed for FreeBSD where overwise they will be implicitly hidden and cause linker issues.");
            w.WriteLine("__progname");
            w.WriteLine("environ");
            w.WriteLine();
            w.WriteLine();

            pinvokes.Sort();

            foreach (var e in pinvokes)
            {
                w.WriteLine(e);
                System.Console.WriteLine(e);
            }
        }
    }

    private void CollectPInvokes(List<string> pinvokes, Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
        {
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            {
                var dllimport = method.CustomAttributes.First(attr => attr.AttributeType.Name == "DllImportAttribute");
                var entrypoint = (string)dllimport.NamedArguments.First(arg => arg.MemberName == "EntryPoint").TypedValue.Value!;

                if (entrypoint.StartsWith("SystemNative_")
                    || entrypoint.StartsWith("CompressionNative_")
                    || entrypoint.StartsWith("CryptoNative_")
                    || entrypoint.StartsWith("Brotli")
                    )

                    pinvokes.Add(entrypoint);
            }
        }
    }
}
