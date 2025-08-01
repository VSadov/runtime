// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace System.Collections
{
    /// <summary>
    /// Designed to support hashtables which require case-insensitive behavior while still maintaining case,
    /// this provides an efficient mechanism for getting the hashcode of the string ignoring case.
    /// </summary>
    [Obsolete("CaseInsensitiveHashCodeProvider has been deprecated. Use StringComparer instead.")]
    public class CaseInsensitiveHashCodeProvider : IHashCodeProvider
    {
        private readonly CompareInfo _compareInfo;

        public CaseInsensitiveHashCodeProvider()
        {
            _compareInfo = CultureInfo.CurrentCulture.CompareInfo;
        }

        public CaseInsensitiveHashCodeProvider(CultureInfo culture)
        {
            ArgumentNullException.ThrowIfNull(culture);

            _compareInfo = culture.CompareInfo;
        }

        public static CaseInsensitiveHashCodeProvider Default => new CaseInsensitiveHashCodeProvider();

        public static CaseInsensitiveHashCodeProvider DefaultInvariant => field ??= new CaseInsensitiveHashCodeProvider(CultureInfo.InvariantCulture);

        public int GetHashCode(object obj)
        {
            ArgumentNullException.ThrowIfNull(obj);

            string? s = obj as string;
            return s != null ?
                _compareInfo.GetHashCode(s, CompareOptions.IgnoreCase) :
                obj.GetHashCode();
        }
    }
}
