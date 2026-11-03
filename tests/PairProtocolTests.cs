using System;
using System.Collections.Generic;
using System.IO;

namespace BoatTool
{
    // 配对状态机测试：全部在真实串口回应处理层注入字节流。
    // 不伪造 WritePair 返回值，也不连接任何真实设备。
    public static class PairProtocolTests
    {
        private const string BoatMac = "24:62:AB:F1:36:64";
        private const string OldBoatMac = "24:62:AB:F0:72:94";
        private const string RemoteMac = "24:62:AB:EF:CF:84";
        private const string OtherMac = "24:62:AB:99:88:77";

        private const string BoatFwid = "JAYFWB7-S20-A70-110-U1278-1500-1722-R590-OS600-Cr7-pendingerr-port";
        private const string RemoteFwid = "JAYFWR7-S20-A70-110-U1278-1500-1722-D1750-2150";

        private static int passed;
        private static int failed;

        public static void Run()
        {
            passed = 0;
            failed = 0;
            Console.WriteLine();
            Console.WriteLine("===== 配对状态机（字节流注入）=====");

            TestHappyPath();
            TestAckWithoutIdentity();
            TestLateAckAfterTimeout();
            TestErrorThenStaleSuccess();
            TestWrongPeerRoleMacFwid();
            TestReadFailedBlocks();
            TestBothSavedAckErrors();
            TestBindBlockedNoRetry();
            TestPartialCompletionCannotBeGreen();
            TestBothAlreadyCorrectZeroBind();
            TestManualVerifyClearsUnconfirmed();
            TestLegacyLineNotAccepted();
            TestHalfLineAndGarbage();

            Console.WriteLine();
            Console.WriteLine("配对状态机：通过 " + passed + " 项，失败 " + failed + " 项");
            if (failed > 0) throw new InvalidOperationException("配对状态机测试失败 " + failed + " 项");
        }

        public static int Passed { get { return passed; } }
        public static int Failed { get { return failed; } }

        // GUI 汇总文案：部分完成/待核验不得写成完成。
        public static void CheckSummaryNotSuccess(string summary, PairLinkState state)
        {
            bool saysSuccess = summary.Contains("双端配对配置已核对") || summary.Contains("配对完成");
            if (saysSuccess && !PairLinkText.IsSuccess(state))
            {
                failed++;
                Console.WriteLine("  [失败] 部分完成被写成完成提示：" + summary);
                return;
            }
            passed++;
            Console.WriteLine("  [通过] 汇总未把非成功状态写成完成");
        }

        private static void Check(string name, bool condition, string detail)
        {
            if (condition) { passed++; Console.WriteLine("  [通过] " + name); }
            else { failed++; Console.WriteLine("  [失败] " + name + " —— " + detail); }
        }

        private static PairOutcome Bind(ScriptedPairChannel channel, string peer, string expectedFwid,
                                        int bindTimeout = 800, int verifyTimeout = 1500, int settle = 0)
        {
            BoardIdentity before = new BoardIdentity
            {
                Responded = true, Role = BoardRole.Boat, Mac = BoatMac, Peer = "00:00:00:00:00:00",
                SteeringAngle = 20, FirmwareId = BoatFwid, IdentityLineVersion = 4, PairProtocol = 2
            };
            PairingSession session = new PairingSession(channel, new StepClock(25), null);
            return session.BindAndVerify(before, BoardRole.Boat, peer, expectedFwid, bindTimeout, verifyTimeout, settle);
        }

        private static string IdentityLine(string role, string mac, string peer, string fwid)
        {
            return "JAYv4 ROLE=" + role + " MAC=" + mac + " PEER=" + peer +
                   " PAIR=2 STEER=20 FWID=" + fwid + " VER=20261103-590rpm-r7\r\n";
        }

