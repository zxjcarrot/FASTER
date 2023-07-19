﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.core
{
    /// <summary>
    /// Faster Context implementation that allows manual control of record locking and epoch management. For advanced use only.
    /// </summary>
    public readonly struct LockableContext<Key, Value, Input, Output, Context, Functions> : IFasterContext<Key, Value, Input, Output, Context>, ILockableContext<Key>
        where Functions : IFunctions<Key, Value, Input, Output, Context>
    {
        readonly ClientSession<Key, Value, Input, Output, Context, Functions> clientSession;
        readonly InternalFasterSession FasterSession;

        /// <summary>Indicates whether this struct has been initialized</summary>
        public bool IsNull => this.clientSession is null;

        internal LockableContext(ClientSession<Key, Value, Input, Output, Context, Functions> clientSession)
        {
            this.clientSession = clientSession;
            FasterSession = new InternalFasterSession(clientSession);
        }

        #region Begin/EndLockable

        /// <inheritdoc/>
        public void BeginLockable() => clientSession.AcquireLockable();

        /// <inheritdoc/>
        public void EndLockable() => clientSession.ReleaseLockable();

        #endregion Begin/EndLockable

        #region Key Locking

        /// <inheritdoc/>
        public bool NeedKeyLockCode => clientSession.NeedKeyLockCode;

        /// <inheritdoc/>
        public long GetLockCode(Key key, out long keyHash) => clientSession.GetLockCode(ref key, out keyHash);

        /// <inheritdoc/>
        public long GetLockCode(ref Key key, out long keyHash) => clientSession.GetLockCode(ref key, out keyHash);

        /// <inheritdoc/>
        public long GetLockCode(Key key, long keyHash) => clientSession.GetLockCode(ref key, keyHash);

        /// <inheritdoc/>
        public long GetLockCode(ref Key key, long keyHash) => clientSession.GetLockCode(ref key, keyHash);

        /// <inheritdoc/>
        public int CompareLockCodes<TLockableKey>(TLockableKey key1, TLockableKey key2) where TLockableKey : ILockableKey => clientSession.CompareLockCodes(key1, key2);

        /// <inheritdoc/>
        public int CompareLockCodes<TLockableKey>(ref TLockableKey key1, ref TLockableKey key2) where TLockableKey : ILockableKey => clientSession.CompareLockCodes(ref key1, ref key2);

        /// <inheritdoc/>
        public void SortLockCodes<TLockableKey>(TLockableKey[] keys) where TLockableKey : ILockableKey => clientSession.SortLockCodes(keys);

        /// <inheritdoc/>
        public void SortLockCodes<TLockableKey>(TLockableKey[] keys, int start, int count) where TLockableKey : ILockableKey => clientSession.SortLockCodes(keys, start, count);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe void DoInternalLockOp<FasterSession, TLockableKey>(FasterSession fasterSession, ClientSession<Key, Value, Input, Output, Context, Functions> clientSession,
                                                                   TLockableKey[] keys, int start, int count, LockOperationType lockOpType)
            where FasterSession : IFasterSession<Key, Value, Input, Output, Context>
            where TLockableKey : ILockableKey
        {
            // The key codes are sorted, but there may be duplicates; the sorting is such that exclusive locks come first for each key code,
            // which of course allows the session to do shared operations as well, so we take the first occurrence of each key code.
            // Unlock has to be done in the reverse order of locking, so we take the *last* occurrence of each key there.
            var end = start + count - 1;
            if (lockOpType == LockOperationType.Lock)
            {
                for (int ii = start; ii <= end; ++ii)
                {
                    var lockType = DoLockOp(fasterSession, clientSession, keys, start, lockOpType, ii);
                    if (lockType == LockType.Exclusive)
                        ++clientSession.exclusiveLockCount;
                    else if (lockType == LockType.Shared)
                        ++clientSession.sharedLockCount;
                }
                return;
            }

            // LockOperationType.Unlock; go through the keys in reverse.
            for (int ii = end; ii >= start; --ii)
            {
                var lockType = DoLockOp(fasterSession, clientSession, keys, start, lockOpType, ii);
                if (lockType == LockType.Exclusive)
                    --clientSession.exclusiveLockCount;
                else if (lockType == LockType.Shared)
                    --clientSession.sharedLockCount;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe LockType DoLockOp<FasterSession, TLockableKey>(FasterSession fasterSession, ClientSession<Key, Value, Input, Output, Context, Functions> clientSession,
                                                                             TLockableKey[] keys, int start, LockOperationType lockOpType, int idx)
            where FasterSession : IFasterSession<Key, Value, Input, Output, Context>
            where TLockableKey : ILockableKey
        {
            ref var key = ref keys[idx];
            if (idx == start || clientSession.fht.LockTable.GetBucketIndex(key.LockCode) != clientSession.fht.LockTable.GetBucketIndex(keys[idx - 1].LockCode))
            {
                OperationStatus status;
                do
                    status = clientSession.fht.InternalLock(key.LockCode, new(lockOpType, key.LockType));
                while (clientSession.fht.HandleImmediateNonPendingRetryStatus<Input, Output, Context, FasterSession>(status, fasterSession));
                Debug.Assert(status == OperationStatus.SUCCESS);
                return key.LockType;
            }
            return LockType.None;
        }

        /// <inheritdoc/>
        public void Lock<TLockableKey>(TLockableKey[] keys) where TLockableKey : ILockableKey => Lock(keys, 0, keys.Length);
        
        /// <inheritdoc/>
        public void Lock<TLockableKey>(TLockableKey[] keys, int start, int count)
            where TLockableKey : ILockableKey
        {
            clientSession.CheckIsAcquiredLockable();
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected(), "Trying to protect an already-protected epoch for LockableUnsafeContext.Lock()");

            clientSession.UnsafeResumeThread();
            try
            {
                DoInternalLockOp(FasterSession, clientSession, keys, start, count, LockOperationType.Lock);
            }
            finally
            {
                clientSession.UnsafeSuspendThread();
            }
        }

        /// <inheritdoc/>
        public void Unlock<TLockableKey>(TLockableKey[] keys) where TLockableKey : ILockableKey => Unlock(keys, 0, keys.Length);

        /// <inheritdoc/>
        public void Unlock<TLockableKey>(TLockableKey[] keys, int start, int count)
            where TLockableKey : ILockableKey
        {
            clientSession.CheckIsAcquiredLockable();
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected(), "Trying to protect an already-protected epoch for LockableUnsafeContext.Unlock()");

            clientSession.UnsafeResumeThread();
            try
            {
                DoInternalLockOp(FasterSession, clientSession, keys, start, count, LockOperationType.Unlock);
            }
            finally
            {
                clientSession.UnsafeSuspendThread();
            }
        }

        /// <summary>
        /// The session id of FasterSession
        /// </summary>
        public int SessionID { get { return clientSession.ctx.sessionID; } }

        #endregion Key Locking

        #region IFasterContext

        /// <inheritdoc/>
        public bool CompletePending(bool wait = false, bool spinWaitForCommit = false)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            clientSession.UnsafeResumeThread();
            try
            {
                return this.clientSession.UnsafeCompletePending(this.FasterSession, false, wait, spinWaitForCommit);
            }
            finally
            {
                clientSession.UnsafeSuspendThread();
            }
        }

        /// <inheritdoc/>
        public bool CompletePendingWithOutputs(out CompletedOutputIterator<Key, Value, Input, Output, Context> completedOutputs, bool wait = false, bool spinWaitForCommit = false)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            clientSession.UnsafeResumeThread();
            try
            {
                return this.clientSession.UnsafeCompletePendingWithOutputs(this.FasterSession, out completedOutputs, wait, spinWaitForCommit);
            }
            finally
            {
                clientSession.UnsafeSuspendThread();
            }
        }

        /// <inheritdoc/>
        public ValueTask CompletePendingAsync(bool waitForCommit = false, CancellationToken token = default)
            => this.clientSession.CompletePendingAsync(waitForCommit, token);

        /// <inheritdoc/>
        public ValueTask<CompletedOutputIterator<Key, Value, Input, Output, Context>> CompletePendingWithOutputsAsync(bool waitForCommit = false, CancellationToken token = default)
            => this.clientSession.CompletePendingWithOutputsAsync(waitForCommit, token);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status ReadAtAddressOrKey(ref Key key, long logicalAddress, ref Input input, ref Output output, Context userContext = default, long serialNo = 0)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            clientSession.UnsafeResumeThread();
            try
            {
                return clientSession.fht.ContextReadAtAddressOrKey(ref key, logicalAddress, ref input, ref output, userContext, FasterSession, serialNo);
            }
            finally
            {
                clientSession.UnsafeSuspendThread();
            }
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status Read(ref Key key, ref Input input, ref Output output, Context userContext = default, long serialNo = 0)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            clientSession.UnsafeResumeThread();
            try
            {
                return clientSession.fht.ContextRead(ref key, ref input, ref output, userContext, FasterSession, serialNo);
            }
            finally
            {
                clientSession.UnsafeSuspendThread();
            }
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status Read(Key key, Input input, out Output output, Context userContext = default, long serialNo = 0)
        {
            output = default;
            return Read(ref key, ref input, ref output, userContext, serialNo);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status Read(ref Key key, ref Output output, Context userContext = default, long serialNo = 0)
        {
            Input input = default;
            return Read(ref key, ref input, ref output, userContext, serialNo);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status Read(Key key, out Output output, Context userContext = default, long serialNo = 0)
        {
            Input input = default;
            output = default;
            return Read(ref key, ref input, ref output, userContext, serialNo);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public (Status status, Output output) Read(Key key, Context userContext = default, long serialNo = 0)
        {
            Input input = default;
            Output output = default;
            return (Read(ref key, ref input, ref output, userContext, serialNo), output);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status Read(ref Key key, ref Input input, ref Output output, ref ReadOptions readOptions, out RecordMetadata recordMetadata, Context userContext = default, long serialNo = 0)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            clientSession.UnsafeResumeThread();
            try
            {
                return clientSession.fht.ContextRead(ref key, ref input, ref output, ref readOptions, out recordMetadata, userContext, FasterSession, serialNo);
            }
            finally
            {
                clientSession.UnsafeSuspendThread();
            }
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status ReadAtAddress(ref Input input, ref Output output, ref ReadOptions readOptions, Context userContext = default, long serialNo = 0)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            clientSession.UnsafeResumeThread();
            try
            {
                return clientSession.fht.ContextReadAtAddress(ref input, ref output, ref readOptions, userContext, FasterSession, serialNo);
            }
            finally
            {
                clientSession.UnsafeSuspendThread();
            }
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<FasterKV<Key, Value>.ReadAsyncResult<Input, Output, Context>> ReadAsync(ref Key key, ref Input input, Context userContext = default, long serialNo = 0, CancellationToken cancellationToken = default)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            ReadOptions readOptions = default;
            return clientSession.fht.ReadAsync(FasterSession, ref key, ref input, ref readOptions, userContext, serialNo, cancellationToken);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<FasterKV<Key, Value>.ReadAsyncResult<Input, Output, Context>> ReadAsync(Key key, Input input, Context context = default, long serialNo = 0, CancellationToken token = default)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            ReadOptions readOptions = default;
            return clientSession.fht.ReadAsync(FasterSession, ref key, ref input, ref readOptions, context, serialNo, token);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<FasterKV<Key, Value>.ReadAsyncResult<Input, Output, Context>> ReadAsync(ref Key key, Context userContext = default, long serialNo = 0, CancellationToken token = default)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            Input input = default;
            ReadOptions readOptions = default;
            return clientSession.fht.ReadAsync(FasterSession, ref key, ref input, ref readOptions, userContext, serialNo, token);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<FasterKV<Key, Value>.ReadAsyncResult<Input, Output, Context>> ReadAsync(Key key, Context context = default, long serialNo = 0, CancellationToken token = default)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            Input input = default;
            ReadOptions readOptions = default;
            return clientSession.fht.ReadAsync(FasterSession, ref key, ref input, ref readOptions, context, serialNo, token);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<FasterKV<Key, Value>.ReadAsyncResult<Input, Output, Context>> ReadAsync(ref Key key, ref Input input, ref ReadOptions readOptions,
                                                                                                 Context userContext = default, long serialNo = 0, CancellationToken cancellationToken = default)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            return clientSession.fht.ReadAsync(FasterSession, ref key, ref input, ref readOptions, userContext, serialNo, cancellationToken);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<FasterKV<Key, Value>.ReadAsyncResult<Input, Output, Context>> ReadAtAddressAsync(ref Input input, ref ReadOptions readOptions,
                                                                                                          Context userContext = default, long serialNo = 0, CancellationToken cancellationToken = default)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            Key key = default;
            return clientSession.fht.ReadAsync(FasterSession, ref key, ref input, ref readOptions, userContext, serialNo, cancellationToken, noKey: true);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status Upsert(ref Key key, ref Value desiredValue, Context userContext = default, long serialNo = 0)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            Input input = default;
            Output output = default;
            return Upsert(ref key, ref input, ref desiredValue, ref output, out _, userContext, serialNo);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status Upsert(ref Key key, ref Input input, ref Value desiredValue, ref Output output, Context userContext = default, long serialNo = 0)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            clientSession.UnsafeResumeThread();
            try
            {
                return clientSession.fht.ContextUpsert(ref key, ref input, ref desiredValue, ref output, userContext, FasterSession, serialNo);
            }
            finally
            {
                clientSession.UnsafeSuspendThread();
            }
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status Upsert(ref Key key, ref Input input, ref Value desiredValue, ref Output output, out RecordMetadata recordMetadata, Context userContext = default, long serialNo = 0)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            clientSession.UnsafeResumeThread();
            try
            {
                return clientSession.fht.ContextUpsert(ref key, ref input, ref desiredValue, ref output, out recordMetadata, userContext, FasterSession, serialNo);
            }
            finally
            {
                clientSession.UnsafeSuspendThread();
            }
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status Upsert(Key key, Value desiredValue, Context userContext = default, long serialNo = 0)
            => Upsert(ref key, ref desiredValue, userContext, serialNo);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status Upsert(Key key, Input input, Value desiredValue, ref Output output, Context userContext = default, long serialNo = 0)
            => Upsert(ref key, ref input, ref desiredValue, ref output, userContext, serialNo);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<FasterKV<Key, Value>.UpsertAsyncResult<Input, Output, Context>> UpsertAsync(ref Key key, ref Value desiredValue, Context userContext = default, long serialNo = 0, CancellationToken token = default)
        {
            Input input = default;
            return UpsertAsync(ref key, ref input, ref desiredValue, userContext, serialNo, token);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<FasterKV<Key, Value>.UpsertAsyncResult<Input, Output, Context>> UpsertAsync(ref Key key, ref Input input, ref Value desiredValue, Context userContext = default, long serialNo = 0, CancellationToken token = default)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            return clientSession.fht.UpsertAsync<Input, Output, Context, InternalFasterSession>(FasterSession, ref key, ref input, ref desiredValue, userContext, serialNo, token);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<FasterKV<Key, Value>.UpsertAsyncResult<Input, Output, Context>> UpsertAsync(Key key, Value desiredValue, Context userContext = default, long serialNo = 0, CancellationToken token = default)
            => UpsertAsync(ref key, ref desiredValue, userContext, serialNo, token);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<FasterKV<Key, Value>.UpsertAsyncResult<Input, Output, Context>> UpsertAsync(Key key, Input input, Value desiredValue, Context userContext = default, long serialNo = 0, CancellationToken token = default)
            => UpsertAsync(ref key, ref input, ref desiredValue, userContext, serialNo, token);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status RMW(ref Key key, ref Input input, ref Output output, Context userContext = default, long serialNo = 0)
            => RMW(ref key, ref input, ref output, out _, userContext, serialNo);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status RMW(ref Key key, ref Input input, ref Output output, out RecordMetadata recordMetadata, Context userContext = default, long serialNo = 0)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            clientSession.UnsafeResumeThread();
            try
            {
                return clientSession.fht.ContextRMW(ref key, ref input, ref output, out recordMetadata, userContext, FasterSession, serialNo);
            }
            finally
            {
                clientSession.UnsafeSuspendThread();
            }
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status RMW(Key key, Input input, out Output output, Context userContext = default, long serialNo = 0)
        {
            output = default;
            return RMW(ref key, ref input, ref output, userContext, serialNo);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status RMW(ref Key key, ref Input input, Context userContext = default, long serialNo = 0)
        {
            Output output = default;
            return RMW(ref key, ref input, ref output, userContext, serialNo);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status RMW(Key key, Input input, Context userContext = default, long serialNo = 0)
        {
            Output output = default;
            return RMW(ref key, ref input, ref output, userContext, serialNo);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<FasterKV<Key, Value>.RmwAsyncResult<Input, Output, Context>> RMWAsync(ref Key key, ref Input input, Context context = default, long serialNo = 0, CancellationToken token = default)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            return clientSession.fht.RmwAsync<Input, Output, Context, InternalFasterSession>(FasterSession, ref key, ref input, context, serialNo, token);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<FasterKV<Key, Value>.RmwAsyncResult<Input, Output, Context>> RMWAsync(Key key, Input input, Context context = default, long serialNo = 0, CancellationToken token = default)
            => RMWAsync(ref key, ref input, context, serialNo, token);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status Delete(ref Key key, Context userContext = default, long serialNo = 0)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            clientSession.UnsafeResumeThread();
            try
            {
                return clientSession.fht.ContextDelete<Input, Output, Context, InternalFasterSession>(ref key, userContext, FasterSession, serialNo);
            }
            finally
            {
                clientSession.UnsafeSuspendThread();
            }
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status Delete(Key key, Context userContext = default, long serialNo = 0)
            => Delete(ref key, userContext, serialNo);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<FasterKV<Key, Value>.DeleteAsyncResult<Input, Output, Context>> DeleteAsync(ref Key key, Context userContext = default, long serialNo = 0, CancellationToken token = default)
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            return clientSession.fht.DeleteAsync<Input, Output, Context, InternalFasterSession>(FasterSession, ref key, userContext, serialNo, token);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<FasterKV<Key, Value>.DeleteAsyncResult<Input, Output, Context>> DeleteAsync(Key key, Context userContext = default, long serialNo = 0, CancellationToken token = default)
            => DeleteAsync(ref key, userContext, serialNo, token);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResetModified(ref Key key)
            => clientSession.ResetModified(ref key);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool IsModified(Key key)
            => clientSession.IsModified(ref key);

        /// <inheritdoc/>
        public void Refresh()
        {
            Debug.Assert(!clientSession.fht.epoch.ThisInstanceProtected());
            clientSession.UnsafeResumeThread();
            try
            {
                clientSession.fht.InternalRefresh<Input, Output, Context, InternalFasterSession>(FasterSession);
            }
            finally
            {
                clientSession.UnsafeSuspendThread();
            }
        }

        #endregion IFasterContext

        #region IFasterSession

        // This is a struct to allow JIT to inline calls (and bypass default interface call mechanism)
        internal readonly struct InternalFasterSession : IFasterSession<Key, Value, Input, Output, Context>
        {
            private readonly ClientSession<Key, Value, Input, Output, Context, Functions> _clientSession;

            public InternalFasterSession(ClientSession<Key, Value, Input, Output, Context, Functions> clientSession)
            {
                _clientSession = clientSession;
            }

            public bool IsManualLocking => true;
            public FasterKV<Key, Value> Store => _clientSession.fht;

            #region IFunctions - Reads
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool SingleReader(ref Key key, ref Input input, ref Value value, ref Output dst, ref RecordInfo recordInfo, ref ReadInfo readInfo)
                => _clientSession.functions.SingleReader(ref key, ref input, ref value, ref dst, ref readInfo);


            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool ConcurrentReaderValidAndNonSealed(ref Key key, ref Input input, ref Value value, ref Output dst, ref RecordInfo recordInfo, ref ReadInfo readInfo, out EphemeralLockResult lockResult)
            {
                lockResult = EphemeralLockResult.Success;
                if (recordInfo.IsSealed || recordInfo.Invalid)
                    return false;
                return _clientSession.functions.ConcurrentReader(ref key, ref input, ref value, ref dst, ref readInfo);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool ConcurrentReader(ref Key key, ref Input input, ref Value value, ref Output dst, ref RecordInfo recordInfo, ref ReadInfo readInfo, out EphemeralLockResult lockResult)
            {
                lockResult = EphemeralLockResult.Success;       // Ephemeral locking is not used with Lockable contexts
                return _clientSession.functions.ConcurrentReader(ref key, ref input, ref value, ref dst, ref readInfo);
            }

            public void ReadCompletionCallback(ref Key key, ref Input input, ref Output output, Context ctx, Status status, RecordMetadata recordMetadata)
                => _clientSession.functions.ReadCompletionCallback(ref key, ref input, ref output, ctx, status, recordMetadata);

            #endregion IFunctions - Reads

            #region IFunctions - Upserts
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool SingleWriter(ref Key key, ref Input input, ref Value src, ref Value dst, ref Output output, ref RecordInfo recordInfo, ref UpsertInfo upsertInfo, WriteReason reason)
                => _clientSession.functions.SingleWriter(ref key, ref input, ref src, ref dst, ref output, ref upsertInfo, reason);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void PostSingleWriter(ref Key key, ref Input input, ref Value src, ref Value dst, ref Output output, ref RecordInfo recordInfo, ref UpsertInfo upsertInfo, WriteReason reason)
            {
                recordInfo.SetDirtyAndModified();
                _clientSession.functions.PostSingleWriter(ref key, ref input, ref src, ref dst, ref output, ref upsertInfo, reason);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool ConcurrentWriter(ref Key key, ref Input input, ref Value src, ref Value dst, ref Output output, ref RecordInfo recordInfo, ref UpsertInfo upsertInfo, out EphemeralLockResult lockResult)
            {
                lockResult = EphemeralLockResult.Success;       // Ephemeral locking is not used with Lockable contexts
                if (!_clientSession.functions.ConcurrentWriter(ref key, ref input, ref src, ref dst, ref output, ref upsertInfo))
                    return false;
                recordInfo.SetDirtyAndModified();
                return true;
            }
            #endregion IFunctions - Upserts

            #region IFunctions - RMWs
            #region InitialUpdater
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool NeedInitialUpdate(ref Key key, ref Input input, ref Output output, ref RMWInfo rmwInfo)
                => _clientSession.functions.NeedInitialUpdate(ref key, ref input, ref output, ref rmwInfo);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool InitialUpdater(ref Key key, ref Input input, ref Value value, ref Output output, ref RecordInfo recordInfo, ref RMWInfo rmwInfo)
                => _clientSession.functions.InitialUpdater(ref key, ref input, ref value, ref output, ref rmwInfo);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void PostInitialUpdater(ref Key key, ref Input input, ref Value value, ref Output output, ref RecordInfo recordInfo, ref RMWInfo rmwInfo)
            {
                recordInfo.SetDirtyAndModified();
                _clientSession.functions.PostInitialUpdater(ref key, ref input, ref value, ref output, ref rmwInfo);
            }
            #endregion InitialUpdater

            #region CopyUpdater
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool NeedCopyUpdate(ref Key key, ref Input input, ref Value oldValue, ref Output output, ref RMWInfo rmwInfo)
                => _clientSession.functions.NeedCopyUpdate(ref key, ref input, ref oldValue, ref output, ref rmwInfo);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool CopyUpdater(ref Key key, ref Input input, ref Value oldValue, ref Value newValue, ref Output output, ref RecordInfo recordInfo, ref RMWInfo rmwInfo)
                => _clientSession.functions.CopyUpdater(ref key, ref input, ref oldValue, ref newValue, ref output, ref rmwInfo);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void PostCopyUpdater(ref Key key, ref Input input, ref Value oldValue, ref Value newValue, ref Output output, ref RecordInfo recordInfo, ref RMWInfo rmwInfo) 
            {
                recordInfo.SetDirtyAndModified();
                _clientSession.functions.PostCopyUpdater(ref key, ref input, ref oldValue, ref newValue, ref output, ref rmwInfo);
            }
            #endregion CopyUpdater

            #region InPlaceUpdater
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool InPlaceUpdater(ref Key key, ref Input input, ref Value value, ref Output output, ref RecordInfo recordInfo, ref RMWInfo rmwInfo, out OperationStatus status, out EphemeralLockResult lockResult)
            {
                lockResult = EphemeralLockResult.Success;       // Ephemeral locking is not used with Lockable contexts
                if (!_clientSession.InPlaceUpdater(ref key, ref input, ref value, ref output, ref recordInfo, ref rmwInfo, out status))
                    return false;
                recordInfo.SetDirtyAndModified();
                return true;
            }

            public void RMWCompletionCallback(ref Key key, ref Input input, ref Output output, Context ctx, Status status, RecordMetadata recordMetadata)
                => _clientSession.functions.RMWCompletionCallback(ref key, ref input, ref output, ctx, status, recordMetadata);

            #endregion InPlaceUpdater
            #endregion IFunctions - RMWs

            #region IFunctions - Deletes
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool SingleDeleter(ref Key key, ref Value value, ref RecordInfo recordInfo, ref DeleteInfo deleteInfo)
                => _clientSession.functions.SingleDeleter(ref key, ref value, ref deleteInfo);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void PostSingleDeleter(ref Key key, ref RecordInfo recordInfo, ref DeleteInfo deleteInfo) 
            {
                recordInfo.SetDirtyAndModified();
                _clientSession.functions.PostSingleDeleter(ref key, ref deleteInfo);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool ConcurrentDeleter(ref Key key, ref Value value, ref RecordInfo recordInfo, ref DeleteInfo deleteInfo, out EphemeralLockResult lockResult)
            {
                lockResult = EphemeralLockResult.Success;       // Ephemeral locking is not used with Lockable contexts
                if (!_clientSession.functions.ConcurrentDeleter(ref key, ref value, ref deleteInfo))
                    return false;
                recordInfo.SetDirtyAndModified();
                recordInfo.SetTombstone();
                return true;
            }
            #endregion IFunctions - Deletes

            #region IFunctions - Dispose
            public void DisposeSingleWriter(ref Key key, ref Input input, ref Value src, ref Value dst, ref Output output, ref RecordInfo recordInfo, ref UpsertInfo upsertInfo, WriteReason reason)
                => _clientSession.functions.DisposeSingleWriter(ref key, ref input, ref src, ref dst, ref output, ref upsertInfo, reason);
            public void DisposeCopyUpdater(ref Key key, ref Input input, ref Value oldValue, ref Value newValue, ref Output output, ref RecordInfo recordInfo, ref RMWInfo rmwInfo)
                => _clientSession.functions.DisposeCopyUpdater(ref key, ref input, ref oldValue, ref newValue, ref output, ref rmwInfo);
            public void DisposeInitialUpdater(ref Key key, ref Input input, ref Value value, ref Output output, ref RecordInfo recordInfo, ref RMWInfo rmwInfo)
                => _clientSession.functions.DisposeInitialUpdater(ref key, ref input, ref value, ref output, ref rmwInfo);
            public void DisposeSingleDeleter(ref Key key, ref Value value, ref RecordInfo recordInfo, ref DeleteInfo deleteInfo)
                => _clientSession.functions.DisposeSingleDeleter(ref key, ref value, ref deleteInfo);
            public void DisposeDeserializedFromDisk(ref Key key, ref Value value, ref RecordInfo recordInfo)
                => _clientSession.functions.DisposeDeserializedFromDisk(ref key, ref value);
            #endregion IFunctions - Dispose

            #region IFunctions - Checkpointing
            public void CheckpointCompletionCallback(int sessionID, string sessionName, CommitPoint commitPoint)
            {
                _clientSession.functions.CheckpointCompletionCallback(sessionID, sessionName, commitPoint);
                _clientSession.LatestCommitPoint = commitPoint;
            }
            #endregion IFunctions - Checkpointing

            #region Transient locking
            public bool TryLockTransientExclusive(ref Key key, ref OperationStackContext<Key, Value> stackCtx)
            {
                Debug.Assert(Store.LockTable.IsLockedExclusive(ref key, ref stackCtx.hei),
                            $"Attempting to use a non-XLocked key in a Lockable context (requesting XLock):"
                            + $" XLocked {_clientSession.fht.LockTable.IsLockedExclusive(ref key, ref stackCtx.hei)},"
                            + $" Slocked {_clientSession.fht.LockTable.IsLockedShared(ref key, ref stackCtx.hei)}");
                return true;
            }

            public bool TryLockTransientShared(ref Key key, ref OperationStackContext<Key, Value> stackCtx)
            {
                Debug.Assert(Store.LockTable.IsLocked(ref key, ref stackCtx.hei),
                            $"Attempting to use a non-Locked (S or X) key in a Lockable context (requesting SLock):"
                            + $" XLocked {_clientSession.fht.LockTable.IsLockedExclusive(ref key, ref stackCtx.hei)},"
                            + $" Slocked {_clientSession.fht.LockTable.IsLockedShared(ref key, ref stackCtx.hei)}");
                return true;
            }

            public void UnlockTransientExclusive(ref Key key, ref OperationStackContext<Key, Value> stackCtx)
            {
                Debug.Assert(Store.LockTable.IsLockedExclusive(ref key, ref stackCtx.hei),
                            $"Attempting to unlock a non-XLocked key in a Lockable context (requesting XLock):"
                            + $" XLocked {_clientSession.fht.LockTable.IsLockedExclusive(ref key, ref stackCtx.hei)},"
                            + $" Slocked {_clientSession.fht.LockTable.IsLockedShared(ref key, ref stackCtx.hei)}");
            }

            public void UnlockTransientShared(ref Key key, ref OperationStackContext<Key, Value> stackCtx)
            {
                Debug.Assert(Store.LockTable.IsLockedShared(ref key, ref stackCtx.hei),
                            $"Attempting to use a non-XLocked key in a Lockable context (requesting XLock):"
                            + $" XLocked {_clientSession.fht.LockTable.IsLockedExclusive(ref key, ref stackCtx.hei)},"
                            + $" Slocked {_clientSession.fht.LockTable.IsLockedShared(ref key, ref stackCtx.hei)}");
            }
            #endregion

            #region Internal utilities
            public int GetInitialLength(ref Input input)
                => _clientSession.variableLengthStruct.GetInitialLength(ref input);

            public int GetLength(ref Value t, ref Input input)
                => _clientSession.variableLengthStruct.GetLength(ref t, ref input);

            public IHeapContainer<Input> GetHeapContainer(ref Input input)
            {
                if (_clientSession.inputVariableLengthStruct == default)
                    return new StandardHeapContainer<Input>(ref input);
                return new VarLenHeapContainer<Input>(ref input, _clientSession.inputVariableLengthStruct, _clientSession.fht.hlog.bufferPool);
            }

            public void UnsafeResumeThread() => _clientSession.UnsafeResumeThread();

            public void UnsafeSuspendThread() => _clientSession.UnsafeSuspendThread();

            public bool CompletePendingWithOutputs(out CompletedOutputIterator<Key, Value, Input, Output, Context> completedOutputs, bool wait = false, bool spinWaitForCommit = false)
                => _clientSession.CompletePendingWithOutputs(out completedOutputs, wait, spinWaitForCommit);

            public FasterKV<Key, Value>.FasterExecutionContext<Input, Output, Context> Ctx => this._clientSession.ctx;
            #endregion Internal utilities
        }
        #endregion IFasterSession
    }
}
