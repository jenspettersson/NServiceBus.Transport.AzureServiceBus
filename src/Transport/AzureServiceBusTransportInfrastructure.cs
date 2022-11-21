﻿namespace NServiceBus.Transport.AzureServiceBus
{
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Azure.Messaging.ServiceBus;
    using Azure.Messaging.ServiceBus.Administration;
    using Transport;

    sealed class AzureServiceBusTransportInfrastructure : TransportInfrastructure
    {
        readonly AzureServiceBusTransport transportSettings;

        readonly ServiceBusAdministrationClient administrationClient;
        readonly NamespacePermissions namespacePermissions;
        readonly MessageSenderRegistry messageSenderRegistry;
        readonly HostSettings hostSettings;
        readonly ServiceBusClient defaultClient;
        readonly (ReceiveSettings receiveSettings, ServiceBusClient client)[] receiveSettingsAndClientPairs;

        public AzureServiceBusTransportInfrastructure(AzureServiceBusTransport transportSettings, HostSettings hostSettings, (ReceiveSettings receiveSettings, ServiceBusClient client)[] receiveSettingsAndClientPairs, ServiceBusClient defaultClient, ServiceBusAdministrationClient administrationClient, NamespacePermissions namespacePermissions)
        {
            this.transportSettings = transportSettings;

            this.hostSettings = hostSettings;
            this.defaultClient = defaultClient;
            this.receiveSettingsAndClientPairs = receiveSettingsAndClientPairs;
            this.administrationClient = administrationClient;
            this.namespacePermissions = namespacePermissions;

            messageSenderRegistry = new MessageSenderRegistry(defaultClient);

            Dispatcher = new MessageDispatcher(messageSenderRegistry, transportSettings.Topology.TopicToPublishTo);
            Receivers = receiveSettingsAndClientPairs.ToDictionary(static settingsAndClient =>
            {
                var (receiveSettings, _) = settingsAndClient;
                return receiveSettings.Id;
            }, settingsAndClient =>
            {
                (ReceiveSettings receiveSettings, ServiceBusClient client) = settingsAndClient;
                return CreateMessagePump(receiveSettings, client);
            });

            WriteStartupDiagnostics(hostSettings.StartupDiagnostic);
        }


        void WriteStartupDiagnostics(StartupDiagnosticEntries startupDiagnostic) =>
            startupDiagnostic.Add("Azure Service Bus transport", new
            {
                transportSettings.Topology,
                EntityMaximumSize = transportSettings.EntityMaximumSize.ToString(),
                EnablePartitioning = transportSettings.EnablePartitioning.ToString(),
                PrefetchMultiplier = transportSettings.PrefetchMultiplier.ToString(),
                PrefetchCount = transportSettings.PrefetchCount?.ToString() ?? "default",
                UseWebSockets = transportSettings.UseWebSockets.ToString(),
                TimeToWaitBeforeTriggeringCircuitBreaker = transportSettings.TimeToWaitBeforeTriggeringCircuitBreaker.ToString(),
                CustomTokenProvider = transportSettings.TokenCredential?.ToString() ?? "default",
                CustomRetryPolicy = transportSettings.RetryPolicyOptions?.ToString() ?? "default"
            });

        IMessageReceiver CreateMessagePump(ReceiveSettings receiveSettings, ServiceBusClient client)
        {
            string receiveAddress = TranslateAddress(receiveSettings.ReceiveAddress);
            return new MessagePump(
                client,
                transportSettings,
                receiveAddress,
                receiveSettings,
                hostSettings.CriticalErrorAction,
                receiveSettings.UsePublishSubscribe
                    ? new SubscriptionManager(receiveAddress, transportSettings, administrationClient,
                        namespacePermissions)
                    : null);
        }

        public override async Task Shutdown(CancellationToken cancellationToken = default)
        {
            if (messageSenderRegistry != null)
            {
                await messageSenderRegistry.Close(cancellationToken).ConfigureAwait(false);
            }

            foreach (var (_, serviceBusClient) in receiveSettingsAndClientPairs)
            {
                await serviceBusClient.DisposeAsync().ConfigureAwait(false);
            }

            await defaultClient.DisposeAsync().ConfigureAwait(false);
        }

        public override string ToTransportAddress(QueueAddress address) => TranslateAddress(address);

        // this can be inlined once the TransportDefinition.ToTransportAddress() has been obsoleted with ERROR
        public static string TranslateAddress(QueueAddress address)
        {
            var queue = new StringBuilder(address.BaseAddress);

            if (address.Discriminator != null)
            {
                queue.Append($"-{address.Discriminator}");
            }

            if (address.Qualifier != null)
            {
                queue.Append($".{address.Qualifier}");
            }

            return queue.ToString();
        }
    }
}