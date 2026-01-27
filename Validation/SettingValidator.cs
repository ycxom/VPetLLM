namespace VPetLLM.Validation;

/// <summary>
/// Validator for settings data
/// </summary>
public class SettingValidator
{
    /// <summary>
    /// Validate settings before saving
    /// </summary>
    /// <param name="settings">Settings to validate</param>
    /// <returns>Validation result</returns>
    public ValidationResult Validate(Setting settings)
    {
        var result = new ValidationResult { IsValid = true };

        if (settings == null)
        {
            result.IsValid = false;
            result.Errors.Add("Settings object is null");
            return result;
        }

        // Validate basic properties
        if (string.IsNullOrWhiteSpace(settings.Language))
        {
            result.IsValid = false;
            result.Errors.Add("Language cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(settings.AiName))
        {
            result.IsValid = false;
            result.Errors.Add("AI name cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(settings.UserName))
        {
            result.IsValid = false;
            result.Errors.Add("User name cannot be empty");
        }

        // Validate numeric ranges
        if (settings.MaxLogCount < 0)
        {
            result.IsValid = false;
            result.Errors.Add("MaxLogCount cannot be negative");
        }

        if (settings.SayTimeMultiplier < 0)
        {
            result.IsValid = false;
            result.Errors.Add("SayTimeMultiplier cannot be negative");
        }

        if (settings.SayTimeMin < 0)
        {
            result.IsValid = false;
            result.Errors.Add("SayTimeMin cannot be negative");
        }

        if (settings.HistoryCompressionThreshold < 0)
        {
            result.IsValid = false;
            result.Errors.Add("HistoryCompressionThreshold cannot be negative");
        }

        if (settings.HistoryCompressionTokenThreshold < 0)
        {
            result.IsValid = false;
            result.Errors.Add("HistoryCompressionTokenThreshold cannot be negative");
        }

        if (settings.StreamingBatchWindowMs < 0)
        {
            result.IsValid = false;
            result.Errors.Add("StreamingBatchWindowMs cannot be negative");
        }

        // Validate provider settings
        if (settings.Ollama != null)
        {
            ValidateOllamaSettings(settings.Ollama, result);
        }

        if (settings.OpenAI != null)
        {
            ValidateOpenAISettings(settings.OpenAI, result);
        }

        if (settings.Gemini != null)
        {
            ValidateGeminiSettings(settings.Gemini, result);
        }

        return result;
    }

    private void ValidateOllamaSettings(Setting.OllamaSetting ollama, ValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(ollama.Url))
        {
            result.IsValid = false;
            result.Errors.Add("Ollama URL cannot be empty");
        }

        if (ollama.Temperature < 0 || ollama.Temperature > 2)
        {
            result.IsValid = false;
            result.Errors.Add("Ollama Temperature must be between 0 and 2");
        }

        if (ollama.MaxTokens < 1)
        {
            result.IsValid = false;
            result.Errors.Add("Ollama MaxTokens must be at least 1");
        }
    }

    private void ValidateOpenAISettings(Setting.OpenAISetting openAI, ValidationResult result)
    {
        if (openAI.OpenAINodes != null)
        {
            foreach (var node in openAI.OpenAINodes)
            {
                if (node.Enabled)
                {
                    if (string.IsNullOrWhiteSpace(node.Url))
                    {
                        result.IsValid = false;
                        result.Errors.Add($"OpenAI node '{node.Name}' URL cannot be empty");
                    }

                    if (node.Temperature < 0 || node.Temperature > 2)
                    {
                        result.IsValid = false;
                        result.Errors.Add($"OpenAI node '{node.Name}' Temperature must be between 0 and 2");
                    }

                    if (node.MaxTokens < 1)
                    {
                        result.IsValid = false;
                        result.Errors.Add($"OpenAI node '{node.Name}' MaxTokens must be at least 1");
                    }
                }
            }
        }
    }

    private void ValidateGeminiSettings(Setting.GeminiSetting gemini, ValidationResult result)
    {
        if (gemini.GeminiNodes != null)
        {
            foreach (var node in gemini.GeminiNodes)
            {
                if (node.Enabled)
                {
                    if (string.IsNullOrWhiteSpace(node.Url))
                    {
                        result.IsValid = false;
                        result.Errors.Add($"Gemini node '{node.Name}' URL cannot be empty");
                    }

                    if (node.Temperature < 0 || node.Temperature > 2)
                    {
                        result.IsValid = false;
                        result.Errors.Add($"Gemini node '{node.Name}' Temperature must be between 0 and 2");
                    }

                    if (node.MaxTokens < 1)
                    {
                        result.IsValid = false;
                        result.Errors.Add($"Gemini node '{node.Name}' MaxTokens must be at least 1");
                    }
                }
            }
        }
    }
}
