using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Local runs opt in; release validation must fail when its models are missing.</summary>
internal sealed class NeuralOcrLiveFactAttribute : FactAttribute
{
    internal const string ModelsVariable = "MYCAPTURE_OCR_LIVE_MODELS";
    internal const string RequiredVariable = "MYCAPTURE_REQUIRE_OCR_LIVE";

    public NeuralOcrLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ModelsVariable))
            && Environment.GetEnvironmentVariable(RequiredVariable) != "1")
            Skip = $"Live OCR models are not configured. Set {ModelsVariable}; release validation sets {RequiredVariable}=1 and requires every live test to pass.";
    }
}
