// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Extensions.Logging;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.core
{
    /// <summary>
    /// Empty type
    /// </summary>
    public readonly struct Empty
    {
        /// <summary>
        /// Default
        /// </summary>
        public static readonly Empty Default = default;
    }

    /// <summary>
    /// FASTER utility functions
    /// </summary>
    public static class Utility
    {
        /// <summary>
        /// Get size of type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <returns></returns>
        internal static unsafe int GetSize<T>(this T value)
        {
            T[] arr = new T[2];
            return (int)((long)Unsafe.AsPointer(ref arr[1]) - (long)Unsafe.AsPointer(ref arr[0]));
        }

        internal static bool IsBlittableType(Type t)
        {
            var mi = typeof(Utility).GetMethod("IsBlittable", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.InvokeMethod);
            var fooRef = mi.MakeGenericMethod(t);
            return (bool)fooRef.Invoke(null, null);
        }

        /// <summary>
        /// Parse size in string notation into long.
        /// Examples: 4k, 4K, 4KB, 4 KB, 8m, 8MB, 12g, 12 GB, 16t, 16 TB, 32p, 32 PB.
        /// </summary>
        /// <param name="value">String version of number</param>
        /// <returns>The number</returns>
        public static long ParseSize(string value)
        {
            char[] suffix = new char[] { 'k', 'm', 'g', 't', 'p' };
            long result = 0;
            foreach (char c in value)
            {
                if (char.IsDigit(c))
                {
                    result = result * 10 + (byte)c - '0';
                }
                else
                {
                    for (int i = 0; i < suffix.Length; i++)
                    {
                        if (char.ToLower(c) == suffix[i])
                        {
                            result *= (long)Math.Pow(1024, i + 1);
                            return result;
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Num bits in the previous power of 2 for specified number
        /// </summary>
        /// <param name="v"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        internal static int NumBitsPreviousPowerOf2(long v, ILogger logger = null)
        {
            long adjustedSize = PreviousPowerOf2(v);
            if (v != adjustedSize)
                logger?.LogError($"Warning: using lower value {adjustedSize} instead of specified value {v}");
            return (int)Math.Log(adjustedSize, 2);
        }

        /// <summary>
        /// Previous power of 2
        /// </summary>
        /// <param name="v"></param>
        /// <returns></returns>
        internal static long PreviousPowerOf2(long v)
        {
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            v |= v >> 32;
            return v - (v >> 1);
        }

        /// <summary>
        /// Pretty print value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        internal static string PrettySize(long value)
        {
            char[] suffix = new char[] { 'K', 'M', 'G', 'T', 'P' };
            double v = value;
            int exp = 0;
            while (v - Math.Floor(v) > 0)
            {
                if (exp >= 18)
                    break;
                exp += 3;
                v *= 1024;
                v = Math.Round(v, 12);
            }

            while (Math.Floor(v).ToString().Length > 3)
            {
                if (exp <= -18)
                    break;
                exp -= 3;
                v /= 1024;
                v = Math.Round(v, 12);
            }
            if (exp > 0)
                return v.ToString() + suffix[exp / 3 - 1] + "B";
            else if (exp < 0)
                return v.ToString() + suffix[-exp / 3 - 1] + "B";
            return v.ToString() + "B";
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsReadCache(long address) => (address & Constants.kReadCacheBitMask) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long AbsoluteAddress(long address) => address & ~Constants.kReadCacheBitMask;

        /// <summary>
        /// Is type blittable
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        internal static bool IsBlittable<T>()
        {
            if (default(T) == null)
                return false;

            try
            {
                var tmp = new T[1];
                var h = GCHandle.Alloc(tmp, GCHandleType.Pinned);
                h.Free();
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Check if two byte arrays of given length are equal
        /// </summary>
        /// <param name="src"></param>
        /// <param name="dst"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe static bool IsEqual(byte* src, byte* dst, int length)
        {
            for (int i = 0; i < length; i++)
            {
                if (*(src + i) != *(dst + i))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Copy numBytes bytes from src to dest
        /// </summary>
        /// <param name="src"></param>
        /// <param name="dest"></param>
        /// <param name="numBytes"></param>
        public unsafe static void Copy(byte* src, byte* dest, int numBytes)
        {
            for (int i = 0; i < numBytes; i++)
            {
                *(dest + i) = *(src + i);
            }
        }

        /// <summary>
        /// Get 64-bit hash code for a long value
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long GetHashCode(long input)
        {
            long local_rand = input;
            long local_rand_hash = 8;

            local_rand_hash = 40343 * local_rand_hash + ((local_rand) & 0xFFFF);
            local_rand_hash = 40343 * local_rand_hash + ((local_rand >> 16) & 0xFFFF);
            local_rand_hash = 40343 * local_rand_hash + ((local_rand >> 32) & 0xFFFF);
            local_rand_hash = 40343 * local_rand_hash + (local_rand >> 48);
            local_rand_hash = 40343 * local_rand_hash;

            return (long)Rotr64((ulong)local_rand_hash, 45);
        }

        /// <summary>
        /// Get 64-bit hash code for a byte array
        /// </summary>
        /// <param name="pbString"></param>
        /// <param name="len"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe long HashBytes(byte* pbString, int len)
        {
            const long magicno = 40343;
            char* pwString = (char*)pbString;
            int cbBuf = len / 2;
            ulong hashState = (ulong)len;

            for (int i = 0; i < cbBuf; i++, pwString++)
                hashState = magicno * hashState + *pwString;

            if ((len & 1) > 0)
            {
                byte* pC = (byte*)pwString;
                hashState = magicno * hashState + *pC;
            }

            return (long)Rotr64(magicno * hashState, 4);
        }

        /// <summary>
        /// Compute XOR of all provided bytes
        /// </summary>
        /// <param name="src"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe ulong XorBytes(byte* src, int length)
        {
            ulong result = 0;
            byte* curr = src;
            byte* end = src + length;
            while (curr + 4 * sizeof(ulong) <= end)
            {
                result ^= *(ulong*)curr;
                result ^= *(1 + (ulong*)curr);
                result ^= *(2 + (ulong*)curr);
                result ^= *(3 + (ulong*)curr);
                curr += 4 * sizeof(ulong);
            }
            while (curr + sizeof(ulong) <= end)
            {
                result ^= *(ulong*)curr;
                curr += sizeof(ulong);
            }
            while (curr + 1 <= end)
            {
                result ^= *curr;
                curr++;
            }

            return result;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong Rotr64(ulong x, int n)
        {
            return (((x) >> n) | ((x) << (64 - n)));
        }

        /// <summary>
        /// Is power of 2
        /// </summary>
        /// <param name="x"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPowerOfTwo(long x)
        {
            return (x > 0) && ((x & (x - 1)) == 0);
        }

        internal static readonly int[] MultiplyDeBruijnBitPosition2 = new int[32]
        {
            0, 1, 28, 2, 29, 14, 24, 3, 30, 22, 20, 15, 25, 17, 4, 8,
            31, 27, 13, 23, 21, 19, 16, 7, 26, 12, 18, 6, 11, 5, 10, 9
        };

        /// <summary>
        /// Get log base 2
        /// </summary>
        /// <param name="x"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetLogBase2(int x)
        {
            return MultiplyDeBruijnBitPosition2[(uint)(x * 0x077CB531U) >> 27];
        }

        /// <summary>
        /// Get log base 2
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static int GetLogBase2(ulong value)
        {
            int i;
            for (i = -1; value != 0; i++)
                value >>= 1;

            return (i == -1) ? 0 : i;
        }

        /// <summary>
        /// Check if power of two
        /// </summary>
        /// <param name="x"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Is32Bit(long x)
        {
            return ((ulong)x < 4294967295ul);
        }

        /// <summary>
        /// Updates the variable to newValue only if the current value is smaller than the new value.
        /// </summary>
        /// <param name="variable">The variable to possibly replace</param>
        /// <param name="newValue">The value that replaces the variable if successful</param>
        /// <param name="oldValue">The orignal value in the variable</param>
        /// <returns> if oldValue less than newValue </returns>
        public static bool MonotonicUpdate(ref long variable, long newValue, out long oldValue)
        {
            do
            {
                oldValue = variable;
                if (oldValue >= newValue) return false;
            } while (Interlocked.CompareExchange(ref variable, newValue, oldValue) != oldValue);
            return true;
        }

        /// <summary>
        /// Updates the variable to newValue only if the current value is smaller than the new value.
        /// </summary>
        /// <param name="variable">The variable to possibly replace</param>
        /// <param name="newValue">The value that replaces the variable if successful</param>
        /// <param name="oldValue">The orignal value in the variable</param>
        /// <returns>if oldValue less than or equal to newValue</returns>
        public static bool MonotonicUpdate(ref int variable, int newValue, out int oldValue)
        {
            do
            {
                oldValue = variable;
                if (oldValue >= newValue) return false;
            } while (Interlocked.CompareExchange(ref variable, newValue, oldValue) != oldValue);
            return true;
        }

        /// <summary>
        /// Throws OperationCanceledException if token cancels before the real task completes.
        /// Doesn't abort the inner task, but allows the calling code to get "unblocked" and react to stuck tasks.
        /// </summary>
        internal static Task<T> WithCancellationAsync<T>(this Task<T> task, CancellationToken token, bool useSynchronizationContext = false)
        {
            if (!token.CanBeCanceled || task.IsCompleted)
            {
                return task;
            }
            else if (token.IsCancellationRequested)
            {
                return Task.FromCanceled<T>(token);
            }

            return SlowWithCancellationAsync(task, token, useSynchronizationContext);
        }

        private static async Task<T> SlowWithCancellationAsync<T>(Task<T> task, CancellationToken token, bool useSynchronizationContext)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs, useSynchronizationContext))
            {
                if (task != await Task.WhenAny(task, tcs.Task))
                {
                    token.ThrowIfCancellationRequested();
                }
            }

            // make sure any exceptions in the task get unwrapped and exposed to the caller.
            return await task;
        }
    }
}