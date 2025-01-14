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
    class MemoryPartitionQueueWithSerialization : MemoryQueue<PartitionEvent, byte[]>, IMemoryQueue<PartitionEvent>
    {
        readonly TransportAbstraction.IPartition partition;

        public MemoryPartitionQueueWithSerialization(TransportAbstraction.IPartition partition, CancellationToken cancellationToken, ILogger logger)
            : base(cancellationToken, $"Part{partition.PartitionId:D2}", logger)
        {
            this.partition = partition;
        }

        protected override byte[] Serialize(PartitionEvent evt)
        {
            var stream = new MemoryStream();
            Packet.Serialize(evt, stream, new byte[16]);
            DurabilityListeners.ConfirmDurable(evt);
            return stream.ToArray();
        }

        protected override PartitionEvent Deserialize(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, false))
            {
                Packet.Deserialize(stream, out PartitionEvent partitionEvent, null);
                return partitionEvent;
            }
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