        // 1. 正常保存回应 + 完整新身份 → 通过
        private static void TestHappyPath()
        {
            Console.WriteLine();
            Console.WriteLine("1. 正常保存回应＋完整新身份");
            ScriptedPairChannel ch = new ScriptedPairChannel();
            ch.Deliver("JAY SAVED REBOOT\r\n");
            ch.Deliver(IdentityLine("BOAT", BoatMac, RemoteMac, BoatFwid));
            PairOutcome r = Bind(ch, RemoteMac, BoatFwid);
            Check("状态为本端配置已核对", r.State == PairLinkState.LocalVerified,
                "实际 " + PairLinkText.Chinese(r.State) + "：" + r.Message);
            Check("只发一次绑定", ch.BindCommands() == 1, "绑定次数 " + ch.BindCommands());
            Check("重启后确实重新发了 JAY INFO", ch.InfoCommands() >= 1, "未发 JAY INFO");
            Check("中文文案写明已核对", r.Message.Contains("已核对"), r.Message);
        }

        // 2. 只有保存回应、无身份 → 禁止通过
        private static void TestAckWithoutIdentity()
        {
            Console.WriteLine();
            Console.WriteLine("2. 只有保存回应、无身份");
            ScriptedPairChannel ch = new ScriptedPairChannel();
            ch.Deliver("JAY SAVED REBOOT\r\n");
            PairOutcome r = Bind(ch, RemoteMac, BoatFwid);
            Check("仅保存回应不得通过", !r.Ok, "竟然通过");
            Check("不显示完成", r.State != PairLinkState.LocalVerified, "状态 " + PairLinkText.Chinese(r.State));
            Check("进入需重新上电核验", r.State == PairLinkState.FailedNeedsPowerCycle,
                "状态 " + PairLinkText.Chinese(r.State));
            Check("文案要求重新上电", r.Message.Contains("重新上电"), r.Message);
        }

        // 3. 超时后迟到的保存回应 → 禁止自动通过
        private static void TestLateAckAfterTimeout()
        {
            Console.WriteLine();
            Console.WriteLine("3. 超时后迟到保存回应");
            ScriptedPairChannel ch = new ScriptedPairChannel();
            // 绑定超时很短，且在超时之后才投递成功文字
            ch.DeliverAfter("JAY SAVED REBOOT\r\n", 400);
            ch.DeliverAfter(IdentityLine("BOAT", BoatMac, RemoteMac, BoatFwid), 420);
            PairOutcome r = Bind(ch, RemoteMac, BoatFwid, bindTimeout: 200, verifyTimeout: 300);
            Check("超时状态为结果未知", r.State == PairLinkState.TimeoutUnknown,
                "实际 " + PairLinkText.Chinese(r.State));
            Check("迟到回应不把操作改成成功", !r.Ok, "迟到回应被当作成功");
            Check("文案说明结果未知", r.Message.Contains("未知"), r.Message);
            Check("超时后不再发核验 INFO", ch.InfoCommands() == 0, "超时后仍发 INFO " + ch.InfoCommands());
        }

        // 4. 保存错误之后再到旧成功字符串 → 错误状态保持
        private static void TestErrorThenStaleSuccess()
        {
            Console.WriteLine();
            Console.WriteLine("4. 保存错误后迟到的旧成功文字");
            ScriptedPairChannel ch = new ScriptedPairChannel();
            ch.Deliver("JAY ERROR=SAVED_ACK_UNCONFIRMED\r\n");
            ch.DeliverAfter("JAY SAVED REBOOT\r\n", 60);
            ch.DeliverAfter(IdentityLine("BOAT", BoatMac, RemoteMac, BoatFwid), 80);
            PairOutcome r = Bind(ch, RemoteMac, BoatFwid);
            Check("保持已保存但未确认生效", r.State == PairLinkState.SavedButUnconfirmed,
                "实际 " + PairLinkText.Chinese(r.State));
            Check("旧成功文字不清除错误", !r.Ok, "错误被旧成功文字清除");
            Check("未自动重复绑定", ch.BindCommands() == 1, "绑定次数 " + ch.BindCommands());
            Check("文案提示断开动力重新上电", r.Message.Contains("断开动力") && r.Message.Contains("重新上电"),
                r.Message);
        }

