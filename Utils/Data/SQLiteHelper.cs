using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;

namespace VPetLLM.Utils.Data
{
    /// <summary>
    /// SQLite 加载辅助类
    /// 解决 e_sqlite3.dll 加载失败的问题
    /// </summary>
    public static class SQLiteHelper
    {
        private static bool _initialized = false;
        private static bool _loadSuccess = false;
        private static string _errorMessage = string.Empty;

        /// <summary>
        /// 初始化 SQLite，确保 e_sqlite3.dll 可以被正确加载
        /// </summary>
        /// <returns>是否成功加载</returns>
        public static bool Initialize()
        {
            if (_initialized)
            {
                return _loadSuccess;
            }

            _initialized = true;

            try
            {
                // 获取当前 DLL 所在目录
                var dllPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(dllPath))
                {
                    _errorMessage = "Cannot determine DLL directory";
                    Logger.Log($"SQLite initialization failed: {_errorMessage}");
                    return false;
                }

                // 确定架构
                var architecture = Environment.Is64BitProcess ? "win-x64" : "win-x86";
                var nativePath = Path.Combine(dllPath, "runtimes", architecture, "native");
                var sqliteDllPath = Path.Combine(nativePath, "e_sqlite3.dll");

                Logger.Log($"SQLite initialization: Architecture={architecture}, Path={sqliteDllPath}");

                // 检查文件是否存在
                if (!File.Exists(sqliteDllPath))
                {
                    _errorMessage = $"e_sqlite3.dll not found at: {sqliteDllPath}";
                    Logger.Log($"SQLite initialization failed: {_errorMessage}");
                    
                    // 尝试查找其他可能的位置
                    TryFindAlternativePaths(dllPath);
                    return false;
                }

                // 尝试预加载 DLL
                try
                {
                    var handle = LoadLibrary(sqliteDllPath);
                    if (handle == IntPtr.Zero)
                    {
                        var error = Marshal.GetLastWin32Error();
                        _errorMessage = $"Failed to load e_sqlite3.dll, Win32 Error: {error}";
                        Logger.Log($"SQLite initialization failed: {_errorMessage}");
                        
                        // 检查依赖项
                        CheckDependencies();
                        return false;
                    }

                    Logger.Log($"SQLite native library loaded successfully from: {sqliteDllPath}");
                    _loadSuccess = true;
                    return true;
                }
                catch (Exception ex)
                {
                    _errorMessage = $"Exception loading e_sqlite3.dll: {ex.Message}";
                    Logger.Log($"SQLite initialization failed: {_errorMessage}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _errorMessage = $"Unexpected error during SQLite initialization: {ex.Message}";
                Logger.Log($"SQLite initialization failed: {_errorMessage}");
                return false;
            }
        }

        /// <summary>
        /// 获取初始化错误信息
        /// </summary>
        public static string GetErrorMessage()
        {
            return _errorMessage;
        }

        /// <summary>
        /// 检查是否成功加载
        /// </summary>
        public static bool IsLoaded()
        {
            return _loadSuccess;
        }

        /// <summary>
        /// 尝试查找备用路径
        /// </summary>
        private static void TryFindAlternativePaths(string basePath)
        {
            Logger.Log("Searching for e_sqlite3.dll in alternative locations:");
            
            var searchPaths = new[]
            {
                Path.Combine(basePath, "e_sqlite3.dll"),
                Path.Combine(basePath, "runtimes", "win-x64", "native", "e_sqlite3.dll"),
                Path.Combine(basePath, "runtimes", "win-x86", "native", "e_sqlite3.dll"),
                Path.Combine(basePath, "runtimes", "win", "native", "e_sqlite3.dll"),
            };

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                {
                    Logger.Log($"  Found at: {path}");
                }
                else
                {
                    Logger.Log($"  Not found: {path}");
                }
            }
        }

        /// <summary>
        /// 检查依赖项
        /// </summary>
        private static void CheckDependencies()
        {
            Logger.Log("Checking system dependencies:");
            Logger.Log($"  OS: {Environment.OSVersion}");
            Logger.Log($"  64-bit Process: {Environment.Is64BitProcess}");
            Logger.Log($"  64-bit OS: {Environment.Is64BitOperatingSystem}");
            Logger.Log($"  .NET Version: {Environment.Version}");
            
            // 检查 VC++ 运行库
            Logger.Log("Note: e_sqlite3.dll requires Visual C++ Redistributable");
            Logger.Log("If loading fails, please install:");
            Logger.Log("  - Visual C++ Redistributable for Visual Studio 2015-2022");
            Logger.Log("  - Download from: https://aka.ms/vs/17/release/vc_redist.x64.exe");
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);
    }
}
