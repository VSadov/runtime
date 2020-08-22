// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;

namespace System.Tests
{
    public static class CastCacheTests
    {
        private const int BucketSize = 8;

        // [OuterLoop] // may run for a few seconds.
        // the test targets internal implementation detail of CoreCLR
        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsNotMonoRuntime))]
        static void Test()
        {
            const int DefaultReadbackCount = 1;
            const int DefaultCastFailureThreadCount = 4;
            const int DefaultCastSuccessThreadCount = 4;
            const int NumberOfTableDoublingEventsToForce = 15;

            ICastFailureDriver[] castFailureDriverSet;
            ICastSuccessDriver[] castSuccessDriverSet;
            int castFailureThreadCount;
            int castSuccessThreadCount;
            int phase;
            int readbackCount;
            ReproConfig reproConfig;
            List<Thread> threadList;

            readbackCount = DefaultReadbackCount;
            castFailureThreadCount = DefaultCastFailureThreadCount;
            castSuccessThreadCount = DefaultCastSuccessThreadCount;

            Util.Log(
                "\r\n" +
                "Settings:\r\n" +
                "    ReadbackCount: {0}\r\n" +
                "    CastFailureThreadCount: {1}\r\n" +
                "    CastSuccessThreadCount: {1}\r\n" +
                "\r\n",

                readbackCount,
                castFailureThreadCount,
                castSuccessThreadCount
            );


            //
            // Build enough hash-conflicting instantiations for each thread to operate on a set of 8
            // (i.e., a set large enough to fill the associated hash bucket on its own).
            //

            reproConfig = Helpers.BuildReproConfig(
                castTargetType: typeof(CastTarget),
                openType1: typeof(CastFailureDriver<>),
                exactRequiredCount1: (BucketSize * castFailureThreadCount),
                openType2: typeof(CastSuccessDriver<>),
                exactRequiredCount2: (BucketSize * castSuccessThreadCount)
            );

            castFailureDriverSet = reproConfig.InstanceSet1.Cast<ICastFailureDriver>().ToArray();
            castSuccessDriverSet = reproConfig.InstanceSet2.Cast<ICastSuccessDriver>().ToArray();

            Util.Log(
                "SharedHashCode: 0x{0:x8} ({1}, {2})\r\n",
                reproConfig.SharedHashCode,
                castFailureDriverSet.Length,
                castSuccessDriverSet.Length
            );

            //
            // The number of cast success drivers exceeds the BUCKET_SIZE.  As a result, having each
            // one insert an entry will overflow the associated cast cache bucket.  Each time the
            // bucket overflows, the CastCache::TrySet will double the table size.  Force this to occur
            // (many) more than enough times to ensure that the table reaches the maximum size (i.e.,
            // 0x1000 entries) that is assumed by the remainder of the program.
            //

            for (phase = 0; phase < NumberOfTableDoublingEventsToForce; phase++)
            {
                foreach (ICastSuccessDriver castSuccessDriver in castSuccessDriverSet)
                {
                    castSuccessDriver.RunOneSuccessfulCast();
                }
            }

            //
            // Run all of the success and failure drivers in parallel, with each thread operating on a
            // set of 8 (i.e., a set large enough to fill the associated hash bucket on its own).
            //

            threadList = new List<Thread>();

            threadList.AddRange(
              EnumerateInChunks(
                  castFailureDriverSet
                    .Cast<IGenericCastDriver>()
                    .ToArray(),
                  chunkSize: BucketSize)
                .Select(
                    driverGroup => new Thread(
                        () => RunOneCastDriverThread(
                            readbackCount: readbackCount,
                            driverGroup: driverGroup
                        )
                    )
                )
            );

            threadList.AddRange(
              EnumerateInChunks(
                  castSuccessDriverSet
                    .Cast<IGenericCastDriver>()
                    .ToArray(),
                  chunkSize: BucketSize)
                .Select(
                    driverGroup => new Thread(
                        () => RunOneCastDriverThread(
                            readbackCount: readbackCount,
                            driverGroup: driverGroup
                        )
                    )
                )
            );

            Util.Log(
                "Starting threads: TotalCount={0}, CastFailureThreadCount={1}, CastSuccessThreadCount={2}\r\n",
                threadList.Count,
                (castFailureDriverSet.Length / BucketSize),
                (castSuccessDriverSet.Length / BucketSize)
            );

            foreach (Thread thread in threadList)
            {
                thread.Start();
            }

            foreach (Thread thread in threadList)
            {
                thread.Join();
            }

            return;
        }

        public static partial class Util
        {
            public static Exception Fail(string format, params object[] args)
            {
                Console.Write("FAIL! {0}", String.Format(format, args));

                Console.Write(
                    "\r\n" +
                    "StackTrace ----\r\n" +
                    "{0}\r\n",
                    Environment.StackTrace
                );

                Environment.Exit(101);
                throw new Exception("Not reached.");
            }


            public static void Log(string format, params object[] args)
            {
                // Console.Write("{0}", String.Format(format, args));
                return;
            }


            public static int ParseAndValidateInt32(string text, int smallestAllowedValue, int largestAllowedValue, string caption)
            {
                int value;

                try
                {
                    value = Convert.ToInt32(text);

                    if ((value >= smallestAllowedValue) && (value <= largestAllowedValue))
                    {
                        return value;
                    }
                    else
                    {
                        throw Util.Fail(
                            "Error: `{0}' value is not in the allowed [{1},{2}] range: {3}\r\n",
                            caption,
                            smallestAllowedValue,
                            largestAllowedValue,
                            text
                        );
                    }
                }
                catch (Exception)
                {
                    throw Util.Fail(
                        "Error: Failed to parse the `{0}' value as an integer: {1}\r\n",
                        caption,
                        text
                    );
                }
            }


            public static readonly IEqualityComparer<object> ObjectrefComparer = new ObjectrefComparerImpl();


            private sealed class ObjectrefComparerImpl : IEqualityComparer<object>
            {
                int IEqualityComparer<object>.GetHashCode(object obj)
                {
                    return obj.GetHashCode();
                }


                bool IEqualityComparer<object>.Equals(object obj1, object obj2)
                {
                    return Object.ReferenceEquals(obj1, obj2);
                }
            }
        }

        private static IEnumerable<T[]> EnumerateInChunks<T>(T[] array, int chunkSize)
        {
            T[] chunk;
            int nextSize;
            int remaining;
            int sourceIndex;

            sourceIndex = 0;
            remaining = array.Length;

            while (remaining > 0)
            {
                nextSize = ((remaining < chunkSize) ? remaining : chunkSize);
                chunk = new T[nextSize];

                Array.Copy(
                    sourceArray: array,
                    sourceIndex: sourceIndex,
                    destinationArray: chunk,
                    destinationIndex: 0,
                    length: nextSize
                );

                yield return chunk;
                sourceIndex += nextSize;
                remaining -= nextSize;
            }

            yield break;
        }

        public class TG00 { public int F1; public TG00(int f1) { this.F1 = f1; return; } }
        public class TG01 { public int F1; public TG01(int f1) { this.F1 = f1; return; } }
        public class TG02 { public int F1; public TG02(int f1) { this.F1 = f1; return; } }
        public class TG03 { public int F1; public TG03(int f1) { this.F1 = f1; return; } }
        public class TG04 { public int F1; public TG04(int f1) { this.F1 = f1; return; } }
        public class TG05 { public int F1; public TG05(int f1) { this.F1 = f1; return; } }
        public class TG06 { public int F1; public TG06(int f1) { this.F1 = f1; return; } }
        public class TG07 { public int F1; public TG07(int f1) { this.F1 = f1; return; } }
        public class TG08 { public int F1; public TG08(int f1) { this.F1 = f1; return; } }
        public class TG09 { public int F1; public TG09(int f1) { this.F1 = f1; return; } }
        public class TG10 { public int F1; public TG10(int f1) { this.F1 = f1; return; } }
        public class TG11 { public int F1; public TG11(int f1) { this.F1 = f1; return; } }
        public class TG12 { public int F1; public TG12(int f1) { this.F1 = f1; return; } }
        public class TG13 { public int F1; public TG13(int f1) { this.F1 = f1; return; } }
        public class TG14 { public int F1; public TG14(int f1) { this.F1 = f1; return; } }
        public class TG15 { public int F1; public TG15(int f1) { this.F1 = f1; return; } }


        public class TGBase<T1, T2, T3, T4, T5>
        {
            public T1 F1;
            public T2 F2;
            public T3 F3;
            public T4 F4;
            public T5 F5;

            public TGBase(T1 f1, T2 f2, T3 f3, T4 f4, T5 f5)
            {
                this.F1 = f1;
                this.F2 = f2;
                this.F3 = f3;
                this.F4 = f4;
                this.F5 = f5;
                return;
            }
        }


        public sealed partial class TypeGenerator
        {
            private static readonly Type[] leafClassSet;
            private static readonly Type openBaseClassType;
            private static readonly int baseClassArity;
            private static readonly int maxSubtypesPerOpenType;
            private static readonly Dictionary<Type, TypeGenerator> generatorsByOpenType;


            static TypeGenerator()
            {
                int baseArity;
                int index;
                int leafCount;
                Type[] leafSet;
                int maxSubtypes;
                Type openBaseClass;


                //
                // Bind to the core set of leaf class types that are composed to form the TGBase
                // instantiations used to generate a large number of unique subtypes.
                //

                leafSet = new Type[16];
                leafSet[0] = typeof(TG00);
                leafSet[1] = typeof(TG01);
                leafSet[2] = typeof(TG02);
                leafSet[3] = typeof(TG03);
                leafSet[4] = typeof(TG04);
                leafSet[5] = typeof(TG05);
                leafSet[6] = typeof(TG06);
                leafSet[7] = typeof(TG07);
                leafSet[8] = typeof(TG08);
                leafSet[9] = typeof(TG09);
                leafSet[10] = typeof(TG10);
                leafSet[11] = typeof(TG11);
                leafSet[12] = typeof(TG12);
                leafSet[13] = typeof(TG13);
                leafSet[14] = typeof(TG14);
                leafSet[15] = typeof(TG15);


                //
                // Bind to the TGBase type which will be instantiated over combinations of the leaf classes
                // defined above.
                //

                openBaseClass = typeof(TGBase<,,,,>);
                baseArity = openBaseClass.GetGenericArguments().Length;


                //
                // Commit the configuration implied by the fundamental leaf class and base class
                // information loaded above.
                //

                leafCount = leafSet.Length;
                maxSubtypes = 1;

                for (index = 0; index < baseArity; index++)
                {
                    maxSubtypes = checked(maxSubtypes * leafCount);
                }

                TypeGenerator.leafClassSet = leafSet;
                TypeGenerator.openBaseClassType = openBaseClass;
                TypeGenerator.baseClassArity = baseArity;
                TypeGenerator.maxSubtypesPerOpenType = maxSubtypes;
                TypeGenerator.generatorsByOpenType = new Dictionary<Type, TypeGenerator>(Util.ObjectrefComparer);
                return;
            }


            public static TypeGenerator GetOrCreateGeneratorForOpenType(Type openType)
            {
                int arity;
                TypeGenerator generator;
                Dictionary<Type, TypeGenerator> map;
                object testInstance;


                //
                // Take a fast path if a generator already exists for the supplied type (implying that the
                // type passed all of the consistency checks during an earlier call to this function).
                //

                map = TypeGenerator.generatorsByOpenType;

                if (map.TryGetValue(openType, out generator))
                {
                    return generator;
                }


                //
                // No existing generator matches the supplied type.  Verify that the type is an arity-one
                // open generic type which can be activated via Activator.CreateInstance, build a new
                // generator instance for it, add the generator to the global map, and return the new
                // generator to the caller.
                //

                if (!openType.IsGenericTypeDefinition)
                {
                    throw Util.Fail("Error: Type does not appear to be an open generic type: {0}\r\n", openType.ToString());
                }

                arity = openType.GetGenericArguments().Length;

                if (arity != 1)
                {
                    throw Util.Fail("Error: Arity of the open type must be one: ({0}) {1}\r\n", arity, openType.ToString());
                }

                try
                {
                    testInstance = Activator.CreateInstance(
                        openType.MakeGenericType(
                            typeof(TestClass)
                        )
                    );
                }
                catch (Exception e)
                {
                    throw Util.Fail(
                        "Error: Activating a test instantiation generated an exception:\r\n" +
                        "\r\n" +
                        "OpenType:  {0}\r\n" +
                        "Exception: ({1}) {2}\r\n" +
                        "\r\n",

                        openType.ToString(),
                        e.GetType().ToString(),
                        e.Message
                    );
                }

                generator = new TypeGenerator(openType);
                map.Add(openType, generator);
                return generator;
            }


            private class TestClass
            {
                public int F1;
                public TestClass(int f1) { this.F1 = f1; return; }
            }


            private static Type GetBaseClassInstantiation(int subtypeIndex)
            {
                int arity;
                int index;
                Type[] instantiation;
                int leafCount;
                int leafIndex;
                Type[] leafSet;
                int remainingIndex;

                if (subtypeIndex >= TypeGenerator.maxSubtypesPerOpenType)
                {
                    throw Util.Fail("Internal error: Out-of-range indices should be detected and rejected by the caller.\r\n");
                }

                leafSet = TypeGenerator.leafClassSet;
                leafCount = leafSet.Length;
                arity = TypeGenerator.baseClassArity;
                remainingIndex = subtypeIndex;
                instantiation = new Type[arity];

                for (index = (arity - 1); index >= 0; index--)
                {
                    remainingIndex = Math.DivRem(remainingIndex, leafCount, out leafIndex);
                    instantiation[index] = leafSet[leafIndex];
                }

                if (remainingIndex != 0)
                {
                    throw Util.Fail("Internal error: Logic breakdown allowed leftover index bits even after consuming all leaf indices.\r\n");
                }

                return TypeGenerator.openBaseClassType.MakeGenericType(instantiation);
            }
        }


        public sealed partial class TypeGenerator
        {
            public readonly Type OpenType;
            private int nextSubtypeIndex = 0;


            private TypeGenerator(Type openType)
            {
                this.OpenType = openType;
                return;
            }


            public object BuildInstanceOfNextInstantiation()
            {
                Type baseClass;
                int subtypeIndex;


                //
                // Acquire the index of the next subtype to build.
                //

                subtypeIndex = this.nextSubtypeIndex;
                this.nextSubtypeIndex = (subtypeIndex + 1);

                if (subtypeIndex >= TypeGenerator.maxSubtypesPerOpenType)
                {
                    throw Util.Fail(
                        "Error: All available subtypes of the current open type have been exhausted.\r\n" +
                        "\r\n" +
                        "TypeIndex: 0x{0:x8}\r\n" +
                        "OpenType:  {1}\r\n" +
                        "\r\n",

                        subtypeIndex,
                        this.OpenType.ToString()
                    );
                }


                //
                // Acquire the base class instantiation associated with the next subtype index, then
                // instantiate the externally-supplied open type over this instantiation.
                //

                baseClass = TypeGenerator.GetBaseClassInstantiation(subtypeIndex);

                return Activator.CreateInstance(
                    this.OpenType.MakeGenericType(
                        baseClass
                    )
                );
            }
        }


        public sealed class ReproConfig
        {
            public readonly Type CastTargetType;
            public readonly int SharedHashCode;
            public readonly object[] InstanceSet1;
            public readonly object[] InstanceSet2;


            public ReproConfig(Type castTargetType, int sharedHashCode, object[] instanceSet1, object[] instanceSet2)
            {
                this.CastTargetType = castTargetType;
                this.SharedHashCode = sharedHashCode;
                this.InstanceSet1 = instanceSet1;
                this.InstanceSet2 = instanceSet2;
                return;
            }
        }


        public static class Helpers
        {
            private const int BucketCount = 0x1000;


            public static
            ReproConfig
            BuildReproConfig(
                Type castTargetType,
                Type openType1,
                int exactRequiredCount1,
                Type openType2,
                int exactRequiredCount2
                )
            {
                int index;
                object[] instanceSet1;
                object[] instanceSet2;
                List<object>[] listsByCode1;
                List<object>[] listsByCode2;


                //
                // Build (more than) enough instantiations to ensure that at least one associated hash code
                // will match at least the required number of hash-conflicting instantiations requested by
                // the caller.
                //

                Util.Log(
                    "Building lots of instantiations to create hash conflicts (total number is roughly {0:N0})...\r\n",
                    checked(BucketCount * (exactRequiredCount1 + exactRequiredCount2))
                );

                instanceSet1 = Helpers.BuildDistinctInstantiations(
                    typesToBuild: checked(exactRequiredCount1 * BucketCount),
                    openType: openType1
                );

                instanceSet2 = Helpers.BuildDistinctInstantiations(
                    typesToBuild: checked(exactRequiredCount2 * BucketCount),
                    openType: openType2
                );


                //
                // Group the instances by the cast cache bucket that will be used to record any casts from
                // the instance type over to the supplied cast target type.
                //

                listsByCode1 = Helpers.BuildOneInstanceListForEachPossibleHashCode(
                    instanceSet: instanceSet1,
                    castTargetType: castTargetType
                );

                listsByCode2 = Helpers.BuildOneInstanceListForEachPossibleHashCode(
                    instanceSet: instanceSet2,
                    castTargetType: castTargetType
                );


                //
                // Find the first cast cache bucket that matches at least the required number of instances
                // instantiated from each of the supplied open types (but skip hash code zero just so
                // program output makes it clearer that the search really did succeed as opposed to just
                // defaulting to zero).
                //

                for (index = 2; index < BucketCount; index++)
                {
                    if ((listsByCode1[index].Count >= exactRequiredCount1) &&
                        (listsByCode2[index].Count >= exactRequiredCount2))
                    {
                        //
                        // This bucket contains enough instantiations to meet the caller's needs, and can easily
                        // contain more instantiations than the caller requested.  Trim the returned set down to
                        // the exact caller-requeste size.
                        //

                        return new ReproConfig(
                            castTargetType: castTargetType,
                            sharedHashCode: index,
                            instanceSet1: listsByCode1[index].Take(exactRequiredCount1).ToArray(),
                            instanceSet2: listsByCode2[index].Take(exactRequiredCount2).ToArray()
                        );
                    }
                }

                throw Util.Fail("Internal error: The instance set size should always guarantee that enough hash conflicts occur.\r\n");
            }


            private static
            List<object>[]
            BuildOneInstanceListForEachPossibleHashCode(
                object[] instanceSet,
                Type castTargetType
                )
            {
                int index;
                int hashCode;
                List<object>[] listsByCode;

                listsByCode = new List<object>[BucketCount];

                for (index = 0; index < listsByCode.Length; index++)
                {
                    listsByCode[index] = new List<object>();
                }

                foreach (object instance in instanceSet)
                {
                    if (IntPtr.Size == 8)
                    {
                        hashCode = Helpers.GetHashCodeForUnderlyingMethodTable64(
                            instance: instance,
                            eventualCastTargetType: castTargetType
                        );
                    }
                    else
                    {
                        hashCode = Helpers.GetHashCodeForUnderlyingMethodTable32(
                            instance: instance,
                            eventualCastTargetType: castTargetType
                        );
                    }

                    if ((hashCode < 0) || (hashCode >= listsByCode.Length))
                    {
                        throw Util.Fail("Internal error: Helper returned an out-of-range hash code.\r\n");
                    }

                    listsByCode[hashCode].Add(instance);
                }

                return listsByCode;
            }


            private static
            int
            GetHashCodeForUnderlyingMethodTable64(
                object instance,
                Type eventualCastTargetType
                )
            {
                UInt64 finalHash;
                UInt64 intermediateHash;
                UInt64 sourceMt;
                UInt64 targetMt;

                //
                // This function computes the hash code relative to the maximally-sized 0x1000 entry table.
                // Indexes into the table are 12 bits wide.  Since indexes are extracted from the top 12
                // bits of the intermediate 64-bit value generated by combining the MethodTable addresses,
                // this implies that a 52 bit shift will extract the relevant high bits.
                //
                // Note that hash codes in smaller tables are computed by extracting fewer bits from the
                // top of the same intermediate 64-bit value.  As a result, MethodTable pairs that have the
                // same hash code in the 0x1000 entry table will also have the same hash code in all
                // smaller tables.
                //

                sourceMt = (UInt64)instance.GetType().TypeHandle.Value.ToInt64();
                targetMt = (UInt64)eventualCastTargetType.TypeHandle.Value.ToInt64();
                intermediateHash = (((sourceMt << 32) | (sourceMt >> 32)) ^ targetMt);
                finalHash = ((intermediateHash * 11400714819323198485UL) >> 52);
                return checked((int)finalHash);
            }

            private static
            int
            GetHashCodeForUnderlyingMethodTable32(
                object instance,
                Type eventualCastTargetType
            )
            {
                UInt32 finalHash;
                UInt32 intermediateHash;
                UInt32 sourceMt;
                UInt32 targetMt;

                //
                // This function computes the hash code relative to the maximally-sized 0x1000 entry table.
                // Indexes into the table are 12 bits wide.  Since indexes are extracted from the top 12
                // bits of the intermediate 64-bit value generated by combining the MethodTable addresses,
                // this implies that a 52 bit shift will extract the relevant high bits.
                //
                // Note that hash codes in smaller tables are computed by extracting fewer bits from the
                // top of the same intermediate 64-bit value.  As a result, MethodTable pairs that have the
                // same hash code in the 0x1000 entry table will also have the same hash code in all
                // smaller tables.
                //

                sourceMt = (uint)instance.GetType().TypeHandle.Value.ToInt32();
                targetMt = (uint)eventualCastTargetType.TypeHandle.Value.ToInt32();
                intermediateHash = (((sourceMt << 16) | (sourceMt >> 16)) ^ targetMt);
                finalHash = ((intermediateHash * 2654435769U) >> 20);
                return checked((int)finalHash);
            }

            private static
            object[]
            BuildDistinctInstantiations(
                int typesToBuild,
                Type openType
                )
            {
                TypeGenerator generator;
                int index;
                object[] instanceSet;

                generator = TypeGenerator.GetOrCreateGeneratorForOpenType(openType);
                instanceSet = new object[typesToBuild];

                for (index = 0; index < typesToBuild; index++)
                {
                    instanceSet[index] = generator.BuildInstanceOfNextInstantiation();
                }

                return instanceSet;
            }
        }


        //
        // N.B. IBase<CastTarget> is the only instantiation used during the repro sequence.
        //

        public interface IBase<T>
        {
        }


        public abstract class CastTarget
        {
        }


        //
        // N.B. The casts in this class are done against the raw type argument (as opposed to being
        //      done against CastTarget) to ensure that the IsInstanceOfAny helper is used instead of
        //      IsInstanceOfClass.  For the purposes of this repro, IsInstanceOfClass and
        //      IsInstanceOfInterface do not work since they cosult the parent chain and interface map
        //      content directly in managed code and therefore generally bypass the cast cache
        //      completely.
        //

        public static class CastSite
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void RunOneFailedCast<T>(IBase<T> input)
            {
                if (input is T)
                {
                    throw Util.Fail($"Error: Known-unsuccessful cast spuriously succeeded: { typeof(T).ToString()}");
                }

                return;
            }


            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void RunOneSuccessfulCast<T>(IBase<T> input)
            {
                if (input is T)
                {
                    return;
                }

                throw Util.Fail($"Error: Known-successful cast spuriously failed: { typeof(T).ToString()}");
            }
        }


        public interface IGenericCastDriver
        {
            void RunOneCastCacheOperation();
        }


        public interface ICastFailureDriver
        {
            void RunOneFailedCast();
        }


        //
        // This class implements IBase<CastTarget> but does not inherit from CastTarget, ensuring
        // that the casts in the CastSite class are guaranteed to fail.
        //
        // N.B. The repro sequence uses the TypeGenerator to create a large number of distinct
        //      instantiations of this class.
        //

        public sealed class CastFailureDriver<T> : IBase<CastTarget>, ICastFailureDriver, IGenericCastDriver
        {
            void ICastFailureDriver.RunOneFailedCast()
            {
                this.DispatchOneFailedCast();
                return;
            }


            void IGenericCastDriver.RunOneCastCacheOperation()
            {
                this.DispatchOneFailedCast();
                return;
            }


            private void DispatchOneFailedCast()
            {
                CastSite.RunOneFailedCast<CastTarget>(this);
                return;
            }
        }


        public interface ICastSuccessDriver
        {
            void RunOneSuccessfulCast();
        }


        //
        // This class implements IBase<CastTarget> and also inherits from CastTarget, ensuring that
        // the casts in the CastSite class are guaranteed to succeed.
        //
        // N.B. The repro sequence uses the TypeGenerator to create a large number of distinct
        //      instantiations of this class.
        //

        public sealed class CastSuccessDriver<T> : CastTarget, IBase<CastTarget>, ICastSuccessDriver, IGenericCastDriver
        {
            void ICastSuccessDriver.RunOneSuccessfulCast()
            {
                this.DispatchOneSuccessfulCast();
                return;
            }


            void IGenericCastDriver.RunOneCastCacheOperation()
            {
                this.DispatchOneSuccessfulCast();
                return;
            }


            private void DispatchOneSuccessfulCast()
            {
                CastSite.RunOneSuccessfulCast<CastTarget>(this);
                return;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunOneCastDriverThread(int readbackCount, IGenericCastDriver[] driverGroup)
        {
            int index;

            if (driverGroup.Length != BucketSize)
            {
                throw Util.Fail("Internal error: Cast failure thread is attached to a partial gruop.\r\n");
            }

            //            while (true)
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 500)
            {
                foreach (IGenericCastDriver driver in driverGroup)
                {
                    driver.RunOneCastCacheOperation();

                    //
                    // Because this thread goes through a full bucket worth of driver types in round-robin
                    // fashion, it is guaranteed that the call above did not find a match in the cast cache and
                    // therefore called through to coreclr!CastCache::TrySet to record the cast result.
                    //
                    // In the overall repro, the "source" type of each cast varies widely (since every driver
                    // instance has its own distinct type), but the "target" type is always the same.
                    //
                    // The problem targeted by this repro app relates to cases where, under write-contention,
                    // cast cache entries sometimes end up in a "corrupt" form where the final entry content
                    // lists "source" and "target" types that match the operation above, but encodes the wrong
                    // cast result (i.e., encodes the cast result from the coreclr!CastCache::TrySet call
                    // generated by a racing cast operation that had the same "target" but had a different
                    // "source" and therefore had a cast success/failure was opposite to the one above.).
                    //
                    // Since the current "source" is unique to this driver instance (which is in turn unique to
                    // this worker thread), operations on this driver instance are the only ones which will
                    // carry a matching "source".  All other operations will ignore the corrupt entry (due to
                    // the "source" mismatch), meaning operations on this driver instance are the only ones
                    // that will potentially "observe" the corruption and therefore turn it into an observable.
                    // error.
                    //
                    // The readback loop below is used to generate a configurable number of "extra" operations
                    // against this driver instance.  When a corrupt entry has been generated, carrying out
                    // these operations increases the chance of observing a "noisy" error before other activity
                    // across the program pushes the corrupt entry out of the cache.
                    //

                    for (index = 0; index < readbackCount; index++)
                    {
                        driver.RunOneCastCacheOperation();
                    }
                }
            }
        }
    }
}