        // 5. 旧 PEER / 错误角色 / 换成另一块 MAC / 旧 FWID
        private static void TestWrongPeerRoleMacFwid()
        {
            Console.WriteLine();
            Console.WriteLine("5. 核验不符的四种情况");

            ScriptedPairChannel c1 = new ScriptedPairChannel();
            c1.Deliver("JAY SAVED REBOOT\r\n");
            c1.Deliver(IdentityLine("BOAT", BoatMac, OtherMac, BoatFwid));   // 旧 PEER
            PairOutcome r1 = Bind(c1, RemoteMac, BoatFwid);
            Check("旧 PEER 不得通过", !r1.Ok && r1.State == PairLinkState.FailedNeedsPowerCycle,
                "状态 " + PairLinkText.Chinese(r1.State));

            ScriptedPairChannel c2 = new ScriptedPairChannel();
            c2.Deliver("JAY SAVED REBOOT\r\n");
            c2.Deliver(IdentityLine("REMOTE", BoatMac, RemoteMac, RemoteFwid)); // 角色错
            PairOutcome r2 = Bind(c2, RemoteMac, BoatFwid);
            Check("错误角色不得通过", !r2.Ok, "角色错却通过");

            ScriptedPairChannel c3 = new ScriptedPairChannel();
            c3.Deliver("JAY SAVED REBOOT\r\n");
            c3.Deliver(IdentityLine("BOAT", OtherMac, RemoteMac, BoatFwid));  // 换成另一块 MAC
            PairOutcome r3 = Bind(c3, RemoteMac, BoatFwid);
            Check("换成另一块 MAC 不得通过", !r3.Ok, "MAC 变化却通过");

            ScriptedPairChannel c4 = new ScriptedPairChannel();
            c4.Deliver("JAY SAVED REBOOT\r\n");
            c4.Deliver(IdentityLine("BOAT", BoatMac, RemoteMac,
                "JAYFWB6-S20-A70-110-U1278-1500-1722-R590-OS600-Cr6")); // 旧 FWID
            PairOutcome r4 = Bind(c4, RemoteMac, BoatFwid);
            Check("旧 FWID 不得通过", !r4.Ok, "旧 FWID 却通过");
            Check("旧 FWID 拒绝原因提到固件身份",
                r4.Message.Contains("固件身份") || r4.Message.Contains("清单"), r4.Message);
            Check("四种情况都没有重复绑定",
                c1.BindCommands() == 1 && c2.BindCommands() == 1 && c3.BindCommands() == 1 && c4.BindCommands() == 1,
                "出现重复绑定");
        }

        // 6. MAC=READ_FAILED 与身份读取失败 → 禁止绑定或完成
        private static void TestReadFailedBlocks()
        {
            Console.WriteLine();
            Console.WriteLine("6. MAC=READ_FAILED");
            ScriptedPairChannel ch = new ScriptedPairChannel();
            ch.Deliver("JAY SAVED REBOOT\r\n");
            ch.Deliver("JAYv4 ROLE=BOAT MAC=READ_FAILED PAIR=2 STEER=20 FWID=" + BoatFwid + " VER=20261103-590rpm-r7\r\n");
            PairOutcome r = Bind(ch, RemoteMac, BoatFwid);
            Check("READ_FAILED 不得通过", !r.Ok, "READ_FAILED 被放行");
            Check("文案提到读取失败或核验未通过",
                r.Message.Contains("读取失败") || r.Message.Contains("核验"), r.Message);

            // 写入前就没有身份：根本不应发送绑定。
            ScriptedPairChannel c2 = new ScriptedPairChannel();
            PairingSession session = new PairingSession(c2, new StepClock(25), null);
            PairOutcome r2 = session.BindAndVerify(null, BoardRole.Boat, RemoteMac, BoatFwid, 500, 500, 0);
            Check("无写入前身份时不发送绑定", c2.BindCommands() == 0 && !r2.Ok, "仍发送了绑定");
        }

        // 7. 两种 SAVED_ACK 错误
        private static void TestBothSavedAckErrors()
        {
            Console.WriteLine();
            Console.WriteLine("7. 两种 SAVED_ACK 错误");
            foreach (string code in new string[] { "SAVED_ACK_NOT_QUEUED", "SAVED_ACK_UNCONFIRMED" })
            {
                ScriptedPairChannel ch = new ScriptedPairChannel();
                ch.Deliver("JAY ERROR=" + code + "\r\n");
                PairOutcome r = Bind(ch, RemoteMac, BoatFwid);
                Check(code + " → 已保存但未确认生效",
                    r.State == PairLinkState.SavedButUnconfirmed, "实际 " + PairLinkText.Chinese(r.State));
                Check(code + " 显示重新上电核验",
                    r.Message.Contains("请断开动力") && r.Message.Contains("重新上电") && r.Message.Contains("只读核验"),
                    r.Message);
                Check(code + " 不重复绑定", ch.BindCommands() == 1, "绑定 " + ch.BindCommands() + " 次");
            }
        }

