# Dapr Pub/Sub — Post 25 Notes
# Distributed Systems with .NET — hyperscale-log-monitoring
#
# Dapr pub/sub is demonstrated in OrderService (/orders/create-dapr endpoint).
# The subscriber endpoint is on OrderService /orders/dapr-subscribe (same process
# for simplicity in Post 25 demonstration).
#
# COMPONENT:       order-pubsub (config/dapr/components/pubsub.yaml)
# LOCAL BROKER:    Redis (started by dapr init)
# PRODUCTION:      Azure Service Bus (change 3 metadata lines in YAML)
#
# PUBLISH:
#   POST /orders/create-dapr → daprClient.PublishEventAsync("order-pubsub", "orders", event)
#
# SUBSCRIBE:
#   GET /dapr/subscribe → sidecar polls for subscription list
#   POST /orders/dapr-subscribe → receives events with .WithTopic("order-pubsub", "orders")
#
# ACK BEHAVIOUR (compare to Service Bus Phase 2):
#   Service Bus: AutoCompleteMessages=false + CompleteMessageAsync (explicit ACK)
#   Dapr pub/sub: return 200 = ACK, return 500 = NACK (retry), return 400 = dead-letter
#   Simpler than Service Bus at the application level; retries managed by Dapr component.
#
# PRODUCTION YAML CHANGE (Redis → Azure Service Bus):
#   spec:
#     type: pubsub.azure.servicebus.topics  # or pubsub.azure.servicebus.queues
#     version: v1
#     metadata:
#     - name: namespaceName
#       value: "your-namespace.servicebus.windows.net"
#     - name: azureClientId
#       value: "managed-identity-client-id"  # DefaultAzureCredential
