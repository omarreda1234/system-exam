using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Exam.Services.RabbitMQ
{
    public class RabbitMqEmailProducer : IRabbitMqEmailProducer, IDisposable
    {
        private readonly RabbitMqSettings _settings;
        private readonly ILogger<RabbitMqEmailProducer> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private IConnection? _connection;
        private IModel? _channel;
        private readonly object _lock = new();
        private bool _disposed;

        public RabbitMqEmailProducer(
            IConfiguration configuration,
            ILogger<RabbitMqEmailProducer> logger,
            IServiceScopeFactory scopeFactory)
        {
            _settings = new RabbitMqSettings();
            configuration.GetSection("RabbitMqSettings").Bind(_settings);
            _logger = logger;
            _scopeFactory = scopeFactory;
        }

        private bool TryEnsureChannel()
        {
            if (!_settings.Enabled) return false;

            if (_channel != null && _channel.IsOpen) return true;

            lock (_lock)
            {
                if (_channel != null && _channel.IsOpen) return true;

                try
                {
                    _channel?.Dispose();
                    _connection?.Dispose();

                    var factory = new ConnectionFactory
                    {
                        HostName = _settings.HostName.Trim(),
                        Port = _settings.Port,
                        UserName = _settings.UserName.Trim(),
                        Password = _settings.Password.Trim(),
                        VirtualHost = string.IsNullOrWhiteSpace(_settings.VirtualHost) ? "/" : _settings.VirtualHost.Trim(),
                        RequestedHeartbeat = TimeSpan.FromSeconds(_settings.RequestedHeartbeatSeconds > 0 ? _settings.RequestedHeartbeatSeconds : 60),
                        RequestedConnectionTimeout = TimeSpan.FromSeconds(2), // Fast 2s timeout for seamless fallback if host is offline
                        AutomaticRecoveryEnabled = true,
                        NetworkRecoveryInterval = TimeSpan.FromSeconds(_settings.NetworkRecoveryIntervalSeconds > 0 ? _settings.NetworkRecoveryIntervalSeconds : 10)
                    };

                    _connection = factory.CreateConnection("Exam_Trainee_Email_Producer");
                    _channel = _connection.CreateModel();

                    _channel.QueueDeclare(
                        queue: _settings.QueueName,
                        durable: true,
                        exclusive: false,
                        autoDelete: false,
                        arguments: null);

                    _logger.LogInformation("[RabbitMQ Producer] Connected successfully to broker at {Host}:{Port}, Queue: {Queue}",
                        _settings.HostName, _settings.Port, _settings.QueueName);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[RabbitMQ Producer] Broker unreachable at {Host}:{Port} ({Error}). System will gracefully use background direct email dispatch.",
                        _settings.HostName, _settings.Port, ex.Message);
                    _channel = null;
                    _connection = null;
                    return false;
                }
            }
        }

        public async Task<bool> EnqueueEmailAsync(EmailNotificationMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.To))
            {
                _logger.LogWarning("[RabbitMQ Producer] Invalid email message payload, skipped.");
                return false;
            }

            try
            {
                if (TryEnsureChannel() && _channel != null)
                {
                    lock (_lock)
                    {
                        var body = JsonSerializer.SerializeToUtf8Bytes(message);
                        var props = _channel.CreateBasicProperties();
                        props.Persistent = true;
                        props.MessageId = message.MessageId;
                        props.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        props.ContentType = "application/json";

                        _channel.BasicPublish(
                            exchange: "",
                            routingKey: _settings.QueueName,
                            basicProperties: props,
                            body: body);
                    }

                    _logger.LogInformation("[RabbitMQ Producer] Published email message {MessageId} to queue '{Queue}' for recipient '{To}' (Event: {Event})",
                        message.MessageId, _settings.QueueName, message.To, message.EventType);

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[RabbitMQ Producer] Error publishing to queue ({Error}). Falling back to direct email sending.", ex.Message);
            }

            // Fallback: If RabbitMQ is not running yet (e.g. dev environment before production deployment to 192.168.1.57),
            // send directly in background so users NEVER lose emails!
            DispatchFallbackEmail(message);
            return true;
        }

        public async Task<int> EnqueueEmailsAsync(IEnumerable<EmailNotificationMessage> messages)
        {
            if (messages == null) return 0;

            int count = 0;
            foreach (var msg in messages)
            {
                if (await EnqueueEmailAsync(msg))
                {
                    count++;
                }
            }
            return count;
        }

        private void DispatchFallbackEmail(EmailNotificationMessage msg)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var emailSender = scope.ServiceProvider.GetService<IEmailSender>();
                    if (emailSender != null)
                    {
                        if (msg.AttachmentBytes != null && msg.AttachmentBytes.Length > 0)
                        {
                            await emailSender.SendEmailWithAttachmentAsync(
                                msg.To, msg.Subject, msg.BodyHtml, msg.AttachmentBytes, msg.AttachmentName ?? "Attachment.pdf");
                        }
                        else
                        {
                            await emailSender.SendEmailAsync(msg.To, msg.Subject, msg.BodyHtml);
                        }
                        _logger.LogInformation("[Fallback Email] Successfully delivered email to {To} via fallback sender.", msg.To);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("[Fallback Email] Failed to deliver email to {To}: {Error}", msg.To, ex.Message);
                }
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _channel?.Close(); _channel?.Dispose(); } catch { }
            try { _connection?.Close(); _connection?.Dispose(); } catch { }
        }
    }
}