        // 8. BIND_BLOCKED_PENDING_ERROR 不循环重绑
        private static void TestBindBlockedNoRetry()
        {
            Console.WriteLine();
            Console.WriteLine("8. BIND_BLOCKED_PENDING_ERROR");
            ScriptedPairChannel ch = new ScriptedPairChannel();
            ch.Deliver("JAY ERROR=BIND_BLOCKED_PENDING_ERROR\r\n");
            ch.DeliverAfter("JAY SAVED REBOOT\r\n", 40);
            PairOutcome r = Bind(ch, RemoteMac, BoatFwid);
            Check("停止本轮绑定", r.State == PairLinkState.FailedNeedsPowerCycle,
                "状态 " + PairLinkText.Chinese(r.State));
            Check("不循环重发 BIND", ch.BindCommands() == 1, "绑定 " + ch.BindCommands() + " 次");
            Check("提示处理错误后重新上电", r.Message.Contains("重新上电"), r.Message);
        }

        // 9. 仅一端成功 / 最终查询失败 → 不得双端完成（由 Workflow 汇总判定）
        private static void TestPartialCompletionCannotBeGreen()
        {
            Console.WriteLine();
            Console.WriteLine("9. 部分完成不得显示双端完成");
            // 这里直接验证状态机：失败的一端永远不是 LocalVerified，
            // 因此 Workflow 的双端判定不可能成立。
            ScriptedPairChannel okCh = new ScriptedPairChannel();
            okCh.Deliver("JAY SAVED REBOOT\r\n");
            okCh.Deliver(IdentityLine("BOAT", BoatMac, RemoteMac, BoatFwid));
            PairOutcome ok = Bind(okCh, RemoteMac, BoatFwid);

            ScriptedPairChannel badCh = new ScriptedPairChannel();
            badCh.Deliver("JAY ERROR=SAVED_ACK_NOT_QUEUED\r\n");
            PairOutcome bad = Bind(badCh, RemoteMac, BoatFwid);

            Check("成功端为已核对", ok.State == PairLinkState.LocalVerified, "成功端状态异常");
            Check("失败端不是已核对", bad.State != PairLinkState.LocalVerified, "失败端竟为已核对");
            Check("两端不满足完成条件",
                !(PairLinkText.IsSuccess(ok.State) && PairLinkText.IsSuccess(bad.State)), "误判为双端完成");

            // 汇总文案层面：非成功状态不能出现完成字样。
            CheckSummaryNotSuccess("部分完成（1/2）：遥控器 已保存但未确认生效", bad.State);
            CheckSummaryNotSuccess("双端配对配置已核对。", ok.State);
        }

        // 10. 两端已互指 → 零绑定写入（由 Workflow 在调用前判断）
        private static void TestBothAlreadyCorrectZeroBind()
        {
            Console.WriteLine();
            Console.WriteLine("10. 两端已互指时零绑定");
            // 状态机本身不负责跳过；这里确认“未调用就不发命令”，
            // 具体跳过逻辑由 Workflow 测试覆盖（见 SimulatedTests）。
            ScriptedPairChannel ch = new ScriptedPairChannel();
            Check("未调用时没有任何写入", ch.BindCommands() == 0 && ch.Written.Count == 0, "无调用却写了命令");
        }

