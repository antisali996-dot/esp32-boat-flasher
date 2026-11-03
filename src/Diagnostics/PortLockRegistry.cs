using System;
using System.Collections.Generic;

namespace BoatTool
{
    // 串口占用表：诊断记录期间锁定该串口，核心层据此拒绝烧录/配对/扫描。
    // 锁带 owner；TryAcquire 不会覆盖其他 owner；释放按 owner 校验。
    public interface IPortLockRegistry
    {
        bool IsLocked(string port);
        bool IsOwnedBy(string port, string owner);
        void Lock(string port, string owner);
        // 仅在空闲或已被同一 owner 持有时成功；被其他 owner 占用时返回 false。
        bool TryAcquire(string port, string owner);
        void Unlock(string port);
        // 只释放指定 owner 持有的锁；不是该 owner 时不动。
        bool ReleaseIfOwned(string port, string owner);
        IList<string> LockedPorts { get; }
    }

    public sealed class PortLockRegistry : IPortLockRegistry
    {
        private readonly Dictionary<string, string> locks =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();

        public bool IsLocked(string port)
        {
            if (string.IsNullOrEmpty(port)) return false;
            lock (gate) { return locks.ContainsKey(port); }
        }

        public bool IsOwnedBy(string port, string owner)
        {
            if (string.IsNullOrEmpty(port) || string.IsNullOrEmpty(owner)) return false;
            lock (gate)
            {
                string current;
                return locks.TryGetValue(port, out current) &&
                       string.Equals(current, owner, StringComparison.Ordinal);
            }
        }

        // 兼容旧调用：仅在没有其他 owner 时建立锁。
        public void Lock(string port, string owner)
        {
            if (string.IsNullOrEmpty(port)) return;
            lock (gate)
            {
                string current;
                if (locks.TryGetValue(port, out current) &&
                    !string.Equals(current, owner ?? "diagnostics", StringComparison.Ordinal))
                {
                    // 不覆盖他人锁：静默保留原 owner（调用方应改用 TryAcquire 判断结果）。
                    return;
                }
                locks[port] = owner ?? "diagnostics";
            }
        }

        public bool TryAcquire(string port, string owner)
        {
            if (string.IsNullOrEmpty(port)) return false;
            string o = owner ?? "diagnostics";
            lock (gate)
            {
                string current;
                if (!locks.TryGetValue(port, out current)) { locks[port] = o; return true; }
                return string.Equals(current, o, StringComparison.Ordinal);
            }
        }

        public void Unlock(string port)
        {
            if (string.IsNullOrEmpty(port)) return;
            lock (gate) { locks.Remove(port); }
        }

        public bool ReleaseIfOwned(string port, string owner)
        {
            if (string.IsNullOrEmpty(port) || string.IsNullOrEmpty(owner)) return false;
            lock (gate)
            {
                string current;
                if (!locks.TryGetValue(port, out current)) return true;
                if (!string.Equals(current, owner, StringComparison.Ordinal)) return false;
                locks.Remove(port);
                return true;
            }
        }

        public IList<string> LockedPorts
        {
            get { lock (gate) { return new List<string>(locks.Keys); } }
        }
    }
}
