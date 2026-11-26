using System;
using System.Collections.Generic;
using S2A.Services;

namespace S2A.Services.Tests
{
    public static partial class Program
    {
        // 即时存档请求日志有界（维护者 11-19「快照日志要有界」；D-NXYJ：哈迪斯离线轮询 nifm，开局 40 秒 128 MB / 63.8 万条，托管堆涨到 abort）。
        //   用生产的规则（SnapLog.Drop / NifmStateRule）与上限判定（SnapLog.CapReached）+ 真 ReqTable，每条日志 200 字节，期望值手算：
        //   ① nifm IRequest：Submit 一次后每帧取请求状态 10 万次 → 进日志 1 条（Submit 之后第一次改状态）；取结果 10 万次 → 0 条
        //   ② 改状态 / 建句柄：取事件句柄 2、取消 3、提交 4、设选项 11 各 1 次 → 4 条；第二次 Submit 后再取状态 → 又 1 条
        //   ③ 规则管不到的服务每帧轮询 10 万次（上限设 1 MB）：日志字节 ≤ 1 MB + 一条 200 字节
        //   ④ 负对照（= 旧代码：规则关、不设上限）：③ 的 10 万次全进日志 = 2000 万字节，超过 ③ 的上界 → 判红
        [SoloTest("--snap-log-bound")]
        private static int SnapLogBound()
        {
            const string Nifm = "Ryujinx.HLE.HOS.Services.Nifm.StaticService.IRequest", Other = "Some.Unruled.IPolledService";
            const int Polls = 100000, EntBytes = 200;
            var key = new ReqTable.Key(0x1234, 7);
            int seq = 0;
            // 生产判定：不记 = Drop 或 NifmStateRule；记之前超上限就停（同 SnapLog.Begin 的 CapLocked）
            long Run(ReqTable tab, string n, IEnumerable<long> cmds, long cap, out int kept)
            {
                kept = 0;
                foreach (long c in cmds)
                {
                    if (SnapLog.Drop(n, c) || SnapLog.NifmStateRule(n, c, key)) continue;
                    if (SnapLog.CapReached(tab, cap)) continue;
                    tab.Add(seq++, new byte[EntBytes], key); kept++;
                }
                return tab.Bytes;
            }
            IEnumerable<long> Repeat(long c, int k) { for (int i = 0; i < k; i++) yield return c; }
            IEnumerable<long> SubmitThenPoll() { yield return 4; foreach (long c in Repeat(0, Polls)) yield return c; }
            IEnumerable<long> Again() { yield return 4; yield return 0; yield return 0; }
            var rows = new List<(string N, string E, string A)>();
            var t1 = new ReqTable { Collecting = false };
            Run(t1, Nifm, SubmitThenPoll(), long.MaxValue, out int k1);
            rows.Add(("① Submit 后取请求状态 10 万次，进日志条数（含 Submit）", "2", k1.ToString()));
            Run(new ReqTable { Collecting = false }, Nifm, Repeat(1, Polls), long.MaxValue, out int k1b);
            rows.Add(("① 取结果 10 万次，进日志条数", "0", k1b.ToString()));
            Run(new ReqTable { Collecting = false }, Nifm, new long[] { 2, 3, 11 }, long.MaxValue, out int k2);
            Run(new ReqTable { Collecting = false }, Nifm, Again(), long.MaxValue, out int k2b);
            rows.Add(("② 取事件句柄 / 取消 / 设选项各 1 次，进日志条数", "3", k2.ToString()));
            rows.Add(("② 再 Submit 一次后取状态两次，进日志条数（Submit + 第一次取状态）", "2", k2b.ToString()));
            const long Cap = 1L << 20;
            long b3 = Run(new ReqTable { Collecting = false }, Other, Repeat(5, Polls), Cap, out _);
            rows.Add(("③ 规则管不到的服务轮询 10 万次，日志字节 ≤ 上限 + 一条", $"≤ {Cap + EntBytes}", b3 <= Cap + EntBytes ? $"≤ {Cap + EntBytes}" : b3.ToString()));
            SnapLog.NifmRulesOn = false;
            try
            {
                long b4 = Run(new ReqTable { Collecting = false }, Other, Repeat(5, Polls), long.MaxValue, out _);
                rows.Add(("④ 负对照（规则关、不设上限 = 旧代码）：日志字节超过 ③ 的上界", "超过", b4 > Cap + EntBytes ? "超过" : $"没超（{b4}）"));
                Run(new ReqTable { Collecting = false }, Nifm, Repeat(0, Polls), long.MaxValue, out int k4);
                rows.Add(("④ 负对照：nifm 规则关掉，取请求状态 10 万次进日志条数", Polls.ToString(), k4.ToString()));
            }
            finally { SnapLog.NifmRulesOn = true; }
            int failures = 0;
            foreach ((string n, string ex, string a) in rows) { bool ok = ex == a; if (!ok) failures++; Console.WriteLine($"{(ok ? "✓" : "✗")} {n}：{ex} → {a}"); }
            return failures == 0 ? 0 : 1;
        }
    }
}
