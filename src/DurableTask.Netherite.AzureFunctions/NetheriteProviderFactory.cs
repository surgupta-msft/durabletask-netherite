﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if !NETCOREAPP2_2
namespace DurableTask.Netherite.AzureFunctions
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using DurableTask.Core;
    using DurableTask.Netherite;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.DurableTask;
    using Microsoft.Azure.WebJobs.Host.Executors;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Newtonsoft.Json;

    public class NetheriteProviderFactory : IDurabilityProviderFactory
    {
        readonly static ConcurrentDictionary<(string taskhub, string storage, string transport), NetheriteProvider> CachedProviders
            = new ConcurrentDictionary<(string taskhub, string storage, string transport), NetheriteProvider>();

        static (string taskhub, string storage, string transport) CacheKey(NetheriteOrchestrationServiceSettings settings)
            => (settings.HubName, settings.StorageConnectionName, settings.EventHubsConnectionName);

        readonly DurableTaskOptions options;
        readonly INameResolver nameResolver;
        readonly IHostIdProvider hostIdProvider;

        readonly bool inConsumption;
        
        // the following are boolean options that can be specified in host.json,
        // but are not passed on to the backend
        public bool TraceToConsole { get; }
        public bool TraceToBlob { get; }
     
        NetheriteProvider defaultProvider;
        ILoggerFactory loggerFactory;

        internal static BlobLogger BlobLogger { get; set; }

        public const string ProviderName = "Netherite";
        public string Name => ProviderName;

        // Called by the Azure Functions runtime dependency injection infrastructure
        public NetheriteProviderFactory(
            IOptions<DurableTaskOptions> extensionOptions,
            ILoggerFactory loggerFactory,
#pragma warning disable CS0618 // Type or member is obsolete
            IConnectionStringResolver connectionStringResolver,
#pragma warning restore CS0618 // Type or member is obsolete
            IHostIdProvider hostIdProvider,
            INameResolver nameResolver,
#pragma warning disable CS0612 // Type or member is obsolete
            IPlatformInformation platformInfo)
#pragma warning restore CS0612 // Type or member is obsolete
        {
            this.options = extensionOptions?.Value ?? throw new ArgumentNullException(nameof(extensionOptions));
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            this.nameResolver = nameResolver ?? throw new ArgumentNullException(nameof(nameResolver));

            this.hostIdProvider = hostIdProvider;
            this.inConsumption = platformInfo.IsInConsumptionPlan();

            bool ReadBooleanSetting(string name) => this.options.StorageProvider.TryGetValue(name, out object objValue)
                && objValue is string stringValue && bool.TryParse(stringValue, out bool boolValue) && boolValue;

            this.TraceToConsole = ReadBooleanSetting(nameof(this.TraceToConsole));
            this.TraceToBlob = ReadBooleanSetting(nameof(this.TraceToBlob));
        }

        NetheriteOrchestrationServiceSettings GetNetheriteOrchestrationServiceSettings(string taskHubNameOverride = null, string connectionName = null)
        {
            var netheriteSettings = new NetheriteOrchestrationServiceSettings();

            // override DTFx defaults to the defaults we want to use in DF
            netheriteSettings.ThrowExceptionOnInvalidDedupeStatus = true;

            // The consumption plan has different performance characteristics so we provide
            // different defaults for key configuration values.
            int maxConcurrentOrchestratorsDefault = this.inConsumption ? 5 : 10 * Environment.ProcessorCount;
            int maxConcurrentActivitiesDefault = this.inConsumption ? 20 : 25 * Environment.ProcessorCount;
            int maxEntityOperationBatchSizeDefault = this.inConsumption ? 50 : 5000;

            // The following defaults are only applied if the customer did not explicitely set them on `host.json`
            this.options.MaxConcurrentOrchestratorFunctions = this.options.MaxConcurrentOrchestratorFunctions ?? maxConcurrentOrchestratorsDefault;
            this.options.MaxConcurrentActivityFunctions = this.options.MaxConcurrentActivityFunctions ?? maxConcurrentActivitiesDefault;
            this.options.MaxEntityOperationBatchSize = this.options.MaxEntityOperationBatchSize ?? maxEntityOperationBatchSizeDefault;

            // copy all applicable fields from both the options and the storageProvider options
            JsonConvert.PopulateObject(JsonConvert.SerializeObject(this.options), netheriteSettings);
            JsonConvert.PopulateObject(JsonConvert.SerializeObject(this.options.StorageProvider), netheriteSettings);
 
            // configure the cache size if not already configured
            netheriteSettings.InstanceCacheSizeMB ??= (this.inConsumption ? 100 : 200 * Environment.ProcessorCount);

            // if worker id is specified in environment, it overrides the configured setting
            string workerId = Environment.GetEnvironmentVariable("WorkerId");
            if (!string.IsNullOrEmpty(workerId))
            {
                if (workerId == "HostId")
                {
                    workerId = this.hostIdProvider.GetHostIdAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                netheriteSettings.WorkerId = workerId;
            }

            netheriteSettings.HubName = this.options.HubName;

            if (taskHubNameOverride != null)
            {
                netheriteSettings.HubName = taskHubNameOverride;
            }

            if (!string.IsNullOrEmpty(connectionName))
            {
                int pos = connectionName.IndexOf(',');
                if (pos == -1 || pos == 0 || pos == connectionName.Length - 1 || pos != connectionName.LastIndexOf(','))
                {
                    throw new ArgumentException("For Netherite, connection name must contain both StorageConnectionName and EventHubsConnectionName, separated by a comma", "connectionName");
                }
                netheriteSettings.StorageConnectionName = connectionName.Substring(0, pos).Trim();
                netheriteSettings.EventHubsConnectionName = connectionName.Substring(pos + 1).Trim();
            }

            string runtimeLanguage = this.nameResolver.Resolve("FUNCTIONS_WORKER_RUNTIME");
            if (runtimeLanguage != null && !string.Equals(runtimeLanguage, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                netheriteSettings.CacheOrchestrationCursors = false; // cannot resume orchestrations in the middle
            }

            netheriteSettings.Validate((name) => this.nameResolver.Resolve(name));

            int randomProbability = 0;
            bool attachFaultInjector =
                (this.options.StorageProvider.TryGetValue("FaultInjectionProbability", out object value)
                && value is string str
                && int.TryParse(str, out randomProbability));        

            bool attachReplayChecker = 
                (this.options.StorageProvider.TryGetValue("AttachReplayChecker", out object setting)
                && setting is string s
                && bool.TryParse(s, out bool x)
                && x);
                    
            bool attachCacheDebugger = 
                (this.options.StorageProvider.TryGetValue("AttachCacheDebugger", out object val2)
                && val2 is string s2
                && bool.TryParse(s2, out bool x2)
                && x2);
                    
            if (attachFaultInjector || attachReplayChecker || attachCacheDebugger)
            {
                netheriteSettings.TestHooks = new TestHooks();

                if (attachFaultInjector)
                {
                    netheriteSettings.TestHooks.FaultInjector = new Faster.FaultInjector() { RandomProbability = randomProbability };
                }
                if (attachReplayChecker)
                {
                    netheriteSettings.TestHooks.ReplayChecker = new Faster.ReplayChecker(netheriteSettings.TestHooks);
                }
                if (attachCacheDebugger)
                {
                    netheriteSettings.TestHooks.CacheDebugger = new Faster.CacheDebugger(netheriteSettings.TestHooks);
                }
            }

            if (this.TraceToConsole || this.TraceToBlob)
            {
                // capture trace events generated in the backend and redirect them to additional sinks
                this.loggerFactory = new LoggerFactoryWrapper(this.loggerFactory, netheriteSettings.HubName, netheriteSettings.WorkerId, this);
            }

            return netheriteSettings;
        }

        NetheriteProvider GetOrCreateProvider(NetheriteOrchestrationServiceSettings settings)
        {
            var key = CacheKey(settings);

            var service = CachedProviders.GetOrAdd(key, _ =>
            {
                if (this.TraceToBlob && BlobLogger == null)
                {
                    BlobLogger = new BlobLogger(settings.ResolvedStorageConnectionString, settings.HubName, settings.WorkerId);
                }

                var service = new NetheriteOrchestrationService(settings, this.loggerFactory);

                service.OnStopping += () => CachedProviders.TryRemove(key, out var _);

                return new NetheriteProvider(service, settings);
            });

            return service;
        }

        /// <inheritdoc/>
        public DurabilityProvider GetDurabilityProvider()
        {
            if (this.defaultProvider == null)
            {
                var settings = this.GetNetheriteOrchestrationServiceSettings();
                this.defaultProvider = this.GetOrCreateProvider(settings);
            }
            return this.defaultProvider;
        }

        // Called by the Durable client binding infrastructure
        public DurabilityProvider GetDurabilityProvider(DurableClientAttribute attribute)
        {
            var connectionName = attribute?.ConnectionName;

            // infer the second part of the connection name if it matches the default provider
            if (connectionName != null && connectionName.IndexOf(",") == -1)
            {
                this.GetDurabilityProvider();
                if (this.defaultProvider.Settings.StorageConnectionName == attribute.ConnectionName 
                    && this.defaultProvider.Settings.HubName == attribute.TaskHub)
                {
                    connectionName = $"{connectionName},{this.defaultProvider.Settings.EventHubsConnectionName}";
                }
            }

            var settings = this.GetNetheriteOrchestrationServiceSettings(attribute?.TaskHub, connectionName);
            return this.GetOrCreateProvider(settings);
        }
    }
}
#endif