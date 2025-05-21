using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.InkML;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using spConnectors = LogicApps.Connectors.ServiceProviders;
using azConnectors = LogicApps.Connectors.Managed;
using LogicApps.Connectors.ServiceProviders.AzureBlob;
using LogicApps.Connectors.ServiceProviders.Mq;
using LogicApps.Connectors.ServiceProviders.ServiceBus;

namespace Company.Function.ClaimCheck
{
    public static class ClaimCheckOrchestrator
    {
        [FunctionName("ClaimCheckWorkflow")]
        public static async Task<string> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            try
            {
                var triggerInput = context.GetInput<ClaimCheckInput>();
                log.LogInformation("Starting Claim Check workflow.");

                // Initialize claimID
                string claimId = context.NewGuid().ToString();
                log.LogInformation("Generated claim ID: {claimId}", claimId);

                // Upload blob to storage container
                var uploadBlobInput = new spConnectors.AzureBlob.UploadBlobInput
                {
                    ContainerName = "claim-check-pattern", // This would typically come from parameters
                    BlobName = claimId,
                    Content = triggerInput.Content
                };

                try
                {
                    log.LogInformation($"Uploading blob {uploadBlobInput.BlobName} to container {uploadBlobInput.ContainerName}");

                    var uploadBlobResponse = await context.UploadBlobAsync(
                        connectionId: "AzureBlob",
                        input: uploadBlobInput);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, $"Error uploading blob {uploadBlobInput.BlobName} to container {uploadBlobInput.ContainerName}");
                    throw;
                }
                log.LogInformation("Blob uploaded successfully with claim ID: {claimId}", claimId);

                // Set Claim-Check message
                var claimCheckMessage = new ClaimCheckMessage
                {
                    MessageId = triggerInput.MessageId,
                    ClaimId = claimId,
                    FileName = triggerInput.FileName
                };

                // Send message to Service Bus
                var sendMessageInput = new spConnectors.ServiceBus.SendMessageInput
                {
                    EntityName = "claim-check-pattern", // This would typically come from parameters
                    Message = new spConnectors.ServiceBus.SendMessageInputMessageType
                    {
                        ContentData = JsonSerializer.Serialize(claimCheckMessage),
                        ContentType = "application/json"
                    }

                };

                try
                {
                    log.LogInformation($"Sending message to Service Bus queue {sendMessageInput.EntityName}");
                    var sendMessageResponse = await context.SendMessageAsync(
                        connectionId: "serviceBus",
                        input: sendMessageInput);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, $"Error sending message to Service Bus queue {sendMessageInput.EntityName}");
                    throw;
                }

                return JsonSerializer.Serialize(new { status = "Success", claimId });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error in Claim Check workflow.");
                return JsonSerializer.Serialize(new { status = "Failed", error = ex.Message });
            }
        }


        [FunctionName("ClaimCheckHttpTrigger")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {
            // Parse request content
            var content = await req.Content.ReadAsStringAsync();
            log.LogInformation("Received HTTP request with content: {content}", content);

            ClaimCheckInput workflowInput;
            try
            {
                workflowInput = JsonSerializer.Deserialize<ClaimCheckInput>(content, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to deserialize request body.");
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("Invalid request format.")
                };
            }

            // Start the orchestration
            string instanceId = await starter.StartNewAsync("ClaimCheckWorkflow", workflowInput);
            log.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);

            // Wait for the workflow to complete
            DurableOrchestrationStatus status;
            do
            {
                status = await starter.GetStatusAsync(instanceId);
                await Task.Delay(500); // Wait for 500ms before checking the status again
            } while (status.RuntimeStatus == OrchestrationRuntimeStatus.Running ||
                   status.RuntimeStatus == OrchestrationRuntimeStatus.Pending);

            // Return appropriate response based on workflow status
            if (status.RuntimeStatus == OrchestrationRuntimeStatus.Completed)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(status.Output.ToString(), System.Text.Encoding.UTF8, "application/json")
                };
            }
            else
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("Claim check workflow failed to complete successfully.")
                };
            }
        }
    }

    // Input model based on the workflow JSON schema
    public class ClaimCheckInput
    {
        public string MessageId { get; set; }
        public string FileName { get; set; }
        public string Content { get; set; }
    }

    // Claim-check message model
    public class ClaimCheckMessage
    {
        public string MessageId { get; set; }
        public string ClaimId { get; set; }
        public string FileName { get; set; }
    }

    // Models for activity functions
    public class UploadBlobInput
    {
        public string ContainerName { get; set; }
        public string BlobName { get; set; }
        public string Content { get; set; }
    }

    public class SendServiceBusMessageInput
    {
        public string EntityName { get; set; }
        public ServiceBusMessage Message { get; set; }
    }

    public class ServiceBusMessage
    {
        public string ContentData { get; set; }
        public string ContentType { get; set; }
    }
}