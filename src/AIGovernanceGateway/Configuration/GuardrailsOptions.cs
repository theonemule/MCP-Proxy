namespace AIGovernanceGateway.Configuration;

/// <summary>Behavior when the guardrail evaluator is unavailable or returns an invalid decision.</summary>
public enum GuardrailFailureMode
{
    /// <summary>Permit the request/response and emit failure telemetry.</summary>
    Allow,
    /// <summary>Refuse the request/response when governance cannot be evaluated reliably.</summary>
    Block
}

/// <summary>Configures model-based input and output governance at the proxy boundary.</summary>
public sealed class GuardrailsOptions
{
    /// <summary>Master switch for model-based governance.</summary>
    public bool Enabled { get; set; }
    /// <summary>Evaluate inbound payloads before they reach MCP/model routing.</summary>
    public bool EvaluateInputs { get; set; } = true;
    /// <summary>Evaluate outbound payloads before they are released to the caller.</summary>
    public bool EvaluateOutputs { get; set; } = true;
    /// <summary>Public model alias used internally as the policy evaluator.</summary>
    public string Model { get; set; } = "";
    /// <summary>Policy instructions supplied to the evaluator for both input and output checks.</summary>
    public string PolicyPrompt { get; set; } =
        "Block content that violates the organization's configured acceptable-use, data-handling, or safety policy. " +
        "Allow content that does not violate the policy.";
    /// <summary>Risk score at or above which content is refused even if the evaluator action is allow.</summary>
    public int BlockThreshold { get; set; } = 70;
    /// <summary>Maximum UTF-8 bytes sent to the evaluator for one input or output.</summary>
    public int MaxEvaluationBytes { get; set; } = 262144;
    /// <summary>Whether inbound payloads larger than the evaluation limit are refused instead of partially evaluated.</summary>
    public bool BlockOversizedInputs { get; set; } = true;
    /// <summary>Maximum response bytes buffered while output governance is enabled.</summary>
    public int MaxBufferedResponseBytes { get; set; } = 4194304;
    /// <summary>Whether oversized governed responses are refused rather than released unevaluated.</summary>
    public bool BlockOversizedResponses { get; set; } = true;
    /// <summary>Controls fail-open versus fail-closed behavior when the evaluator fails.</summary>
    public GuardrailFailureMode FailureMode { get; set; } = GuardrailFailureMode.Allow;
    /// <summary>HTTP status used when an inbound request is refused.</summary>
    public int InputRefusalStatusCode { get; set; } = 403;
    /// <summary>HTTP status used when an outbound response is replaced by a refusal.</summary>
    public int OutputRefusalStatusCode { get; set; } = 403;
    /// <summary>Only these path prefixes are governed. Empty means all application paths.</summary>
    public string[] PathPrefixes { get; set; } = ["/mcp", "/servers", "/models", "/v1"];
}