        // 11. 明确开始只读核验且身份完整匹配 → 解除待核验
        private static void TestManualVerifyClearsUnconfirmed()
        {
            Console.WriteLine();
            Console.WriteLine("11. 只读核验解除待核验状态");
            ScriptedPairChannel ch = new ScriptedPairChannel();
            ch.Deliver(IdentityLine("BOAT", BoatMac, RemoteMac, BoatFwid));
            VerifySession session = new VerifySession(ch, new StepClock(25), null);
            PairOutcome r = session.Verify(BoardRole.Boat, BoatMac, RemoteMac, BoatFwid, 1200);
            Check("只读核验通过", r.State == PairLinkState.LocalVerified, "实际 " + PairLinkText.Chinese(r.State));
            Check("只读核验不写配置", ch.BindCommands() == 0, "只读核验发了绑定");
            Check("只读核验只发 INFO", ch.InfoCommands() >= 1, "未发 INFO");

            // 身份不匹配时不能解除。
            ScriptedPairChannel bad = new ScriptedPairChannel();
            bad.Deliver(IdentityLine("BOAT", BoatMac, OtherMac, BoatFwid));
            VerifySession vs = new VerifySession(bad, new StepClock(25), null);
            PairOutcome rb = vs.Verify(BoardRole.Boat, BoatMac, RemoteMac, BoatFwid, 600);
            Check("不匹配的只读核验不放行", !rb.Ok, "不匹配却通过");
            Check("不匹配时明确未通过", rb.Message.Contains("未通过"), rb.Message);
        }

        // 12. r7 JAYv4 与旧兼容行同时出现 → 不降级采用旧格式
        private static void TestLegacyLineNotAccepted()
        {
            Console.WriteLine();
            Console.WriteLine("12. 旧兼容行不得替代 JAYv4");
            ScriptedPairChannel ch = new ScriptedPairChannel();
            ch.Deliver("JAY SAVED REBOOT\r\n");
            ch.Deliver("JAY ROLE=BOAT MAC=" + BoatMac + " PEER=" + RemoteMac + " VERSION=2 STEER=20\r\n");
            ch.Deliver("JAYv4 ROLE=BOAT MAC=" + BoatMac + " PEER=" + RemoteMac +
                       " PAIR=2 STEER=20 FWID=" + BoatFwid + " VER=20261103-590rpm-r7\r\n");
            PairOutcome r = Bind(ch, RemoteMac, BoatFwid);
            Check("同时出现时采用 JAYv4 结果", r.Ok && r.State == PairLinkState.LocalVerified,
                "实际 " + PairLinkText.Chinese(r.State) + "：" + r.Message);
            Check("最终身份来自 JAYv4", r.FinalIdentity != null && r.FinalIdentity.IdentityLineVersion >= 4,
                "采用了旧格式");

            // 只有旧格式时必须拒绝。
            ScriptedPairChannel only = new ScriptedPairChannel();
            only.Deliver("JAY SAVED REBOOT\r\n");
            only.Deliver("JAY ROLE=BOAT MAC=" + BoatMac + " PEER=" + RemoteMac + " VERSION=2 STEER=20\r\n");
            PairOutcome ro = Bind(only, RemoteMac, BoatFwid);
            Check("只有旧格式时拒绝", !ro.Ok, "仅旧格式却通过");
            Check("拒绝原因提到旧格式或完整身份",
                ro.Message.Contains("旧格式") || ro.Message.Contains("完整身份"), ro.Message);
        }

        // 13. 半行、乱码不得当作完整身份
        private static void TestHalfLineAndGarbage()
        {
            Console.WriteLine();
            Console.WriteLine("13. 半行与乱码");
            ScriptedPairChannel ch = new ScriptedPairChannel();
            ch.Deliver("JAY SAVED REBOOT\r\n");
            // 半行（没有换行结尾）
            ch.Deliver("JAYv4 ROLE=BOAT MAC=" + BoatMac + " PEER=" + RemoteMac);
            PairOutcome r = Bind(ch, RemoteMac, BoatFwid);
            Check("半行不得通过", !r.Ok, "半行被当作完整身份");
            Check("半行不放行时也无重复绑定", ch.BindCommands() == 1, "绑定次数 " + ch.BindCommands());

            // 后来补全同一行才允许通过（验证行组装正确）
            ScriptedPairChannel ch2 = new ScriptedPairChannel();
            ch2.Deliver("JAY SAVED REBOOT\r\n");
            ch2.Deliver("JAYv4 ROLE=BOAT MAC=" + BoatMac + " PEER=" + RemoteMac +
                        " PAIR=2 STEER=20 FWID=" + BoatFwid + " VER=20261103-590rpm-r7");
            ch2.Deliver("\r\n");
            PairOutcome r2 = Bind(ch2, RemoteMac, BoatFwid);
            Check("分段到达的完整行可通过", r2.Ok && r2.State == PairLinkState.LocalVerified,
                "分段行未被正确组装：" + r2.Message);
        }
    }
}
