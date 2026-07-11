using System;
using System.Collections.Generic;
using System.Globalization;

namespace ShangCloud.MMO.Threading
{
    /// <summary>
    /// 插帧同步引擎（移植自 core.js 的 _ensureInterpLoop / _mmoInterpState）。
    ///
    /// 接收端按 uid / varName 维护 { current, target } 状态，在游戏主循环里逐帧
    /// 用帧率无关的指数平滑把 current 逼近 target。规则与 core.js 完全一致：
    ///   - 首次收到：snap（current = target）
    ///   - 瞬移阈值（|target - current| >= 200）：snap
    ///   - 正常：只更新 target，由 Tick 做平滑写入 current
    ///   - 动态追赶：|diff| > 50 时提高因子（上限 0.8）
    ///   - 自动休眠：所有变量均到达 target 时 Tick 直接返回空列表
    ///
    /// 非数值、或不在 interp 集合中的变量，不参与插帧，仅存原始值供 GetSyncVar 回退。
    /// </summary>
    public sealed class MmoInterpEngine
    {
        private const double BaseFactor = 0.15;          // _MMO_INTERP_BASE_FACTOR
        private const double TeleportThreshold = 200.0;  // _MMO_INTERP_TELEPORT_THRESHOLD
        private const double Epsilon = 0.001;            // 收敛阈值
        private const double FrameRefMs = 16.67;         // 60fps 归一化

        private struct InterpState
        {
            public double Current;
            public double Target;
        }

        // uid -> (varName -> state)
        private readonly Dictionary<string, Dictionary<string, InterpState>> _state =
            new Dictionary<string, Dictionary<string, InterpState>>();

        // uid -> 需要 interp 的 varName 集合（由发送方在 __sync_var__ 中同步过来）
        private readonly Dictionary<string, HashSet<string>> _interpNames =
            new Dictionary<string, HashSet<string>>();

        // uid -> varName -> 最新原始值（非插帧变量与尚未建立状态时的回退值）
        private readonly Dictionary<string, Dictionary<string, string>> _raw =
            new Dictionary<string, Dictionary<string, string>>();

        /// <summary>
        /// 应用一次 __sync_var__ 更新。
        /// </summary>
        /// <param name="uid">发送方 UID</param>
        /// <param name="vars">变量名→值（字符串）</param>
        /// <param name="interp">需要插帧平滑的变量名集合，可为 null（沿用已有集合）。</param>
        public void ApplySync(string uid, IDictionary<string, string> vars, IReadOnlyCollection<string> interp)
        {
            if (string.IsNullOrEmpty(uid) || vars == null) return;

            if (!_state.TryGetValue(uid, out var byVar))
            {
                byVar = new Dictionary<string, InterpState>();
                _state[uid] = byVar;
            }
            if (!_raw.TryGetValue(uid, out var rawByVar))
            {
                rawByVar = new Dictionary<string, string>();
                _raw[uid] = rawByVar;
            }

            // 更新 interp 集合
            if (interp != null)
            {
                _interpNames[uid] = new HashSet<string>(interp);
            }
            _interpNames.TryGetValue(uid, out var interpSet);

            foreach (var kv in vars)
            {
                string name = kv.Key;
                string v = kv.Value ?? string.Empty;
                rawByVar[name] = v;

                bool needInterp = interpSet != null && interpSet.Contains(name);
                if (!needInterp) continue;

                // 非数值不插帧
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double numV))
                    continue;

                if (!byVar.TryGetValue(name, out var st))
                {
                    // 首次收到：snap
                    byVar[name] = new InterpState { Current = numV, Target = numV };
                }
                else if (Math.Abs(numV - st.Current) >= TeleportThreshold)
                {
                    // 瞬移检测：snap
                    byVar[name] = new InterpState { Current = numV, Target = numV };
                }
                else
                {
                    // 正常：仅更新 target，由 Tick 平滑
                    st.Target = numV;
                    byVar[name] = st;
                }
            }
        }

        /// <summary>
        /// 在游戏主循环中调用，推进所有插帧变量的 current → target。
        /// 返回本次发生显著变化的 (uid, varName, current) 列表，调用方据此回写到场景对象。
        /// 全部收敛时返回空列表（自动休眠）。
        /// </summary>
        /// <param name="deltaTimeSeconds">自上一帧以来的时间（秒）。</param>
        public List<(string uid, string varName, double value)> Tick(double deltaTimeSeconds)
        {
            var changed = new List<(string, string, double)>();
            if (_state.Count == 0) return changed;

            double dtMs = deltaTimeSeconds * 1000.0;
            // 帧率无关平滑因子
            double baseFactor = 1.0 - Math.Pow(1.0 - BaseFactor, dtMs / FrameRefMs);

            foreach (var uidEntry in _state)
            {
                string uid = uidEntry.Key;
                var byVar = uidEntry.Value;
                // 收集需要修改的键值，避免边遍历边修改
                var updates = new List<KeyValuePair<string, InterpState>>();
                foreach (var nameEntry in byVar)
                {
                    string name = nameEntry.Key;
                    InterpState st = nameEntry.Value;
                    double diff = st.Target - st.Current;

                    if (Math.Abs(diff) <= Epsilon)
                    {
                        if (st.Current != st.Target)
                        {
                            st.Current = st.Target;
                            updates.Add(new KeyValuePair<string, InterpState>(name, st));
                        }
                        continue;
                    }

                    // 动态追赶：偏差过大时提高因子
                    double factor = baseFactor;
                    if (Math.Abs(diff) > 50.0)
                    {
                        factor = Math.Min(baseFactor * (Math.Abs(diff) / 50.0), 0.8);
                    }

                    st.Current += diff * factor;
                    updates.Add(new KeyValuePair<string, InterpState>(name, st));

                    // 脏检查：仅在变化显著时上报
                    if (Math.Abs(diff * factor) > Epsilon)
                    {
                        changed.Add((uid, name, st.Current));
                    }
                }
                foreach (var u in updates)
                {
                    byVar[u.Key] = u.Value;
                }
            }
            return changed;
        }

        /// <summary>清理指定 uid 的所有插帧状态（玩家离开时调用）。</summary>
        public void ClearUid(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            _state.Remove(uid);
            _interpNames.Remove(uid);
            _raw.Remove(uid);
        }

        /// <summary>清空全部状态（断开连接时调用）。</summary>
        public void Clear()
        {
            _state.Clear();
            _interpNames.Clear();
            _raw.Clear();
        }

        /// <summary>
        /// 读取指定 uid 的同步变量当前值（插帧变量的 current）。
        /// 优先返回插帧平滑值；若该变量不在插帧集合或尚未建立状态，回退到最近原始值。
        /// </summary>
        public double GetSyncVar(string uid, string name)
        {
            if (_state.TryGetValue(uid, out var byVar) && byVar.TryGetValue(name, out var st))
                return st.Current;
            if (_raw.TryGetValue(uid, out var rawByVar) && rawByVar.TryGetValue(name, out var raw))
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;
            return 0.0;
        }

        /// <summary>读取指定 uid 的同步变量原始字符串值（不做插帧，最近一次收到的值）。</summary>
        public string GetSyncVarRaw(string uid, string name)
        {
            if (_raw.TryGetValue(uid, out var rawByVar) && rawByVar.TryGetValue(name, out var raw))
                return raw;
            return string.Empty;
        }
    }
}
