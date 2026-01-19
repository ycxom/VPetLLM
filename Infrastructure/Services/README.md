# VPetLLM 服务架构

## 概述

这个目录包含了VPetLLM插件的新服务架构实现。新架构采用了模块化设计，通过依赖注入、事件驱动和统一的服务管理来提高代码的可维护性和可扩展性。

## 架构组件

### 核心服务 (Core Services)

#### ChatService
- **职责**: 管理所有聊天核心的统一服务
- **功能**: 
  - 聊天请求处理
  - 多模态消息支持
  - 提供商切换
  - 配置热重载
  - 事件发布

#### TTSService
- **职责**: 管理所有TTS核心的统一服务
- **功能**:
  - 语音合成
  - 队列处理
  - 提供商切换
  - 配置管理
  - 健康监控

#### ASRService
- **职责**: 管理所有ASR核心的统一服务
- **功能**:
  - 语音识别
  - 队列处理
  - 模型管理
  - 提供商切换
  - 配置管理

### 基础设施服务 (Infrastructure Services)

#### ServiceManager
- **职责**: 统一的服务生命周期管理
- **功能**:
  - 服务注册和解析
  - 依赖关系管理
  - 健康检查
  - 自动重启
  - 拓扑排序启动

#### CoreFactory
- **职责**: 创建具体的核心实现实例
- **功能**:
  - 聊天核心创建
  - TTS核心创建
  - ASR核心创建
  - 配置驱动的实例化

#### ServiceRegistration
- **职责**: 服务注册和初始化
- **功能**:
  - 依赖注入注册
  - 服务启动顺序管理
  - 批量操作支持

## 使用方式

### 1. 基本初始化

```csharp
// 创建依赖注入容器
var container = new DependencyContainer();

// 注册所有服务
ServiceRegistration.RegisterAllServices(container);

// 获取服务管理器
var serviceManager = container.Resolve<IServiceManager>();

// 启动核心服务
await ServiceRegistration.InitializeAndStartCoreServicesAsync(serviceManager);
```

### 2. 使用服务

```csharp
// 获取聊天服务
var chatService = await serviceManager.GetServiceAsync<ChatService>();

// 发送聊天消息
var response = await chatService.ChatAsync("Hello, how are you?");

// 获取TTS服务
var ttsService = await serviceManager.GetServiceAsync<TTSService>();

// 生成语音
var audioData = await ttsService.GenerateAudioAsync("Hello world");

// 获取ASR服务
var asrService = await serviceManager.GetServiceAsync<ASRService>();

// 转录音频
var transcription = await asrService.TranscribeAsync(audioData);
```

### 3. 服务监控

```csharp
// 检查服务状态
var status = serviceManager.GetServiceStatus<ChatService>();
var health = serviceManager.GetServiceHealth<ChatService>();

// 订阅服务事件
serviceManager.ServiceStatusChanged += (sender, e) => {
    Console.WriteLine($"Service {e.ServiceType.Name} status changed to {e.Status}");
};

serviceManager.ServiceHealthChanged += (sender, e) => {
    Console.WriteLine($"Service {e.ServiceType.Name} health changed to {e.Health}");
};
```

### 4. 优雅关闭

```csharp
// 停止所有服务
await ServiceRegistration.StopAllServicesAsync(serviceManager);

// 释放资源
if (serviceManager is IDisposable disposable)
{
    disposable.Dispose();
}
```

## 事件系统

新架构使用事件驱动模式来解耦各模块间的直接依赖：

### 聊天服务事件
- `ChatServiceStartedEvent` - 服务启动
- `ChatRequestStartedEvent` - 聊天请求开始
- `ChatRequestCompletedEvent` - 聊天请求完成
- `ChatRequestFailedEvent` - 聊天请求失败
- `ChatProviderSwitchedEvent` - 提供商切换

### TTS服务事件
- `TTSServiceStartedEvent` - 服务启动
- `TTSRequestStartedEvent` - TTS请求开始
- `TTSRequestCompletedEvent` - TTS请求完成
- `TTSRequestFailedEvent` - TTS请求失败
- `TTSProviderSwitchedEvent` - 提供商切换

### ASR服务事件
- `ASRServiceStartedEvent` - 服务启动
- `ASRRequestStartedEvent` - ASR请求开始
- `ASRRequestCompletedEvent` - ASR请求完成
- `ASRRequestFailedEvent` - ASR请求失败
- `ASRProviderSwitchedEvent` - 提供商切换

## 配置管理

每个服务都支持配置热重载：

```csharp
// 配置变更会自动触发服务重新配置
_configurationManager.ConfigurationChanged += OnConfigurationChanged;
```

## 健康监控

所有服务都实现了健康检查接口：

```csharp
public override async Task<ServiceHealthStatus> CheckHealthAsync()
{
    // 返回服务健康状态
    return new ServiceHealthStatus
    {
        ServiceName = ServiceName,
        Status = HealthStatus.Healthy,
        Description = "Service is healthy",
        Metrics = metrics,
        CheckTime = DateTime.UtcNow
    };
}
```

## 扩展指南

### 添加新的核心提供商

1. 在 `CoreFactory` 中添加新的核心创建逻辑
2. 实现对应的核心基类 (`ChatCoreBase`, `TTSCoreBase`, `ASRCoreBase`)
3. 更新配置类以支持新提供商

### 添加新的应用服务

1. 实现 `IService` 接口
2. 继承 `ServiceBase` 基类
3. 在 `ServiceRegistration` 中注册新服务
4. 添加依赖关系属性（如需要）

## 注意事项

1. **TODO标记**: 当前实现中的核心工厂包含TODO标记，需要根据实际的核心实现来完成
2. **依赖关系**: 确保正确设置服务间的依赖关系
3. **异常处理**: 所有服务操作都包含完整的异常处理和日志记录
4. **资源管理**: 服务实现了正确的资源释放机制
5. **线程安全**: 所有服务都是线程安全的

## 下一步

1. 实现具体的聊天、TTS和ASR核心类
2. 完善CoreFactory中的核心创建逻辑
3. 添加应用服务层（VoiceInputService、ScreenshotService等）
4. 集成到主插件类中
5. 编写单元测试和集成测试