using System;

namespace ESAPI_RegistrationQA.Models
{
    public enum QASemaphore { Green, Yellow, Red, NotAvailable }

    public class MetricResult
    {
        public string MetricName { get; set; }
        public double? Value { get; set; }
        public QASemaphore Status { get; set; }
        public string ThresholdCriteria { get; set; }
        public string UnavailableReason { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}