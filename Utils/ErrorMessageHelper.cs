namespace VPetLLM.Utils
{
    public static class ErrorMessageHelper
    {
        public static string GetLocalizedError(string errorKey, string langCode, string fallbackMessage, Exception ex)
        {
            var localizedMessage = LanguageHelper.GetError(errorKey, langCode);
            var localizedTitle = LanguageHelper.GetError("Error", langCode);

            if (localizedMessage == $"[{errorKey}]")
            {
                localizedMessage = fallbackMessage;
            }

            return $"{localizedMessage}: {ex.Message}";
        }

        public static string GetLocalizedMessage(string messageKey, string langCode, string fallbackMessage)
        {
            var localizedMessage = LanguageHelper.GetError(messageKey, langCode);
            if (localizedMessage == $"[{messageKey}]")
            {
                localizedMessage = fallbackMessage;
            }
            return localizedMessage;
        }

        public static string GetLocalizedTitle(string titleKey, string langCode, string fallbackTitle)
        {
            var localizedTitle = LanguageHelper.GetError(titleKey, langCode);
            if (localizedTitle == $"[{titleKey}]")
            {
                localizedTitle = fallbackTitle;
            }
            return localizedTitle;
        }
    }
}