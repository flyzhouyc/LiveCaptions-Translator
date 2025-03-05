using System.Windows;
using System.Windows.Automation;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        private static AutomationElement? window = null;
        private static Caption? captions = null;
        private static Setting? settings = null;

        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }
        public static Caption? Captions
        {
            get => captions;
        }
        public static Setting? Settings
        {
            get => settings;
        }

        App()
        {
            try
            {
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

                Console.WriteLine("初始化LiveCaptions...");
                window = LiveCaptionsHandler.LaunchLiveCaptions();
                LiveCaptionsHandler.FixLiveCaptions(window);
                LiveCaptionsHandler.HideLiveCaptions(window);
                Console.WriteLine("LiveCaptions初始化完成");

                Console.WriteLine("加载字幕和设置组件...");
                captions = Caption.GetInstance();
                settings = Setting.Load();
                Console.WriteLine("组件加载完成");

                // 修改: 使用事件监听代替轮询同步
                Console.WriteLine("启动字幕监听...");
                captions.StartListening();
                Console.WriteLine("字幕监听已启动");
                
                // 仍然需要翻译线程
                Console.WriteLine("启动翻译线程...");
                Task.Run(async () => 
                {
                    try 
                    {
                        if (Captions != null)
                        {
                            await Captions.Translate();
                        }
                        else
                        {
                            Console.WriteLine("错误: Captions实例为空");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"翻译线程异常: {ex.Message}");
                    }
                });
                Console.WriteLine("应用初始化完成");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"应用初始化失败: {ex.Message}");
                MessageBox.Show($"应用启动失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        static void OnProcessExit(object sender, EventArgs e)
        {
            if (window != null)
            {
                LiveCaptionsHandler.RestoreLiveCaptions(window);
                LiveCaptionsHandler.KillLiveCaptions(window);
            }
        }
    }
}