namespace CadAI.Core
{
    internal sealed class ToolActivity
    {
        public string CallId { get; set; }
        public string Name { get; set; }
        public string Json { get; set; }
        public bool IsResult { get; set; }
        public bool IsError { get; set; }

        public static ToolActivity Call(string callId, string name, string argumentsJson)
            => new ToolActivity { CallId = callId, Name = name, Json = JsonDisplayFormatter.FormatIfJson(argumentsJson), IsResult = false };

        public static ToolActivity Result(string callId, string name, string resultJson, bool isError)
            => new ToolActivity { CallId = callId, Name = name, Json = JsonDisplayFormatter.FormatIfJson(resultJson), IsResult = true, IsError = isError };
    }
}
