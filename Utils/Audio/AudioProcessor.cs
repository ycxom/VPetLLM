namespace VPetLLM.Utils.Audio
{
    /// <summary>
    /// 音频处理工具类，支持音量增益等功能
    /// </summary>
    public static class AudioProcessor
    {
        /// <summary>
        /// 对音频数据应用音量增益
        /// </summary>
        /// <param name="audioData">原始音频数据</param>
        /// <param name="gainDb">增益值（dB），正值增大音量，负值减小音量</param>
        /// <returns>处理后的音频数据</returns>
        public static byte[] ApplyVolumeGain(byte[] audioData, double gainDb)
        {
            if (audioData is null || audioData.Length == 0)
                return audioData;

            // 如果增益为0，直接返回原数据
            if (Math.Abs(gainDb) < 0.01)
                return audioData;

            try
            {
                // 将dB转换为线性增益系数
                double gainLinear = Math.Pow(10.0, gainDb / 20.0);

                Logger.Log($"AudioProcessor: 应用音量增益 {gainDb:F1}dB (线性系数: {gainLinear:F3})");

                // 检查是否为WAV格式
                if (IsWavFormat(audioData))
                {
                    return ApplyGainToWav(audioData, gainLinear);
                }
                else
                {
                    // 对于MP3等压缩格式，我们无法直接处理音频数据
                    // 但增益信息会在TTSService中通过播放器音量控制应用
                    Logger.Log($"AudioProcessor: 检测到压缩音频格式，增益 {gainDb:F1}dB 将通过播放器音量控制应用");
                    return audioData;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"AudioProcessor: 音频增益处理失败: {ex.Message}");
                return audioData; // 失败时返回原数据
            }
        }

        /// <summary>
        /// 检查是否为WAV格式
        /// </summary>
        private static bool IsWavFormat(byte[] audioData)
        {
            if (audioData.Length < 12)
                return false;

            // 检查WAV文件头 "RIFF" 和 "WAVE"
            return audioData[0] == 0x52 && audioData[1] == 0x49 && audioData[2] == 0x46 && audioData[3] == 0x46 && // "RIFF"
                   audioData[8] == 0x57 && audioData[9] == 0x41 && audioData[10] == 0x56 && audioData[11] == 0x45;   // "WAVE"
        }

        /// <summary>
        /// 对WAV格式音频应用增益
        /// </summary>
        private static byte[] ApplyGainToWav(byte[] wavData, double gainLinear)
        {
            byte[] result = new byte[wavData.Length];
            Array.Copy(wavData, result, wavData.Length);

            try
            {
                // 查找data chunk
                int dataChunkStart = FindDataChunk(wavData);
                if (dataChunkStart == -1)
                {
                    Logger.Log("AudioProcessor: 未找到WAV数据块");
                    return wavData;
                }

                // 获取音频格式信息
                var formatInfo = ParseWavFormat(wavData);
                if (formatInfo is null)
                {
                    Logger.Log("AudioProcessor: 无法解析WAV格式信息");
                    return wavData;
                }

                Logger.Log($"AudioProcessor: WAV格式 - 通道数: {formatInfo.Channels}, 位深: {formatInfo.BitsPerSample}, 采样率: {formatInfo.SampleRate}Hz");

                // 处理音频样本
                int dataStart = dataChunkStart + 8; // 跳过"data"标识和长度字段
                int dataLength = BitConverter.ToInt32(wavData, dataChunkStart + 4);

                if (formatInfo.BitsPerSample == 16)
                {
                    ProcessInt16Samples(result, dataStart, dataLength, gainLinear);
                }
                else if (formatInfo.BitsPerSample == 8)
                {
                    ProcessInt8Samples(result, dataStart, dataLength, gainLinear);
                }
                else
                {
                    Logger.Log($"AudioProcessor: 不支持的位深度: {formatInfo.BitsPerSample}");
                    return wavData;
                }

                Logger.Log($"AudioProcessor: WAV音频增益处理完成，处理了 {dataLength} 字节数据");
            }
            catch (Exception ex)
            {
                Logger.Log($"AudioProcessor: WAV处理异常: {ex.Message}");
                return wavData;
            }

            return result;
        }

        /// <summary>
        /// 查找WAV文件中的data chunk
        /// </summary>
        private static int FindDataChunk(byte[] wavData)
        {
            for (int i = 12; i < wavData.Length - 8; i++)
            {
                if (wavData[i] == 0x64 && wavData[i + 1] == 0x61 &&
                    wavData[i + 2] == 0x74 && wavData[i + 3] == 0x61) // "data"
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// 解析WAV格式信息
        /// </summary>
        private static WavFormatInfo? ParseWavFormat(byte[] wavData)
        {
            try
            {
                // 查找fmt chunk (通常在位置12)
                int fmtStart = 12;
                if (wavData[fmtStart] != 0x66 || wavData[fmtStart + 1] != 0x6D ||
                    wavData[fmtStart + 2] != 0x74 || wavData[fmtStart + 3] != 0x20) // "fmt "
                {
                    // 如果不在标准位置，搜索fmt chunk
                    fmtStart = -1;
                    for (int i = 12; i < wavData.Length - 16; i++)
                    {
                        if (wavData[i] == 0x66 && wavData[i + 1] == 0x6D &&
                            wavData[i + 2] == 0x74 && wavData[i + 3] == 0x20)
                        {
                            fmtStart = i;
                            break;
                        }
                    }
                }

                if (fmtStart == -1)
                    return null;

                return new WavFormatInfo
                {
                    Channels = BitConverter.ToInt16(wavData, fmtStart + 10),
                    SampleRate = BitConverter.ToInt32(wavData, fmtStart + 12),
                    BitsPerSample = BitConverter.ToInt16(wavData, fmtStart + 22)
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 处理16位音频样本
        /// </summary>
        private static void ProcessInt16Samples(byte[] data, int start, int length, double gain)
        {
            for (int i = start; i < start + length - 1; i += 2)
            {
                short sample = BitConverter.ToInt16(data, i);
                int newSample = (int)(sample * gain);

                // 防止溢出
                newSample = Math.Max(-32768, Math.Min(32767, newSample));

                byte[] newBytes = BitConverter.GetBytes((short)newSample);
                data[i] = newBytes[0];
                data[i + 1] = newBytes[1];
            }
        }

        /// <summary>
        /// 处理8位音频样本
        /// </summary>
        private static void ProcessInt8Samples(byte[] data, int start, int length, double gain)
        {
            for (int i = start; i < start + length; i++)
            {
                int sample = data[i] - 128; // 转换为有符号
                int newSample = (int)(sample * gain);

                // 防止溢出
                newSample = Math.Max(-128, Math.Min(127, newSample));

                data[i] = (byte)(newSample + 128); // 转换回无符号
            }
        }

        /// <summary>
        /// WAV格式信息
        /// </summary>
        private class WavFormatInfo
        {
            public int Channels { get; set; }
            public int SampleRate { get; set; }
            public int BitsPerSample { get; set; }
        }
    }
}