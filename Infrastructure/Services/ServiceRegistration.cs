namespace VPetLLM.Infrastructure.Services
{
    /// <summary>
    /// 服务注册器 - 负责注册服务到依赖注入容器。
    ///
    /// 这里只保留 VPetLLM.RegisterServices() 实际会调用的两个方法。
    /// 历史上还有 RegisterInfrastructureServices / RegisterAllServices /
    /// InitializeAndStartCoreServicesAsync / StopAllServicesAsync，以及
    /// ChatService、Infrastructure 层的 TTSService / ASRService —— 那一整套
    /// 从未被真正启动过：RegisterSingleton 只登记 ServiceDescriptor 不构造实例，
    /// 而唯一的启动入口挂在无人调用的示例/验证代码上。
    ///
    /// 其中 ChatService.ChatWithImageAsync 会绕开 Screenshot.ProcessingMode
    /// 直接调 ChatCore.ChatWithImage，一旦有人接上，就会出现第二条与
    /// 「截图与模型视觉」行为不一致的图像链路，因此整套已删除，避免误接。
    ///
    /// 图像处理请统一走 ScreenshotService / PreprocessingMultimodal / SeeScreenHandler；
    /// TTS / ASR 请用 Utils.Audio 下的实现（UtilsTTSService / UtilsASRService）。
    /// </summary>
    public static class ServiceRegistration
    {
        /// <summary>
        /// 注册核心服务
        /// </summary>
        public static void RegisterCoreServices(IDependencyContainer container, VPetLLM plugin, Setting settings)
        {
            if (container is null)
                throw new ArgumentNullException(nameof(container));

            // 注册核心工厂
            container.RegisterSingleton<CoreFactory, CoreFactory>();
        }

        /// <summary>
        /// 注册应用服务（当前应用层服务均由 VPetLLM 自行构造，此处暂无注册项）
        /// </summary>
        public static void RegisterApplicationServices(IDependencyContainer container, VPetLLM plugin)
        {
            if (container is null)
                throw new ArgumentNullException(nameof(container));
        }
    }
}
