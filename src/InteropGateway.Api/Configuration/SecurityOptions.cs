using System.ComponentModel.DataAnnotations;

namespace InteropGateway.Api.Configuration;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public bool RequireApiKey { get; set; } = true;

    [Required]
    public string ApiKeyHeaderName { get; set; } = "Ocp-Apim-Subscription-Key";

    public string[] ApiKeys { get; set; } = [];
}
