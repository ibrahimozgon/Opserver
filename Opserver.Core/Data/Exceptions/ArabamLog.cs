using System;
using StackExchange.Exceptional;

namespace StackExchange.Opserver.Data.Exceptions
{
    public class ArabamLog
    {
        public int Id { get; set; }
        public DateTime Date { get; set; }
        public string Level { get; set; }
        public string Logger { get; set; }
        public string Message { get; set; }
        public string Exception { get; set; }
        public string MachineName { get; set; }
        public string Url { get; set; }
        public string IpAddress { get; set; }
        public string AuthToken { get; set; }
        public string ApplicationName { get; set; }
        public string ApiKey { get; set; }
        public string AppVersion { get; set; }
        public string UserAgent { get; set; }
    }
    public class ArabamError : Error
    {
        public string AuthToken { get; set; }
        public string AppName { get; set; }
        public string AppVersion { get; set; }
        public string UserAgent { get; set; }
    }
}
