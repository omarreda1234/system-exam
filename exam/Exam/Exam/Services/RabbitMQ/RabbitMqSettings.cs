namespace Exam.Services.RabbitMQ
{
    public class RabbitMqSettings
    {
        public string HostName { get; set; } = "192.168.1.57";
        public int Port { get; set; } = 5672;
        public string UserName { get; set; } = "guest";
        public string Password { get; set; } = "guest";
        public string VirtualHost { get; set; } = "/";
        public string QueueName { get; set; } = "trainee_email_queue";
        public bool Enabled { get; set; } = true;
        public int RequestedHeartbeatSeconds { get; set; } = 60;
        public int NetworkRecoveryIntervalSeconds { get; set; } = 10;
    }
}
