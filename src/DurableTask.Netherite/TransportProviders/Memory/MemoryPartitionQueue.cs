﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace DurableTask.Netherite.Emulated
{
    using Microsoft.Extensions.Logging;
    using System;
    using System.IO;
    using System.Threading;

    /// <summary>
    /// Simulates a in-memory queue for delivering events. Used for local testing and debugging.
    /// </summary>
    class MemoryPartitionQueue : MemoryQueue<PartitionEvent, PartitionEvent>, IMemoryQueue<PartitionEvent>
    {
        readonly TransportAbstraction.IPartition partition;

        public MemoryPartitionQueue(TransportAbstraction.IPartition partition, CancellationToken cancellationToken, ILogger logger)
            : base(cancellationToken, $"Part{partition.PartitionId:D2}", logger)
        {
            this.partition = partition;
        }

        protected override PartitionEvent Serialize(PartitionEvent evt)
        {
            return evt;
        }

        protected override PartitionEvent Deserialize(PartitionEvent evt)
        {
            return evt;
        }

        protected override void Deliver(PartitionEvent evt)
        {
            try
            {
                evt.ReceivedTimestamp = this.partition.CurrentTimeMs;

                this.partition.SubmitEvents(new PartitionEvent[] { evt });
            }
            catch (System.Threading.Tasks.TaskCanceledException)
            {
                // this is normal during shutdown
            }
            catch (Exception e)
            {
                this.partition.ErrorHandler.HandleError(nameof(MemoryPartitionQueueWithSerialization), $"Encountered exception while trying to deliver event {evt} id={evt.EventIdString}", e, true, false);
            }
        }
    }
}
