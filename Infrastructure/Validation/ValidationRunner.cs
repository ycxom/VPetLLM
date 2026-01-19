namespace VPetLLM.Infrastructure.Validation
{
    /// <summary>
    /// 验证运行器 - 执行基础设施验证的入口点
    /// </summary>
    public class ValidationRunner
    {
        /// <summary>
        /// 运行所有验证测试
        /// </summary>
        public static async Task<bool> RunAllValidationsAsync()
        {
            try
            {
                Console.WriteLine("=== VPetLLM 基础设施验证 ===\n");

                var result = await InfrastructureValidation.ValidateAllAsync();

                Console.WriteLine();
                result.PrintSummary();

                if (result.IsAllSuccess)
                {
                    Console.WriteLine("\n🎉 所有基础设施组件验证通过！");
                    return true;
                }
                else
                {
                    Console.WriteLine("\n⚠️  部分基础设施组件验证失败，请检查上述错误信息。");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ 验证过程中发生严重错误: {ex.Message}");
                Console.WriteLine($"堆栈跟踪: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// 主入口点（用于独立运行验证）
        /// </summary>
        public static async Task Main(string[] args)
        {
            var success = await RunAllValidationsAsync();
            Environment.Exit(success ? 0 : 1);
        }
    }
}