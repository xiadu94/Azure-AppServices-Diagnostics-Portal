﻿using AppLensV3.Helpers;
using AppLensV3.Models;
using AppLensV3.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AppLensV3.Attributes;
using Newtonsoft.Json;
using System.Net.Http;

namespace AppLensV3.Controllers
{
    [Route("api")]
    [FreshChatAuth(true)]
    public class FreshChatController: ControllerBase
    {
        private readonly IDiagnosticClientService _diagnosticClient;
        private readonly IFreshChatClientService _freshChatClientService;

        public FreshChatController(IDiagnosticClientService diagnosticClient, IFreshChatClientService freshChatClientService)
        {
            _diagnosticClient = diagnosticClient;
            _freshChatClientService = freshChatClientService;
        }

        private List<string> ExtractAllTextContent(List<MessagePart> msgParts)
        {
            List<string> textContent = new List<string>();
            if (msgParts != null && msgParts.Count > 0)
            {
                foreach (MessagePart currPart in msgParts)
                {
                    textContent.AddRange(ExtractAllTextContent(currPart));
                }
            }

            return textContent;
        }

        private List<string> ExtractAllTextContent(MessagePart msgPart)
        {
            List<string> textContent = new List<string>();
            if (msgPart.Text != null)
            {
                textContent.Add(_freshChatClientService.RemovePII(msgPart.Text.Content));
            }

            if (msgPart.Collection != null && msgPart.Collection.SubParts.Count > 0)
            {
                foreach (MessagePart currPart in msgPart.Collection.SubParts)
                {
                    textContent.AddRange(ExtractAllTextContent(currPart));
                }
            }

            return textContent;
        }

        private List<string> ExtractAllImageUrls(List<MessagePart> msgParts)
        {
            List<string> imageUrl = new List<string>();

            if (msgParts != null && msgParts.Count > 0)
            {
                foreach (MessagePart currPart in msgParts)
                {
                    imageUrl.AddRange(ExtractAllImageUrls(currPart));
                }
            }

            return imageUrl;
        }

        private List<string> ExtractAllImageUrls(MessagePart msgPart)
        {
            List<string> imageUrl= new List<string>();
            if (msgPart.Image != null)
            {
                imageUrl.Add(msgPart.Image.Url);
            }

            if (msgPart.Collection != null && msgPart.Collection.SubParts.Count > 0)
            {
                foreach (MessagePart currPart in msgPart.Collection.SubParts)
                {
                    imageUrl.AddRange(ExtractAllImageUrls(currPart));
                }
            }

            return imageUrl;
        }

