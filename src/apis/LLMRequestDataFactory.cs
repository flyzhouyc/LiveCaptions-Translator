using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.apis
{
    public static class LLMRequestDataFactory
    {
        private static readonly IReadOnlyList<Type> typeSequence =
        [
            typeof(IntegratedLLMRequestData),
            typeof(AliyunRequestData),
            typeof(AnthropicRequestData),
            typeof(OllamaRequestData),
            typeof(OpenRouterRequestData),
            typeof(OpenAIRequestData),
            typeof(XAIRequestData),
            typeof(BaseLLMRequestData)
        ];

        private static readonly IReadOnlyDictionary<string, Type> typesByPlatform = new Dictionary<string, Type>
        {
            ["integrated"] = typeof(IntegratedLLMRequestData),
            ["Aliyun"] = typeof(AliyunRequestData),
            ["Anthropic"] = typeof(AnthropicRequestData),
            ["Ollama"] = typeof(OllamaRequestData),
            ["OpenRouter"] = typeof(OpenRouterRequestData),
            ["OpenAI"] = typeof(OpenAIRequestData),
            ["XAI"] = typeof(XAIRequestData),
            ["base"] = typeof(BaseLLMRequestData)
        };

        public static int FallbackCount => typeSequence.Count;

        public static BaseLLMRequestData Create(string platform, string model, List<BaseLLMConfig.Message> messages, double temperature)
        {
            return typesByPlatform.TryGetValue(platform, out var type)
                ? Create(type, model, messages, temperature)
                : Create(model, messages, temperature);
        }

        public static BaseLLMRequestData Create(int index, string model, List<BaseLLMConfig.Message> messages, double temperature)
        {
            if (index < 0 || index >= typeSequence.Count)
                return Create(model, messages, temperature);

            return Create(typeSequence[index], model, messages, temperature);
        }

        public static BaseLLMRequestData Create(string model, List<BaseLLMConfig.Message> messages, double temperature)
        {
            return Create(typeof(BaseLLMRequestData), model, messages, temperature);
        }

        private static BaseLLMRequestData Create(Type type, string model, List<BaseLLMConfig.Message> messages, double temperature)
        {
            return (BaseLLMRequestData)Activator.CreateInstance(type, model, messages, temperature)!;
        }
    }
}
