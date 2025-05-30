using System;
using System.Collections.Generic;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;

using spConnectors = LogicApps.Connectors.ServiceProviders;
using azConnectors = LogicApps.Connectors.Managed;
using LogicApps.Connectors.ServiceProviders.AzureBlob;
using LogicApps.Connectors.Managed.Outlook;
using LogicApps.Connectors.ServiceProviders.ServiceBus;
using Azure.Messaging.ServiceBus;

namespace Company.Function.ClaimCheck
{
    public static class ClaimCheckReceiver
    {
        [FunctionName("ClaimCheckReceiveWorkflow")]
        public static async Task<List<string>> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context,
            ILogger log)
        {
            log.LogInformation("ClaimCheckReceiveWorkflow started.");
            var outputs = new List<string>();
            // Get input from trigger
            var messageInput = context.GetInput<ReceiveQueueMessagesOutputItem>();
            log.LogInformation("Received message: {MessageId}", messageInput.MessageId);
            //var messageContent = JsonSerializer.Deserialize<ClaimCheckReceiveMessage>(messageInput.ContentData.ToString());
            try
            {


                // Add to outputs for return
                outputs.Add($"Received message with ID: {messageInput.MessageId}");
                log.LogInformation($"Message ID: {messageInput.MessageId}, Body: {messageInput.ContentData}");

                var claimCheckMessage = JsonSerializer.Deserialize<ClaimCheckReceiveMessage>(messageInput.ContentData.ToString());
                await context.CallActivityAsync("ValidateMessage", claimCheckMessage);
                log.LogInformation("Message validation completed successfully.");

                // Read blob content
                var blobParams = new spConnectors.AzureBlob.ReadBlobInput
                {
                    ContainerName = "claim-check-pattern",  // This would typically come from parameters
                    BlobName = claimCheckMessage.ClaimId.ToString() // Assuming ClaimId is passed in the message; 
                };

                ReadBlobOutput blobOutput = await context.ReadBlobAsync(
                    connectionId: "AzureBlob",
                    input: blobParams);
                log.LogInformation("Successfully read blob content.");

                // Send email with attachment
                var sendEmailV2Input = new azConnectors.Outlook.ClientSendHtmlMessage
                {
                    To = "recipient@example.com", // This would typically come from parameters
                    Subject = $"Message received (ID {messageInput.MessageId})",
                    Body = "<p>This message was received by Logic Apps claim-check pattern. Check attachment.</p>",
                    Attachments = new azConnectors.Outlook.ClientSendAttachment[]
                    {
                        new azConnectors.Outlook.ClientSendAttachment
                        {
                            Name = context.NewGuid().ToString(), // Assuming FileName is passed in the message
                            ContentBytes = Convert.ToBase64String(Encoding.UTF8.GetBytes(blobOutput.Content.ToString()))
                        }

                    }

                };

                await context.SendEmailV2Async(
                    connectionId: "outlook", emailMessage: sendEmailV2Input);
                log.LogInformation("Email sent successfully.");

                // Delete blob
                var deleteBlobParams = new spConnectors.AzureBlob.DeleteBlobInput
                {
                    ContainerName = "claim-check-pattern", // This would typically come from parameters
                    BlobName = claimCheckMessage.ClaimId.ToString() // Assuming ClaimId is passed in the message
                };

                await context.DeleteBlobAsync(connectionId: "AzureBlob", input: deleteBlobParams);
                log.LogInformation("Blob deleted successfully.");

                // Complete the message

                var completeMessageInput = new spConnectors.ServiceBus.CompleteQueueMessageV2Input
                {
                    LockToken = messageInput.LockToken,
                    QueueName = "claim-check-pattern"
                };
                await context.CompleteQueueMessageV2Async(connectionId: "serviceBus", input: completeMessageInput);
                outputs.Add("Successfully processed claim check message.");

                return outputs;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error in claim check receive workflow.");
                outputs.Add($"Error: {ex.Message}");

                // Get input from trigger
                var abandonMessage = new spConnectors.ServiceBus.AbandonQueueMessageV2Input
                {
                    LockToken = messageInput.LockToken,
                    QueueName = "claim-check-pattern"
                };

                // Abandon the message
                await context.AbandonQueueMessageV2Async("ServiceBus", abandonMessage);

                return outputs;
            }
        }

        [FunctionName("ValidateMessage")]
        public static void ValidateMessage([ActivityTrigger] ClaimCheckReceiveMessage input, ILogger log)
        {
            log.LogInformation("Validating message: MessageId={MessageId}, ClaimId={ClaimId}, FileName={FileName}",
                input.MessageId, input.ClaimId, input.FileName);

            if (string.IsNullOrEmpty(input.MessageId))
                throw new ArgumentException("MessageId cannot be empty");

            if (string.IsNullOrEmpty(input.ClaimId))
                throw new ArgumentException("ClaimId cannot be empty");

            if (string.IsNullOrEmpty(input.FileName))
                throw new ArgumentException("FileName cannot be empty");

            log.LogInformation("Message validation successful");
        }

        [FunctionName("ServiceBusQueueTrigger")]
        public static async Task Run(
            [ServiceBusTrigger("claim-check-pattern", Connection = "serviceBus_connectionString", AutoCompleteMessages = false)] ServiceBusReceivedMessage[] messages,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {
            foreach (var msg in messages)
            {
                log.LogInformation("C# ServiceBus queue trigger function processed message: {MessageId} and LockToken: {LockToken}", msg.MessageId, msg.LockToken);

                try
                {
                    /* this is working convering the ServiceBus message to a ReceiveQueueMessagesOutputItem */
                    // Parse the message content
                    var messageContent = msg.Body.ToString();

                    // Create input for the orchestrator
                    var orchestratorInput = new ReceiveQueueMessagesOutputItem
                    {
                        MessageId = msg.MessageId,
                        ContentData = messageContent,
                        LockToken = msg.LockToken
                    };
                    // Log the input
                    log.LogInformation("Orchestrator input: {OrchestratorInput}", JsonSerializer.Serialize(orchestratorInput));
                    // Start the orchestration
                    string instanceId = await starter.StartNewAsync("ClaimCheckReceiveWorkflow", orchestratorInput);
                    log.LogInformation($"Started claim check receive workflow with ID = {instanceId}");

                    /* doesn't work, the message is not deserialized correctly (ReceiveQueueMessagesOutputItem is is missing lockToken - I think the two models have different serilization)
                    var orchestratorInput = msg;
                    orchestratorInput.ContentData = msg.ContentData.ToString(); // Ensure ContentData is set from the message body
                    log.LogInformation("Orchestrator input: {OrchestratorInput}", JsonSerializer.Serialize(orchestratorInput));
                    // Start the orchestration
                    string instanceId = await starter.StartNewAsync("ClaimCheckReceiveWorkflow", orchestratorInput);
                    log.LogInformation($"Started claim check receive workflow with ID = {instanceId}");
                    */

                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Error processing Service Bus message");
                }
            }
        }
    }

    internal class LocalMessage
    {
        public string MessageId { get; set; }
        public string ContentData { get; set; }
        public string LockToken { get; set; }
    }

    // Input/Output Models

    public class ClaimCheckReceiveMessage
    {
        public string MessageId { get; set; }
        public string ClaimId { get; set; }
        public string FileName { get; set; }
    }


}