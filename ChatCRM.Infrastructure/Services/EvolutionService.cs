using System.Text;
using System.Text.Json;
using ChatCRM.Application.Chats.DTOs;
using ChatCRM.Application.Interfaces;
using ChatCRM.Domain.Entities;
using ChatCRM.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChatCRM.Infrastructure.Services
{
    public class EvolutionService : IEvolutionService
    {
        private readonly AppDbContext _db;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IHubContext<ChatHub> _hub;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<EvolutionService> _logger;

        public EvolutionService(
            AppDbContext db,
            IHttpClientFactory httpClientFactory,
            IHubContext<ChatHub> hub,
            IWebHostEnvironment env,
            ILogger<EvolutionService> logger)
        {
            _db = db;
            _httpClientFactory = httpClientFactory;
            _hub = hub;
            _env = env;
            _logger = logger;
        }

        public async Task<bool> SendMessageAsync(string instanceName, string phone, string message, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(instanceName))
            {
                _logger.LogError("SendMessageAsync called with empty instanceName.");
                return false;
            }

            try
            {
                var client = _httpClientFactory.CreateClient("Evolution");
                var payload = new { number = phone, text = message };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await client.PostAsync($"/message/sendText/{instanceName}", content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("Evolution API error {Status} for {Instance}: {Body}", response.StatusCode, instanceName, error);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send WhatsApp message to {Phone} via {Instance}", phone, instanceName);
                return false;
            }
        }

        public async Task HandleIncomingWebhookAsync(WebhookPayloadDto payload, CancellationToken cancellationToken = default)
        {
            if (payload.Data?.Key is null || payload.Data.Message is null)
                return;

            if (payload.Data.Key.FromMe)
                return;

            if (string.IsNullOrWhiteSpace(payload.Instance))
            {
                _logger.LogWarning("Webhook missing instance name — cannot route.");
                return;
            }

            // Find the ChatCRM instance that owns this WhatsApp number.
            var instance = await _db.WhatsAppInstances
                .FirstOrDefaultAsync(i => i.InstanceName == payload.Instance, cancellationToken);

            if (instance is null)
            {
                _logger.LogWarning("Webhook for unknown instance {Instance} — dropping.", payload.Instance);
                return;
            }

            var externalId = payload.Data.Key.Id;

            // Deduplicate
            var alreadyProcessed = await _db.Messages
                .AnyAsync(m => m.ExternalId == externalId, cancellationToken);

            if (alreadyProcessed)
                return;

            var rawJid = payload.Data.Key.RemoteJid;
            var phone = rawJid.Split('@')[0];

            var (kind, body, mime, fileName) = ResolveMessagePayload(payload.Data);

            // If we couldn't classify it as anything we render, skip silently.
            // (Reactions, typing, presence, protocol messages, etc. fall through here.)
            if (kind == MessageKind.Text && string.IsNullOrWhiteSpace(body))
                return;

            var sentAt = payload.Data.MessageTimestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(payload.Data.MessageTimestamp).UtcDateTime
                : DateTime.UtcNow;

            // Upsert contact (contacts are global — a person is the same across all lines).
            var contact = await _db.WhatsAppContacts
                .FirstOrDefaultAsync(c => c.PhoneNumber == phone, cancellationToken);

            if (contact is null)
            {
                contact = new WhatsAppContact
                {
                    PhoneNumber = phone,
                    DisplayName = payload.Data.PushName,
                    Country = PhoneCountryDetector.Detect(phone),
                    CreatedAt = DateTime.UtcNow
                };
                _db.WhatsAppContacts.Add(contact);
                await _db.SaveChangesAsync(cancellationToken);
            }
            else if (contact.DisplayName is null && payload.Data.PushName is not null)
            {
                contact.DisplayName = payload.Data.PushName;
            }

            // Hard-block check — if the user has blocked this contact, the message is dropped silently.
            // The contact row stays in the DB so any past chat history is preserved and the agent can
            // unblock later from the Contacts page.
            if (contact.IsBlocked)
            {
                _logger.LogInformation("Dropped inbound message from blocked contact {Phone} (id {ContactId})",
                    phone, contact.Id);
                return;
            }

            // One conversation per (contact, instance).
            var conversation = await _db.Conversations
                .FirstOrDefaultAsync(c => c.ContactId == contact.Id && c.WhatsAppInstanceId == instance.Id, cancellationToken);

            if (conversation is null)
            {
                conversation = new Conversation
                {
                    ContactId = contact.Id,
                    WhatsAppInstanceId = instance.Id,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Conversations.Add(conversation);
                await _db.SaveChangesAsync(cancellationToken);
            }

            var message = new Message
            {
                ConversationId = conversation.Id,
                Body = body ?? string.Empty,
                Direction = MessageDirection.Incoming,
                Status = MessageStatus.Sent,
                ExternalId = externalId,
                SentAt = sentAt,
                Kind = kind,
                MediaMimeType = mime,
                MediaFileName = fileName
            };

            _db.Messages.Add(message);
            conversation.LastMessageAt = sentAt;
            conversation.UnreadCount += 1;

            await _db.SaveChangesAsync(cancellationToken);

            // Fetch + persist the actual media file so it's renderable in the UI.
            if (kind != MessageKind.Text)
            {
                var mediaUrl = await DownloadAndStoreMediaAsync(payload.Instance!, externalId, message.Id, mime, cancellationToken);
                if (mediaUrl is not null)
                {
                    message.MediaUrl = mediaUrl;
                    await _db.SaveChangesAsync(cancellationToken);
                }
            }

            var instanceUnread = await _db.Conversations
                .Where(c => c.WhatsAppInstanceId == instance.Id && !c.IsArchived)
                .SumAsync(c => c.UnreadCount, cancellationToken);

            var instanceChatCount = await _db.Conversations
                .Where(c => c.WhatsAppInstanceId == instance.Id && !c.IsArchived)
                .CountAsync(cancellationToken);

            // Broadcast to the SignalR group for this specific instance.
            await _hub.Clients.Group(ChatHub.InstanceGroupName(instance.Id))
                .SendAsync("ReceiveMessage", new
                {
                    instanceId = instance.Id,
                    instanceUnread,
                    instanceChatCount,
                    conversationId = conversation.Id,
                    contactPhone = contact.PhoneNumber,
                    contactName = contact.DisplayName,
                    message = new
                    {
                        id = message.Id,
                        body = message.Body,
                        direction = (int)message.Direction,
                        sentAt = message.SentAt,
                        kind = (int)message.Kind,
                        mediaUrl = message.MediaUrl,
                        mediaMimeType = message.MediaMimeType,
                        mediaFileName = message.MediaFileName
                    },
                    unreadCount = conversation.UnreadCount
                }, cancellationToken);
        }

        /// <summary>
        /// Inspects the Baileys message envelope and extracts (kind, text, mime, filename).
        /// Text falls back to caption for images/videos/documents so the conversation list
        /// still has something readable as a preview.
        /// </summary>
        private static (MessageKind kind, string? body, string? mime, string? fileName) ResolveMessagePayload(WebhookMessageData data)
        {
            var msg = data.Message!;

            if (!string.IsNullOrEmpty(msg.Conversation))
                return (MessageKind.Text, msg.Conversation, null, null);

            if (msg.ExtendedTextMessage?.Text is { Length: > 0 } extText)
                return (MessageKind.Text, extText, null, null);

            if (msg.ImageMessage is not null)
                return (MessageKind.Image, msg.ImageMessage.Caption, msg.ImageMessage.Mimetype, null);

            if (msg.VideoMessage is not null)
                return (MessageKind.Video, msg.VideoMessage.Caption, msg.VideoMessage.Mimetype, null);

            if (msg.AudioMessage is not null)
                return (MessageKind.Audio, null, msg.AudioMessage.Mimetype, null);

            if (msg.StickerMessage is not null)
                return (MessageKind.Sticker, null, msg.StickerMessage.Mimetype, null);

            if (msg.DocumentMessage is not null)
                return (MessageKind.Document, msg.DocumentMessage.Caption, msg.DocumentMessage.Mimetype, msg.DocumentMessage.FileName);

            // WhatsApp wraps captioned documents in this nested envelope.
            var docInner = msg.DocumentWithCaptionMessage?.Message?.DocumentMessage;
            if (docInner is not null)
                return (MessageKind.Document, docInner.Caption, docInner.Mimetype, docInner.FileName);

            return (MessageKind.Text, null, null, null);
        }

        /// <summary>
        /// Calls Evolution's /chat/getBase64FromMediaMessage/{instance} endpoint to decrypt
        /// the WhatsApp media, then writes it under wwwroot/media/{messageId}.{ext} and
        /// returns the public URL. Returns null on failure (the message row already exists,
        /// so the chat will show a placeholder bubble).
        /// </summary>
        private async Task<string?> DownloadAndStoreMediaAsync(
            string instanceName,
            string externalMessageId,
            int messageId,
            string? mimeType,
            CancellationToken cancellationToken)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("Evolution");
                var requestPayload = new
                {
                    message = new { key = new { id = externalMessageId } },
                    convertToMp4 = false
                };
                var content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json");

                var response = await client.PostAsync($"/chat/getBase64FromMediaMessage/{instanceName}", content, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("Evolution media fetch failed {Status} for msg {Id}: {Err}",
                        response.StatusCode, externalMessageId, err);
                    return null;
                }

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                if (!doc.RootElement.TryGetProperty("base64", out var b64Element))
                {
                    _logger.LogError("Evolution media response missing 'base64' for msg {Id}", externalMessageId);
                    return null;
                }

                var b64 = b64Element.GetString();
                if (string.IsNullOrEmpty(b64))
                    return null;

                var bytes = Convert.FromBase64String(b64);

                var ext = ExtensionForMime(mimeType);
                var rootForMedia = string.IsNullOrWhiteSpace(_env.WebRootPath)
                    ? Path.Combine(_env.ContentRootPath, "wwwroot")
                    : _env.WebRootPath;
                var mediaDir = Path.Combine(rootForMedia, "media");
                Directory.CreateDirectory(mediaDir);

                var fileName = $"{messageId}{ext}";
                var fullPath = Path.Combine(mediaDir, fileName);
                await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);

                return $"/media/{fileName}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download media for message {Id}", externalMessageId);
                return null;
            }
        }

        private static string ExtensionForMime(string? mime)
        {
            if (string.IsNullOrEmpty(mime)) return ".bin";
            var bare = mime.Split(';')[0].Trim().ToLowerInvariant();
            return bare switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "video/mp4" => ".mp4",
                "video/3gpp" => ".3gp",
                "video/webm" => ".webm",
                "audio/ogg" or "audio/ogg; codecs=opus" => ".ogg",
                "audio/mpeg" => ".mp3",
                "audio/mp4" => ".m4a",
                "audio/wav" => ".wav",
                "application/pdf" => ".pdf",
                "application/zip" => ".zip",
                "application/msword" => ".doc",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
                "application/vnd.ms-excel" => ".xls",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
                "text/plain" => ".txt",
                _ => ".bin"
            };
        }
    }
}
