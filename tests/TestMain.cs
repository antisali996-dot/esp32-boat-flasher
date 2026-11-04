using System;
using System.IO;
using System.Text;

namespace BoatTool
{
    // 控制台模拟入口：用假板子跑完整流程，不打开任何真实串口。
    internal static class Program
    {
        private static int grandPassed;
        private static int grandFailed;
        private static readonly System.Text.RegularExpressions.Regex GroupResult =
            new System.Text.RegularExpressions.Regex("通过 (?<p>[0-9]+) 项，失败 (?<f>[0-9]+) 项");

        // 记录最近一行组结果；由汇总器解析。
        internal static void ReportGroupResult(string line)
        {
            System.Text.RegularExpressions.Match m = GroupResult.Match(line);
            if (m.Success)
            {
                grandPassed += int.Parse(m.Groups["p"].Value);
                grandFailed += int.Parse(m.Groups["f"].Value);
            }
        }

        internal static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
            CountingWriter writer = new CountingWriter(Console.Out);
            Console.SetOut(writer);
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
                try { DynamicLibraryTests.Run(); }
                catch (Exception ex) { Console.WriteLine("动态固件库测试失败：" + ex.Message); return 1; }
                try { DynamicIntegrationFixTests.Run(); }
                catch (Exception ex) { Console.WriteLine("动态集成修复测试失败：" + ex.Message); return 1; }
                try { SuiteSelectionGuiTests.Run(); }
                catch (Exception ex) { Console.WriteLine("套装摘要一致性测试失败：" + ex.Message); return 1; }
                return code;
            }
            catch (Exception ex)
            {
                Console.WriteLine("测试异常：" + ex);
                return 1;
            }
            finally
            {
                Console.SetOut(writer.Original);
                Console.WriteLine("==== 自动汇总：通过 " + writer.Passed + " 项，失败 " + writer.Failed + " 项 ====");
            }
        }

        // 计数 Console 输出中的组结果行（各测试组已有"通过 X 项，失败 Y 项"行）。
        private sealed class CountingWriter : TextWriter
        {
            public readonly TextWriter Original;
            public int Passed;
            public int Failed;

            public CountingWriter(TextWriter original) { Original = original; }
            public override Encoding Encoding { get { return Encoding.UTF8; } }

            public override void WriteLine(string value)
            {
                Original.WriteLine(value);
                System.Text.RegularExpressions.Match m =
                    System.Text.RegularExpressions.Regex.Match(value ?? "",
                        "通过 (?<p>[0-9]+) 项，失败 (?<f>[0-9]+) 项");
                if (m.Success)
                {
                    Passed += int.Parse(m.Groups["p"].Value);
                    Failed += int.Parse(m.Groups["f"].Value);
                }
            }
            public override void Write(string value) { Original.Write(value); }
            public override void Write(char value) { Original.Write(value); }
            public override void WriteLine() { Original.WriteLine(); }
        }
    }
}
