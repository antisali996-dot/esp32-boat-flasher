using System;
using System.Text;

namespace BoatTool
{
    // 控制台模拟入口：用假板子跑完整流程，不打开任何真实串口。
    internal static class Program
    {
        internal static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
            try
            {
                return SimulatedTests.Run(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine("测试异常：" + ex);
                return 1;
            }
        }
    }
}
