using Microsoft.Extensions.Configuration;

namespace Api.Models
{
    public class JwtSettings
    {
        public bool ValidateIssuerSigningKey { get; set; }
        public string? IssuerSigningKey { get; set; } = null;
        public string? CredetialsSigningKey { get; set; } = null;
        public bool ValidateIssuer { get; set; } = true;
        public string? ValidIssuer { get; set; } = null;
        public bool ValidateAudience { get; set; } = true;
        public string? ValidAudience { get; set; } = null;
        public bool RequireExpirationTime { get; set; }
        public bool ValidateLifetime { get; set; } = true;
        public int TokenLifetime { get; set; } = 3600;
        public int RefreshTokenLifetime { get; set; } = 7;
        public string JwtTokenType { get; set; } = "Bearer";
        public string CompressionAlgorithm { get; set; } = "DEF";
        public string Owner { get; set; } = "https://loginlink.websvc.in/developers";
    }
}
