namespace VPetLLM.Core.Plugin
{
    public class FailedPlugin
    {
        public string Name { get; set; }
        public string Author { get; set; } = "Unknown";
        public string Description { get; set; }
        public string FilePath { get; set; }
        public Exception Error { get; set; }
    }
}