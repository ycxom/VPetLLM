// Global using statements for VPetLLM
// 这个文件定义了项目中常用的命名空间，减少每个文件中的 using 语句

// System namespaces
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading.Tasks;
global using System.Text;
global using System.Text.Json;

// 第三方库 - 优先使用 Newtonsoft.Json
global using Newtonsoft.Json;
global using JsonException = Newtonsoft.Json.JsonException;

// VPetLLM Core - 重组后的核心命名空间
global using VPetLLM.Core.Services;
global using VPetLLM.Core.Integration;
global using VPetLLM.Core.Data.Managers;
global using VPetLLM.Core.Data.Database;
global using VPetLLM.Core.Data.Models;
global using VPetLLM.Core.Cache;
global using VPetLLM.Core.Engine;
global using VPetLLM.Core.Plugin;

// VPetLLM Core - 抽象层
global using VPetLLM.Core.Abstractions.Base;
global using VPetLLM.Core.Abstractions.Interfaces;
global using VPetLLM.Core.Abstractions.Interfaces.Plugin;

// VPetLLM Core - 提供商实现
global using VPetLLM.Core.Providers.ASR;
global using VPetLLM.Core.Providers.Chat;
global using VPetLLM.Core.Providers.TTS;
global using VPetLLM.Core.Providers.TTS.GPTSoVITS;

// VPetLLM Core - 统一TTS系统 (使用别名避免冲突)
global using VPetLLM.Core.Integration.UnifiedTTS.Adapters;
global using VPetLLM.Core.Integration.UnifiedTTS.Management;
global using VPetLLM.Core.Integration.UnifiedTTS.Utils;
// 为冲突的接口使用别名
global using UnifiedTTSConfigManager = VPetLLM.Core.Integration.UnifiedTTS.Interfaces.IConfigurationManager;
global using UnifiedTTSConfigChangedEventArgs = VPetLLM.Core.Integration.UnifiedTTS.Interfaces.ConfigurationChangedEventArgs;
global using ITTSDispatcher = VPetLLM.Core.Integration.UnifiedTTS.Interfaces.ITTSDispatcher;
global using ITTSAdapter = VPetLLM.Core.Integration.UnifiedTTS.Interfaces.ITTSAdapter;
global using AdapterHealthStatus = VPetLLM.Core.Integration.UnifiedTTS.Interfaces.AdapterHealthStatus;

// VPetLLM 其他核心命名空间
global using VPetLLM.Configuration;
global using VPetLLM.Models;
global using VPetLLM.Services;
global using VPetLLM.Utils.Common;
global using VPetLLM.Utils.System;
global using VPetLLM.Utils.UI;

// VPetLLM Handlers
global using VPetLLM.Handlers.Actions;
global using VPetLLM.Handlers.Core;
global using VPetLLM.Handlers.TTS;
global using VPetLLM.Handlers.UI;


// VPetLLM Infrastructure - 使用别名避免冲突
global using VPetLLM.Infrastructure.Services;
global using VPetLLM.Infrastructure.Events;
global using VPetLLM.Infrastructure.Configuration;
global using VPetLLM.Infrastructure.DependencyInjection;
global using VPetLLM.Infrastructure.Logging;
global using VPetLLM.Infrastructure.Exceptions;

// 別名逻辑 - 解决 Infrastructure 命名空间冲突
global using InfraServiceStatus = VPetLLM.Infrastructure.Services.ServiceStatus;
global using InfraConfigManager = VPetLLM.Infrastructure.Configuration.IConfigurationManager;
global using InfraTTSConfiguration = VPetLLM.Infrastructure.Configuration.Configurations.TTSConfiguration; 
global using InfraConfigChangedEventArgs = VPetLLM.Infrastructure.Configuration.InfraConfigChangedEventArgs;
global using InfraConfigurationManager = VPetLLM.Infrastructure.Configuration.ConfigurationManager;
global using ServiceStatusChangedEvent = VPetLLM.Infrastructure.Events.InfraServiceStatusChangedEvent;

// 別名逻辑 - 解决服务类冲突
global using InfraTTSService = VPetLLM.Infrastructure.Services.TTSService;
global using InfraASRService = VPetLLM.Infrastructure.Services.ASRService;
global using InfraTTSRequest = VPetLLM.Infrastructure.Services.TTSRequest;
global using UtilsTTSService = VPetLLM.Utils.Audio.TTSService;
global using UtilsASRService = VPetLLM.Utils.Audio.ASRService;

// 別名逻辑 - 解决模型类冲突
global using ModelsTTSRequest = VPetLLM.Models.TTSRequest;
global using ModelsServiceStatus = VPetLLM.Models.ServiceStatus;
global using ConfigTTSSettings = VPetLLM.Configuration.TTSSettings;
global using JsonSerializer = System.Text.Json.JsonSerializer;
