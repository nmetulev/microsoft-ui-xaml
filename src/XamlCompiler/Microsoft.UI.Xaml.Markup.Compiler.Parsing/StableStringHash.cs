// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Microsoft.UI.Xaml.Markup.Compiler
{
    /// <summary>
    /// A string hash that is the same in every process, used where a hash becomes part of a
    /// generated name (for example x:Bind function bindings).
    ///
    /// string.GetHashCode() isn't suitable: .NET (Core) randomizes it per process, and .NET Framework
    /// gives different values in 32-bit and 64-bit processes. So the same XAML produced different
    /// generated code depending on which compiler host ran it, and on every run of the .NET task.
    ///
    /// This is the .NET Framework 64-bit string hash, so generated names stay the same as those
    /// produced by the 64-bit .NET Framework compiler (Visual Studio's in-process build).
    /// </summary>
    internal static class StableStringHash
    {
        public static uint Compute(string value)
        {
            unchecked
            {
                int hash1 = 5381;
                int hash2 = hash1;
                for (int i = 0; i < value.Length; i += 2)
                {
                    hash1 = ((hash1 << 5) + hash1) ^ value[i];
                    if (i + 1 >= value.Length)
                    {
                        break;
                    }
                    hash2 = ((hash2 << 5) + hash2) ^ value[i + 1];
                }
                return (uint)(hash1 + (hash2 * 1566083941));
            }
        }
    }
}
