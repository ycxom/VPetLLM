using VPetLLM.Infrastructure.Configuration;
using VPetLLM.Infrastructure.DependencyInjection;
using VPetLLM.Infrastructure.Events;
using VPetLLM.Infrastructure.Exceptions;
using VPetLLM.Infrastructure.Logging;

namespace VPetLLM.Infrastructure.Services
{
    /// <summary>
    /// 服务注册器 - 负责注册所有服务到依赖注入容器
    /// </summary>
    public static class ServiceRegistration
    {
        /// <summary>
        /// 注册所有基础设施服务
        /// </summary>
        public static void RegisterInfrastructureServices(IDependencyContainer container)
        {
            if (container == null)
                throw new ArgumentNullException(nameof(container));

            // 注册基础设施服务
            container.RegisterSingleton<IStructuredLogger, StructuredLogger>();
            container.RegisterSingleton<IEventBus, EventBus>();
            container.RegisterSingleton<IConfigurationManager, ConfigurationManager>();
            container.RegisterSingleton<IServiceManager, ServiceManager>();
        }

        /// <summary>
        /// 注册所有核心服务
        /// </summary>
        public static void RegisterCoreServices(IDependencyContainer container, VPetLLM plugin, Setting settings)
        {
            if (container == null)
                throw new ArgumentNullException(nameof(container));

            // 注册核心工厂
            container.RegisterSingleton<CoreFactory, CoreFactory>();

            // 注册核心服务
            container.RegisterSingleton<ChatService, ChatService>();
            container.RegisterSingleton<TTSService, TTSService>();
            container.RegisterSingleton<ASRService, ASRService>();
        }

        /// <summary>
        /// 注册所有应用服务
        /// </summary>
        public static void RegisterApplicationServices(IDependencyContainer container, VPetLLM plugin)
        {
            if (container == null)
                throw new ArgumentNullException(nameof(container));

            // TODO: 注册应用服务
            // container.RegisterSingleton<VoiceInputService, VoiceInputService>();
            // container.RegisterSingleton<ScreenshotService, ScreenshotService>();
            // container.RegisterSingleton<PurchaseService, PurchaseService>();
            // container.RegisterSingleton<MediaPlaybackService, MediaPlaybackService>();
        }

        /// <summary>
        /// 注册所有服务
        /// </summary>
        public static void RegisterAllServices(IDependencyContainer container, VPetLLM plugin, Setting settings)
        {
            RegisterInfrastructureServices(container);
            RegisterCoreServices(container, plugin, settings);
            RegisterApplicationServices(container, plugin);
        }

        /// <summary>
        /// 初始化并启动所有核心服务
        /// </summary>
        public static async Task InitializeAndStartCoreServicesAsync(IServiceManager serviceManager)
        {
            if (serviceManager == null)
                throw new ArgumentNullException(nameof(serviceManager));

            try
            {
                // 按依赖顺序启动服务
                // 1. 首先启动配置管理服务（已经在基础设施中启动）
                // 2. 启动核心服务
                await serviceManager.StartServiceAsync<ChatService>();
                await serviceManager.StartServiceAsync<TTSService>();
                await serviceManager.StartServiceAsync<ASRService>();

                // TODO: 启动应用服务
                // await serviceManager.StartServiceAsync<VoiceInputService>();
                // await serviceManager.StartServiceAsync<ScreenshotService>();
                // await serviceManager.StartServiceAsync<PurchaseService>();
                // await serviceManager.StartServiceAsync<MediaPlaybackService>();
            }
            catch (Exception ex)
            {
                throw new ServiceException("CoreServices", "Failed to initialize and start core services", ex);
            }
        }

        /// <summary>
        /// 停止所有服务
        /// </summary>
        public static async Task StopAllServicesAsync(IServiceManager serviceManager)
        {
            if (serviceManager == null)
                throw new ArgumentNullException(nameof(serviceManager));

            try
            {
                await serviceManager.StopAllServicesAsync();
            }
            catch (Exception ex)
            {
                throw new ServiceException("AllServices", "Failed to stop all services", ex);
            }
        }
    }
}