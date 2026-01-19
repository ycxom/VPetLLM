using VPetLLM.Infrastructure.DependencyInjection;

namespace VPetLLM.Infrastructure.Services
{
    /// <summary>
    /// 服务初始化示例 - 展示如何正确初始化和使用新的服务架构
    /// </summary>
    public class ServiceInitializationExample
    {
        /// <summary>
        /// 完整的服务初始化流程示例
        /// </summary>
        public static async Task<IServiceManager> InitializeServicesAsync()
        {
            try
            {
                // 1. 创建依赖注入容器
                var container = new DependencyContainer();

                // 2. 注册所有服务
                // 注意：这里使用null参数仅用于示例目的
                ServiceRegistration.RegisterAllServices(container, null, null);

                // 3. 验证依赖关系
                container.ValidateDependencies();

                // 4. 解析服务管理器
                var serviceManager = container.Resolve<IServiceManager>();

                // 5. 初始化并启动核心服务
                await ServiceRegistration.InitializeAndStartCoreServicesAsync(serviceManager);

                Console.WriteLine("所有服务已成功初始化并启动");
                return serviceManager;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"服务初始化失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 使用服务的示例
        /// </summary>
        public static async Task UseServicesExample(IServiceManager serviceManager)
        {
            try
            {
                // 获取聊天服务
                var chatService = await serviceManager.GetServiceAsync<ChatService>();

                // 检查服务状态
                var chatStatus = serviceManager.GetServiceStatus<ChatService>();
                var chatHealth = serviceManager.GetServiceHealth<ChatService>();

                Console.WriteLine($"ChatService - Status: {chatStatus}, Health: {chatHealth}");

                // 获取TTS服务
                var ttsService = await serviceManager.GetServiceAsync<TTSService>();
                var ttsStatus = serviceManager.GetServiceStatus<TTSService>();
                var ttsHealth = serviceManager.GetServiceHealth<TTSService>();

                Console.WriteLine($"TTSService - Status: {ttsStatus}, Health: {ttsHealth}");

                // 获取ASR服务
                var asrService = await serviceManager.GetServiceAsync<ASRService>();
                var asrStatus = serviceManager.GetServiceStatus<ASRService>();
                var asrHealth = serviceManager.GetServiceHealth<ASRService>();

                Console.WriteLine($"ASRService - Status: {asrStatus}, Health: {asrHealth}");

                // 示例：使用聊天服务（需要实际的核心实现）
                /*
                try
                {
                    var response = await chatService.ChatAsync("Hello, how are you?");
                    Console.WriteLine($"Chat response: {response}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Chat failed: {ex.Message}");
                }
                */

                // 示例：使用TTS服务（需要实际的核心实现）
                /*
                try
                {
                    var audioData = await ttsService.GenerateAudioAsync("Hello world");
                    Console.WriteLine($"Generated audio: {audioData.Length} bytes");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"TTS failed: {ex.Message}");
                }
                */

                // 示例：使用ASR服务（需要实际的核心实现）
                /*
                try
                {
                    byte[] audioData = new byte[1024]; // 示例音频数据
                    var transcription = await asrService.TranscribeAsync(audioData);
                    Console.WriteLine($"Transcription: {transcription}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ASR failed: {ex.Message}");
                }
                */
            }
            catch (Exception ex)
            {
                Console.WriteLine($"使用服务时发生错误: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 优雅关闭服务的示例
        /// </summary>
        public static async Task ShutdownServicesAsync(IServiceManager serviceManager)
        {
            try
            {
                Console.WriteLine("正在关闭所有服务...");

                await ServiceRegistration.StopAllServicesAsync(serviceManager);

                // 释放服务管理器
                if (serviceManager is IDisposable disposable)
                {
                    disposable.Dispose();
                }

                Console.WriteLine("所有服务已成功关闭");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"关闭服务时发生错误: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 完整的生命周期示例
        /// </summary>
        public static async Task FullLifecycleExample()
        {
            IServiceManager serviceManager = null;

            try
            {
                // 初始化服务
                serviceManager = await InitializeServicesAsync();

                // 使用服务
                await UseServicesExample(serviceManager);

                // 等待一段时间模拟实际使用
                await Task.Delay(5000);
            }
            finally
            {
                // 确保服务被正确关闭
                if (serviceManager != null)
                {
                    await ShutdownServicesAsync(serviceManager);
                }
            }
        }
    }
}