# VPetLLM 后处理集成指南

## 功能概述

本模块为VPetLLM添加了后处理功能，支持LLM模型调用VPet状态信息，实现基于宠物状态的智能对话。

## 核心组件

### 1. LLMStateService
- 提供宠物状态查询功能
- 生成LLM提示模板
- 处理LLM响应

### 2. LLMProcessorController  
- 控制LLM处理流程
- 管理流式响应输出
- 提供状态检查功能

### 3. LLMIntegrationService
- 集成LLM功能到主程序
- 提供统一的API接口
- 处理错误和异常

## 使用方法

### 基本状态查询
```csharp
var status = VPetLLM.Instance.GetPetStatusInfo();
// 返回: "宠物[名字]状态: 等级5, 健康80.0, 体力60.0..."
```

### LLM增强处理
```csharp
var response = await VPetLLM.Instance.ProcessWithLLMAsync("用户输入");
// 返回SayInfo对象，支持流式输出
```

### 状态检查
```csharp
bool canFeed = VPetLLM.Instance.LLMService?.CanPerformAction("feed") ?? false;
```

## 集成示例

### 在TalkBox中使用
```csharp
// 在处理用户输入时调用LLM后处理
var llmResponse = await VPetLLM.Instance.ProcessWithLLMAsync(userInput);
MW.AddSay(llmResponse);
```

### 自定义LLM提示
```csharp
var prompt = VPetLLM.Instance.LLMService?.GeneratePromptTemplate(userInput);
// 发送到自定义LLM服务
```

## 配置选项

### 环境要求
- .NET 8.0+
- VPet-Simulator.Core 引用
- LLM API 密钥（如OpenAI、Ollama等）

### 扩展功能
1. **实时状态监控**: 持续获取宠物状态变化
2. **动作建议**: 基于状态推荐互动动作  
3. **情绪分析**: 结合心情值生成回应
4. **健康预警**: 低健康值时发出警告

## 开发说明

### 添加新的LLM提供商
1. 实现IChatCore接口
2. 在Setting.cs中添加配置选项
3. 在VPetLLM.cs中更新ChatCore初始化

### 自定义状态处理
重写LLMStateService中的方法来自定义状态信息格式和处理逻辑。

## 故障排除

### 常见问题
1. **状态服务未初始化**: 检查GameCore实例是否可用
2. **LLM调用失败**: 验证API密钥和网络连接
3. **性能问题**: 优化提示生成和响应处理

### 日志查看
所有操作都会记录到Logger中，可通过日志诊断问题。