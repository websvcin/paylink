using Newtonsoft.Json;

namespace Api.Models
{
    public class ErrorModel
    {
        [JsonProperty("error")]
        public required string Error { get; set; }
        [JsonProperty("error_description")]
        public required string ErrorDescription { get; set; }
    }
}