namespace VPetLLM.Models
{
    /// <summary>
    /// Result of parameter validation operations
    /// </summary>
    public class ValidationResult
    {
        /// <summary>
        /// Whether the validation passed
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Primary error message if validation failed
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Additional warning messages
        /// </summary>
        public string[] Warnings { get; set; }

        /// <summary>
        /// Detailed validation errors by field
        /// </summary>
        public Dictionary<string, string> FieldErrors { get; set; }

        /// <summary>
        /// List of all error messages
        /// </summary>
        public List<string> Errors { get; set; }

        public ValidationResult()
        {
            IsValid = true;
            Warnings = new string[0];
            FieldErrors = new Dictionary<string, string>();
            Errors = new List<string>();
        }

        public ValidationResult(bool isValid, string errorMessage = null)
            : this()
        {
            IsValid = isValid;
            ErrorMessage = errorMessage;
        }

        /// <summary>
        /// Create a successful validation result
        /// </summary>
        public static ValidationResult Success()
        {
            return new ValidationResult(true);
        }

        /// <summary>
        /// Create a failed validation result with error message
        /// </summary>
        public static ValidationResult Failure(string errorMessage)
        {
            return new ValidationResult(false, errorMessage);
        }

        /// <summary>
        /// Create a failed validation result with field-specific errors
        /// </summary>
        public static ValidationResult Failure(Dictionary<string, string> fieldErrors)
        {
            var result = new ValidationResult(false);
            result.FieldErrors = fieldErrors ?? new Dictionary<string, string>();
            result.ErrorMessage = fieldErrors?.Values.FirstOrDefault();
            return result;
        }

        /// <summary>
        /// Add a warning to this validation result
        /// </summary>
        public void AddWarning(string warning)
        {
            var warnings = Warnings?.ToList() ?? new List<string>();
            warnings.Add(warning);
            Warnings = warnings.ToArray();
        }

        /// <summary>
        /// Add a field-specific error
        /// </summary>
        public void AddFieldError(string fieldName, string error)
        {
            FieldErrors[fieldName] = error;
            IsValid = false;
            if (string.IsNullOrEmpty(ErrorMessage))
            {
                ErrorMessage = error;
            }
        }

        /// <summary>
        /// Add an error message
        /// </summary>
        public void AddError(string error)
        {
            Errors.Add(error);
            IsValid = false;
            if (string.IsNullOrEmpty(ErrorMessage))
            {
                ErrorMessage = error;
            }
        }
    }
}