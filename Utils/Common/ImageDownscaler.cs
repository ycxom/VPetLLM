using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace VPetLLM.Utils.Common
{
    /// <summary>
    /// 把图像的边长钳制在上限以内，避免整屏 4K 截图直接怼给模型 API
    /// —— 请求体过大时服务端通常直接拒绝（413 / "image too large"），
    /// 而且 base64 编码后内存占用还要再翻一倍。
    /// </summary>
    public static class ImageDownscaler
    {
        /// <summary>
        /// 长边上限。1080 对主流视觉模型的识别效果已经足够，
        /// 再大基本只是徒增请求体积。
        /// </summary>
        public const int MaxDimension = 1080;

        /// <summary>
        /// 按比例缩放到长宽都不超过 <paramref name="maxDimension"/>，输出 PNG。
        ///
        /// 幂等：尺寸已经在范围内时原样返回入参，不做重新编码。
        /// 任何解码/编码失败都退回原始数据，宁可发一张大图也不要把功能弄挂。
        /// </summary>
        public static byte[]? ClampToMaxDimension(byte[]? imageData, int maxDimension = MaxDimension)
        {
            if (imageData is null || imageData.Length == 0 || maxDimension <= 0)
            {
                return imageData;
            }

            try
            {
                using var input = new MemoryStream(imageData, writable: false);
                using var source = Image.FromStream(input);

                if (source.Width <= maxDimension && source.Height <= maxDimension)
                {
                    return imageData;
                }

                var scale = Math.Min(
                    (double)maxDimension / source.Width,
                    (double)maxDimension / source.Height);
                var width = Math.Max(1, (int)Math.Round(source.Width * scale));
                var height = Math.Max(1, (int)Math.Round(source.Height * scale));

                var originalWidth = source.Width;
                var originalHeight = source.Height;

                using var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(resized))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.DrawImage(source, 0, 0, width, height);
                }

                using var output = new MemoryStream();
                resized.Save(output, ImageFormat.Png);
                var result = output.ToArray();

                Logger.Log($"ImageDownscaler: {originalWidth}x{originalHeight} ({imageData.Length} bytes) " +
                           $"缩放至 {width}x{height} ({result.Length} bytes)");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Log($"ImageDownscaler: 缩放失败，沿用原图: {ex.Message}");
                return imageData;
            }
        }
    }
}
