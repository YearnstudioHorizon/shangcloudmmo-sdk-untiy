using System;

namespace ShangCloud.MMO.Api
{
    public class ShangCloudApiException : Exception
    {
        public int StatusCode { get; }
        public string ResponseBody { get; }

        public ShangCloudApiException(int statusCode, string responseBody)
            : base($"Server returned error status: {statusCode}, body: {responseBody}")
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }

        public ShangCloudApiException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
