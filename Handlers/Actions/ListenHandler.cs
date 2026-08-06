using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers.Actions
{
    /// <summary>
    /// 「听用户说话」工具：AI 主动唤起语音输入（STT）。
    /// 命令格式：&lt;|listen_begin|&gt; &lt;|listen_end|&gt;
    ///
    /// 录音窗口弹出后由用户说话，转写文本会按普通用户消息回到对话里，
    /// 所以本工具只负责「开麦」，不需要也不应该阻塞等待结果。
    /// </summary>
    public class ListenHandler : IActionHandler
    {
        private readonly Setting _settings;

        public string Keyword => "listen";
        public ActionType ActionType => ActionType.Tool;
        public ActionCategory Category => ActionCategory.Unknown;

        /// <summary>
        /// ASR 关闭时描述为空，避免 AI 以为自己能听
        /// </summary>
        public string Description => IsAvailable
            ? PromptHelper.Get("Handler_Listen_Description", VPetLLM.Instance?.Settings?.PromptLanguage ?? "zh")
            : "";

        public bool IsAvailable => _settings?.ASR?.IsEnabled == true;

        public ListenHandler(Setting settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public Task Execute(string value, IMainWindow mainWindow)
        {
            if (!IsAvailable)
            {
                Logger.Log("ListenHandler: 语音识别未启用，忽略命令");
                return Task.CompletedTask;
            }

            if (InterruptManager.IsInterrupted)
            {
                Logger.Log("ListenHandler: 已中断，不唤起录音");
                return Task.CompletedTask;
            }

            var plugin = VPetLLM.Instance;
            if (plugin is null)
            {
                return Task.CompletedTask;
            }

            // 已经在录音了就别再敲一次——ShowVoiceInputWindow 走的是快捷键的切换语义，
            // 重复调用反而会把正在进行的录音停掉
            if (plugin.IsVoiceInputActive)
            {
                Logger.Log("ListenHandler: 录音已在进行中，忽略重复请求");
                return Task.CompletedTask;
            }

            if (!RateLimiter.TryAcquire("listen", 3, TimeSpan.FromMinutes(1)))
            {
                Logger.Log("ListenHandler: 触发限流，拒绝唤起录音");
                return Task.CompletedTask;
            }

            try
            {
                Logger.Log("ListenHandler: AI 主动唤起语音输入");
                System.Windows.Application.Current.Dispatcher.Invoke(() => plugin.ShowVoiceInputWindow());
            }
            catch (Exception ex)
            {
                Logger.Log($"ListenHandler: 唤起语音输入失败: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public Task Execute(int value, IMainWindow mainWindow) => Task.CompletedTask;
        public Task Execute(IMainWindow mainWindow) => Execute("", mainWindow);
        public int GetAnimationDuration(string animationName) => 0;
    }
}
