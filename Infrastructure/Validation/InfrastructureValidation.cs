using VPetLLM.Infrastructure.Configuration.Configurations;

namespace VPetLLM.Infrastructure.Validation
{
    /// <summary>
    /// 基础设施验证器 - 验证所有核心基础设施组件是否正常工作
    /// </summary>
    public class InfrastructureValidation
    {
        /// <summary>
        /// 执行完整的基础设施验证
        /// </summary>
        public static async Task<ValidationResult> ValidateAllAsync()
        {
            var result = new ValidationResult();

            try
            {
                Console.WriteLine("开始验证核心基础设施...");

                // 1. 验证依赖注入容器
                var diResult = ValidateDependencyInjection();
                result.AddResult("依赖注入容器", diResult);

                // 2. 验证事件总线
                var eventResult = await ValidateEventBusAsync();
                result.AddResult("事件总线", eventResult);

                // 3. 验证结构化日志
                var logResult = ValidateStructuredLogging();
                result.AddResult("结构化日志", logResult);

                // 4. 验证配置管理
                var configResult = ValidateConfigurationManagement();
                result.AddResult("配置管理", configResult);

                // 5. 验证服务管理
                var serviceResult = await ValidateServiceManagementAsync();
                result.AddResult("服务管理", serviceResult);

                // 6. 验证核心服务
                var coreResult = await ValidateCoreServicesAsync();
                result.AddResult("核心服务", coreResult);

                Console.WriteLine($"基础设施验证完成 - 成功: {result.SuccessCount}, 失败: {result.FailureCount}");

                return result;
            }
            catch (Exception ex)
            {
                result.AddResult("整体验证", false, $"验证过程中发生异常: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// 验证依赖注入容器功能
        /// </summary>
        private static bool ValidateDependencyInjection()
        {
            try
            {
                Console.WriteLine("验证依赖注入容器...");

                var container = new DependencyContainer();

                // 测试单例注册
                container.RegisterSingleton<IStructuredLogger, StructuredLogger>();

                // 测试瞬态注册
                container.RegisterTransient<IEventBus, EventBus>();

                // 测试解析
                var logger1 = container.Resolve<IStructuredLogger>();
                var logger2 = container.Resolve<IStructuredLogger>();

                // 验证单例行为
                if (logger1 != logger2)
                {
                    Console.WriteLine("❌ 单例注册验证失败");
                    return false;
                }

                // 测试瞬态行为
                var eventBus1 = container.Resolve<IEventBus>();
                var eventBus2 = container.Resolve<IEventBus>();

                if (eventBus1 == eventBus2)
                {
                    Console.WriteLine("❌ 瞬态注册验证失败");
                    return false;
                }

                // 测试依赖验证
                container.ValidateDependencies();

                Console.WriteLine("依赖注入容器验证成功");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 依赖注入容器验证失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 验证事件总线功能
        /// </summary>
        private static async Task<bool> ValidateEventBusAsync()
        {
            try
            {
                Console.WriteLine("验证事件总线...");

                var eventBus = new EventBus();
                bool eventReceived = false;
                string receivedMessage = null;

                // 订阅测试事件
                eventBus.Subscribe<TestEvent>(async (testEvent) =>
                {
                    eventReceived = true;
                    receivedMessage = testEvent.Message;
                });

                // 发布测试事件
                var testMessage = "Hello from EventBus!";
                await eventBus.PublishAsync(new TestEvent { Message = testMessage });

                // 等待事件处理
                await Task.Delay(100);

                if (!eventReceived || receivedMessage != testMessage)
                {
                    Console.WriteLine("❌ 事件总线消息传递验证失败");
                    return false;
                }

                Console.WriteLine("事件总线验证成功");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 事件总线验证失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 验证结构化日志功能
        /// </summary>
        private static bool ValidateStructuredLogging()
        {
            try
            {
                Console.WriteLine("验证结构化日志...");

                var logger = new StructuredLogger();

                // 测试不同级别的日志
                logger.LogTrace("Trace message");
                logger.LogDebug("Debug message");
                logger.LogInformation("Information message");
                logger.LogWarning("Warning message");

                // 测试带上下文的日志
                logger.LogInformation("Test message with context", new { TestProperty = "TestValue" });

                // 测试异常日志
                try
                {
                    throw new InvalidOperationException("Test exception");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Test error message");
                }

                Console.WriteLine("结构化日志验证成功");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 结构化日志验证失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 验证配置管理功能
        /// </summary>
        private static bool ValidateConfigurationManagement()
        {
            try
            {
                Console.WriteLine("验证配置管理...");

                var logger = new StructuredLogger();
                var configManager = new InfraConfigurationManager(".", logger);

                // 测试配置获取
                var llmConfig = configManager.GetConfiguration<LLMConfiguration>();
                if (llmConfig is null)
                {
                    Console.WriteLine("❌ 配置获取验证失败");
                    return false;
                }

                // 测试配置验证
                var validationResult = llmConfig.Validate();
                if (validationResult is null)
                {
                    Console.WriteLine("❌ 配置验证功能验证失败");
                    return false;
                }

                Console.WriteLine("配置管理验证成功");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 配置管理验证失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 验证服务管理功能
        /// </summary>
        private static async Task<bool> ValidateServiceManagementAsync()
        {
            try
            {
                Console.WriteLine("验证服务管理...");

                var container = new DependencyContainer();
                var eventBus = new EventBus();
                var logger = new StructuredLogger();

                var serviceManager = new ServiceManager(container, eventBus, logger);

                // 注册测试服务
                container.RegisterSingleton<TestService, TestService>();

                // 测试服务获取
                var testService = await serviceManager.GetServiceAsync<TestService>();
                if (testService is null)
                {
                    Console.WriteLine("❌ 服务获取验证失败");
                    return false;
                }

                // 测试服务状态
                var status = serviceManager.GetServiceStatus<TestService>();
                if (status == InfraServiceStatus.NotRegistered)
                {
                    Console.WriteLine("❌ 服务状态验证失败");
                    return false;
                }

                Console.WriteLine("服务管理验证成功");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 服务管理验证失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 验证核心服务功能
        /// </summary>
        private static async Task<bool> ValidateCoreServicesAsync()
        {
            try
            {
                Console.WriteLine("验证核心服务...");

                var container = new DependencyContainer();

                // 注册所有服务
                // 注意：这里使用null参数仅用于验证目的
                ServiceRegistration.RegisterAllServices(container, null, null);

                // 验证依赖关系
                container.ValidateDependencies();

                // 获取服务管理器
                var serviceManager = container.Resolve<IServiceManager>();
                if (serviceManager is null)
                {
                    Console.WriteLine("❌ 服务管理器解析失败");
                    return false;
                }

                // 测试核心服务创建（不启动，因为需要具体的核心实现）
                var chatService = await serviceManager.GetServiceAsync<ChatService>();
                var ttsService = await serviceManager.GetServiceAsync<TTSService>();
                var asrService = await serviceManager.GetServiceAsync<ASRService>();

                if (chatService is null || ttsService is null || asrService is null)
                {
                    Console.WriteLine("❌ 核心服务创建验证失败");
                    return false;
                }

                Console.WriteLine("核心服务验证成功");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 核心服务验证失败: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 测试事件类
    /// </summary>
    public class TestEvent
    {
        public string Message { get; set; }
    }

    /// <summary>
    /// 测试服务类
    /// </summary>
    public class TestService : ServiceBase
    {
        public override string ServiceName => "TestService";

        public TestService() : base(null)
        {
        }

        protected override Task OnInitializeAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        protected override Task OnStartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        protected override Task OnStopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        protected override Task OnHealthCheckAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 验证结果类
    /// </summary>
    public class ValidationResult
    {
        private readonly List<ValidationItem> _results = new();

        public int SuccessCount => _results.Count(r => r.Success);
        public int FailureCount => _results.Count(r => !r.Success);
        public bool IsAllSuccess => _results.All(r => r.Success);

        public void AddResult(string component, bool success, string message = null)
        {
            _results.Add(new ValidationItem
            {
                Component = component,
                Success = success,
                Message = message
            });
        }

        public void PrintSummary()
        {
            Console.WriteLine("\n=== 验证结果摘要 ===");
            foreach (var result in _results)
            {
                var status = result.Success ? "✅" : "❌";
                var message = string.IsNullOrEmpty(result.Message) ? "" : $" - {result.Message}";
                Console.WriteLine($"{status} {result.Component}{message}");
            }
            Console.WriteLine($"\n总计: {SuccessCount} 成功, {FailureCount} 失败");
        }
    }

    /// <summary>
    /// 验证项目
    /// </summary>
    public class ValidationItem
    {
        public string Component { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
    }
}