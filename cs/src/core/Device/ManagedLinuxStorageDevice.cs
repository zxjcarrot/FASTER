﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.core
{
    /// <summary>
    /// Managed device using .NET streams
    /// </summary>
    public sealed class ManagedLinuxStorageDevice : StorageDeviceBase
    {
        public static int F_GETFL = 3;
        public static int F_SETFL = 4;
        public static int O_DIRECT = 0x00040000;
		public static class Native
		{
			// TODO: C# 9, nuint instead of UIntPtr
			/// <summary>
			/// http://man7.org/linux/man-pages/man2/pwrite.2.html
			/// </summary>
			[DllImport("c", SetLastError = true)]
			public static extern unsafe IntPtr pread(IntPtr fd, void* buf, UIntPtr count, IntPtr offset);


			[DllImport("c", SetLastError = true)]
			public static extern unsafe IntPtr fcntl(IntPtr fd, IntPtr cmd, IntPtr args);

            /// <summary>
			/// http://man7.org/linux/man-pages/man2/pwrite.2.html
			/// </summary>
			[DllImport("c", SetLastError = true)]
			public static extern unsafe IntPtr pwrite(IntPtr fd, void* buf, UIntPtr count, IntPtr offset);
		}

		/// <summary>
		/// Performs a <c>pread</c> on a filestream at an offset to a buffer.
		/// </summary>
		/// <param name="fileStream">The file to read from.</param>
		/// <param name="buffer">The buffer to write data to.</param>
		/// <param name="fileOffset">The offset in the file to read data from.</param>
		/// <returns>Number of bytes.</returns>
		// Pread -> PRead: inline with P.Read better, aligns with C# naming conventions better
		// Span<byte>, FileStream, ulong -> FileStream, Span<byte>, ulong: aligns with P.Read better.
		public static unsafe long PRead(FileStream fileStream, Span<byte> buffer, ulong fileOffset)
		{
			var fileDescriptor = fileStream.SafeFileHandle.DangerousGetHandle();

			fixed (void* bufferPtr = buffer)
			{
				var bytesRead = (long)Native.pread(fileDescriptor, bufferPtr, (UIntPtr)buffer.Length, (IntPtr)fileOffset);
				return bytesRead;
			}
		}

        public static unsafe bool FileHandleSetODirect(FileStream fileStream) {
            var fileDescriptor = fileStream.SafeFileHandle.DangerousGetHandle();
            var oldFlags = (int)Native.fcntl(fileDescriptor, (IntPtr)F_GETFL, (IntPtr)0);
            var newFlags = oldFlags | O_DIRECT;
            var ret = Native.fcntl(fileDescriptor, (IntPtr)F_SETFL, (IntPtr)newFlags);
            return (int)ret == 0;
        }

        /// <summary>
		/// Performs a <c>pwrite</c> on a filestream at an offset to a buffer.
		/// </summary>
		/// <param name="fileStream">The file to write to.</param>
		/// <param name="data">The data to write to the file.</param>
		/// <param name="fileOffset">The offset in the file to write data at.</param>
		/// <returns>A <see cref="PResult"/>.</returns>
		// Pwrite -> PWrite: inline with P.Write better, aligns with C# naming conventions better
		// ReadOnlySpan<byte>, FileStream, ulong -> FileStream, ReadOnlySpan<byte>, ulong: aligns with P.Write better.
		// buffer -> data: better name, aligns with Windows.PWrite, was typo.
		public static unsafe long PWrite(FileStream fileStream, ReadOnlySpan<byte> data, ulong fileOffset)
		{
			var fileDescriptor = fileStream.SafeFileHandle.DangerousGetHandle();

			fixed (void* bufferPtr = data)
			{
				var bytesWritten = (long)Native.pwrite(fileDescriptor, bufferPtr, (UIntPtr)data.Length, (IntPtr)fileOffset);
				return bytesWritten;
			}
		}

        private readonly bool preallocateFile;
        private readonly bool deleteOnClose;
        private readonly bool osReadBuffering;
        private readonly SafeConcurrentDictionary<int, (AsyncPool<Stream>, AsyncPool<Stream>)> logHandles;
        private readonly SectorAlignedBufferPool pool;

        /// <summary>
        /// Number of pending reads on device
        /// </summary>
        private int numPending = 0;

        private bool _disposed;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="filename">File name (or prefix) with path</param>
        /// <param name="preallocateFile"></param>
        /// <param name="deleteOnClose"></param>
        /// <param name="capacity">The maximal number of bytes this storage device can accommondate, or CAPACITY_UNSPECIFIED if there is no such limit</param>
        /// <param name="recoverDevice">Whether to recover device metadata from existing files</param>
        /// <param name="osReadBuffering">Enable OS read buffering</param>
        public ManagedLinuxStorageDevice(string filename, bool preallocateFile = false, bool deleteOnClose = false, long capacity = Devices.CAPACITY_UNSPECIFIED, bool recoverDevice = false, bool osReadBuffering = false)
            : base(filename, GetSectorSize(filename), capacity)
        {
            pool = new(1, 1);
            ThrottleLimit = 120;

            string path = new FileInfo(filename).Directory.FullName;
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            this._disposed = false;
            this.preallocateFile = preallocateFile;
            this.deleteOnClose = deleteOnClose;
            this.osReadBuffering = osReadBuffering;
            logHandles = new();
            if (recoverDevice)
                RecoverFiles();
        }

        /// <inheritdoc />
        public override void Reset()
        {
            while (logHandles.Count > 0)
            {
                foreach (var entry in logHandles)
                {
                    if (logHandles.TryRemove(entry.Key, out _))
                    {
                        entry.Value.Item1.Dispose();
                        entry.Value.Item2.Dispose();
                        if (deleteOnClose)
                            File.Delete(GetSegmentName(entry.Key));
                    }
                }
            }
        }

        /// <inheritdoc />
        // We do not throttle ManagedLocalStorageDevice because our AsyncPool of handles takes care of this
        public override bool Throttle() => false;

        private void RecoverFiles()
        {
            FileInfo fi = new(FileName); // may not exist
            DirectoryInfo di = fi.Directory;
            if (!di.Exists) return;

            string bareName = fi.Name;

            List<int> segids = new();
            foreach (FileInfo item in di.GetFiles(bareName + "*"))
            {
                if (item.Name == bareName)
                {
                    continue;
                }
                segids.Add(Int32.Parse(item.Name.Replace(bareName, "").Replace(".", "")));
            }
            segids.Sort();

            int prevSegmentId = -1;
            foreach (int segmentId in segids)
            {
                if (segmentId != prevSegmentId + 1)
                {
                    startSegment = segmentId;
                }
                else
                {
                    endSegment = segmentId;
                }
                prevSegmentId = segmentId;
            }
            // No need to populate map because logHandles use Open or create on files.
        }

        /// <summary>
        /// Read async
        /// </summary>
        /// <param name="segmentId"></param>
        /// <param name="sourceAddress"></param>
        /// <param name="destinationAddress"></param>
        /// <param name="readLength"></param>
        /// <param name="callback"></param>
        /// <param name="context"></param>
        public override void ReadAsync(int segmentId, ulong sourceAddress,
                                     IntPtr destinationAddress,
                                     uint readLength,
                                     DeviceIOCompletionCallback callback,
                                     object context)
        {
            Stream logReadHandle = null;
            AsyncPool<Stream> streampool = null;
            uint errorCode = 0;
            //Task<int> readTask = default;
            bool gotHandle;
            long numBytes = 0;

#if NETSTANDARD2_1 || NET
            UnmanagedMemoryManager<byte> umm = default;
#else
            SectorAlignedMemory memory = default;
#endif

            try
            {
                Interlocked.Increment(ref numPending);
                streampool = GetOrAddHandle(segmentId).Item1;
                gotHandle = streampool.TryGet(out logReadHandle);
                if (gotHandle)
                {
                    //logReadHandle.Seek((long)sourceAddress, SeekOrigin.Begin);
#if NETSTANDARD2_1 || NET
                    unsafe
                    {
                        umm = new UnmanagedMemoryManager<byte>((byte*)destinationAddress, (int)readLength);
                    }
#else
                    memory = pool.Get((int)readLength);
#endif
                }
            }
            catch
            {
                Interlocked.Decrement(ref numPending);

                // Perform pool returns and disposals
#if !(NETSTANDARD2_1 || NET)
                memory?.Return();
#endif
                if (logReadHandle != null) streampool?.Return(logReadHandle);

                // Issue user callback
                callback(uint.MaxValue, 0, context);
                return;
            }

            _ = Task.Run(async () =>
            {
                if (!gotHandle)
                {
                    try
                    {
                        logReadHandle = await streampool.GetAsync().ConfigureAwait(false);
                        //logReadHandle.Seek((long)sourceAddress, SeekOrigin.Begin);
#if NETSTANDARD2_1 || NET
                        unsafe
                        {
                            umm = new UnmanagedMemoryManager<byte>((byte*)destinationAddress, (int)readLength);
                        }

                        // FileStream.ReadAsync is not thread-safe hence need a lock here
                        //lock (this)
                        //{
                        //    readTask = logReadHandle.ReadAsync(umm.Memory).AsTask();
                        //}
#else
                        memory = pool.Get((int)readLength);
                        // FileStream.ReadAsync is not thread-safe hence need a lock here
                        //lock (this)
                        //{
                        //    readTask = logReadHandle.ReadAsync(memory.buffer, 0, (int)readLength);
                        //}
#endif
                    }
                    catch
                    {
                        Interlocked.Decrement(ref numPending);

                        // Perform pool returns and disposals
#if !(NETSTANDARD2_1 || NET)
                        memory?.Return();
#endif
                        if (logReadHandle != null) streampool?.Return(logReadHandle);

                        // Issue user callback
                        callback(uint.MaxValue, 0, context);
                        return;
                    }
                }
               
                try
                {

                    #if NETSTANDARD2_1 || NET
                        numBytes = PRead((FileStream)logReadHandle, umm.Memory.Span, sourceAddress);
                    #else
                        numBytes = PRead((FileStream)logReadHandle, memory.buffer.AsSpan(), sourceAddress);
                    #endif
                    Debug.Assert(numBytes != -1);

#if !(NETSTANDARD2_1 || NET)
                    unsafe
                    {
                        fixed (void* source = memory.buffer)
                            Buffer.MemoryCopy(source, (void*)destinationAddress, numBytes, numBytes);
                    }
#endif
                }
                catch (Exception ex)
                {
                    if (ex.InnerException != null && ex.InnerException is IOException ioex)
                        errorCode = (uint)(ioex.HResult & 0x0000FFFF);
                    else
                        errorCode = uint.MaxValue;
                    numBytes = 0;
                }
                finally
                {
                    Interlocked.Decrement(ref numPending);

                    // Perform pool returns and disposals
#if !(NETSTANDARD2_1 || NET)
                    memory?.Return();
#endif
                    // Sequentialize all reads from same handle
                    streampool?.Return(logReadHandle);

                    // Issue user callback
                    callback(errorCode, (uint)numBytes, context);
                }
            });
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sourceAddress"></param>
        /// <param name="segmentId"></param>
        /// <param name="destinationAddress"></param>
        /// <param name="numBytesToWrite"></param>
        /// <param name="callback"></param>
        /// <param name="context"></param>
        public override void WriteAsync(IntPtr sourceAddress,
                                      int segmentId,
                                      ulong destinationAddress,
                                      uint numBytesToWrite,
                                      DeviceIOCompletionCallback callback,
                                      object context)
        {
            Stream logWriteHandle = null;
            AsyncPool<Stream> streampool = null;
            uint errorCode = 0;
            Task writeTask = default;
            bool gotHandle;

#if NETSTANDARD2_1 || NET
            UnmanagedMemoryManager<byte> umm = default;
#else
            SectorAlignedMemory memory = default;
#endif

            HandleCapacity(segmentId);

            try
            {
                Interlocked.Increment(ref numPending);
                streampool = GetOrAddHandle(segmentId).Item2;
                gotHandle = streampool.TryGet(out logWriteHandle);
                if (gotHandle)
                {
                    //logWriteHandle.Seek((long)destinationAddress, SeekOrigin.Begin);
#if NETSTANDARD2_1 || NET
                    unsafe
                    {
                        umm = new UnmanagedMemoryManager<byte>((byte*)sourceAddress, (int)numBytesToWrite);
                    }

                    // FileStream.WriteAsync is not thread-safe hence need a lock here
                    //lock (this)
                    //{
                    //    writeTask = logWriteHandle.WriteAsync(umm.Memory).AsTask();
                    //}
#else
                    memory = pool.Get((int)numBytesToWrite);
                    unsafe
                    {
                        fixed (void* destination = memory.buffer)
                        {
                            Buffer.MemoryCopy((void*)sourceAddress, destination, numBytesToWrite, numBytesToWrite);
                        }
                    }
                    // FileStream.WriteAsync is not thread-safe hence need a lock here
                    //lock (this)
                    //{
                    //    writeTask = logWriteHandle.WriteAsync(memory.buffer, 0, (int)numBytesToWrite);
                    //}
#endif
                }
            }
            catch
            {
                Interlocked.Decrement(ref numPending);

                // Perform pool returns and disposals
#if !(NETSTANDARD2_1 || NET)
                memory?.Return();
#endif
                if (logWriteHandle != null) streampool?.Return(logWriteHandle);

                // Issue user callback
                callback(uint.MaxValue, 0, context);
                return;
            }

            _ = Task.Run(async () =>
            {
                if (!gotHandle)
                {
                    try
                    {
                        logWriteHandle = await streampool.GetAsync().ConfigureAwait(false);
                        //logWriteHandle.Seek((long)destinationAddress, SeekOrigin.Begin);
#if NETSTANDARD2_1 || NET
                        unsafe
                        {
                            umm = new UnmanagedMemoryManager<byte>((byte*)sourceAddress, (int)numBytesToWrite);
                        }

                        // FileStream.WriteAsync is not thread-safe hence need a lock here
                        //lock (this)
                        //{
                        //    writeTask = logWriteHandle.WriteAsync(umm.Memory).AsTask();
                        //}
#else
                        memory = pool.Get((int)numBytesToWrite);
                        unsafe
                        {
                            fixed (void* destination = memory.buffer)
                            {
                                Buffer.MemoryCopy((void*)sourceAddress, destination, numBytesToWrite, numBytesToWrite);
                            }
                        }
                        // FileStream.WriteAsync is not thread-safe hence need a lock here
                        //lock (this)
                        //{
                        //    writeTask = logWriteHandle.WriteAsync(memory.buffer, 0, (int)numBytesToWrite);
                        //}
#endif
                    }
                    catch
                    {
                        Interlocked.Decrement(ref numPending);

                        // Perform pool returns and disposals
#if !(NETSTANDARD2_1 || NET)
                        memory?.Return();
#endif
                        if (logWriteHandle != null) streampool?.Return(logWriteHandle);

                        // Issue user callback
                        callback(uint.MaxValue, 0, context);
                        return;
                    }
                }

                try
                {
                    //await writeTask.ConfigureAwait(false);
                    #if NETSTANDARD2_1 || NET
                        PWrite((FileStream)logWriteHandle, umm.Memory.Span, destinationAddress);
                    #else
                        PWrite((FileStream)logWriteHandle, memory.buffer.AsSpan(), destinationAddress);
                    #endif
                    //Debug.Assert(numBytesToWrite != -1);
                }
                catch (Exception ex)
                {
                    if (ex.InnerException != null && ex.InnerException is IOException ioex)
                        errorCode = (uint)(ioex.HResult & 0x0000FFFF);
                    else
                        errorCode = uint.MaxValue;
                    numBytesToWrite = 0;
                }
                finally
                {
                    Interlocked.Decrement(ref numPending);

                    // Perform pool returns and disposals
#if !(NETSTANDARD2_1 || NET)
                    memory?.Return();
#endif
                    // Sequentialize all writes to same handle
                    await ((FileStream)logWriteHandle).FlushAsync().ConfigureAwait(false);
                    streampool?.Return(logWriteHandle);

                    // Issue user callback
                    callback(errorCode, numBytesToWrite, context);
                }
            });
        }

        /// <summary>
        /// <see cref="IDevice.RemoveSegment(int)"/>
        /// </summary>
        /// <param name="segment"></param>
        public override void RemoveSegment(int segment)
        {
            if (logHandles.TryRemove(segment, out (AsyncPool<Stream>, AsyncPool<Stream>) logHandle))
            {
                logHandle.Item1.Dispose();
                logHandle.Item2.Dispose();
            }
            try
            {
                File.Delete(GetSegmentName(segment));
            } catch { }
        }

        /// <summary>
        /// <see cref="IDevice.RemoveSegmentAsync(int, AsyncCallback, IAsyncResult)"/>
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="callback"></param>
        /// <param name="result"></param>
        public override void RemoveSegmentAsync(int segment, AsyncCallback callback, IAsyncResult result)
        {
            RemoveSegment(segment);
            callback(result);
        }

        /// <inheritdoc/>
        public override long GetFileSize(int segment)
        {
            if (segmentSize > 0) return segmentSize;
            var pool = GetOrAddHandle(segment);
            if (!pool.Item1.TryGet(out var stream))
                stream = pool.Item1.Get();

            long size = stream.Length;
            pool.Item1.Return(stream);
            return size;
        }


        /// <summary>
        /// Close device
        /// </summary>
        public override void Dispose()
        {
            _disposed = true;
            foreach (var entry in logHandles)
            {
                entry.Value.Item1.Dispose();
                entry.Value.Item2.Dispose();
                if (deleteOnClose)
                    File.Delete(GetSegmentName(entry.Key));
            }
            pool.Free();
        }

        private string GetSegmentName(int segmentId) => GetSegmentFilename(FileName, segmentId);

        private static uint GetSectorSize(string filename)
        {
         if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Debug.WriteLine("Assuming 512 byte sector alignment for disk with file " + filename);
                return 512;
            }

            if (!Native32.GetDiskFreeSpace(filename.Substring(0, 3),
                                        out uint lpSectorsPerCluster,
                                        out uint _sectorSize,
                                        out uint lpNumberOfFreeClusters,
                                        out uint lpTotalNumberOfClusters))
            {
                Debug.WriteLine("Unable to retrieve information for disk " + filename.Substring(0, 3) + " - check if the disk is available and you have specified the full path with drive name. Assuming sector size of 512 bytes.");
                _sectorSize = 512;
            }
            return _sectorSize;
        }

        private Stream CreateReadHandle(int segmentId)
        {
            const int FILE_FLAG_NO_BUFFERING = 0x20000000;
            FileOptions fo = FileOptions.None;
            

            var logReadHandle = new FileStream(
                GetSegmentName(segmentId), FileMode.OpenOrCreate,
                FileAccess.Read, FileShare.ReadWrite, 512, fo);

            if (!osReadBuffering) {
                var res = FileHandleSetODirect(logReadHandle);
                if (res != true) {        
                    //int error = Marshal.GetLastPInvokeError();
                    //Console.WriteLine($"Last p/invoke error: {error}");
                }
                Debug.Assert(res);
            }

            return logReadHandle;
        }

        private Stream CreateWriteHandle(int segmentId)
        {
            const int FILE_FLAG_NO_BUFFERING = 0x20000000;
            FileOptions fo =
                (FileOptions)FILE_FLAG_NO_BUFFERING |
                FileOptions.WriteThrough |
                FileOptions.Asynchronous |
                FileOptions.None;

            var logWriteHandle = new FileStream(
                GetSegmentName(segmentId), FileMode.OpenOrCreate,
                FileAccess.Write, FileShare.ReadWrite, 512, fo);

            if (preallocateFile && segmentSize != -1)
                SetFileSize(logWriteHandle, segmentSize);

            return logWriteHandle;
        }

        private (AsyncPool<Stream>, AsyncPool<Stream>) AddHandle(int _segmentId)
        {
            return (new AsyncPool<Stream>(ThrottleLimit, () => CreateReadHandle(_segmentId)), new AsyncPool<Stream>(ThrottleLimit, () => CreateWriteHandle(_segmentId)));
        }

        private (AsyncPool<Stream>, AsyncPool<Stream>) GetOrAddHandle(int _segmentId)
        {
            if (logHandles.TryGetValue(_segmentId, out var h))
            {
                return h;
            }
            var result = logHandles.GetOrAdd(_segmentId, e => AddHandle(e));

            if (_disposed)
            {
                // If disposed, dispose the fixed pools and return the (disposed) result
                foreach (var entry in logHandles)
                {
                    entry.Value.Item1.Dispose();
                    entry.Value.Item2.Dispose();
                    if (deleteOnClose)
                        File.Delete(GetSegmentName(entry.Key));
                }
            }
            return result;
        }

        /// <summary>
        /// Sets file size to the specified value.
        /// Does not reset file seek pointer to original location.
        /// </summary>
        /// <param name="logHandle"></param>
        /// <param name="size"></param>
        /// <returns></returns>
        private bool SetFileSize(Stream logHandle, long size)
        {
            logHandle.SetLength(size);
            return true;
        }
    }
}