        private async Task<bool> logToKusto(InternalEventBody logMessage)
        {
            try
            {
                string body = JsonConvert.SerializeObject(logMessage);
                var response = await _diagnosticClient.Execute("POST", "/internal/logger", body);
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (JsonSerializationException jsException)
            {
                throw new JsonSerializationException("Failed to serialize data while sengding a request to log in Kusto.", jsException);
            }
            catch (HttpRequestException hException)
            {
                throw new HttpRequestException("Failed to send a to log in Kusto.", hException);
            }
            catch(Exception ex)
            {
                throw new Exception("Unknown exception. Review for more details.", ex);
            }
        }

        /// <summary>
        /// Action for freshchat webhook.
        /// </summary>
        /// <param name="body">Request body.</param>
        /// <returns>Task for handling freshchat webhook.</returns>
        [HttpPost("chatHook")]
        public async Task<IActionResult> ChatHook([FromBody]JToken body)
        {
            if (body != null)
            {
                try
                {
                    DateTime startTime = DateTime.Now;
                    FreshChatPayload incomingPayload = incomingPayload = body.ToObject<FreshChatPayload>();
                    ChatMessageToLog logMsg = new ChatMessageToLog();
                    InternalEventBody kustoLog = new InternalEventBody();
                    switch (incomingPayload.Action)
                    {
                        case Actions.MessageCreate:
                            MessageCreateData msgData = (MessageCreateData) incomingPayload.Data;
                            logMsg.ResourceUri = string.Empty;
                            logMsg.Id = msgData.Message.Id;
                            logMsg.ChannelId = msgData.Message.ChannelId;
                            logMsg.ConversationId = msgData.Message.ConversationId;
                            logMsg.TimeStamp = msgData.Message.CreatedTime;
                            logMsg.MessageType = msgData.Message.MessageType;

                            if (msgData.Message.ActorType == ActorTypes.Agent)
                            {
                                logMsg.Sender = new AgentDetails();
                            }
                            else
                            {
                                logMsg.Sender = new UserDetails();
                            }

                            logMsg.Sender.Id = msgData.Message.ActorId;

                            if (msgData.Message.MsgParts != null && msgData.Message.MsgParts.Count > 0)
                            {
                                logMsg.TextContent = ExtractAllTextContent(msgData.Message.MsgParts);
                                logMsg.ImageUrls = ExtractAllImageUrls(msgData.Message.MsgParts);
                            }

                            if (msgData.Message.ReplyParts != null && msgData.Message.ReplyParts.Count > 0)
                            {
                                logMsg.TextContent = ExtractAllTextContent(msgData.Message.ReplyParts);
                                logMsg.ImageUrls = ExtractAllImageUrls(msgData.Message.ReplyParts);
                            }

                            break;
                        case Actions.ConversationReopen:
                            ReopenDetails reopenData = ((ConvReopenData) incomingPayload.Data).Reopen;
                            logMsg.ResourceUri = string.Empty;
                            logMsg.Id = string.Empty;
                            logMsg.ChannelId = reopenData.Conversation.ChannelId;
                            logMsg.ConversationId = reopenData.Conversation.ChannelId;
                            logMsg.TimeStamp = incomingPayload.ActionTime;
                            logMsg.MessageType = MessageTypes.Private;
                            logMsg.TextContent = new List<string>();
                            if (reopenData.Reopener == ActorTypes.Agent)
                            {
                                logMsg.Sender = new AgentDetails();
                            }
                            else
                            {
                                logMsg.Sender = new UserDetails();
                            }

                            logMsg.Sender.Id = reopenData.ReopenerId;
                            logMsg.TextContent.Add($"Conversation re-opened by {reopenData.Reopener.ToString()}.");
                            break;
                        case Actions.ConversationAssignment:
                            AssignmentDetails assignmentData = ((ConvAssignmentData)incomingPayload.Data).Assignment;
                            logMsg.ResourceUri = string.Empty;
                            logMsg.Id = string.Empty;
                            logMsg.ChannelId = assignmentData.Conversation.ChannelId;
                            logMsg.ConversationId = assignmentData.Conversation.ChannelId;
                            logMsg.TimeStamp = incomingPayload.ActionTime;
                            logMsg.MessageType = MessageTypes.Private;
                            string assignedToDetails = string.Empty;
                            if (string.IsNullOrWhiteSpace(assignmentData.Conversation.AssignedAgentId))
                            {
                                // Conversation assigned to a group and waiting to be assigned to an agent
                                logMsg.Sender = new UserDetails();
                                logMsg.Sender.Id = assignmentData.Conversation.AssignedGroupId;
                                assignedToDetails = " to group";
                            }
                            else
                            {
                                // Conversation assigned to an agent.
                                logMsg.Sender = new AgentDetails();
                                logMsg.Sender.Id = assignmentData.Conversation.AssignedAgentId;
                                assignedToDetails = " to agent";
                            }

                            logMsg.TextContent = new List<string>();
                            logMsg.TextContent.Add($"Conversation assigned {assignedToDetails}.");
                            break;
                        case Actions.ConversationResolution:
                            ResolveDetails resolveData = ((ConvResolutionData) incomingPayload.Data).Resolve;
                            logMsg.ResourceUri = string.Empty;
                            logMsg.Id = string.Empty;
                            logMsg.ChannelId = resolveData.Conversation.ChannelId;
                            logMsg.ConversationId = resolveData.Conversation.ChannelId;
                            logMsg.TimeStamp = incomingPayload.ActionTime;
                            logMsg.MessageType = MessageTypes.Private;
                            if (resolveData.Resolver == ActorTypes.Agent)
                            {
                                logMsg.Sender = new AgentDetails();
                                logMsg.Sender.Id = resolveData.ResolverId;
                            }
                            else
                            {
                                logMsg.Sender = new UserDetails();
                                if (resolveData.Resolver == ActorTypes.User)
                                {
                                    logMsg.Sender.Id = resolveData.ResolverId;
                                }
                                else
                                {
                                    logMsg.Sender.Id = string.Empty;
                                }
                            }

                            logMsg.TextContent = new List<string>();
                            logMsg.TextContent.Add($"Conversation resolved by {resolveData.Resolver.ToString()}.");
                            break;
                        default:
                            kustoLog.EventType = "FreshChatLoggingUnhandledException";

                            // Remove PII function will likely garble the text body and make the object non serializable however it will be helpful for a human to debug the problem.
                            kustoLog.EventContent = $"Unhandled message type {incomingPayload.Action.ToString()}. Unable to handle. Body : {_freshChatClientService.RemovePII(JsonConvert.SerializeObject(body))}";
                            logToKusto(kustoLog);

                            throw new Exception($"Unhandled message type {incomingPayload.Action.ToString()}. Unable to handle.");
                    }

                    logMsg.TimeInMilliSeconds = DateTime.Now.Subtract(startTime).TotalMilliseconds;

                    kustoLog.EventType = "FreshChatLoggingUnhandledException";
                    kustoLog.EventContent = JsonConvert.SerializeObject(logMsg);
                    logToKusto(kustoLog);

                    return Content(JsonConvert.SerializeObject(logMsg));
                }
                catch (JsonSerializationException sException)
                {
                    InternalEventBody kustoLog = new InternalEventBody();
                    kustoLog.EventType = "FreshChatLoggingUnhandledException";

                    // Remove PII function will likely garble the text body and make the object non serializable however it will be helpful for a human to debug the problem.
                    kustoLog.EventContent = JsonConvert.SerializeObject(new JsonSerializationException("Failed to deserialize the incoming data.", sException));
                    logToKusto(kustoLog);

                    throw new JsonSerializationException("Failed to deserialize the incoming data.", sException);
                }
                catch (Exception ex)
                {
                    InternalEventBody kustoLog = new InternalEventBody();
                    kustoLog.EventType = "FreshChatLoggingUnhandledException";

                    // Remove PII function will likely garble the text body and make the object non serializable however it will be helpful for a human to debug the problem.
                    kustoLog.EventContent = JsonConvert.SerializeObject(new ArgumentException("Unknwon error while trying to read the incoming post body ", ex));
                    logToKusto(kustoLog);

                    throw new ArgumentException("Unknwon error while trying to read the incoming post body ", ex);
                }
            }
            else
            {
                return BadRequest("Empty post body");
            }
        }

    }
}