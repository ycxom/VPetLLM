# DIY TTS 配置说明

## 概述

DIY TTS 功能允许您使用自定义的 TTS API 服务，支持强大的自定义功能，类似于 Apifox 的请求配置。

## 配置文件位置

配置文件位于：`文档\VPetLLM\DiyTTS\Config.json`

## 配置文件格式

```json
{
  "enabled": false,
  "baseUrl": "https://api.example.com/tts",
  "method": "POST",
  "contentType": "application/json",
  "requestBody": {
    "text": "{text}",
    "voice": "default",
    "format": "mp3",
    "speed": 1.0,
    "pitch": 1.0
  },
  "customHeaders": {
    "User-Agent": "VPetLLM",
    "Accept": "audio/mpeg",
    "Authorization": "Bearer your-api-key-here"
  },
  "responseFormat": "mp3",
  "timeout": 30000,
  "description": "DIY TTS 配置"
}
```

## 配置参数说明

### 基本配置

- **enabled**: `boolean` - 是否启用 DIY TTS 功能
- **baseUrl**: `string` - TTS API 的基础 URL
- **method**: `string` - HTTP 请求方法（GET 或 POST）
- **contentType**: `string` - 请求内容类型（仅 POST 请求时使用）
- **timeout**: `number` - 请求超时时间（毫秒）
- **description**: `string` - 配置描述（可选）

### 请求体配置

- **requestBody**: `object` - 请求体配置（仅 POST 请求时使用）
  - 使用键值对格式，与 customHeaders 一致，便于编写和维护
  - 使用 `{text}` 作为文本占位符
  - 支持字符串、数字、布尔值等类型
  - 示例：
    ```json
    "requestBody": {
      "text": "{text}",
      "voice": "default",
      "format": "mp3",
      "speed": 1.0,
      "pitch": 1.0
    }
    ```

### 自定义请求头

- **customHeaders**: `object` - 自定义 HTTP 请求头
  - 键值对格式：`"Header-Name": "Header-Value"`
  - **User-Agent** 会被强制设置为 "VPetLLM"
  - 常用请求头：
    - `Authorization`: API 认证信息
    - `Accept`: 接受的响应类型
    - `X-API-Key`: API 密钥

### 响应配置

- **responseFormat**: `string` - 响应音频格式
  - 支持格式：mp3, wav, ogg, flac
  - 用于确定保存的临时文件扩展名

## 使用步骤

1. **启用功能**：在 VPetLLM 设置中选择 "DIY" 作为 TTS 提供商
2. **打开配置**：点击 "打开配置文件夹" 按钮
3. **编辑配置**：修改 `Config.json` 文件
4. **设置启用**：将 `enabled` 设置为 `true`
5. **测试功能**：使用 TTS 测试按钮验证配置

## 配置示例

### 示例 1：POST 请求 JSON API

```json
{
  "enabled": true,
  "baseUrl": "https://api.your-tts-service.com/v1/tts",
  "method": "POST",
  "contentType": "application/json",
  "requestBody": {
    "text": "{text}",
    "voice_id": "zh-CN-XiaoxiaoNeural",
    "output_format": "mp3",
    "speed": 1.0,
    "pitch": 1.0
  },
  "customHeaders": {
    "User-Agent": "VPetLLM",
    "Authorization": "Bearer sk-your-api-key",
    "Accept": "audio/mpeg"
  },
  "responseFormat": "mp3",
  "timeout": 30000
}
```

### 示例 2：GET 请求

```json
{
  "enabled": true,
  "baseUrl": "https://api.your-tts-service.com/speak",
  "method": "GET",
  "customHeaders": {
    "User-Agent": "VPetLLM",
    "X-API-Key": "your-api-key"
  },
  "responseFormat": "wav",
  "timeout": 15000
}
```

### 示例 3：带认证的 API（ElevenLabs 风格）

```json
{
  "enabled": true,
  "baseUrl": "https://api.elevenlabs.io/v1/text-to-speech/voice-id",
  "method": "POST",
  "contentType": "application/json",
  "requestBody": {
    "text": "{text}",
    "model_id": "eleven_multilingual_v2",
    "voice_settings": {
      "stability": 0.5,
      "similarity_boost": 0.5
    }
  },
  "customHeaders": {
    "User-Agent": "VPetLLM",
    "xi-api-key": "your-elevenlabs-api-key",
    "Accept": "audio/mpeg"
  },
  "responseFormat": "mp3",
  "timeout": 30000
}
```

### 示例 4：复杂参数配置

```json
{
  "enabled": true,
  "baseUrl": "https://api.advanced-tts.com/synthesize",
  "method": "POST",
  "contentType": "application/json",
  "requestBody": {
    "text": "{text}",
    "voice": {
      "name": "neural-voice-1",
      "language": "zh-CN",
      "gender": "female"
    },
    "audio_config": {
      "format": "mp3",
      "sample_rate": 22050,
      "bitrate": 128
    },
    "prosody": {
      "rate": 1.0,
      "pitch": 0.0,
      "volume": 0.8
    },
    "enable_ssml": false
  },
  "customHeaders": {
    "User-Agent": "VPetLLM",
    "Authorization": "Bearer your-token",
    "Content-Type": "application/json"
  },
  "responseFormat": "mp3",
  "timeout": 45000
}
```

## 安全注意事项

1. **User-Agent 限制**：User-Agent 请求头会被强制设置为 "VPetLLM"，无法修改
2. **API 密钥安全**：请妥善保管您的 API 密钥，不要分享配置文件
3. **网络安全**：建议使用 HTTPS 协议的 API 端点

## 故障排除

### 常见问题

1. **配置文件不生效**
   - 检查 JSON 格式是否正确
   - 确认 `enabled` 设置为 `true`
   - 查看日志输出获取详细错误信息

2. **请求失败**
   - 验证 API URL 是否正确
   - 检查网络连接
   - 确认 API 密钥是否有效

3. **音频播放失败**
   - 检查响应格式是否正确
   - 确认 API 返回的是音频数据
   - 尝试不同的 `responseFormat` 设置

### 调试方法

1. 查看 VPetLLM 日志输出
2. 使用 TTS 测试功能验证配置
3. 检查 API 服务的文档和示例

## 新功能特性

### 键值对格式的优势

- **易于编写**：requestBody 现在使用与 customHeaders 相同的键值对格式
- **类型支持**：支持字符串、数字、布尔值、嵌套对象等多种数据类型
- **维护简单**：无需手动处理 JSON 字符串转义
- **可读性强**：配置结构清晰，便于理解和修改

### 占位符支持

- `{text}` 占位符会在所有字符串值中被替换
- 支持嵌套对象中的占位符替换
- 自动处理 JSON 序列化

## 技术支持

如果您在使用 DIY TTS 功能时遇到问题，请：

1. 检查配置文件格式
2. 查看应用程序日志
3. 参考 API 服务商的文档
4. 在 VPetLLM 项目中提交 Issue

---

**注意**：DIY TTS 功能需要您有一定的技术基础，包括了解 HTTP 请求、JSON 格式和 API 使用方法。新的键值对格式使配置更加简单易用。