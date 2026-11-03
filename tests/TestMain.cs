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
                int code = SimulatedTests.Run(args);
                try { PairProtocolTests.Run(); }
                catch (Exception ex) { Console.WriteLine("配对状态机测试失败：" + ex.Message); return 1; }
                try { DiagnosticsTests.Run(); }
                catch (Exception ex) { Console.WriteLine("现场诊断测试失败：" + ex.Message); return 1; }
                try { DiagnosticsReviewTests.Run(); }
                catch (Exception ex) { Console.WriteLine("复审修复测试失败：" + ex.Message); return 1; }
                try { ReconnectFixTests.Run(); }
                catch (Exception ex) { Console.WriteLine("重连修复测试失败：" + ex.Message); return 1; }
                try { BoundaryFixTests.Run(); }
                catch (Exception ex) { Console.WriteLine("边界修复测试失败：" + ex.Message); return 1; }
                try { R10InterfaceTests.Run(); }
                catch (Exception ex) { Console.WriteLine("r10 接口测试失败：" + ex.Message); return 1; }
                try { R10FaultFixTests.Run(); }
                catch (Exception ex) { Console.WriteLine("故障关联修复测试失败：" + ex.Message); return 1; }
                try { FaultNumberTests.Run(); }
                catch (Exception ex) { Console.WriteLine("FAULT 数值校验测试失败：" + ex.Message); return 1; }
                try { R11AdaptationTests.Run(); }
                catch (Exception ex) { Console.WriteLine("r11 适配测试失败：" + ex.Message); return 1; }
                return code;
            }
            catch (Exception ex)
            {
                Console.WriteLine("测试异常：" + ex);
                return 1;
            }
        }
    }
}
