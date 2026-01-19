namespace VPetLLM.Services
{
    /// <summary>
    /// 语音输入状态枚举
    /// </summary>
    public enum VoiceInputState
    {
        /// <summary>
        /// 空闲状态
        /// </summary>
        Idle,

        /// <summary>
        /// 正在录音
        /// </summary>
        Recording,

        /// <summary>
        /// 编辑状态（识别完成但未发送）
        /// </summary>
        Editing
    }

    /// <summary>
    /// 语音输入服务接口
    /// </summary>
    public interface IVoiceInputService : IDisposable
    {
        /// <summary>
        /// 当前语音输入状态
        /// </summary>
        VoiceInputState CurrentState { get; }

        /// <summary>
        /// 转录完成事件，参数为转录文本
        /// </summary>
        event EventHandler<string>? TranscriptionCompleted;

        /// <summary>
        /// 状态变更事件
        /// </summary>
        event EventHandler<VoiceInputState>? StateChanged;

        /// <summary>
        /// 开始录音
        /// </summary>
        void StartRecording();

        /// <summary>
        /// 停止录音
        /// </summary>
        void StopRecording();

        /// <summary>
        /// 取消录音
        /// </summary>
        void CancelRecording();

        /// <summary>
        /// 处理快捷键按下事件
        /// </summary>
        void HandleHotkeyPressed();

        /// <summary>
        /// 更新快捷键配置
        /// </summary>
        void UpdateHotkey();

        /// <summary>
        /// 显示语音输入窗口
        /// </summary>
        void ShowVoiceInputWindow();
    }
}
