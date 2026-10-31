using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Exam.Services.RabbitMQ
{
    public class RabbitMqEmailConsumer : BackgroundService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<RabbitMqEmailConsumer> _logger;
        private readonly IServiceScopeFactory _scopeFactory;

        public RabbitMqEmailConsumer(
            IConfiguration configuration,
            ILogger<RabbitMqEmailConsumer> logger,
            IServiceScopeFactory scopeFactory)
        {
            _configuration = configuration;
            _logger = logger;
            _scopeFactory = scopeFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[RabbitMQ Consumer] Service started in background.");

            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = new RabbitMqSettings();
                _configuration.GetSection("RabbitMqSettings").Bind(settings);

                if (!settings.Enabled)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                IConnection? connection = null;
                IModel? channel = null;

                try
                {
                    var factory = new ConnectionFactory
                    {
                        HostName = settings.HostName.Trim(),
                        Port = settings.Port,
                        UserName = settings.UserName.Trim(),
                        Password = settings.Password.Trim(),
                        VirtualHost = string.IsNullOrWhiteSpace(settings.VirtualHost) ? "/" : settings.VirtualHost.Trim(),
                        RequestedHeartbeat = TimeSpan.FromSeconds(settings.RequestedHeartbeatSeconds > 0 ? settings.RequestedHeartbeatSeconds : 60),
                        RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
                        AutomaticRecoveryEnabled = true,
                        NetworkRecoveryInterval = TimeSpan.FromSeconds(settings.NetworkRecoveryIntervalSeconds > 0 ? settings.NetworkRecoveryIntervalSeconds : 10)
                    };

                    connection = factory.CreateConnection("Exam_Trainee_Email_Consumer");
                    channel = connection.CreateModel();

                    channel.QueueDeclare(
                        queue: settings.QueueName,
                        durable: true,
                        exclusive: false,
                        autoDelete: false,
                        arguments: null);

                    channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

                    _logger.LogInformation("[RabbitMQ Consumer] Connected to broker at {Host}:{Port}. Actively listening on queue '{Queue}'...",
                        settings.HostName, settings.Port, settings.QueueName);

                    var consumer = new EventingBasicConsumer(channel);
                    consumer.Received += async (sender, ea) =>
                    {
                        try
                        {
                            var body = ea.Body.ToArray();
                            var message = JsonSerializer.Deserialize<EmailNotificationMessage>(body);

                            if (message != null && !string.IsNullOrWhiteSpace(message.To))
                            {
                                using var scope = _scopeFactory.CreateScope();
                                var emailSender = scope.ServiceProvider.GetService<IEmailSender>();
                                if (emailSender != null)
                                {
                                    if (message.AttachmentBytes != null && message.AttachmentBytes.Length > 0)
                                    {
                                        await emailSender.SendEmailWithAttachmentAsync(
                                            message.To, message.Subject, message.BodyHtml, message.AttachmentBytes, message.AttachmentName ?? "Attachment.pdf");
                                    }
                                    else
                                    {
                                        await emailSender.SendEmailAsync(message.To, message.Subject, message.BodyHtml);
                                    }

                                    _logger.LogInformation("[RabbitMQ Consumer] Successfully processed & sent email to '{To}' for event '{Event}' (MsgId: {Id})",
                                        message.To, message.EventType, message.MessageId);
                                }
                            }

                            channel.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError("[RabbitMQ Consumer] Error processing message: {Error}", ex.Message);
                            // Acknowledge to prevent poison message loop if it's unrecoverable
                            channel.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
                        }
                    };

                    channel.BasicConsume(queue: settings.QueueName, autoAck: false, consumer: consumer);

                    // Wait until cancellation or connection closed
                    while (!stoppingToken.IsCancellationRequested && connection.IsOpen && channel.IsOpen)
                    {
                        await Task.Delay(2000, stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("[RabbitMQ Consumer] Waiting for RabbitMQ broker at {Host}:{Port} ({Error}). Will retry in 30 seconds...",
                        settings.HostName, settings.Port, ex.Message);

                    try { channel?.Dispose(); } catch { }
                    try { connection?.Dispose(); } catch { }

                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
                finally
                {
                    try { channel?.Dispose(); } catch { }
                    try { connection?.Dispose(); } catch { }
                }
            }

            _logger.LogInformation("[RabbitMQ Consumer] Background service stopped.");
        }
    }
}
