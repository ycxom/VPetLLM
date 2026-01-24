namespace VPetLLM.Validation
{
    /// <summary>
    /// Robust validation for all VPet API calls
    /// Ensures parameters meet VPet's calling conventions
    /// </summary>
    public static class ParameterValidator
    {
        // Common animation names that are typically available
        private static readonly string[] CommonAnimations = new[]
        {
            "say", "happy", "sad", "angry", "surprised", "thinking",
            "sleep", "eat", "play", "work", "idle", "move", "nomal"
        };

        /// <summary>
        /// Validate text parameter for VPet API calls
        /// </summary>
        /// <param name="text">Text to validate</param>
        /// <returns>Validation result</returns>
        public static ValidationResult ValidateText(string text)
        {
            try
            {
                if (text is null)
                {
                    Logger.Log("ParameterValidator: Text is null");
                    return ValidationResult.Failure("Text cannot be null");
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    Logger.Log("ParameterValidator: Text is empty or whitespace");
                    return ValidationResult.Failure("Text cannot be empty or contain only whitespace");
                }

                if (text.Length > 10000) // Reasonable limit for bubble text
                {
                    Logger.Log($"ParameterValidator: Text is too long ({text.Length} characters)");
                    var result = ValidationResult.Success();
                    result.AddWarning($"Text is very long ({text.Length} characters). Consider shortening for better display.");
                    return result;
                }

                if (text.Length < 1)
                {
                    Logger.Log("ParameterValidator: Text is too short");
                    return ValidationResult.Failure("Text must contain at least one character");
                }

                Logger.Log($"ParameterValidator: Text validation passed (length: {text.Length})");
                return ValidationResult.Success();
            }
            catch (Exception ex)
            {
                Logger.Log($"ParameterValidator: Error validating text: {ex.Message}");
                return ValidationResult.Failure($"Error validating text: {ex.Message}");
            }
        }

        /// <summary>
        /// Validate animation name parameter
        /// </summary>
        /// <param name="animationName">Animation name to validate (can be null)</param>
        /// <returns>Validation result</returns>
        public static ValidationResult ValidateAnimationName(string animationName)
        {
            try
            {
                // Null animation name is valid (means no animation)
                if (animationName is null)
                {
                    Logger.Log("ParameterValidator: Animation name is null (no animation)");
                    return ValidationResult.Success();
                }

                if (string.IsNullOrWhiteSpace(animationName))
                {
                    Logger.Log("ParameterValidator: Animation name is empty or whitespace");
                    return ValidationResult.Failure("Animation name cannot be empty or contain only whitespace");
                }

                if (animationName.Length > 100) // Reasonable limit for animation names
                {
                    Logger.Log($"ParameterValidator: Animation name is too long ({animationName.Length} characters)");
                    return ValidationResult.Failure($"Animation name is too long ({animationName.Length} characters). Maximum length is 100.");
                }

                // Check for invalid characters
                if (animationName.Any(c => char.IsControl(c) && c != '\t'))
                {
                    Logger.Log("ParameterValidator: Animation name contains invalid control characters");
                    return ValidationResult.Failure("Animation name cannot contain control characters");
                }

                // Warn if animation name is not in common list (but still valid)
                if (!CommonAnimations.Contains(animationName.ToLower()))
                {
                    Logger.Log($"ParameterValidator: Animation name '{animationName}' is not in common list");
                    var result = ValidationResult.Success();
                    result.AddWarning($"Animation '{animationName}' may not be available. Common animations: {string.Join(", ", CommonAnimations)}");
                    return result;
                }

                Logger.Log($"ParameterValidator: Animation name validation passed: '{animationName}'");
                return ValidationResult.Success();
            }
            catch (Exception ex)
            {
                Logger.Log($"ParameterValidator: Error validating animation name: {ex.Message}");
                return ValidationResult.Failure($"Error validating animation name: {ex.Message}");
            }
        }

        /// <summary>
        /// Validate force parameter
        /// </summary>
        /// <param name="force">Force parameter to validate</param>
        /// <returns>Validation result</returns>
        public static ValidationResult ValidateForceParameter(bool force)
        {
            try
            {
                // Boolean parameters are always valid
                Logger.Log($"ParameterValidator: Force parameter validation passed: {force}");
                return ValidationResult.Success();
            }
            catch (Exception ex)
            {
                Logger.Log($"ParameterValidator: Error validating force parameter: {ex.Message}");
                return ValidationResult.Failure($"Error validating force parameter: {ex.Message}");
            }
        }

        /// <summary>
        /// Validate all parameters for VPet Say method call
        /// </summary>
        /// <param name="text">Text parameter</param>
        /// <param name="animationName">Animation name parameter</param>
        /// <param name="force">Force parameter</param>
        /// <returns>Comprehensive validation result</returns>
        public static ValidationResult ValidateAll(string text, string animationName, bool force)
        {
            try
            {
                Logger.Log($"ParameterValidator: Validating all parameters - text length: {text?.Length}, animation: '{animationName}', force: {force}");

                var result = new ValidationResult();
                var fieldErrors = new Dictionary<string, string>();
                var warnings = new List<string>();

                // Validate text
                var textResult = ValidateText(text);
                if (!textResult.IsValid)
                {
                    fieldErrors["text"] = textResult.ErrorMessage;
                    result.IsValid = false;
                }
                if (textResult.Warnings?.Length > 0)
                {
                    warnings.AddRange(textResult.Warnings);
                }

                // Validate animation name
                var animationResult = ValidateAnimationName(animationName);
                if (!animationResult.IsValid)
                {
                    fieldErrors["animationName"] = animationResult.ErrorMessage;
                    result.IsValid = false;
                }
                if (animationResult.Warnings?.Length > 0)
                {
                    warnings.AddRange(animationResult.Warnings);
                }

                // Validate force parameter
                var forceResult = ValidateForceParameter(force);
                if (!forceResult.IsValid)
                {
                    fieldErrors["force"] = forceResult.ErrorMessage;
                    result.IsValid = false;
                }
                if (forceResult.Warnings?.Length > 0)
                {
                    warnings.AddRange(forceResult.Warnings);
                }

                // Set result properties
                result.FieldErrors = fieldErrors;
                result.Warnings = warnings.ToArray();

                if (!result.IsValid)
                {
                    result.ErrorMessage = fieldErrors.Values.FirstOrDefault() ?? "Validation failed";
                }

                Logger.Log($"ParameterValidator: All parameters validation result - IsValid: {result.IsValid}, Errors: {fieldErrors.Count}, Warnings: {warnings.Count}");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Log($"ParameterValidator: Error validating all parameters: {ex.Message}");
                return ValidationResult.Failure($"Error validating parameters: {ex.Message}");
            }
        }



        /// <summary>
        /// Validate TTS options
        /// </summary>
        /// <param name="ttsOptions">TTS options to validate</param>
        /// <returns>Validation result</returns>
        public static ValidationResult ValidateTTSOptions(TTSOptions ttsOptions)
        {
            try
            {
                if (ttsOptions is null)
                {
                    return ValidationResult.Success(); // TTS options are optional
                }

                var result = ValidationResult.Success();

                // Validate speed
                if (ttsOptions.Speed <= 0 || ttsOptions.Speed > 10)
                {
                    result.AddWarning($"TTS speed {ttsOptions.Speed} is outside normal range (0.1 - 3.0)");
                }

                // Validate timeout
                if (ttsOptions.TimeoutMs <= 0)
                {
                    return ValidationResult.Failure("TTS timeout must be positive");
                }

                if (ttsOptions.TimeoutMs > 300000) // 5 minutes
                {
                    result.AddWarning($"TTS timeout {ttsOptions.TimeoutMs}ms is very long");
                }

                // Validate audio file path if provided
                if (!string.IsNullOrEmpty(ttsOptions.AudioFilePath))
                {
                    if (ttsOptions.AudioFilePath.Any(c => char.IsControl(c)))
                    {
                        return ValidationResult.Failure("Audio file path contains invalid characters");
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                Logger.Log($"ParameterValidator: Error validating TTS options: {ex.Message}");
                return ValidationResult.Failure($"Error validating TTS options: {ex.Message}");
            }
        }

        /// <summary>
        /// Get safe default values for invalid parameters
        /// </summary>
        /// <param name="text">Original text</param>
        /// <param name="animationName">Original animation name</param>
        /// <param name="force">Original force value</param>
        /// <returns>Tuple of safe default values</returns>
        public static (string safeText, string safeAnimationName, bool safeForce) GetSafeDefaults(
            string text, string animationName, bool force)
        {
            try
            {
                // Safe text default
                string safeText = text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    safeText = "[Empty Message]";
                    Logger.Log("ParameterValidator: Using safe default for empty text");
                }
                else if (text.Length > 10000)
                {
                    safeText = text.Substring(0, 10000) + "...";
                    Logger.Log($"ParameterValidator: Truncated long text from {text.Length} to 10000 characters");
                }

                // Safe animation name default
                string safeAnimationName = animationName;
                if (!string.IsNullOrEmpty(animationName) && !CommonAnimations.Contains(animationName.ToLower()))
                {
                    safeAnimationName = "say"; // Default to common animation
                    Logger.Log($"ParameterValidator: Using safe default animation 'say' instead of '{animationName}'");
                }

                // Force parameter is always safe as boolean
                bool safeForce = force;

                Logger.Log($"ParameterValidator: Safe defaults - text length: {safeText.Length}, animation: '{safeAnimationName}', force: {safeForce}");
                return (safeText, safeAnimationName, safeForce);
            }
            catch (Exception ex)
            {
                Logger.Log($"ParameterValidator: Error getting safe defaults: {ex.Message}");
                return ("[Error]", "say", false);
            }
        }
    }
}