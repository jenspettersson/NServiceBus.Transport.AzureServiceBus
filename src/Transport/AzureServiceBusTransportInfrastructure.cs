﻿namespace NServiceBus.Transport.AzureServiceBus
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;
    using DelayedDelivery;
    using Microsoft.Azure.ServiceBus;
    using Microsoft.Azure.ServiceBus.Primitives;
    using Performance.TimeToBeReceived;
    using Routing;
    using Settings;
    using Transport;

    class AzureServiceBusTransportInfrastructure : TransportInfrastructure
    {
        const string defaultTopicName = "bundle-1";
        static readonly Func<string, string> defaultSubscriptionNamingConvention = name => name;
        static readonly Func<Type, string> defaultSubscriptionRuleNamingConvention = type => type.FullName;

        readonly SettingsHolder settings;
        readonly ServiceBusConnectionStringBuilder connectionStringBuilder;
        readonly ITokenProvider tokenProvider;
        readonly string topicName;
        readonly NamespacePermissions namespacePermissions;
        MessageSenderPool messageSenderPool;

        public AzureServiceBusTransportInfrastructure(SettingsHolder settings, string connectionString)
        {
            this.settings = settings;
            connectionStringBuilder = new ServiceBusConnectionStringBuilder(connectionString);

            if (settings.TryGet(SettingsKeys.TransportType, out TransportType transportType))
            {
                connectionStringBuilder.TransportType = transportType;
            }

            settings.TryGet(SettingsKeys.CustomTokenProvider, out tokenProvider);

            if (!settings.TryGet(SettingsKeys.TopicName, out topicName))
            {
                topicName = defaultTopicName;
            }

            namespacePermissions = new NamespacePermissions(connectionStringBuilder, tokenProvider);

            WriteStartupDiagnostics();
        }

        void WriteStartupDiagnostics()
        {
            settings.AddStartupDiagnosticsSection("Azure Service Bus transport", new
            {
                TopicName = settings.TryGet(SettingsKeys.TopicName, out string customTopicName) ? customTopicName : "default",
                EntityMaximumSize = settings.TryGet(SettingsKeys.MaximumSizeInGB, out int entityMaxSize) ? entityMaxSize.ToString() : "default",
                EnablePartitioning = settings.TryGet(SettingsKeys.EnablePartitioning, out bool enablePartitioning) ? enablePartitioning.ToString() : "default",
                SubscriptionNamingConvention = settings.TryGet(SettingsKeys.SubscriptionNamingConvention, out Func<string, string> _) ? "configured" : "default",
                SubscriptionRuleNamingConvention = settings.TryGet(SettingsKeys.SubscriptionRuleNamingConvention, out Func<Type, string> _) ? "configured" : "default",
                PrefetchMultiplier = settings.TryGet(SettingsKeys.PrefetchMultiplier, out int prefetchMultiplier) ? prefetchMultiplier.ToString() : "default",
                PrefetchCount = settings.TryGet(SettingsKeys.PrefetchCount, out int? prefetchCount) ? prefetchCount.ToString() : "default",
                UseWebSockets = settings.TryGet(SettingsKeys.TransportType, out TransportType _) ? "True" : "default",
                TimeToWaitBeforeTriggeringCircuitBreaker = settings.TryGet(SettingsKeys.TransportType, out TimeSpan timeToWait) ? timeToWait.ToString() : "default",
                CustomTokenProvider = settings.TryGet(SettingsKeys.CustomTokenProvider, out ITokenProvider customTokenProvider) ? customTokenProvider.ToString() : "default",
                CustomRetryPolicy = settings.TryGet(SettingsKeys.CustomRetryPolicy, out RetryPolicy customRetryPolicy) ? customRetryPolicy.ToString() : "default"
            });
        }

        public override TransportReceiveInfrastructure ConfigureReceiveInfrastructure()
        {
            return new TransportReceiveInfrastructure(
                () => CreateMessagePump(),
                () => CreateQueueCreator(),
                () => namespacePermissions.CanReceive());
        }

        MessagePump CreateMessagePump()
        {
            if (!settings.TryGet(SettingsKeys.PrefetchMultiplier, out int prefetchMultiplier))
            {
                prefetchMultiplier = 10;
            }

            settings.TryGet(SettingsKeys.PrefetchCount, out int? prefetchCount);

            if (!settings.TryGet(SettingsKeys.TimeToWaitBeforeTriggeringCircuitBreaker, out TimeSpan timeToWaitBeforeTriggeringCircuitBreaker))
            {
                timeToWaitBeforeTriggeringCircuitBreaker = TimeSpan.FromMinutes(2);
            }

            settings.TryGet(SettingsKeys.CustomRetryPolicy, out RetryPolicy retryPolicy);

            return new MessagePump(connectionStringBuilder, tokenProvider, prefetchMultiplier, prefetchCount, timeToWaitBeforeTriggeringCircuitBreaker, retryPolicy);
        }

        QueueCreator CreateQueueCreator()
        {
            if (!settings.TryGet(SettingsKeys.MaximumSizeInGB, out int maximumSizeInGB))
            {
                maximumSizeInGB = 5;
            }

            settings.TryGet(SettingsKeys.EnablePartitioning, out bool enablePartitioning);

            if (!settings.TryGet(SettingsKeys.SubscriptionNamingConvention, out Func<string, string> subscriptionNamingConvention))
            {
                subscriptionNamingConvention = defaultSubscriptionNamingConvention;
            }

            string localAddress;

            try
            {
                localAddress = settings.LocalAddress();
            }
            catch
            {
                // For TransportTests, LocalAddress() will throw. Construct local address manually.
                localAddress = ToTransportAddress(LogicalAddress.CreateLocalAddress(settings.EndpointName(), new Dictionary<string, string>()));
            }

            return new QueueCreator(localAddress, topicName, connectionStringBuilder, tokenProvider, namespacePermissions, maximumSizeInGB * 1024, enablePartitioning, subscriptionNamingConvention);
        }

        public override TransportSendInfrastructure ConfigureSendInfrastructure()
        {
            return new TransportSendInfrastructure(
                () => CreateMessageDispatcher(),
                () => namespacePermissions.CanSend());
        }

        MessageDispatcher CreateMessageDispatcher()
        {
            settings.TryGet(SettingsKeys.CustomRetryPolicy, out RetryPolicy retryPolicy);

            messageSenderPool = new MessageSenderPool(connectionStringBuilder, tokenProvider, retryPolicy);

            return new MessageDispatcher(messageSenderPool, topicName);
        }

        public override TransportSubscriptionInfrastructure ConfigureSubscriptionInfrastructure()
        {
            return new TransportSubscriptionInfrastructure(() => CreateSubscriptionManager());
        }

        SubscriptionManager CreateSubscriptionManager()
        {
            if (!settings.TryGet(SettingsKeys.SubscriptionNamingConvention, out Func<string, string> subscriptionNamingConvention))
            {
                subscriptionNamingConvention = defaultSubscriptionNamingConvention;
            }

            if (!settings.TryGet(SettingsKeys.SubscriptionRuleNamingConvention, out Func<Type, string> subscriptionRuleNamingConvention))
            {
                subscriptionRuleNamingConvention = defaultSubscriptionRuleNamingConvention;
            }

            return new SubscriptionManager(settings.LocalAddress(), topicName, connectionStringBuilder, tokenProvider, namespacePermissions, subscriptionNamingConvention, subscriptionRuleNamingConvention);
        }

        public override EndpointInstance BindToLocalEndpoint(EndpointInstance instance) => instance;

        public override string ToTransportAddress(LogicalAddress logicalAddress)
        {
            var queue = new StringBuilder(logicalAddress.EndpointInstance.Endpoint);

            if (logicalAddress.EndpointInstance.Discriminator != null)
            {
                queue.Append($"-{logicalAddress.EndpointInstance.Discriminator}");
            }

            if (logicalAddress.Qualifier != null)
            {
                queue.Append($".{logicalAddress.Qualifier}");
            }

            return queue.ToString();
        }

        public override async Task Stop()
        {
            if (messageSenderPool != null)
            {
                await messageSenderPool.Close().ConfigureAwait(false);
            }
        }

        public override IEnumerable<Type> DeliveryConstraints => new List<Type>
        {
            typeof(DelayDeliveryWith),
            typeof(DoNotDeliverBefore),
            typeof(DiscardIfNotReceivedBefore)
        };

        public override TransportTransactionMode TransactionMode => TransportTransactionMode.SendsAtomicWithReceive;

        public override OutboundRoutingPolicy OutboundRoutingPolicy => new OutboundRoutingPolicy(OutboundRoutingType.Unicast, OutboundRoutingType.Multicast, OutboundRoutingType.Unicast);
    }
}