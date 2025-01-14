﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace DurableTask.Netherite.Faster
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;
    using DurableTask.Core;
    using DurableTask.Core.Common;
    using DurableTask.Core.Tracing;
    using FASTER.core;
    using Microsoft.Azure.Storage.Blob.Protocol;
    using Newtonsoft.Json;

    class FasterKV : TrackedObjectStore
    {
        readonly FasterKV<Key, Value> fht;

        readonly Partition partition;
        readonly BlobManager blobManager;
        readonly CancellationToken terminationToken;
        readonly CacheDebugger cacheDebugger;
        readonly MemoryTracker.CacheTracker cacheTracker;
        readonly LogSettings storelogsettings;
        readonly Stopwatch compactionStopwatch;
        readonly Dictionary<(PartitionReadEvent,Key), double> pendingReads;
        readonly List<IDisposable> sessionsToDisposeOnShutdown;

        TrackedObject[] singletons;
        Task persistSingletonsTask;

        ClientSession<Key, Value, EffectTracker, Output, object, IFunctions<Key, Value, EffectTracker, Output, object>> mainSession;

        double nextHangCheck;
        const int HangCheckPeriod = 30000;
        const int ReadRetryAfter = 20000;
        EffectTracker effectTracker;

        public FasterTraceHelper TraceHelper => this.blobManager.TraceHelper;

        public int PageSizeBits => this.storelogsettings.PageSizeBits;
 
        public FasterKV(Partition partition, BlobManager blobManager, MemoryTracker memoryTracker)
        {
            this.partition = partition;
            this.blobManager = blobManager;
            this.cacheDebugger = partition.Settings.TestHooks?.CacheDebugger;

            partition.ErrorHandler.Token.ThrowIfCancellationRequested();

            this.storelogsettings = blobManager.GetDefaultStoreLogSettings(
                partition.Settings.UseSeparatePageBlobStorage, 
                memoryTracker.MaxCacheSize, 
                partition.Settings.FasterTuningParameters);

            this.fht = new FasterKV<Key, Value>(
                BlobManager.HashTableSize,
                this.storelogsettings,
                blobManager.StoreCheckpointSettings,
                new SerializerSettings<Key, Value>
                {
                    keySerializer = () => new Key.Serializer(),
                    valueSerializer = () => new Value.Serializer(this.StoreStats, partition.TraceHelper, this.cacheDebugger),
                });

            this.cacheTracker = memoryTracker.NewCacheTracker(this, (int) partition.PartitionId, this.cacheDebugger);

            this.pendingReads = new Dictionary<(PartitionReadEvent, Key), double>();
            this.sessionsToDisposeOnShutdown = new List<IDisposable>();

            this.fht.Log.SubscribeEvictions(new EvictionObserver(this));
            this.fht.Log.Subscribe(new ReadonlyObserver(this));

            partition.Assert(this.fht.ReadCache == null, "Unexpected read cache");

            this.terminationToken = partition.ErrorHandler.Token;
            partition.ErrorHandler.OnShutdown += this.Shutdown;

            this.compactionStopwatch = new Stopwatch();
            this.compactionStopwatch.Start();

            this.nextHangCheck = partition.CurrentTimeMs + HangCheckPeriod;

            this.blobManager.TraceHelper.FasterProgress("Constructed FasterKV");
        }

        void Shutdown()
        {
            try
            {
                this.TraceHelper.FasterProgress("Disposing CacheTracker");
                this.cacheTracker?.Dispose();

                foreach (var s in this.sessionsToDisposeOnShutdown)
                {
                    this.TraceHelper.FasterStorageProgress($"Disposing Temporary Session");
                    s.Dispose();
                }

                this.TraceHelper.FasterProgress("Disposing Main Session");
                try
                {
                    this.mainSession?.Dispose();
                }
                catch(OperationCanceledException)
                {
                    // can happen during shutdown
                }

                this.TraceHelper.FasterProgress("Disposing FasterKV");
                this.fht.Dispose();

                this.TraceHelper.FasterProgress($"Disposing Devices");
                this.blobManager.DisposeDevices();

                if (this.blobManager.FaultInjector != null)
                {
                    this.TraceHelper.FasterProgress($"Unregistering from FaultInjector");
                    this.blobManager.FaultInjector.Disposed(this.blobManager);
                }
            }
            catch (Exception e)
            {
                this.blobManager.TraceHelper.FasterStorageError("Disposing FasterKV", e);
            }
        }

        double GetElapsedCompactionMilliseconds()
        {
            double elapsedMs = this.compactionStopwatch.Elapsed.TotalMilliseconds;
            this.compactionStopwatch.Restart();
            return elapsedMs;
        }

        ClientSession<Key, Value, EffectTracker, Output, object, IFunctions<Key, Value, EffectTracker, Output, object>> CreateASession(string id, bool isScan)
        {
            var functions = new Functions(this.partition, this, this.cacheTracker, isScan);
            return this.fht.NewSession(functions, id);
        }

        public IDisposable TrackTemporarySession(ClientSession<Key, Value, EffectTracker, Output, object, IFunctions<Key, Value, EffectTracker, Output, object>> session)
        {
            return new SessionTracker() { Store = this, Session = session };
        }

        class SessionTracker : IDisposable
        {
            public FasterKV Store;
            public ClientSession<Key, Value, EffectTracker, Output, object, IFunctions<Key, Value, EffectTracker, Output, object>> Session;

            public void Dispose()
            {
                if (this.Store.terminationToken.IsCancellationRequested)
                {
                    this.Store.sessionsToDisposeOnShutdown.Add(this.Session);
                }
                else
                {
                    this.Session.Dispose();
                }
            }
        }

        string RandomSuffix() => Guid.NewGuid().ToString().Substring(0, 5);

        public LogAccessor<Key, Value> Log => this.fht?.Log;

        public override void InitMainSession()
        {
            this.singletons = new TrackedObject[TrackedObjectKey.NumberSingletonTypes];
            this.mainSession = this.CreateASession($"main-{this.RandomSuffix()}", false);
            this.cacheTracker.MeasureCacheSize(true);
            this.CheckInvariants();
        }

        public override async Task<(long commitLogPosition, long inputQueuePosition, string inputQueueFingerprint)> RecoverAsync()
        {
            try
            {
                await this.blobManager.FindCheckpointsAsync();

                // recover singletons
                this.blobManager.TraceHelper.FasterProgress($"Recovering Singletons");
                using (var stream = await this.blobManager.RecoverSingletonsAsync())
                {
                    this.singletons = Serializer.DeserializeSingletons(stream);
                }
                foreach (var singleton in this.singletons)
                {
                    singleton.Partition = this.partition;
                }

                // recover Faster
                this.blobManager.TraceHelper.FasterProgress($"Recovering FasterKV");
                await this.fht.RecoverAsync(this.partition.Settings.FasterTuningParameters?.NumPagesToPreload ?? 1, true, -1, this.terminationToken);
                this.mainSession = this.CreateASession($"main-{this.RandomSuffix()}", false);
                this.cacheTracker.MeasureCacheSize(true);
                this.CheckInvariants();

                return (this.blobManager.CheckpointInfo.CommitLogPosition, this.blobManager.CheckpointInfo.InputQueuePosition, this.blobManager.CheckpointInfo.InputQueueFingerprint);
            }
            catch (Exception exception)
                when (this.terminationToken.IsCancellationRequested && !Utils.IsFatal(exception))
            {
                throw new OperationCanceledException("Partition was terminated.", exception, this.terminationToken);
            }
        }

        public override bool CompletePending()
        {
            try
            {
                var result = this.mainSession.CompletePending(false, false);

                if (this.nextHangCheck <= this.partition.CurrentTimeMs)
                {
                    this.RetrySlowReads();
                    this.nextHangCheck = this.partition.CurrentTimeMs + HangCheckPeriod;
                }

                return result;
            }
            catch (Exception exception)
                when (this.terminationToken.IsCancellationRequested && !Utils.IsFatal(exception))
            {
                throw new OperationCanceledException("Partition was terminated.", exception, this.terminationToken);
            }
        }

        public override ValueTask ReadyToCompletePendingAsync(CancellationToken token)
        {
            return this.mainSession.ReadyToCompletePendingAsync(token);
        }

        public override bool TakeFullCheckpoint(long commitLogPosition, long inputQueuePosition, string inputQueueFingerprint, out Guid checkpointGuid)
        {
            try
            {
                this.blobManager.CheckpointInfo.CommitLogPosition = commitLogPosition;
                this.blobManager.CheckpointInfo.InputQueuePosition = inputQueuePosition;
                this.blobManager.CheckpointInfo.InputQueueFingerprint = inputQueueFingerprint;
                if (this.fht.TryInitiateFullCheckpoint(out checkpointGuid, CheckpointType.FoldOver))
                {
                    byte[] serializedSingletons = Serializer.SerializeSingletons(this.singletons);
                    this.persistSingletonsTask = this.blobManager.PersistSingletonsAsync(serializedSingletons, checkpointGuid);
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception exception)
                when (this.terminationToken.IsCancellationRequested && !Utils.IsFatal(exception))
            {
                throw new OperationCanceledException("Partition was terminated.", exception, this.terminationToken);
            }
        }

        public override async ValueTask CompleteCheckpointAsync()
        {
            try
            {
                // workaround for hanging in CompleteCheckpointAsync: use custom thread.
                await RunOnDedicatedThreadAsync("CompleteCheckpointAsync", () => this.fht.CompleteCheckpointAsync(this.terminationToken).AsTask());
                //await this.fht.CompleteCheckpointAsync(this.terminationToken);

                await this.persistSingletonsTask;
            }
            catch (Exception exception)
                when (this.terminationToken.IsCancellationRequested && !Utils.IsFatal(exception))
            {
                throw new OperationCanceledException("Partition was terminated.", exception, this.terminationToken);
            }
        }

        public override async Task RemoveObsoleteCheckpoints()
        {
            await this.blobManager.RemoveObsoleteCheckpoints();
        }

        public async static Task RunOnDedicatedThreadAsync(string name, Func<Task> asyncAction)
        {
            Task<Task> tasktask = new Task<Task>(() => asyncAction());
            var thread = TrackedThreads.MakeTrackedThread(RunTask, name);
                       
            void RunTask() {
                try
                {
                    tasktask.RunSynchronously();
                }
                catch
                {
                }
            }

            thread.Start();
            await await tasktask;
        }

        public override async Task FinalizeCheckpointCompletedAsync(Guid guid)
        {
            await this.blobManager.FinalizeCheckpointCompletedAsync();

            if (this.cacheDebugger == null)
            {
                // update the cache size tracker after each checkpoint, to compensate for inaccuracies in the tracking
                try
                {
                    this.cacheTracker.MeasureCacheSize(false);
                }
                catch (Exception e)
                {
                    this.TraceHelper.FasterStorageError("Measuring CacheSize", e);
                }
            }
        }

        public override Guid? StartIndexCheckpoint()
        {
            try
            {
                if (this.fht.TryInitiateIndexCheckpoint(out var token))
                {
                    this.persistSingletonsTask = Task.CompletedTask;
                    return token;
                }
                else
                {
                    return null;
                }
            }
            catch (Exception exception)
                when (this.terminationToken.IsCancellationRequested && !Utils.IsFatal(exception))
            {
                throw new OperationCanceledException("Partition was terminated.", exception, this.terminationToken);
            }
        }

        public override Guid? StartStoreCheckpoint(long commitLogPosition, long inputQueuePosition, string inputQueueFingerprint, long? shiftBeginAddress)
        {
            try
            {
                this.blobManager.CheckpointInfo.CommitLogPosition = commitLogPosition;
                this.blobManager.CheckpointInfo.InputQueuePosition = inputQueuePosition;
                this.blobManager.CheckpointInfo.InputQueueFingerprint = inputQueueFingerprint;

                if (shiftBeginAddress > this.fht.Log.BeginAddress)
                {
                    this.fht.Log.ShiftBeginAddress(shiftBeginAddress.Value);
                }

                if (this.fht.TryInitiateHybridLogCheckpoint(out var token, CheckpointType.FoldOver))
                {
                    // according to Badrish this ensures proper fencing w.r.t. session
                    this.mainSession.Refresh();

                    byte[] serializedSingletons = Serializer.SerializeSingletons(this.singletons);
                    this.persistSingletonsTask = this.blobManager.PersistSingletonsAsync(serializedSingletons, token);

                    return token;
                }
                else
                {
                    return null;
                }

                throw new InvalidOperationException("Faster refused store checkpoint");
            }
            catch (Exception exception)
                when (this.terminationToken.IsCancellationRequested && !Utils.IsFatal(exception))
            {
                throw new OperationCanceledException("Partition was terminated.", exception, this.terminationToken);
            }
        }

        long MinimalLogSize
        {
            get
            {
                var stats = (StatsState)this.singletons[(int)TrackedObjectKey.Stats.ObjectType];
                return this.fht.Log.FixedRecordSize * stats.InstanceCount * 2;
            }
        }

        public override long? GetCompactionTarget()
        {
            // TODO empiric validation of the heuristics

            var stats = (StatsState) this.singletons[(int)TrackedObjectKey.Stats.ObjectType];
            long actualLogSize = this.fht.Log.TailAddress - this.fht.Log.BeginAddress;
            long minimalLogSize = this.MinimalLogSize;
            long compactionAreaSize = (long)(0.5 * (this.fht.Log.SafeReadOnlyAddress - this.fht.Log.BeginAddress));
            long mutableSectionSize = (this.fht.Log.TailAddress - this.fht.Log.SafeReadOnlyAddress);

            if (actualLogSize > 2 * minimalLogSize            // there must be significant bloat
               && mutableSectionSize < compactionAreaSize)   // the potential size reduction must outweigh the cost of a foldover
            {
                return this.fht.Log.BeginAddress + compactionAreaSize;
            }
            else
            { 
                this.TraceHelper.FasterCompactionProgress(
                    FasterTraceHelper.CompactionProgress.Skipped,
                    "", 
                    this.Log.BeginAddress,
                    this.Log.SafeReadOnlyAddress,
                    this.Log.TailAddress, 
                    minimalLogSize,
                    compactionAreaSize,
                    this.GetElapsedCompactionMilliseconds());

                return null; 
            }
        }

        readonly static SemaphoreSlim maxCompactionThreads = new SemaphoreSlim((Environment.ProcessorCount + 1) / 2);

        public override async Task<long> RunCompactionAsync(long target)
        {
            string id = this.RandomSuffix(); // for tracing purposes
            await maxCompactionThreads.WaitAsync();
            try
            {
                long beginAddressBeforeCompaction = this.Log.BeginAddress;

                this.TraceHelper.FasterCompactionProgress(
                    FasterTraceHelper.CompactionProgress.Started,
                    id,
                    beginAddressBeforeCompaction,
                    this.Log.SafeReadOnlyAddress,
                    this.Log.TailAddress,
                    this.MinimalLogSize,
                    target - this.Log.BeginAddress,
                    this.GetElapsedCompactionMilliseconds());

                var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                var thread = TrackedThreads.MakeTrackedThread(RunCompaction, $"Compaction.{id}"); 
                thread.Start();
                return await tcs.Task;

                void RunCompaction()
                {
                    try
                    {
                        this.blobManager.TraceHelper.FasterProgress($"Compaction {id} started");

                        var session = this.CreateASession($"compaction-{id}", true);
                        using (this.TrackTemporarySession(session))
                        {
                            long compactedUntil = session.Compact(target, CompactionType.Scan);

                            this.TraceHelper.FasterCompactionProgress(
                                FasterTraceHelper.CompactionProgress.Completed,
                                id, 
                                compactedUntil, 
                                this.Log.SafeReadOnlyAddress,
                                this.Log.TailAddress,
                                this.MinimalLogSize,
                                this.Log.BeginAddress - beginAddressBeforeCompaction,
                                this.GetElapsedCompactionMilliseconds());

                            tcs.SetResult(compactedUntil);
                        }
                    }
                    catch (Exception exception)
                        when (this.terminationToken.IsCancellationRequested && !Utils.IsFatal(exception))
                    {
                        tcs.SetException(new OperationCanceledException("Partition was terminated.", exception, this.terminationToken));
                    }
                    catch (Exception e)
                    {
                        tcs.SetException(e);
                    }
                }
            }
            finally
            {
                maxCompactionThreads.Release();
                this.blobManager.TraceHelper.FasterProgress($"Compaction {id} done");
            }
        }

        // perform a query
        public override async Task QueryAsync(PartitionQueryEvent queryEvent, EffectTracker effectTracker)
        {
            try
            {
                var orchestrationStates = this.ScanOrchestrationStates(effectTracker, queryEvent);
                await effectTracker.ProcessQueryResultAsync(queryEvent, orchestrationStates);
            }
            catch (Exception exception)
                when (this.terminationToken.IsCancellationRequested && !Utils.IsFatal(exception))
            {
                throw new OperationCanceledException("Partition was terminated.", exception, this.terminationToken);
            }
        }

        // kick off a prefetch
        public override async Task RunPrefetchSession(IAsyncEnumerable<TrackedObjectKey> keys)
        {
            int maxConcurrency = 500;
            using SemaphoreSlim prefetchSemaphore = new SemaphoreSlim(maxConcurrency);

            Guid sessionId = Guid.NewGuid();
            this.blobManager.TraceHelper.FasterProgress($"PrefetchSession {sessionId} started (maxConcurrency={maxConcurrency})");

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            long numberIssued = 0;
            long numberMisses = 0;
            long numberHits = 0;
            long lastReport = 0;
            void ReportProgress(int elapsedMillisecondsThreshold)
            {
                if (stopwatch.ElapsedMilliseconds - lastReport >= elapsedMillisecondsThreshold)
                {
                    this.blobManager.TraceHelper.FasterProgress(
                        $"FasterKV PrefetchSession {sessionId} elapsed={stopwatch.Elapsed.TotalSeconds:F2}s issued={numberIssued} pending={maxConcurrency-prefetchSemaphore.CurrentCount} hits={numberHits} misses={numberMisses}");
                    lastReport = stopwatch.ElapsedMilliseconds;
                }
            }

            try
            {
                // these are disposed after the prefetch thread is done
                var prefetchSession = this.CreateASession($"prefetch-{this.RandomSuffix()}", false);

                using (this.TrackTemporarySession(prefetchSession))
                {
                    // for each key, issue a prefetch
                    await foreach (TrackedObjectKey key in keys)
                    {
                        // wait for an available prefetch semaphore token
                        while (!await prefetchSemaphore.WaitAsync(50, this.terminationToken))
                        {
                            prefetchSession.CompletePending();
                            ReportProgress(1000);
                        }

                        FasterKV.Key k = key;
                        EffectTracker noInput = null;
                        Output ignoredOutput = default;
                        var status = prefetchSession.Read(ref k, ref noInput, ref ignoredOutput, userContext: prefetchSemaphore, 0);
                        numberIssued++;

                        if (status.IsCompletedSuccessfully)
                        {
                            numberHits++;
                            prefetchSemaphore.Release();
                        }
                        else if (status.IsPending)
                        {
                            // slow path: upon completion
                            numberMisses++;
                        }
                        else
                        {
                            this.partition.ErrorHandler.HandleError(nameof(RunPrefetchSession), $"FASTER reported ERROR status 0x{status.Value:X2}", null, true, this.partition.ErrorHandler.IsTerminated);
                        }

                        this.terminationToken.ThrowIfCancellationRequested();
                        prefetchSession.CompletePending();
                        ReportProgress(1000);
                    }

                    ReportProgress(0);
                    this.blobManager.TraceHelper.FasterProgress($"PrefetchSession {sessionId} is waiting for completion");

                    // all prefetches were issued; now we wait for them all to complete
                    // by acquiring ALL the semaphore tokens
                    for (int i = 0; i < maxConcurrency; i++)
                    {
                        while (!await prefetchSemaphore.WaitAsync(50, this.terminationToken))
                        {
                            prefetchSession.CompletePending();
                            ReportProgress(1000);
                        }
                    }

                    ReportProgress(0);
                }

                this.blobManager.TraceHelper.FasterProgress($"PrefetchSession {sessionId} completed");
            }
            catch (OperationCanceledException) when (this.terminationToken.IsCancellationRequested)
            {
                // partition is terminating
            }
            catch (Exception e) when (!Utils.IsFatal(e))
            {
                this.partition.ErrorHandler.HandleError(nameof(RunPrefetchSession), "PrefetchSession {sessionId} encountered exception", e, false, this.partition.ErrorHandler.IsTerminated);
            }
        }

        // kick off a read of a tracked object on the main session, completing asynchronously if necessary
        public override void Read(PartitionReadEvent readEvent, EffectTracker effectTracker)
        {
            this.partition.Assert(readEvent != null, "null readEvent in ReadAsync");
            try
            {
                if (readEvent.Prefetch.HasValue)
                {
                    this.TryRead(readEvent, effectTracker, readEvent.Prefetch.Value);
                }

                this.TryRead(readEvent, effectTracker, readEvent.ReadTarget);
            }
            catch (Exception exception)
                when (this.terminationToken.IsCancellationRequested && !Utils.IsFatal(exception))
            {
                throw new OperationCanceledException("Partition was terminated.", exception, this.terminationToken);
            }
        }

        void TryRead(PartitionReadEvent readEvent, EffectTracker effectTracker, Key key)
        {
            this.partition.Assert(!key.Val.IsSingleton, "singletons are not read asynchronously");
            Output output = default;
            this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.StartingRead, null, readEvent.EventIdString, 0);
            var status = this.mainSession.Read(ref key, ref effectTracker, ref output, readEvent, 0);

            if (status.IsCompletedSuccessfully)
            {
                // fast path: we hit in the cache and complete the read
                this.StoreStats.HitCount++;
                this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.CompletedRead, null, readEvent.EventIdString, 0);
                var target = status.Found ? output.Read(this, readEvent.EventIdString) : null;
                this.cacheDebugger?.CheckVersionConsistency(key.Val, target, null);
                effectTracker.ProcessReadResult(readEvent, key, target);
            }
            else if (status.IsPending)
            {
                // slow path: read continuation will be called when complete
                this.StoreStats.MissCount++;
                this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.PendingRead, null, readEvent.EventIdString, 0);
                this.effectTracker ??= effectTracker;
                this.partition.Assert(this.effectTracker == effectTracker, "Only one EffectTracker per FasterKV");
                this.pendingReads.Add((readEvent, key), this.partition.CurrentTimeMs);
            }
            else
            {
                this.partition.ErrorHandler.HandleError(nameof(ReadAsync), $"FASTER reported ERROR status 0x{status.Value:X2}", null, true, this.partition.ErrorHandler.IsTerminated);
            }
        }

        void RetrySlowReads()
        {
            double threshold = this.partition.CurrentTimeMs - ReadRetryAfter;
            var toRetry = this.pendingReads.Where(kvp => kvp.Value < threshold).ToList();
            this.TraceHelper.FasterStorageProgress($"HangDetection limit={ReadRetryAfter / 1000:f0}s pending={this.pendingReads.Count} retry={toRetry.Count}");

            //if (toRetry.Count > 0)
            //{ 
            //    this.partition.Assert(toRetry.Count == 0, $"found a hanging read for {toRetry[0].Key.Item2}");
            //}

            foreach (var kvp in toRetry)
            {
                if (this.pendingReads.Remove(kvp.Key))
                {
                    this.TryRead(kvp.Key.Item1, this.effectTracker, kvp.Key.Item2);
                }
            }
        }

        // read a tracked object on the main session and wait for the response (only one of these is executing at a time)
        public override ValueTask<TrackedObject> ReadAsync(Key key, EffectTracker effectTracker)
        {
            this.partition.Assert(key.Val.IsSingleton, "only singletons expected in ReadAsync");
            return new ValueTask<TrackedObject>(this.singletons[(int)key.Val.ObjectType]);
        }

        // create a tracked object on the main session (only one of these is executing at a time)
        public override ValueTask<TrackedObject> CreateAsync(Key key)
        {
            this.partition.Assert(key.Val.IsSingleton, "only singletons expected in CreateAsync");
            try
            {
                TrackedObject newObject = TrackedObjectKey.Factory(key);
                newObject.Partition = this.partition;
                this.singletons[(int)key.Val.ObjectType] = newObject;
                return new ValueTask<TrackedObject>(newObject);
            }
            catch (Exception exception)
                when (this.terminationToken.IsCancellationRequested && !Utils.IsFatal(exception))
            {
                throw new OperationCanceledException("Partition was terminated.", exception, this.terminationToken);
            }
        }

        public async override ValueTask ProcessEffectOnTrackedObject(Key k, EffectTracker tracker)
        {
            try
            {
                if (k.Val.IsSingleton)
                {
                    tracker.ProcessEffectOn(this.singletons[(int)k.Val.ObjectType]);
                }
                else
                {
                    this.cacheDebugger?.Record(k, CacheDebugger.CacheEvent.StartingRMW, null, tracker.CurrentEventId, 0);

                    await this.PerformFasterRMWAsync(k, tracker);

                    this.cacheDebugger?.Record(k, CacheDebugger.CacheEvent.CompletedRMW, null, tracker.CurrentEventId, 0);
                }
            }
            catch (Exception exception)
               when (this.terminationToken.IsCancellationRequested && !Utils.IsFatal(exception))
            {
                throw new OperationCanceledException("Partition was terminated.", exception, this.terminationToken);
            }
        }

        async ValueTask PerformFasterRMWAsync(Key k, EffectTracker tracker)
        {
            int numTries = 10;

            while (true)
            {
                try
                {
                    var rmwAsyncResult = await this.mainSession.RMWAsync(ref k, ref tracker, token: this.terminationToken);

                    bool IsComplete()
                    {
                        if (rmwAsyncResult.Status.IsCompletedSuccessfully)
                        {
                            return true;
                        }
                        else if (rmwAsyncResult.Status.IsPending)
                        {
                            return false;
                        }
                        else
                        {
                            string msg = $"Could not execute RMW in Faster, received status=0x{rmwAsyncResult.Status:X2}";
                            this.cacheDebugger?.Fail(msg, k);
                            throw new FasterException(msg);
                        }
                    }

                    if (IsComplete())
                    {
                        return;
                    }

                    while (true)
                    {
                        this.cacheDebugger?.Record(k, CacheDebugger.CacheEvent.PendingRMW, null, tracker.CurrentEventId, 0);

                        rmwAsyncResult = await rmwAsyncResult.CompleteAsync();

                        if (IsComplete())
                        {
                            return;
                        }

                        if (--numTries == 0)
                        {
                            this.cacheDebugger?.Fail($"Failed to execute RMW in Faster: status={rmwAsyncResult.Status.ToString()}", k);
                            throw new FasterException("Could not complete RMW even after all retries");
                        }
                    }
                }
                catch (Exception exception) when (!Utils.IsFatal(exception))
                {
                    if (--numTries == 0)
                    {
                        this.cacheDebugger?.Fail($"Failed to execute RMW in Faster, encountered exception: {exception}", k);
                        throw;
                    }
                }
            }
        }

        public override ValueTask RemoveKeys(IEnumerable<TrackedObjectKey> keys)
        {
            foreach (var key in keys)
            {
                this.partition.Assert(!key.IsSingleton, "singletons cannot be deleted");
                this.mainSession.Delete(key);
            }
            return default;
        }

        IAsyncEnumerable<OrchestrationState> ScanOrchestrationStates(
            EffectTracker effectTracker,
            PartitionQueryEvent queryEvent)
        {
            var instanceQuery = queryEvent.InstanceQuery;
            string queryId = queryEvent.EventIdString;
            this.partition.EventDetailTracer?.TraceEventProcessingDetail($"starting query {queryId}");

            // we use a separate thread to iterate, since Faster can iterate synchronously only at the moment
            // and we don't want it to block thread pool worker threads
            var channel = Channel.CreateBounded<OrchestrationState>(500);
            var scanThread = TrackedThreads.MakeTrackedThread(RunScan, $"QueryScan-{queryId}");
            scanThread.Start();

            // read from channel until the channel is completed, or an exception is encountered
            return channel.Reader.ReadAllAsync(this.terminationToken);         

            void RunScan()
            {
                try
                {
                    using var _ = EventTraceContext.MakeContext(0, queryId);
                    var session = this.CreateASession($"scan-{queryId}-{this.RandomSuffix()}", true);
                    using (this.TrackTemporarySession(session))
                    {
                        // get the unique set of keys appearing in the log and emit them
                        using var iter1 = session.Iterate();

                        Stopwatch stopwatch = new Stopwatch();
                        stopwatch.Start();
                        long scanned = 0;
                        long deserialized = 0;
                        long matched = 0;
                        long lastReport;
                        void ReportProgress()
                        {
                            this.partition.EventDetailTracer?.TraceEventProcessingDetail(
                                $"query {queryId} scan position={iter1.CurrentAddress} elapsed={stopwatch.Elapsed.TotalSeconds:F2}s scanned={scanned} deserialized={deserialized} matched={matched}");
                            lastReport = stopwatch.ElapsedMilliseconds;

                            if (queryEvent.TimeoutUtc.HasValue && DateTime.UtcNow > queryEvent.TimeoutUtc.Value)
                            {
                                throw new TimeoutException($"Cancelled query {queryId}");
                            }
                        }

                        ReportProgress();

                        while (iter1.GetNext(out RecordInfo recordInfo, out Key key, out Value val) && !recordInfo.Tombstone)
                        {
                            if (stopwatch.ElapsedMilliseconds - lastReport > 5000)
                            {
                                ReportProgress();
                            }

                            if (key.Val.ObjectType == TrackedObjectKey.TrackedObjectType.Instance)
                            {
                                scanned++;
                                //this.partition.EventDetailTracer?.TraceEventProcessingDetail($"found instance {key.InstanceId}");

                                if (string.IsNullOrEmpty(instanceQuery?.InstanceIdPrefix)
                                    || key.Val.InstanceId.StartsWith(instanceQuery.InstanceIdPrefix))
                                {
                                    //this.partition.EventDetailTracer?.TraceEventProcessingDetail($"reading instance {key.InstanceId}");

                                    //this.partition.EventDetailTracer?.TraceEventProcessingDetail($"read instance {key.InstanceId}, is {(val == null ? "null" : val.GetType().Name)}");

                                    InstanceState instanceState;

                                    if (val.Val is byte[] bytes)
                                    {
                                        instanceState = (InstanceState)Serializer.DeserializeTrackedObject(bytes);
                                        deserialized++;
                                    }
                                    else
                                    {
                                        instanceState = (InstanceState)val.Val;
                                    }

                                    // reading the orchestrationState may race with updating the orchestration state
                                    // but it is benign because the OrchestrationState object is immutable
                                    var orchestrationState = instanceState?.OrchestrationState;

                                    if (orchestrationState != null
                                        && instanceQuery.Matches(orchestrationState))
                                    {
                                        matched++;

                                        this.partition.EventDetailTracer?.TraceEventProcessingDetail($"match instance {key.Val.InstanceId}");

                                        var task = channel.Writer.WriteAsync(orchestrationState);

                                        if (!task.IsCompleted)
                                        {
                                            task.AsTask().Wait();
                                        }
                                    }
                                }
                            }
                        }

                        ReportProgress();
                        channel.Writer.Complete();
                    }

                    this.partition.EventDetailTracer?.TraceEventProcessingDetail($"finished query {queryId}");
                }
                catch (Exception exception)
                    when (this.terminationToken.IsCancellationRequested && !Utils.IsFatal(exception))
                {
                    this.partition.EventDetailTracer?.TraceEventProcessingDetail($"cancelled query {queryId} due to partition termination");
                    channel.Writer.TryComplete(new OperationCanceledException("Partition was terminated.", exception, this.terminationToken));
                }
                catch (TimeoutException e)
                {
                    this.partition.EventTraceHelper.TraceEventProcessingWarning($"query {queryId} timed out");
                    channel.Writer.TryComplete(e);
                }
                catch (Exception e)
                {
                    this.partition.EventTraceHelper.TraceEventProcessingWarning($"query {queryId} failed with exception {e}");
                    channel.Writer.TryComplete(e);
                }
            }
        }

        public override void EmitCurrentState(Action<TrackedObjectKey, TrackedObject> emitItem)
        {
            try
            {
                var stringBuilder = new StringBuilder();

                // iterate singletons
                foreach(var key in TrackedObjectKey.GetSingletons())
                {
                    var singleton = this.singletons[(int)key.ObjectType];
                    emitItem(key, singleton);
                }

                var session = this.CreateASession($"emitCurrentState-{this.RandomSuffix()}", true);
                using (this.TrackTemporarySession(session))
                {
                    // iterate histories
                    using (var iter1 = session.Iterate())
                    {
                        while (iter1.GetNext(out RecordInfo recordInfo, out var key, out var value) && !recordInfo.Tombstone)
                        {
                            TrackedObject trackedObject;
                            if (value.Val == null)
                            {
                                trackedObject = null;
                            }
                            else if (value.Val is TrackedObject t)
                            {
                                trackedObject = t;
                            }
                            else if (value.Val is byte[] bytes)
                            {
                                trackedObject = DurableTask.Netherite.Serializer.DeserializeTrackedObject(bytes);
                            }
                            else
                            {
                                throw new InvalidCastException("cannot cast value to TrackedObject");
                            }

                            this.cacheDebugger?.CheckVersionConsistency(key, trackedObject, value.Version);
                            emitItem(key, trackedObject);
                        }
                    }
                }
            }
            catch (Exception exception)
                when (this.terminationToken.IsCancellationRequested && !Utils.IsFatal(exception))
            {
                throw new OperationCanceledException("Partition was terminated.", exception, this.terminationToken);
            }
        }

        public long MemoryUsedWithoutObjects => this.fht.IndexSize * 64 + this.fht.Log.MemorySizeBytes + this.fht.OverflowBucketCount * 64;

        public override (double totalSizeMB, int fillPercentage) CacheSizeInfo {
            get 
            {
                double totalSize = (double)(this.cacheTracker.TrackedObjectSize + this.MemoryUsedWithoutObjects);
                double targetSize = (double) this.cacheTracker.TargetSize;
                int fillPercentage = (int) Math.Round(100 * (totalSize / targetSize));
                double totalSizeMB = Math.Round(100 * totalSize / (1024 * 1024)) / 100;
                return (totalSizeMB, fillPercentage);
            }
        }

        public override void AdjustCacheSize()
        {
            this.cacheTracker.Notify();
        }

        public override void CheckInvariants()
        {
            this.ValidateMemoryTracker();
        }

        public void ValidateMemoryTracker()
        {
            if (this.cacheDebugger == null)
            {
                return; // we only do this when the cache debugger is attached
            }

            long trackedSizeBefore = 0;
            long totalSize = 0;
            Dictionary<TrackedObjectKey, List<(long delta, long address, string desc)>> perKey = null;

            // we now scan the in-memory part of the log and compute the total size, and store, for each key, the list of records found
            this.ScanMemorySection(Init, Iteration);

            void Init()
            {
                trackedSizeBefore = this.cacheTracker.TrackedObjectSize;
                totalSize = 0;
                perKey = new Dictionary<TrackedObjectKey, List<(long delta, long address, string desc)>>();
            }

            void Iteration(RecordInfo recordInfo, Key key, Value value, long currentAddress)
            {
                long delta = key.Val.EstimatedSize;
                if (!recordInfo.Tombstone)
                {
                    delta += value.EstimatedSize;
                }
                Add(key, delta, currentAddress, $"{(recordInfo.Invalid ? "I" : "")}{(recordInfo.Tombstone ? "T" : "")}{delta}@{currentAddress.ToString("x")}");
            }
              
            void Add(TrackedObjectKey key, long delta, long address, string desc)
            {
                perKey.TryGetValue(key, out var current);
                if (current == null)
                {
                    current = perKey[key] = new List<(long delta, long address, string desc)>();
                }       
                current.Add((delta, address, desc));
                totalSize += delta;
            }

            foreach (var k in this.cacheDebugger.Keys)
            {
                if (!perKey.ContainsKey(k))
                {
                    perKey.Add(k, emptyList); // for keys that were not found in memory, the list of records is empty
                }
            }

            long trackedSizeAfter = this.cacheTracker.TrackedObjectSize;
            bool sizeMatches = true;

            // now we compare, for each key, the list of entries found in memory with what the cache debugger is tracking
            foreach (var kvp in perKey)
            {
                sizeMatches = sizeMatches && this.cacheDebugger.CheckSize(kvp.Key, kvp.Value, this.Log.HeadAddress);
            }

            // if the records matched for each key, then the total size should also match
            if (sizeMatches && trackedSizeBefore == trackedSizeAfter && trackedSizeBefore != totalSize)
            {
                this.cacheDebugger.Fail("total size of tracked objects does not match");
            }
        }

        readonly static List<(long delta, long address, string desc)> emptyList = new List<(long delta, long address, string desc)>();

        internal void ScanMemorySection(Action init, Action<RecordInfo, Key, Value, long> iteration, int retries = 3)
        {
            var headAddress = this.fht.Log.HeadAddress;
          
            try
            {
                using var inMemoryIterator = this.fht.Log.Scan(headAddress, this.fht.Log.TailAddress);
                init();
                while (inMemoryIterator.GetNext(out RecordInfo recordInfo, out Key key, out Value value))
                {
                    iteration(recordInfo, key, value, inMemoryIterator.CurrentAddress);
                }
            }
            catch(FASTER.core.FasterException e) when (retries > 0 && e.Message.StartsWith("Iterator address is less than log BeginAddress"))
            {
                this.ScanMemorySection(init, iteration, retries - 1);
            }

            if (this.fht.Log.HeadAddress > headAddress)
            {
                this.ScanMemorySection(init, iteration, retries - 1);
            }
        }

        internal (int numPages, long size, long numRecords) ComputeMemorySize(bool updateCacheDebugger)
        {
            long totalSize = 0;
            long firstPage = 0;
            long numRecords = 0;
            var cacheDebugger = updateCacheDebugger ? this.cacheDebugger : null;
            
            void Init()
            {
                totalSize = 0;
                numRecords = 0;
                firstPage = this.fht.Log.HeadAddress >> this.storelogsettings.PageSizeBits;
                cacheDebugger?.Reset((string instanceId) => this.partition.PartitionFunction(instanceId) == this.partition.PartitionId);
            }

            void Iteration(RecordInfo recordInfo, Key key, Value value, long currentAddress)
            {
                long delta = key.Val.EstimatedSize;
                if (!recordInfo.Tombstone)
                {
                    delta += value.EstimatedSize;
                }
                numRecords++;
                totalSize += delta;
                cacheDebugger?.UpdateSize(key, delta);
            }

            this.ScanMemorySection(Init, Iteration);
  
            long lastPage = this.fht.Log.TailAddress >> this.storelogsettings.PageSizeBits;
            return ((int) (lastPage-firstPage) + 1, totalSize, numRecords);
        }

        public void SetEmptyPageCount(int emptyPageCount)
        {
            this.fht.Log.SetEmptyPageCount(emptyPageCount, true);
        }

        class EvictionObserver : IObserver<IFasterScanIterator<Key, Value>>
        {
            readonly FasterKV store;
            public EvictionObserver(FasterKV store)
            {
                this.store = store;
            }

            public void OnCompleted() { }
            public void OnError(Exception error) { }

            public void OnNext(IFasterScanIterator<Key, Value> iterator)
            {
                long totalSize = 0;
                while (iterator.GetNext(out RecordInfo recordInfo, out Key key, out Value value))
                {
                    long size;
                    if (!recordInfo.Tombstone)
                    {
                        this.store.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.Evict, value.Version, null, iterator.CurrentAddress);
                        size = key.Val.EstimatedSize + value.EstimatedSize;
                    }
                    else
                    {
                        this.store.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.EvictTombstone, null, null, iterator.CurrentAddress);
                        size = key.Val.EstimatedSize;
                    }

                    this.store.cacheDebugger?.UpdateTrackedObjectSize(-size, key, iterator.CurrentAddress);
                    totalSize += size;
                }
                this.store.TraceHelper.FasterStorageProgress($"Evicted until address={iterator.EndAddress}");
                this.store.cacheTracker.OnEviction(totalSize, iterator.EndAddress);
            }
        }

        class ReadonlyObserver : IObserver<IFasterScanIterator<Key, Value>>
        {
            readonly FasterKV store;
            public ReadonlyObserver(FasterKV store)
            {
                this.store = store;
            }

            public void OnCompleted() { }
            public void OnError(Exception error) { }

            public void OnNext(IFasterScanIterator<Key, Value> iterator)
            {
                while (iterator.GetNext(out RecordInfo recordInfo, out Key key, out Value value))
                {
                    if (!recordInfo.Tombstone)
                    {
                        this.store.cacheDebugger?.Record(key, CacheDebugger.CacheEvent.Readonly, value.Version, null, iterator.CurrentAddress);
                    }
                    else
                    {
                        this.store.cacheDebugger?.Record(key, CacheDebugger.CacheEvent.ReadonlyTombstone, null, null, iterator.CurrentAddress);
                    }
                }
            }
        }

        public struct Key : IFasterEqualityComparer<Key>
        {
            public TrackedObjectKey Val;

            public static implicit operator TrackedObjectKey(Key k) => k.Val;
            public static implicit operator Key(TrackedObjectKey k) => new Key() { Val = k };

            public long GetHashCode64(ref Key k)
            {
                unchecked
                {
                    // Compute an FNV hash
                    var hash = 0xcbf29ce484222325ul; // FNV_offset_basis
                    var prime = 0x100000001b3ul; // FNV_prime

                    // hash the kind
                    hash ^= (byte)k.Val.ObjectType;
                    hash *= prime;

                    // hash the instance id, if applicable
                    if (k.Val.InstanceId != null)
                    {
                        for (int i = 0; i < k.Val.InstanceId.Length; i++)
                        {
                            hash ^= k.Val.InstanceId[i];
                            hash *= prime;
                        }
                    }

                    return (long)hash;
                }
            }

            public override string ToString() => this.Val.ToString();

            public bool Equals(ref Key k1, ref Key k2) 
                => k1.Val.ObjectType == k2.Val.ObjectType && k1.Val.InstanceId == k2.Val.InstanceId;

            public class Serializer : BinaryObjectSerializer<Key>
            {
                public override void Deserialize(out Key obj)
                {
                    obj = new Key();
                    obj.Val.Deserialize(this.reader);
                }

                public override void Serialize(ref Key obj) => obj.Val.Serialize(this.writer);
            }
        }

        public struct Value
        {
            public object Val;

            public int Version; // we use this validate consistency of read/write updates in FASTER, it is not otherwise needed

            public static implicit operator Value(TrackedObject v) => new Value() { Val = v };

            public override string ToString() => this.Val.ToString();

            public long EstimatedSize => 8 + (
                this.Val is byte[] bytes ? 40 + bytes.Length :
                this.Val is TrackedObject o ? o.EstimatedSize :
                0);

            public class Serializer : BinaryObjectSerializer<Value>
            {
                readonly StoreStatistics storeStats;
                readonly PartitionTraceHelper traceHelper;
                readonly CacheDebugger cacheDebugger;

                public Serializer(StoreStatistics storeStats, PartitionTraceHelper traceHelper, CacheDebugger cacheDebugger)
                {
                    this.storeStats = storeStats;
                    this.traceHelper = traceHelper;
                    this.cacheDebugger = cacheDebugger;
                }

                public override void Deserialize(out Value obj)
                {
                    int version = this.reader.ReadInt32();
                    int count = this.reader.ReadInt32();
                    byte[] bytes = this.reader.ReadBytes(count); // lazy deserialization - keep as byte array until used
                    obj = new Value { Val = bytes, Version = version};
                    if (this.cacheDebugger != null)
                    {
                        var trackedObject = DurableTask.Netherite.Serializer.DeserializeTrackedObject(bytes);
                        this.cacheDebugger?.Record(trackedObject.Key, CacheDebugger.CacheEvent.DeserializeBytes, version, null, 0);
                    }
                }

                public override void Serialize(ref Value obj)
                {
                    this.writer.Write(obj.Version);
                    if (obj.Val is byte[] serialized)
                    {
                        // We did already serialize this object on the last CopyUpdate. So we can just use the byte array.
                        this.writer.Write(serialized.Length);
                        this.writer.Write(serialized);
                        if (this.cacheDebugger != null)
                        {
                            var trackedObject = DurableTask.Netherite.Serializer.DeserializeTrackedObject(serialized);
                            this.cacheDebugger?.Record(trackedObject.Key, CacheDebugger.CacheEvent.SerializeBytes, obj.Version, null, 0);
                        }
                    }
                    else
                    {
                        TrackedObject trackedObject = (TrackedObject) obj.Val;
                        var bytes = DurableTask.Netherite.Serializer.SerializeTrackedObject(trackedObject);
                        this.storeStats.Serialize++;
                        this.writer.Write(bytes.Length);
                        this.writer.Write(bytes);
                        this.cacheDebugger?.Record(trackedObject.Key, CacheDebugger.CacheEvent.SerializeObject, obj.Version, null, 0);
                    }
                }
            }
        }

        public struct Output
        {
            public object Val;

            public TrackedObject Read(FasterKV store, string eventId)
            {
                if (this.Val == null)
                {
                    return null;
                }
                else if (this.Val is TrackedObject trackedObject)
                {
                    return trackedObject;
                }
                else
                {
                    byte[] bytes = this.Val as byte[];
                    store.partition.Assert(bytes != null, "unexpected type in Output.Read");
                    trackedObject = DurableTask.Netherite.Serializer.DeserializeTrackedObject(bytes);
                    store.StoreStats.Deserialize++;
                    store.cacheDebugger?.Record(trackedObject.Key, CacheDebugger.CacheEvent.DeserializeObject, null, eventId, 0);
                    trackedObject.Partition = store.partition;
                    return trackedObject;
                }
            }
        }

        public class Functions : IFunctions<Key, Value, EffectTracker, Output, object>
        {
            readonly Partition partition;
            readonly FasterKV store;
            readonly StoreStatistics stats;
            readonly CacheDebugger cacheDebugger;
            readonly MemoryTracker.CacheTracker cacheTracker;
            readonly bool isScan;

            public Functions(Partition partition, FasterKV store, MemoryTracker.CacheTracker cacheTracker, bool isScan)
            {
                this.partition = partition;
                this.store = store;
                this.stats = store.StoreStats;
                this.cacheDebugger = partition.Settings.TestHooks?.CacheDebugger;
                this.cacheTracker = cacheTracker;
                this.isScan = isScan;
            }
            
            // for use with ITraceListener on a modified FASTER branch with extra instrumentation
            //public void TraceKey(Key key, string message)
            //{
            //    this.cacheDebugger?.Record(key, CacheDebugger.CacheEvent.Faster, null, message, 0);
            //}
            //public void TraceRequest(Key key, long id, string message)
            //{
            //    this.cacheDebugger?.Record(key, CacheDebugger.CacheEvent.Faster, null, $"{id:D10}-{message}", 0);
            //    this.store.TraceHelper.FasterStorageProgress($"FASTER: {id:D10}-{message} key={key}");
            //}
            //public void Trace(long id, string message)
            //{
            //    this.store.TraceHelper.FasterStorageProgress($"FASTER: {id:D10}-{message}");
            //}

            bool IFunctions<Key, Value, EffectTracker, Output, object>.NeedInitialUpdate(ref Key key, ref EffectTracker input, ref Output output, ref RMWInfo info)
                => true;

            bool IFunctions<Key, Value, EffectTracker, Output, object>.InitialUpdater(ref Key key, ref EffectTracker tracker, ref Value value, ref Output output, ref RMWInfo info)
            {
                this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.InitialUpdate, 0, tracker.CurrentEventId, info.Address);
                this.cacheDebugger?.ValidateObjectVersion(value, key.Val);
                this.cacheDebugger?.CheckVersionConsistency(key.Val, null, value.Version);
                var trackedObject = TrackedObjectKey.Factory(key.Val);
                this.stats.Create++;
                trackedObject.Partition = this.partition;
                value.Val = trackedObject;
                tracker.ProcessEffectOn(trackedObject);
                value.Version++;
                this.cacheDebugger?.UpdateReferenceValue(ref key.Val, trackedObject, value.Version);
                this.stats.Modify++;
                this.partition.Assert(value.Val != null, "null value.Val in InitialUpdater");
                this.partition.Assert(!this.isScan, "InitialUpdater should not be called from scan");
                this.cacheDebugger?.ValidateObjectVersion(value, key.Val);
                return true;
            }

            void IFunctions<Key, Value, EffectTracker, Output, object>.PostInitialUpdater(ref Key key, ref EffectTracker tracker, ref Value value, ref Output output, ref RMWInfo info)
            {
                this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.PostInitialUpdate, value.Version, tracker.CurrentEventId, info.Address);
                // we have inserted a new entry at the tail
                this.cacheTracker.UpdateTrackedObjectSize(key.Val.EstimatedSize + value.EstimatedSize, key, info.Address);
            }

            bool IFunctions<Key, Value, EffectTracker, Output, object>.InPlaceUpdater(ref Key key, ref EffectTracker tracker, ref Value value, ref Output output, ref RMWInfo info)
            {
                this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.InPlaceUpdate, value.Version, tracker.CurrentEventId, info.Address);
                this.cacheDebugger?.ValidateObjectVersion(value, key.Val);
                long sizeBeforeUpdate = value.EstimatedSize;
                if (!(value.Val is TrackedObject trackedObject))
                {
                    var bytes = (byte[])value.Val;
                    this.partition.Assert(bytes != null, "null bytes in InPlaceUpdater");
                    trackedObject = DurableTask.Netherite.Serializer.DeserializeTrackedObject(bytes);
                    this.stats.Deserialize++;
                    value.Val = trackedObject;
                    this.cacheDebugger?.Record(trackedObject.Key, CacheDebugger.CacheEvent.DeserializeObject, value.Version, tracker.CurrentEventId, 0);
                }
                trackedObject.Partition = this.partition;
                this.cacheDebugger?.CheckVersionConsistency(key.Val, trackedObject, value.Version);
                tracker.ProcessEffectOn(trackedObject);
                value.Version++;
                this.cacheDebugger?.UpdateReferenceValue(ref key.Val, trackedObject, value.Version);
                this.stats.Modify++;
                this.partition.Assert(value.Val != null, "null value.Val in InPlaceUpdater");
                this.cacheTracker.UpdateTrackedObjectSize(value.EstimatedSize - sizeBeforeUpdate, key, info.Address);
                this.cacheDebugger?.ValidateObjectVersion(value, key.Val);
                this.partition.Assert(!this.isScan, "InPlaceUpdater should not be called from scan");
                return true;
            }

            bool IFunctions<Key, Value, EffectTracker, Output, object>.NeedCopyUpdate(ref Key key, ref EffectTracker tracker, ref Value value, ref Output output, ref RMWInfo info)
                => true;

            bool IFunctions<Key, Value, EffectTracker, Output, object>.CopyUpdater(ref Key key, ref EffectTracker tracker, ref Value oldValue, ref Value newValue, ref Output output,  ref RMWInfo info)
            {
                this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.CopyUpdate, oldValue.Version, tracker.CurrentEventId, info.Address);
                this.cacheDebugger?.ValidateObjectVersion(oldValue, key.Val);

                if (oldValue.Val is TrackedObject trackedObject)
                {
                    // replace old object with its serialized snapshot
                    long oldValueSizeBefore = oldValue.EstimatedSize;
                    var bytes = DurableTask.Netherite.Serializer.SerializeTrackedObject(trackedObject);
                    this.stats.Serialize++;
                    this.cacheDebugger?.Record(trackedObject.Key, CacheDebugger.CacheEvent.SerializeObject, oldValue.Version, null, 0);
                    oldValue.Val = bytes;
                    this.cacheTracker.UpdateTrackedObjectSize(oldValue.EstimatedSize - oldValueSizeBefore, key, null); // null indicates we don't know the address
                    this.stats.Copy++;
                }
                else
                {
                    // create new object by deserializing old object
                    var bytes = (byte[])oldValue.Val;
                    this.partition.Assert(bytes != null, "null bytes in CopyUpdater");
                    trackedObject = DurableTask.Netherite.Serializer.DeserializeTrackedObject(bytes);
                    this.stats.Deserialize++;
                    trackedObject.Partition = this.partition;
                    this.cacheDebugger?.Record(trackedObject.Key, CacheDebugger.CacheEvent.DeserializeObject, oldValue.Version, tracker.CurrentEventId, 0);
                }

                newValue.Val = trackedObject;
                this.cacheDebugger?.CheckVersionConsistency(key.Val, trackedObject, oldValue.Version);
                tracker.ProcessEffectOn(trackedObject);
                newValue.Version = oldValue.Version + 1;
                this.cacheDebugger?.UpdateReferenceValue(ref key.Val, trackedObject, newValue.Version);
                this.stats.Modify++;
                this.partition.Assert(newValue.Val != null, "null newValue.Val in CopyUpdater");
                this.cacheDebugger?.ValidateObjectVersion(oldValue, key.Val);
                this.cacheDebugger?.ValidateObjectVersion(newValue, key.Val);
                this.partition.Assert(!this.isScan, "CopyUpdater should not be called from scan");
                return true;
            }

            void IFunctions<Key, Value, EffectTracker, Output, object>.PostCopyUpdater(ref Key key, ref EffectTracker tracker, ref Value oldValue, ref Value newValue, ref Output output, ref RMWInfo info)
            {
                // Note: Post operation is called only when cacheDebugger is attached.
                this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.PostCopyUpdate, newValue.Version, tracker.CurrentEventId, info.Address);
                this.cacheTracker.UpdateTrackedObjectSize(key.Val.EstimatedSize + newValue.EstimatedSize, key, info.Address);
            }

            bool IFunctions<Key, Value, EffectTracker, Output, object>.SingleReader(ref Key key, ref EffectTracker tracker, ref Value src, ref Output dst, ref ReadInfo readInfo)
            {
                this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.SingleReader, src.Version, default, readInfo.Address);
                this.cacheDebugger?.ValidateObjectVersion(src, key.Val);
           
                if (src.Val == null)
                {
                    dst.Val = null;
                }
                else if (src.Val is byte[] bytes)
                {
                    var trackedObject = DurableTask.Netherite.Serializer.DeserializeTrackedObject(bytes);
                    this.stats.Deserialize++;
                    this.cacheDebugger?.Record(trackedObject.Key, CacheDebugger.CacheEvent.DeserializeObject, src.Version, null, 0);
                    trackedObject.Partition = this.partition;
                    dst.Val = trackedObject;
                }
                else if (src.Val is TrackedObject trackedObject)
                {
                    if (!this.isScan)
                    {
                        // replace src with a serialized snapshot of the object - it is now read-only since we did a copy-read-to-tail
                        long oldValueSizeBefore = src.EstimatedSize;
                        src.Val = DurableTask.Netherite.Serializer.SerializeTrackedObject(trackedObject);
                        this.stats.Serialize++;
                        this.cacheDebugger?.Record(trackedObject.Key, CacheDebugger.CacheEvent.SerializeObject, src.Version, null, 0);
                        this.cacheTracker.UpdateTrackedObjectSize(src.EstimatedSize - oldValueSizeBefore, key, readInfo.Address);
                        this.stats.Copy++;
                    }
                    dst.Val = trackedObject;
                }

                this.stats.Read++;
                return true;
            }

            bool IFunctions<Key, Value, EffectTracker, Output, object>.ConcurrentReader(ref Key key, ref EffectTracker tracker, ref Value value, ref Output dst, ref ReadInfo readInfo)
            {
                this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.ConcurrentReader, value.Version, default, readInfo.Address);
                this.cacheDebugger?.ValidateObjectVersion(value, key.Val);

                TrackedObject trackedObject = null;
                if (value.Val != null)
                {
                    if (value.Val is byte[] bytes)
                    {
                        this.cacheDebugger?.Fail("Unexpected byte[] state in mutable section");

                        // we should never get here but for robustness we still continue as best as possible
                        trackedObject = DurableTask.Netherite.Serializer.DeserializeTrackedObject(bytes);
                        this.stats.Deserialize++;
                        trackedObject.Partition = this.partition;
                        this.cacheDebugger?.Record(trackedObject.Key, CacheDebugger.CacheEvent.DeserializeObject, value.Version, default, 0);
                    }
                    else
                    {
                        trackedObject = (TrackedObject)value.Val;
                        this.partition.Assert(trackedObject != null, "null trackedObject in Reader");
                    }
                }

                dst.Val = trackedObject;
                this.stats.Read++;
                return true;
            }

            bool IFunctions<Key, Value, EffectTracker, Output, object>.SingleWriter(ref Key key, ref EffectTracker input, ref Value src, ref Value dst, ref Output output, ref UpsertInfo info, WriteReason reason)
            {
                switch (reason)
                {
                    case WriteReason.Upsert:
                        this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.SingleWriterUpsert, src.Version, default, info.Address);
                        if (!this.isScan)
                        {
                            this.cacheDebugger?.Fail("Do not expect SingleWriter-Upsert outside of scans", key);
                        }
                        break;

                    case WriteReason.CopyToReadCache:
                        this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.SingleWriterCopyToReadCache, src.Version, default, info.Address);
                        this.cacheDebugger?.Fail("Do not expect SingleWriter-CopyToReadCache", key);
                        break;

                    case WriteReason.CopyToTail:
                        this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.SingleWriterCopyToTail, src.Version, default, info.Address);
                        break;

                    case WriteReason.Compaction:
                        this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.SingleWriterCompaction, src.Version, default, info.Address);
                        this.cacheTracker.UpdateTrackedObjectSize(key.Val.EstimatedSize + src.EstimatedSize, key, info.Address);
                        break;

                    default:
                        this.cacheDebugger?.Fail("Invalid WriteReason in SingleWriter", key);
                        break;
                }
                dst.Val = output.Val ?? src.Val;
                dst.Version = src.Version;
                this.cacheDebugger?.ValidateObjectVersion(dst, key.Val);
                return true;
            }

            void IFunctions<Key, Value, EffectTracker, Output, object>.PostSingleWriter(ref Key key, ref EffectTracker input, ref Value src, ref Value dst, ref Output output, ref UpsertInfo info, WriteReason reason)
            {
                switch (reason)
                {
                    case WriteReason.Upsert:
                        this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.PostSingleWriterUpsert, src.Version, default, info.Address);
                        if (!this.isScan)
                        {
                            this.cacheDebugger?.Fail("Do not expect PostSingleWriter-Upsert outside of scans", key);
                        }
                        break;

                    case WriteReason.CopyToReadCache:
                        this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.PostSingleWriterCopyToReadCache, src.Version, default, info.Address);
                        this.cacheDebugger?.Fail("Do not expect PostSingleWriter-CopyToReadCache", key);
                        break;

                    case WriteReason.CopyToTail:
                        this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.PostSingleWriterCopyToTail, src.Version, default, info.Address);
                        if (!this.isScan)
                        {
                            this.cacheTracker.UpdateTrackedObjectSize(key.Val.EstimatedSize + dst.EstimatedSize, key, info.Address);
                        }
                        break;

                    case WriteReason.Compaction:
                        this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.PostSingleWriterCompaction, src.Version, default, info.Address);
                        this.cacheDebugger?.Fail("Do not expect PostSingleWriter-Compaction", key);
                        break;

                    default:
                        this.cacheDebugger?.Fail("Invalid WriteReason in PostSingleWriter", key);
                        break;
                }
            }

            bool IFunctions<Key, Value, EffectTracker, Output, object>.SingleDeleter(ref Key key, ref Value value, ref DeleteInfo info)
            {
                this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.SingleDeleter, null, default, info.Address);
                return true;
            }

            void IFunctions<Key, Value, EffectTracker, Output, object>.PostSingleDeleter(ref Key key, ref DeleteInfo info)
            {
                this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.PostSingleDeleter, null, default, info.Address);
                if (!this.isScan)
                {
                    this.cacheTracker.UpdateTrackedObjectSize(key.Val.EstimatedSize, key, info.Address);
                    this.cacheDebugger?.UpdateReferenceValue(ref key.Val, null, 0);
                }
            }

            bool IFunctions<Key, Value, EffectTracker, Output, object>.ConcurrentWriter(ref Key key, ref EffectTracker input, ref Value src, ref Value dst, ref Output output, ref UpsertInfo info)
            {
                this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.ConcurrentWriter, src.Version, default, info.Address);
                if (!this.isScan)
                {
                    this.cacheDebugger?.Fail("Do not expect ConcurrentWriter; all updates are RMW, and SingleWriter is used for CopyToTail", key);
                }
                dst.Val = src.Val;
                dst.Version = src.Version;
                return true;
            }

            bool IFunctions<Key, Value, EffectTracker, Output, object>.ConcurrentDeleter(ref Key key, ref Value value, ref DeleteInfo info)
            {
                this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.ConcurrentDeleter, value.Version, default, info.Address);
                if (!this.isScan)
                {
                    long removed = value.EstimatedSize;

                    // If record is marked invalid (failed to insert), dispose key as well
                    if (info.RecordInfo.Invalid)
                    {
                        removed += key.Val.EstimatedSize;
                    }

                    this.cacheTracker.UpdateTrackedObjectSize(-removed, key, info.Address);
                    this.cacheDebugger?.UpdateReferenceValue(ref key.Val, null, 0);
                }
                return true;
            }

            #region Completion Callbacks

            void IFunctions<Key, Value, EffectTracker, Output, object>.ReadCompletionCallback(ref Key key, ref EffectTracker tracker, ref Output output, object context, Status status, RecordMetadata recordMetadata)
            {
                if (context == null)
                {
                    // no need to take any action here
                }
                else if (tracker == null)
                {
                    // this is a prefetch
                    try
                    {
                        ((SemaphoreSlim)context).Release();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
                else
                {
                    // the result is passed on to the read event
                    var partitionReadEvent = (PartitionReadEvent)context;

                    if (status.IsCompletedSuccessfully)
                    {
                        this.cacheDebugger?.Record(key.Val, CacheDebugger.CacheEvent.CompletedRead, null, partitionReadEvent.EventIdString, recordMetadata.Address);

                        if (this.store.pendingReads.Remove((partitionReadEvent, key)))
                        {
                            tracker.ProcessReadResult(partitionReadEvent, key, output.Read(this.store, partitionReadEvent.EventIdString));
                        }
                    }
                    else if (status.IsPending)
                    {
                        this.partition.ErrorHandler.HandleError("ReadCompletionCallback", $"unexpected FASTER pending status 0x{status.Value:X2}", null, true, false);
                    }
                    else
                    {
                        this.partition.ErrorHandler.HandleError("ReadCompletionCallback", $"FASTER returned error status 0x{status.Value:X2}", null, true, this.partition.ErrorHandler.IsTerminated);
                    }
                }
            }

            void IFunctions<Key, Value, EffectTracker, Output, object>.CheckpointCompletionCallback(int sessionId, string sessionName, CommitPoint commitPoint) { }
            void IFunctions<Key, Value, EffectTracker, Output, object>.RMWCompletionCallback(ref Key key, ref EffectTracker input, ref Output output, object ctx, Status status, RecordMetadata recordMetadata) { }

            #endregion

            void IFunctions<Key, Value, EffectTracker, Output, object>.DisposeSingleWriter(ref Key key, ref EffectTracker input, ref Value src, ref Value dst, ref Output output, ref UpsertInfo upsertInfo, WriteReason reason)
            {
            }

            void IFunctions<Key, Value, EffectTracker, Output, object>.DisposeCopyUpdater(ref Key key, ref EffectTracker input, ref Value oldValue, ref Value newValue, ref Output output, ref RMWInfo rmwInfo)
            {
            }

            void IFunctions<Key, Value, EffectTracker, Output, object>.DisposeInitialUpdater(ref Key key, ref EffectTracker input, ref Value value, ref Output output, ref RMWInfo rmwInfo)
            {
            }

            void IFunctions<Key, Value, EffectTracker, Output, object>.DisposeSingleDeleter(ref Key key, ref Value value, ref DeleteInfo deleteInfo)
            {
            }

            void IFunctions<Key, Value, EffectTracker, Output, object>.DisposeDeserializedFromDisk(ref Key key, ref Value value)
            {
            }
        }
    }
}
